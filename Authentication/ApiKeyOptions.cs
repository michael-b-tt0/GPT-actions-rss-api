namespace RSS_API.Authentication;

public sealed class ApiKeyOptions
{
    public const string SectionName = "ApiKey";

    public string HeaderName { get; init; } = "X-API-Key";

    public string? Key { get; init; }
}
