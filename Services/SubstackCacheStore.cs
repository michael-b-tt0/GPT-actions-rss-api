using System.Text.Json;
using RSS_API.Controllers;

namespace RSS_API.Services;

/// <summary>
/// Persists the most recently completed Substack refresh and serializes refresh requests.
/// </summary>
public sealed class SubstackCacheStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly string _cachePath;
    private readonly ILogger<SubstackCacheStore> _logger;
    private SubstackCacheDocument? _cache;
    private bool _loaded;

    public SubstackCacheStore(IWebHostEnvironment environment, ILogger<SubstackCacheStore> logger)
    {
        _cachePath = Path.Combine(environment.ContentRootPath, "data", "substack-cache.json");
        _logger = logger;
    }

    public SubstackCacheDocument? Get()
    {
        EnsureLoaded();
        return _cache;
    }

    public async Task<SubstackCacheDocument> RefreshAsync(
        Func<CancellationToken, Task<SubstackDailyResponse>> refresh,
        CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            var response = await refresh(cancellationToken);
            var cache = new SubstackCacheDocument(response, DateTimeOffset.UtcNow);
            await SaveAsync(cache, cancellationToken);
            _cache = cache;
            _loaded = true;
            return cache;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;

        try
        {
            if (File.Exists(_cachePath))
            {
                _cache = JsonSerializer.Deserialize<SubstackCacheDocument>(File.ReadAllText(_cachePath), JsonOptions);
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            _logger.LogWarning(exception, "Could not load the Substack cache from {CachePath}", _cachePath);
        }
        finally
        {
            _loaded = true;
        }
    }

    private async Task SaveAsync(SubstackCacheDocument cache, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_cachePath)!;
        Directory.CreateDirectory(directory);

        var temporaryPath = $"{_cachePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(cache, JsonOptions), cancellationToken);
            File.Move(temporaryPath, _cachePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}

public sealed record SubstackCacheDocument(SubstackDailyResponse Response, DateTimeOffset RefreshedAt);
