using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    private const int MaxTodayResponseBytes = 90_000;
    private static readonly JsonSerializerOptions ResponseJsonOptions = new(JsonSerializerDefaults.Web);

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
    private readonly ILogger<SubstackController> _logger;
    private readonly SubstackCacheStore _cacheStore;
    private readonly GitHubOpmlStore _opmlStore;
    private readonly SubstackRefreshQueue _refreshQueue;

    public SubstackController(
        IHttpClientFactory httpClientFactory,
        ILogger<SubstackController> logger,
        SubstackCacheStore cacheStore,
        GitHubOpmlStore opmlStore,
        SubstackRefreshQueue refreshQueue)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _cacheStore = cacheStore;
        _opmlStore = opmlStore;
        _refreshQueue = refreshQueue;
    }

    [HttpPost("refresh", Name = "RefreshSubstackPosts")]
    [EndpointSummary("Refresh daily Substack posts")]
    [EndpointDescription("Queues a background refresh and returns immediately. Use GET /substack/status to monitor it.")]
    [ProducesResponseType(typeof(SubstackRefreshJobStatus), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(SubstackRefreshJobStatus), StatusCodes.Status409Conflict)]
    public ActionResult<SubstackRefreshJobStatus> Refresh(
        [Description("Optional date in yyyy-MM-dd format. Defaults to today in the selected time zone.")]
        [FromQuery] string? date = null,
        [Description("Time zone used to decide which posts count as belonging to the requested day.")]
        [FromQuery] string? timeZone = "Europe/London",
        [Description("Maximum number of feed downloads allowed to run at the same time.")]
        [FromQuery] int maxConcurrency = 2,
        [Description("Minimum delay in milliseconds between starting outbound Substack feed requests.")]
        [FromQuery] int requestSpacingMs = 1250)
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

        var queued = _refreshQueue.TryQueue(
            token => _cacheStore.RefreshAsync(
                refreshToken => FetchDailyPostsAsync(targetDate.Value, zone, maxConcurrency, requestSpacingMs, refreshToken),
                token),
            out var status);

        return queued
            ? AcceptedAtAction(nameof(GetStatus), status)
            : Conflict(status);
    }

    [HttpGet("today", Name = "GetTodaysSubstackPosts")]
    [EndpointSummary("Get cached daily Substack posts")]
    [EndpointDescription("Returns the most recently refreshed Substack posts without downloading feeds. The JSON response is capped at 100,000 UTF-8 bytes by omitting whole post summaries from the end of the list when necessary. Feed errors are available from GET /substack/errors.")]
    [ProducesResponseType(typeof(SubstackDailySummaryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SubstackDailySummaryResponse>> GetToday(CancellationToken cancellationToken)
    {
        var cache = await _cacheStore.GetAsync(cancellationToken);
        if (cache is null)
        {
            return NotFound("No cached Substack results are available. Call POST /substack/refresh first.");
        }

        var response = cache.Response;
        var summaries = response.Posts.Select(ToSummary).ToArray();
        var includedPosts = new List<SubstackPostSummary>(summaries.Length);

        foreach (var summary in summaries)
        {
            includedPosts.Add(summary);
            // Test the larger non-truncated form. If this post does not fit, the final
            // truncated response is smaller because its isTruncated value is true.
            var candidate = CreateDailySummary(response, includedPosts, isTruncated: false);
            if (JsonSerializer.SerializeToUtf8Bytes(candidate, ResponseJsonOptions).Length <= MaxTodayResponseBytes)
            {
                continue;
            }

            includedPosts.RemoveAt(includedPosts.Count - 1);
            break;
        }

        var isTruncated = includedPosts.Count < summaries.Length;
        return Ok(CreateDailySummary(response, includedPosts, isTruncated));
    }

    [HttpGet("errors", Name = "GetSubstackFeedErrors")]
    [EndpointSummary("Get cached Substack feed errors")]
    [EndpointDescription("Returns feed errors from the most recently refreshed Substack results without downloading feeds.")]
    [ProducesResponseType(typeof(SubstackDailyErrorsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SubstackDailyErrorsResponse>> GetErrors(CancellationToken cancellationToken)
    {
        var cache = await _cacheStore.GetAsync(cancellationToken);
        if (cache is null)
        {
            return NotFound("No cached Substack results are available. Call POST /substack/refresh first.");
        }

        var response = cache.Response;
        return Ok(new SubstackDailyErrorsResponse(
            response.Date,
            response.TimeZone,
            response.Errors.Count,
            response.Errors));
    }

    [HttpGet("post-detail", Name = "GetSubstackPostDetail")]
    [EndpointSummary("Get full text for a cached Substack post")]
    [EndpointDescription("Returns the complete cached content text for one post identified in the daily summary.")]
    [ProducesResponseType(typeof(SubstackPost), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(string), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SubstackPost>> GetPostDetail([FromQuery] string id, CancellationToken cancellationToken)
    {
        var cache = await _cacheStore.GetAsync(cancellationToken);
        var post = cache?.Response.Posts.FirstOrDefault(candidate =>
            string.Equals(GetPostId(candidate), id, StringComparison.Ordinal));

        return post is null
            ? NotFound("No cached Substack post was found for the supplied id.")
            : Ok(post with
            {
                Id = GetPostId(post),
                ContentTextTruncated = post.ContentTextTruncated ?? TruncateContentText(post.ContentText)
            });
    }

    [HttpGet("status", Name = "GetSubstackStatus")]
    [EndpointSummary("Get Substack refresh and cache status")]
    [EndpointDescription("Reports if the latest refresh job state is idle, queued, running, completed, or failed, and whether the cached Substack results are current, stale, or missing.")]
    [ProducesResponseType(typeof(SubstackRefreshJobStatus), StatusCodes.Status200OK)]
    public async Task<ActionResult<SubstackRefreshJobStatus>> GetStatus(CancellationToken cancellationToken)
    {
        var status = _refreshQueue.GetStatus();
        var cache = await _cacheStore.GetLatestAsync(cancellationToken);
        if (cache is null)
        {
            return Ok(status with
            {
                CacheDate = null,
                CacheStatus = "missing",
                FeedCount = null,
                PostCount = null,
                ErrorCount = null
            });
        }

        var response = cache.Response;
        var cacheTimeZone = ResolveTimeZone(response.TimeZone);
        var currentDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, cacheTimeZone).DateTime);

        return Ok(status with
        {
            CacheDate = response.Date,
            CacheStatus = response.Date == currentDate ? "current" : "stale",
            FeedCount = response.FeedCount,
            PostCount = response.PostCount,
            ErrorCount = response.Errors.Count
        });
    }

    private async Task<SubstackDailyResponse> FetchDailyPostsAsync(
        DateOnly targetDate,
        TimeZoneInfo zone,
        int maxConcurrency,
        int requestSpacingMs,
        CancellationToken cancellationToken)
    {
        var feeds = await LoadSubstackFeedsAsync(cancellationToken);
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

    private async Task<IReadOnlyList<SubstackFeed>> LoadSubstackFeedsAsync(CancellationToken cancellationToken)
    {
        var document = await _opmlStore.GetAsync(cancellationToken);

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
        var title = ((string?)item.Element("title"))?.Trim() ?? "Untitled post";

        return new SubstackPost(
            Id: CreatePostId(feed.FeedUrl, link, publishedAt, title),
            FeedTitle: feed.Title,
            FeedUrl: feed.FeedUrl,
            SiteUrl: feed.SiteUrl,
            Title: title,
            Url: link,
            Author: ((string?)item.Element(DcCreatorName))?.Trim(),
            PublishedAt: publishedAt,
            ContentText: contentText,
            ContentTextTruncated: TruncateContentText(contentText),
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
        var title = ((string?)entry.Element(atom + "title"))?.Trim() ?? "Untitled post";

        return new SubstackPost(
            Id: CreatePostId(feed.FeedUrl, link, publishedAt, title),
            FeedTitle: feed.Title,
            FeedUrl: feed.FeedUrl,
            SiteUrl: feed.SiteUrl,
            Title: title,
            Url: link,
            Author: ((string?)entry.Element(atom + "author")?.Element(atom + "name"))?.Trim(),
            PublishedAt: publishedAt,
            ContentText: contentText,
            ContentTextTruncated: TruncateContentText(contentText),
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

    private static string? TruncateContentText(string? contentText) =>
        contentText is null ? null : contentText[..Math.Min(contentText.Length, 2800)];

    private static string CreatePostId(string feedUrl, string? postUrl, DateTimeOffset? publishedAt, string title)
    {
        var source = $"{feedUrl}\n{postUrl}\n{publishedAt:O}\n{title}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..24].ToLowerInvariant();
    }

    private static string GetPostId(SubstackPost post) =>
        string.IsNullOrWhiteSpace(post.Id)
            ? CreatePostId(post.FeedUrl, post.Url, post.PublishedAt, post.Title)
            : post.Id;

    private static SubstackPostSummary ToSummary(SubstackPost post) => new(
        GetPostId(post),
        post.FeedTitle,
        post.FeedUrl,
        post.SiteUrl,
        post.Title,
        post.Url,
        post.Author,
        post.PublishedAt,
        post.ContentTextTruncated ?? TruncateContentText(post.ContentText),
        post.ContentCharacterCount,
        post.IsPaidPost);

    private static SubstackDailySummaryResponse CreateDailySummary(
        SubstackDailyResponse response,
        IReadOnlyList<SubstackPostSummary> posts,
        bool isTruncated) => new(
        response.Date,
        response.TimeZone,
        response.FeedCount,
        response.PostCount,
        posts,
        posts.Count,
        isTruncated);

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
/// Compact daily response for GPT Actions and other clients that do not need every full article.
/// </summary>
public sealed record SubstackDailySummaryResponse(
    DateOnly Date,
    string TimeZone,
    int FeedCount,
    int PostCount,
    IReadOnlyList<SubstackPostSummary> Posts,
    int ReturnedPostCount,
    bool IsTruncated);

/// <summary>
/// Feed errors recorded during the cached Substack refresh.
/// </summary>
public sealed record SubstackDailyErrorsResponse(
    DateOnly Date,
    string TimeZone,
    int ErrorCount,
    IReadOnlyList<FeedError> Errors);

/// <summary>
/// Normalized representation of a Substack post returned from an RSS or Atom feed.
/// </summary>
public sealed record SubstackPost(
    [property: Description("Stable identifier for retrieving this post's full cached content.")]
    string Id,
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
    [property: Description("First 2,800 characters of the plain-text feed content.")]
    string? ContentTextTruncated,
    [property: Description("Number of characters in the plain-text feed content.")]
    int ContentCharacterCount,
    [property: Description("Whether the feed content ends with 'Read more', indicating a paid post preview.")]
    bool IsPaidPost);

/// <summary>
/// Lightweight representation of a post returned by the daily endpoint.
/// </summary>
public sealed record SubstackPostSummary(
    string Id,
    string FeedTitle,
    string FeedUrl,
    string? SiteUrl,
    string Title,
    string? Url,
    string? Author,
    DateTimeOffset? PublishedAt,
    string? ContentTextTruncated,
    int ContentCharacterCount,
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
