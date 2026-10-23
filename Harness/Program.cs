using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;

// A named class instead of top-level statements: those would generate a second
// global Program type here and hide the web app's Program.
internal static class LeakHarness
{
    private const int BatchSize = 25;

    public static async Task Main(string[] args)
    {
        var scenario = args.Length > 0 ? args[0] : "leak";
        var total = args.Length > 1 ? int.Parse(args[1]) : 200;
        var idleSeconds = args.Length > 2 ? int.Parse(args[2]) : 0;

        var (rateLimiter, httpClient, sharedHost) = scenario switch
        {
            "leak" => (true, "off", false),
            "no-limiter" => (false, "off", false),
            "shared" => (true, "off", true),
            "httpclient" => (false, "default", false),
            "httpclient-infinite" => (false, "infinite", false),
            _ => throw new ArgumentException($"Unknown scenario '{scenario}'. Use leak, no-limiter, shared, httpclient or httpclient-infinite."),
        };

        // The app reads these through the default environment-variable configuration source.
        Environment.SetEnvironmentVariable("Demo__RateLimiter", rateLimiter ? "true" : "false");
        Environment.SetEnvironmentVariable("Demo__HttpClient", httpClient);

        var aspNetVersion = FileVersionInfo.GetVersionInfo(typeof(RateLimiterApplicationBuilderExtensions).Assembly.Location).ProductVersion;
        Console.WriteLine($"{RuntimeInformation.FrameworkDescription} | ASP.NET Core {aspNetVersion} | server GC {GCSettings.IsServerGC}");
        Console.WriteLine($"Scenario '{scenario}': UseRateLimiter={rateLimiter}, named HttpClient={httpClient}, one shared host={sharedHost}, {total} tests");
        Report("start", 0, []);

        var providers = new List<WeakReference>();
        var stopwatch = Stopwatch.StartNew();

        if (sharedHost)
        {
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            providers.Add(new WeakReference(factory.Services));
            for (var i = 1; i <= total; i++)
            {
                (await client.GetAsync("/")).EnsureSuccessStatusCode();
                if (i % BatchSize == 0)
                {
                    Report($"test {i}", 1, providers);
                }
            }
        }
        else
        {
            for (var i = 1; i <= total; i++)
            {
                providers.Add(await RunOneTestOnItsOwnHostAsync());
                if (i % BatchSize == 0)
                {
                    Report($"test {i}", i, providers);
                }
            }
        }

        Console.WriteLine($"Loop took {stopwatch.Elapsed.TotalSeconds:F1} s");

        // IHttpClientFactory handlers expire after 2 minutes by default and its cleanup timer ticks
        // every 10 seconds, so an idle watch of about 180 s tells permanent retention from time-bound.
        for (var waited = 30; waited <= idleSeconds; waited += 30)
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            Report($"+{waited} s idle", sharedHost ? 1 : providers.Count, providers);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> RunOneTestOnItsOwnHostAsync()
    {
        var factory = new WebApplicationFactory<Program>();
        using (var client = factory.CreateClient())
        {
            (await client.GetAsync("/")).EnsureSuccessStatusCode();
        }

        var weak = new WeakReference(factory.Services);
        await factory.DisposeAsync();
        return weak;
    }

    private static void Report(string label, int built, List<WeakReference> providers)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        using var process = Process.GetCurrentProcess();
        var alive = providers.Count(w => w.IsAlive);
        Console.WriteLine(
            $"{label,-13} hosts built {built,4} | still alive after full GC {alive,4} | " +
            $"managed heap {GC.GetTotalMemory(false) / 1048576.0,7:F1} MB | private {process.PrivateMemorySize64 / 1048576.0,7:F1} MB");
    }
}
