namespace RSS_API.Services;

public sealed class GitHubCacheOptions
{
    public const string SectionName = "GitHubCache";

    public string Token { get; init; } = string.Empty;
    public string Owner { get; init; } = "michael-b-tt0";
    public string Repository { get; init; } = "my-api-data";
    public string Branch { get; init; } = "main";
    public string Path { get; init; } = "substack-cache.json";
}
