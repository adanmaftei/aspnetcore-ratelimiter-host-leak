using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();

var useRateLimiter = builder.Configuration.GetValue<bool>("Demo:RateLimiter");
var httpClientMode = builder.Configuration["Demo:HttpClient"] ?? "off";
if (httpClientMode is not ("off" or "default" or "infinite"))
{
    throw new InvalidOperationException($"Demo:HttpClient must be off, default or infinite, not '{httpClientMode}'.");
}

if (useRateLimiter)
{
    builder.Services.AddRateLimiter(options =>
        options.AddFixedWindowLimiter("fixed", limiter =>
        {
            limiter.PermitLimit = 1000;
            limiter.Window = TimeSpan.FromSeconds(1);
        }));
}

if (httpClientMode != "off")
{
    var client = builder.Services.AddHttpClient("upstream");
    if (httpClientMode == "infinite")
    {
        client.SetHandlerLifetime(Timeout.InfiniteTimeSpan);
    }
}

var app = builder.Build();

if (useRateLimiter)
{
    app.UseRateLimiter();
}

var hello = app.MapGet("/", (IServiceProvider services) =>
{
    if (httpClientMode != "off")
    {
        using var client = services.GetRequiredService<IHttpClientFactory>().CreateClient("upstream");
    }

    return "ok";
});

if (useRateLimiter)
{
    hello.RequireRateLimiting("fixed");
}

app.Run();

public partial class Program
{
}
