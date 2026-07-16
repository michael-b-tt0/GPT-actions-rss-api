using RSS_API.Authentication;
using RSS_API.Services;

var builder = WebApplication.CreateBuilder(args);


// Add services to the container.

builder.Services.AddControllers();
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("GitHubCache", client => client.BaseAddress = new Uri("https://api.github.com/"));
builder.Services.AddSingleton<SubstackCacheStore>();
builder.Services.Configure<ApiKeyOptions>(builder.Configuration.GetSection(ApiKeyOptions.SectionName));
builder.Services.Configure<GitHubCacheOptions>(builder.Configuration.GetSection(GitHubCacheOptions.SectionName));
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
app.MapGet("/health", () => Results.Ok("OK"));

app.MapGet("/probe", () => Results.Ok(new
{
    message = "ASP.NET Core received this request",
    time = DateTimeOffset.UtcNow
}));

app.MapGet("/debug/routes", (IEnumerable<EndpointDataSource> sources) =>
{
    return sources
        .SelectMany(source => source.Endpoints)
        .OfType<RouteEndpoint>()
        .Select(endpoint => new
        {
            route = endpoint.RoutePattern.RawText,
            methods = endpoint.Metadata
                .GetMetadata<HttpMethodMetadata>()
                ?.HttpMethods
        })
        .OrderBy(endpoint => endpoint.route);
});

app.Use(async (context, next) =>
{
    context.Response.Headers["X-RSS-API"] = "aspnet-core";
    await next();
});

app.UseMiddleware<SubstackApiKeyMiddleware>();

app.UseAuthorization();

app.MapControllers();



app.Run();
