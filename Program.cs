using RSS_API.Authentication;
using RSS_API.Services;

var builder = WebApplication.CreateBuilder(args);


// Add services to the container.

builder.Services.AddControllers();
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("GitHubCache", client => client.BaseAddress = new Uri("https://api.github.com/"));
builder.Services.AddSingleton<SubstackCacheStore>();
builder.Services.AddSingleton<GitHubOpmlStore>();
builder.Services.AddSingleton<SubstackRefreshQueue>();
builder.Services.AddHostedService<SubstackRefreshWorker>();
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

app.MapOpenApi();
app.MapGet("/health", () => Results.Ok("OK"));

app.MapGet("/probe", () => Results.Ok(new
{
    message = "ASP.NET Core received this request",
    time = DateTimeOffset.UtcNow
}));



app.Use(async (context, next) =>
{
    context.Response.Headers["X-RSS-API"] = "aspnet-core";
    await next();
});

app.UseMiddleware<SubstackApiKeyMiddleware>();

app.UseAuthorization();

app.MapControllers();



app.Run();
