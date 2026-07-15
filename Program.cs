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

// Optional: expose OpenAPI in production for GPT Actions.
app.MapOpenApi();

app.UseMiddleware<SubstackApiKeyMiddleware>();

app.UseAuthorization();

app.MapControllers();

app.Run();
