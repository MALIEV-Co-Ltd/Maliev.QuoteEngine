using DotNet.Testcontainers.Configurations;
using Maliev.QuoteEngine.Bff;
using Maliev.QuoteEngine.Bff.Hubs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Testcontainers.Redis;

namespace Maliev.QuoteEngine.Tests;

public sealed class QuoteEngineSignalRBackplaneTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddQuoteEngineSignalR_without_nonblank_redis_uses_in_memory_hub_lifetime_manager(
        string? redisConnectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:redis"] = redisConnectionString
            })
            .Build();

        services.AddQuoteEngineSignalR(configuration);

        using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<HubLifetimeManager<QuoteNotificationsHub>>();

        Assert.IsType<DefaultHubLifetimeManager<QuoteNotificationsHub>>(manager);
    }

    [Fact]
    public void AddQuoteEngineSignalR_with_redis_uses_isolated_resilient_backplane_options()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:redis"] = "localhost:6379"
            })
            .Build();

        services.AddQuoteEngineSignalR(configuration);

        using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<HubLifetimeManager<QuoteNotificationsHub>>();
        var options = provider
            .GetRequiredService<IOptions<Microsoft.AspNetCore.SignalR.StackExchangeRedis.RedisOptions>>()
            .Value;

        Assert.Equal(
            "Microsoft.AspNetCore.SignalR.StackExchangeRedis.RedisHubLifetimeManager`1",
            manager.GetType().GetGenericTypeDefinition().FullName);
        Assert.Equal("maliev:quote-engine:signalr", options.Configuration.ChannelPrefix.ToString());
        Assert.False(options.Configuration.AbortOnConnectFail);
        Assert.Equal(3, options.Configuration.ConnectRetry);
        Assert.Equal(5_000, options.Configuration.ConnectTimeout);
        Assert.Equal(5_000, options.Configuration.AsyncTimeout);
        Assert.Equal(5_000, options.Configuration.SyncTimeout);
        Assert.Equal(30, options.Configuration.KeepAlive);
    }
}

public sealed class QuoteEngineSignalRBackplaneIntegrationTests : IAsyncLifetime
{
    static QuoteEngineSignalRBackplaneIntegrationTests()
    {
        TestcontainersSettings.ResourceReaperEnabled = false;
    }

    private readonly RedisContainer _redis = new RedisBuilder(
        "redis:7.0-alpine@sha256:c9d92d840fd011c908f040592857c724ae6d877f2aba5c40ad963276507386b2")
        .Build();

    public Task InitializeAsync() => _redis.StartAsync();

    public Task DisposeAsync() => _redis.DisposeAsync().AsTask();

    [Fact]
    public async Task Redis_backplane_delivers_a_hub_message_between_independent_service_providers()
    {
        using var firstHost = await CreateHostAsync();
        using var secondHost = await CreateHostAsync();
        await using var secondConnection = CreateHubConnection(secondHost);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = secondConnection.On<string>("BackplaneProbe", payload => received.TrySetResult(payload));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await secondConnection.StartAsync(timeout.Token);

        var expected = $"probe-{Guid.NewGuid():N}";
        var firstHub = firstHost.Services.GetRequiredService<IHubContext<BackplaneProbeHub>>();
        while (!received.Task.IsCompleted)
        {
            timeout.Token.ThrowIfCancellationRequested();
            await firstHub.Clients.All.SendAsync("BackplaneProbe", expected, timeout.Token);
            await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token));
        }

        Assert.Equal(expected, await received.Task.WaitAsync(timeout.Token));
    }

    private Task<IHost> CreateHostAsync() =>
        new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddQuoteEngineSignalR(new ConfigurationBuilder()
                        .AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["ConnectionStrings:redis"] = _redis.GetConnectionString()
                        })
                        .Build());
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapHub<BackplaneProbeHub>("/backplane-probe"));
                }))
            .StartAsync();

    private static HubConnection CreateHubConnection(IHost host) =>
        new HubConnectionBuilder()
            .WithUrl("http://localhost/backplane-probe", options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => host.GetTestServer().CreateHandler();
            })
            .Build();

    public sealed class BackplaneProbeHub : Hub;
}
