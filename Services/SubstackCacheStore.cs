using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RSS_API.Controllers;

namespace RSS_API.Services;

/// <summary>
/// Persists the most recently completed Substack refresh in a private GitHub repository.
/// </summary>
public sealed class SubstackCacheStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GitHubCacheOptions _options;
    private readonly ILogger<SubstackCacheStore> _logger;
    private SubstackCacheDocument? _cache;
    private string? _contentSha;
    private bool _loaded;

    public SubstackCacheStore(
        IHttpClientFactory httpClientFactory,
        IOptions<GitHubCacheOptions> options,
        ILogger<SubstackCacheStore> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<SubstackCacheDocument?> GetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(forceReload: false, cancellationToken);
        return _cache;
    }

    public async Task<SubstackCacheDocument?> GetLatestAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(forceReload: true, cancellationToken);
        return _cache;
    }

    public async Task<SubstackCacheDocument> RefreshAsync(
        Func<CancellationToken, Task<SubstackDailyResponse>> refresh,
        CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            await LoadAsync(forceReload: false, cancellationToken);

            var response = await refresh(cancellationToken);
            var cache = new SubstackCacheDocument(response, DateTimeOffset.UtcNow);
            _contentSha = await SaveAsync(cache, cancellationToken);
            _cache = cache;
            return cache;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task LoadAsync(bool forceReload, CancellationToken cancellationToken)
    {
        if (_loaded && !forceReload) return;

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (_loaded && !forceReload) return;

            ValidateOptions();
            using var request = CreateRequest(HttpMethod.Get);
            using var response = await _httpClientFactory.CreateClient("GitHubCache").SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _cache = null;
                _contentSha = null;
                _loaded = true;
                return;
            }

            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadFromJsonAsync<GitHubContentResponse>(JsonOptions, cancellationToken)
                ?? throw new InvalidOperationException("GitHub returned an empty cache response.");
            if (!string.Equals(content.Encoding, "base64", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"GitHub returned cache content with unsupported encoding '{content.Encoding}'.");
            }

            var cacheJson = Encoding.UTF8.GetString(Convert.FromBase64String(content.Content));
            _contentSha = content.Sha;

            // An empty file is equivalent to there being no cache yet. Keep its SHA so the
            // next refresh updates the existing GitHub file rather than attempting to create it.
            if (string.IsNullOrWhiteSpace(cacheJson))
            {
                _cache = null;
                _loaded = true;
                _logger.LogWarning("The Substack cache file in GitHub is empty; treating it as a missing cache.");
                return;
            }

            _cache = JsonSerializer.Deserialize<SubstackCacheDocument>(cacheJson, JsonOptions);
            _loaded = true;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or FormatException)
        {
            _logger.LogError(exception, "Could not load the Substack cache from GitHub.");
            throw;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task<string> SaveAsync(SubstackCacheDocument cache, CancellationToken cancellationToken)
    {
        var payload = new GitHubUpdateRequest(
            Message: $"Refresh Substack cache ({cache.RefreshedAt:O})",
            Content: Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(cache, JsonOptions))),
            Sha: _contentSha,
            Branch: _options.Branch);

        using var request = CreateRequest(HttpMethod.Put);
        request.Content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await _httpClientFactory.CreateClient("GitHubCache").SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<GitHubUpdateResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("GitHub returned an empty cache update response.");
        return result.Content.Sha;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method)
    {
        ValidateOptions();
        var path = string.Join('/', _options.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        var uri = $"repos/{Uri.EscapeDataString(_options.Owner)}/{Uri.EscapeDataString(_options.Repository)}/contents/{path}";
        if (method == HttpMethod.Get)
        {
            uri += $"?ref={Uri.EscapeDataString(_options.Branch)}";
        }

        var request = new HttpRequestMessage(method, uri);
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
            string.IsNullOrWhiteSpace(_options.Path))
        {
            throw new InvalidOperationException("GitHubCache owner, repository, branch, and path must be configured.");
        }
    }

    private sealed record GitHubContentResponse(string Content, string Encoding, string Sha);
    private sealed record GitHubUpdateRequest(string Message, string Content, string? Sha, string Branch);
    private sealed record GitHubUpdateResponse(GitHubUpdatedContent Content);
    private sealed record GitHubUpdatedContent(string Sha);
}

public sealed record SubstackCacheDocument(SubstackDailyResponse Response, DateTimeOffset RefreshedAt);
