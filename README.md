# UseRateLimiter() keeps every disposed WebApplicationFactory host alive

A small repro of an ASP.NET Core 10 memory leak that only shows up in integration tests.

If your app calls `app.UseRateLimiter()` and your tests create a new `WebApplicationFactory<Program>`
for each test, none of those hosts is ever freed. Disposing the factory doesn't release it, and
neither does a full GC. In a large suite the test process keeps growing until the CI runner runs
out of memory. That's how I ran into it: a CI test job died after almost two hours with
"The runner has lost communication with the server".

## Run it

```bash
dotnet run -c Release --project Harness -- leak 200
```

The harness builds 200 hosts one after another. Each one gets a single request and is then
disposed. Every 25 hosts it forces a full GC and prints how many hosts are still alive
(it keeps a `WeakReference` to each factory's `Services`), plus managed heap and private memory.

| Scenario | What it does |
|---|---|
| `leak` | Rate limiter on, a new host per test |
| `no-limiter` | The same loop without `UseRateLimiter()` |
| `shared` | Rate limiter on, one host reused for every test |
| `httpclient` | No rate limiter, a named `HttpClient` with the default handler lifetime |
| `httpclient-infinite` | The same, with `HandlerLifetime = Timeout.InfiniteTimeSpan` |

Arguments: `<scenario> [tests=200] [idle-seconds=0]`. With idle seconds set, it keeps measuring
every 30 seconds after the loop ends.

## What I measured

Windows 11, .NET 10.0.12, Release build, 200 tests per run. I haven't run it on Linux.

| Scenario | Hosts alive after full GC | Managed heap | Private memory |
|---|---|---|---|
| `leak` | **200 of 200** | 60.5 MB | 97.4 MB |
| `leak`, then 3 minutes idle | **200 of 200** | 59.0 MB | 84.1 MB |
| `no-limiter` | 1, and 0 after 30 s idle | 1.8 MB | 38.2 MB |
| `shared` | 1 (the one in use) | 1.6 MB | 16.9 MB |
| `httpclient` | 200 until +120 s idle, 0 at +150 s | 54.1 MB, then 0.7 MB | 99.3 MB, then 65.4 MB |
| `httpclient-infinite` | 1, and 0 after 30 s idle | 1.7 MB | 40.0 MB |

In the `leak` run the managed heap grows by about 0.3 MB per host (8.8 MB at 25 hosts,
31.1 MB at 100, 60.5 MB at 200) and never comes back down. The demo app is tiny. A real API
host keeps its whole service provider, request pipeline and everything they reference, so each
leaked host costs a lot more.

## What's going on

When `RateLimitingMiddleware` is constructed, it builds a `PartitionedRateLimiter<HttpContext>`
for endpoint policies. That limiter starts a timer that ticks every 100 ms. Timers live in the
runtime's static timer queue, so until the limiter is disposed it can be reached from a static
root.

Nothing disposes it. The middleware isn't `IDisposable`, and the limiter isn't stopped when the
app shuts down. A heap dump of the suite where I first hit this showed the host held through
this chain (the same one reported in dotnet/aspnetcore#69532):

```
TimerQueue (static)
  -> TimerAwaitable
    -> DefaultPartitionedRateLimiter.RunTimer (async state machine)
      -> ExecutionContext captured while the host was starting
        -> HostFactoryResolver.HostingListener
          -> the host, its service provider, the TestServer, ...
```

The limiter's partitioner closure also points back at the middleware, and from there through
`_next` to the rest of the pipeline. So there's more than one way from that timer to your host.

In production there's one host per process, and one timer ticking until the process exits
costs nothing. A test suite that builds hundreds of hosts in one process keeps every one of them.

## The smaller one: IHttpClientFactory

`DefaultHttpClientFactory` starts an expiry timer for each handler, two minutes by default.
Until that timer fires, it keeps the factory reachable, and the factory keeps the host's service
provider. This one does let go: in the `httpclient` run all 200 hosts were still alive at
+120 s and all gone at +150 s. In a fast suite, though, that still means every host from the
last two minutes sits in memory.

If you don't need handler rotation in tests, turn it off in the test host:

```csharp
services.ConfigureHttpClientDefaults(b => b.SetHandlerLifetime(Timeout.InfiniteTimeSpan));
```

Keep the default in production, where rotating handlers is how `HttpClient` picks up DNS changes.

## What you can do about it

**Share one host per test class.** Use an xUnit class fixture (`IClassFixture<...>`) and reset
state before each test. Hosts still leak, but once per class instead of once per test. In my
suite that took the number of hosts from about 1,500 to about 225, and peak memory per CI job
from 5–9 GB to 1.0–1.6 GB. The tests got faster too, since building a host is expensive. The
cost is discipline: every test has to clean up the shared state it touches.

**Wait for the fix.** dotnet/aspnetcore#69914 disposes the limiter on `ApplicationStopped`.
As of 23 October 2026 it's still open and targets `main`, and I couldn't find a backport to 10.0.

**Dispose the limiter yourself with reflection.** dotnet/aspnetcore#69532 shows a
`WebApplicationFactory` subclass that walks private fields from the server through the
middleware chain, collects every `PartitionedRateLimiter<HttpContext>` and disposes them after
the factory. It works, but it depends on private field names and compiler-generated closure
names that can change in any patch release. When they do, the walk usually just finds nothing
and the leak comes back without any error. If you use it, assert that it found at least one
limiter.

## Links

- [dotnet/aspnetcore#66434](https://github.com/dotnet/aspnetcore/issues/66434): the main issue (open)
- [dotnet/aspnetcore#69509](https://github.com/dotnet/aspnetcore/issues/69509): same symptom, separate report (open)
- [dotnet/aspnetcore#69532](https://github.com/dotnet/aspnetcore/issues/69532): minimal repro and the reflection workaround (closed as a duplicate)
- [dotnet/aspnetcore#69914](https://github.com/dotnet/aspnetcore/pull/69914): proposed fix (open)
