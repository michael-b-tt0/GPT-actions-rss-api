using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Threading;
using System.ComponentModel;
using Microsoft.AspNetCore.Mvc;
using RSS_API.Services;

namespace RSS_API.Controllers;

[ApiController]
[Route("[controller]")]
public sealed class SubstackController : ControllerBase
{
    private static readonly XName OutlineName = "outline";
    private static readonly XName RssItemName = "item";
    private static readonly XName AtomEntryName = XName.Get("entry", "http://www.w3.org/2005/Atom");
    private static readonly XName ContentEncodedName = XName.Get("encoded", "http://purl.org/rss/1.0/modules/content/");
    private static readonly XName DcCreatorName = XName.Get("creator", "http://purl.org/dc/elements/1.1/");
    private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Lock RequestPacingLock = new();
    private static DateTimeOffset _nextAllowedRequestStart = DateTimeOffset.MinValue;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<SubstackController> _logger;
    private readonly SubstackCacheStore _cacheStore;

    public SubstackController(
        IHttpClientFactory httpClientFactory,
        IWebHostEnvironment environment,
        ILogger<SubstackController> logger,
        SubstackCacheStore cacheStore)
    {
        _httpClientFactory = httpClientFactory;
        _environment = environment;
        _logger = logger;
        _cacheStore = cacheStore;
    }

