using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Options;

namespace RSS_API.Services;

/// <summary>
/// Loads the Feedbro OPML document from the configured GitHub cache repository.
/// </summary>
public sealed class GitHubOpmlStore
{
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GitHubCacheOptions _options;
    private readonly ILogger<GitHubOpmlStore> _logger;
    private XDocument? _document;

    public GitHubOpmlStore(
        IHttpClientFactory httpClientFactory,
        IOptions<GitHubCacheOptions> options,
        ILogger<GitHubOpmlStore> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<XDocument> GetAsync(CancellationToken cancellationToken)
    {
        if (_document is not null) return _document;

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (_document is not null) return _document;

            ValidateOptions();
            using var request = CreateRequest();
            using var response = await _httpClientFactory.CreateClient("GitHubCache").SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new InvalidOperationException($"The OPML file '{_options.OpmlPath}' was not found in the configured GitHub repository.");
            }

            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadFromJsonAsync<GitHubContentResponse>(cancellationToken: cancellationToken)
                ?? throw new InvalidOperationException("GitHub returned an empty OPML response.");
            if (!string.Equals(content.Encoding, "base64", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"GitHub returned OPML content with unsupported encoding '{content.Encoding}'.");
            }

            _document = XDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(content.Content)));
            return _document;
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or FormatException or System.Xml.XmlException)
        {
            _logger.LogError(exception, "Could not load the Feedbro OPML document from GitHub.");
            throw;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private HttpRequestMessage CreateRequest()
    {
        var path = string.Join('/', _options.OpmlPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        var uri = $"repos/{Uri.EscapeDataString(_options.Owner)}/{Uri.EscapeDataString(_options.Repository)}/contents/{path}?ref={Uri.EscapeDataString(_options.Branch)}";
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.ParseAdd("RSS-API/1.0");
        return request;
    }

    private void ValidateOptions()
    {
        if (string.IsNullOrWhiteSpace(_options.Token))
        {
            throw new InvalidOperationException("GitHubCache:Token must be configured.");
        }

        if (string.IsNullOrWhiteSpace(_options.Owner) ||
            string.IsNullOrWhiteSpace(_options.Repository) ||
            string.IsNullOrWhiteSpace(_options.Branch) ||
            string.IsNullOrWhiteSpace(_options.OpmlPath))
        {
            throw new InvalidOperationException("GitHubCache owner, repository, branch, and OpmlPath must be configured.");
        }
    }

    private sealed record GitHubContentResponse(string Content, string Encoding);
}