    [HttpPost("refresh", Name = "RefreshSubstackPosts")]
    [EndpointSummary("Refresh daily Substack posts")]
    [EndpointDescription("Fetches Substack feeds from the OPML subscription list and saves the results to the local cache.")]
    [ProducesResponseType(typeof(SubstackDailyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SubstackDailyResponse>> Refresh(
        [Description("Optional date in yyyy-MM-dd format. Defaults to today in the selected time zone.")]
        [FromQuery] string? date = null,
        [Description("Time zone used to decide which posts count as belonging to the requested day.")]
        [FromQuery] string? timeZone = "Europe/London",
        [Description("Maximum number of feed downloads allowed to run at the same time.")]
        [FromQuery] int maxConcurrency = 2,
        [Description("Minimum delay in milliseconds between starting outbound Substack feed requests.")]
        [FromQuery] int requestSpacingMs = 1250,
        CancellationToken cancellationToken = default)
    {
        var zone = ResolveTimeZone(timeZone);
        var targetDate = ParseTargetDate(date, zone);
        if (targetDate is null)
        {
            return BadRequest("Use date format yyyy-MM-dd.");
        }

        if (maxConcurrency is < 1 or > 20)
        {
            return BadRequest("Use maxConcurrency between 1 and 20.");
        }

        if (requestSpacingMs is < 0 or > 10000)
        {
            return BadRequest("Use requestSpacingMs between 0 and 10000.");
        }

        var cache = await _cacheStore.RefreshAsync(
            token => FetchDailyPostsAsync(targetDate.Value, zone, maxConcurrency, requestSpacingMs, token),
            cancellationToken);

        return Ok(cache.Response);
    }

    [HttpGet("today", Name = "GetTodaysSubstackPosts")]
    [EndpointSummary("Get cached daily Substack posts")]
    [EndpointDescription("Returns the most recently refreshed Substack results without downloading feeds.")]
    [ProducesResponseType(typeof(SubstackDailyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public ActionResult<SubstackDailyResponse> GetToday()
    {
        var cache = _cacheStore.Get();
        return cache is null
            ? NotFound("No cached Substack results are available. Call POST /substack/refresh first.")
            : Ok(cache.Response);
    }

    [HttpGet("status", Name = "GetSubstackStatus")]
    [EndpointSummary("Get Substack cache status")]
    [ProducesResponseType(typeof(SubstackRefreshStatus), StatusCodes.Status200OK)]
    public ActionResult<SubstackRefreshStatus> GetStatus()
    {
        var cache = _cacheStore.Get();
        if (cache is null)
        {
            return Ok(new SubstackRefreshStatus(null, 0, 0, 0, "missing"));
        }

        var zone = ResolveTimeZone(cache.Response.TimeZone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
        var status = cache.Response.Date == today ? "fresh" : "stale";
        return Ok(new SubstackRefreshStatus(
            cache.RefreshedAt,
            cache.Response.FeedCount,
            cache.Response.PostCount,
            cache.Response.Errors.Count,
            status));
    }

    private async Task<SubstackDailyResponse> FetchDailyPostsAsync(
        DateOnly targetDate,
        TimeZoneInfo zone,
        int maxConcurrency,
        int requestSpacingMs,
        CancellationToken cancellationToken)
    {
        var feeds = LoadSubstackFeeds();
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RSS-API/1.0 (+daily-substack-summary)");
        client.Timeout = TimeSpan.FromSeconds(45);

        var feedResults = await FetchFeedsAsync(client, feeds, targetDate, zone, maxConcurrency, requestSpacingMs, cancellationToken);
        var posts = feedResults.SelectMany(result => result.Posts).OrderByDescending(post => post.PublishedAt).ToArray();
        var errors = feedResults.Where(result => result.Error is not null).Select(result => result.Error!).ToArray();

        return new SubstackDailyResponse(targetDate, zone.Id, feeds.Count, posts.Length, posts, errors);
    }

    private async Task<FeedFetchResult[]> FetchFeedsAsync(
        HttpClient client,
        IReadOnlyList<SubstackFeed> feeds,
        DateOnly targetDate,
        TimeZoneInfo zone,
        int maxConcurrency,
        int requestSpacingMs,
        CancellationToken cancellationToken)
    {
        using var throttler = new SemaphoreSlim(maxConcurrency);
        var tasks = feeds.Select(async feed =>
        {
            await throttler.WaitAsync(cancellationToken);
            try
            {
                await WaitForRequestSlotAsync(requestSpacingMs, cancellationToken);
                return await FetchTodaysPostsAsync(client, feed, targetDate, zone, cancellationToken);
            }
            finally
            {
                throttler.Release();
            }
        });

        return await Task.WhenAll(tasks);
    }

    private static async Task WaitForRequestSlotAsync(int requestSpacingMs, CancellationToken cancellationToken)
    {
        if (requestSpacingMs <= 0)
        {
            return;
        }

        TimeSpan delay;

        lock (RequestPacingLock)
        {
            var now = DateTimeOffset.UtcNow;
            var scheduledStart = now > _nextAllowedRequestStart ? now : _nextAllowedRequestStart;
            delay = scheduledStart - now;
            _nextAllowedRequestStart = scheduledStart.AddMilliseconds(requestSpacingMs);
        }

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken);
        }
    }

    private IReadOnlyList<SubstackFeed> LoadSubstackFeeds()
    {
        var opmlPath = Path.Combine(_environment.ContentRootPath, "feedbro-subscriptions-20260317-015028.opml");
        var document = XDocument.Load(opmlPath);

        return document
            .Descendants(OutlineName)
            .Where(outline => ((string?)outline.Attribute("xmlUrl"))?.Contains("substack", StringComparison.OrdinalIgnoreCase) == true)
            .Select(outline => new SubstackFeed(
                Title: (string?)outline.Attribute("title") ?? (string?)outline.Attribute("text") ?? "Untitled Substack",
                FeedUrl: ((string?)outline.Attribute("xmlUrl"))!,
                SiteUrl: (string?)outline.Attribute("htmlUrl")))
            .DistinctBy(feed => feed.FeedUrl)
            .OrderBy(feed => feed.Title)
            .ToArray();
    }

    private async Task<FeedFetchResult> FetchTodaysPostsAsync(
        HttpClient client,
        SubstackFeed feed,
        DateOnly targetDate,
        TimeZoneInfo zone,
        CancellationToken cancellationToken)
    {
        try
        {
            using var stream = await client.GetStreamAsync(feed.FeedUrl, cancellationToken);
            var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);

            var posts = document.Descendants(RssItemName)
                .Select(item => ParseRssPost(item, feed))
                .Concat(document.Descendants(AtomEntryName).Select(entry => ParseAtomPost(entry, feed)))
                .Where(post => post.PublishedAt is not null)
                .Where(post => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(post.PublishedAt!.Value, zone).DateTime) == targetDate)
                .ToArray();

            return new FeedFetchResult(posts, null);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Xml.XmlException)
        {
            _logger.LogWarning(exception, "Failed to fetch Substack feed {FeedUrl}", feed.FeedUrl);
            return new FeedFetchResult(
                [],
                new FeedError(feed.Title, feed.FeedUrl, exception.Message));
        }
    }

    private static SubstackPost ParseRssPost(XElement item, SubstackFeed feed)
    {
        var publishedAt = ParseDate((string?)item.Element("pubDate") ?? (string?)item.Element(XName.Get("date", "http://purl.org/dc/elements/1.1/")));
        var contentHtml = (string?)item.Element(ContentEncodedName) ?? (string?)item.Element("description");
        var link = ((string?)item.Element("link"))?.Trim();
        var contentText = ToPlainText(contentHtml);

        return new SubstackPost(
            FeedTitle: feed.Title,
            FeedUrl: feed.FeedUrl,
            SiteUrl: feed.SiteUrl,
            Title: ((string?)item.Element("title"))?.Trim() ?? "Untitled post",
            Url: link,
            Author: ((string?)item.Element(DcCreatorName))?.Trim(),
            PublishedAt: publishedAt,
            ContentText: contentText,
            ContentCharacterCount: contentText?.Length ?? 0,
            IsPaidPost: IsPaidPost(contentText));
    }

    private static SubstackPost ParseAtomPost(XElement entry, SubstackFeed feed)
    {
        var atom = XNamespace.Get("http://www.w3.org/2005/Atom");
        var publishedAt = ParseDate((string?)entry.Element(atom + "published") ?? (string?)entry.Element(atom + "updated"));
        var contentHtml = (string?)entry.Element(atom + "content") ?? (string?)entry.Element(atom + "summary");
        var link = entry.Elements(atom + "link")
            .FirstOrDefault(linkElement => ((string?)linkElement.Attribute("rel")) is null or "alternate")
            ?.Attribute("href")
            ?.Value;
        var contentText = ToPlainText(contentHtml);

        return new SubstackPost(
            FeedTitle: feed.Title,
            FeedUrl: feed.FeedUrl,
            SiteUrl: feed.SiteUrl,
            Title: ((string?)entry.Element(atom + "title"))?.Trim() ?? "Untitled post",
            Url: link,
            Author: ((string?)entry.Element(atom + "author")?.Element(atom + "name"))?.Trim(),
            PublishedAt: publishedAt,
            ContentText: contentText,
            ContentCharacterCount: contentText?.Length ?? 0,
            IsPaidPost: IsPaidPost(contentText));
    }

    private static DateTimeOffset? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed)
            ? parsed
            : null;
    }

    private static string? ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var withoutTags = HtmlTagRegex.Replace(html, " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        return WhitespaceRegex.Replace(decoded, " ").Trim();
    }

    private static bool IsPaidPost(string? contentText) =>
        contentText?.EndsWith("Read more", StringComparison.OrdinalIgnoreCase) == true;

    private static DateOnly? ParseTargetDate(string? date, TimeZoneInfo zone)
    {
        if (string.IsNullOrWhiteSpace(date))
        {
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
        }

        return DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }

    private static TimeZoneInfo ResolveTimeZone(string? timeZone)
    {
        if (!string.IsNullOrWhiteSpace(timeZone))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timeZone);
            }
            catch (TimeZoneNotFoundException) when (timeZone.Equals("Europe/London", StringComparison.OrdinalIgnoreCase))
            {
                return TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");
            }
        }

        return TimeZoneInfo.Local;
    }

    private sealed record FeedFetchResult(SubstackPost[] Posts, FeedError? Error);
    private sealed record SubstackFeed(string Title, string FeedUrl, string? SiteUrl);
}

/// <summary>
/// Result payload for the daily Substack feed query.
/// </summary>
public sealed record SubstackDailyResponse(
    [property: Description("The requested calendar date used to filter matching posts.")]
    DateOnly Date,
    [property: Description("The time zone used when deciding which posts belong to the requested date.")]
    string TimeZone,
    [property: Description("Total number of Substack feeds inspected from the OPML file.")]
    int FeedCount,
    [property: Description("Number of posts published on the requested date.")]
    int PostCount,
    [property: Description("Posts published on the requested date.")]
    IReadOnlyList<SubstackPost> Posts,
    [property: Description("Per-feed errors for feeds that could not be fetched or parsed.")]
    IReadOnlyList<FeedError> Errors);

/// <summary>
/// Normalized representation of a Substack post returned from an RSS or Atom feed.
/// </summary>
public sealed record SubstackPost(
    [property: Description("Display title of the Substack feed that produced the post.")]
    string FeedTitle,
    [property: Description("RSS or Atom feed URL that produced the post.")]
    string FeedUrl,
    [property: Description("Primary website URL for the Substack publication, when available from the OPML file.")]
    string? SiteUrl,
    [property: Description("Post title from the feed item.")]
    string Title,
    [property: Description("Canonical post URL, when present in the feed item.")]
    string? Url,
    [property: Description("Author name from the feed item, when available.")]
    string? Author,
    [property: Description("Publication timestamp parsed from the feed item.")]
    DateTimeOffset? PublishedAt,
    [property: Description("Plain-text version of the feed content, suitable for summarization.")]
    string? ContentText,
    [property: Description("Number of characters in the plain-text feed content.")]
    int ContentCharacterCount,
    [property: Description("Whether the feed content ends with 'Read more', indicating a paid post preview.")]
    bool IsPaidPost);

/// <summary>
/// Error details for a feed that could not be fetched or parsed.
/// </summary>
public sealed record FeedError(
    [property: Description("Display title of the Substack feed that failed.")]
    string FeedTitle,
    [property: Description("Feed URL that failed.")]
    string FeedUrl,
    [property: Description("Error message captured while fetching or parsing the feed.")]
    string Message);

/// <summary>
/// Summary of the most recently completed Substack refresh.
/// </summary>
public sealed record SubstackRefreshStatus(
    DateTimeOffset? LastRefreshTime,
    int FeedCount,
    int PostCount,
    int ErrorCount,
    string CacheStatus);
