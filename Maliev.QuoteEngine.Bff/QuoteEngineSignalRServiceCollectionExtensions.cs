using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace Maliev.QuoteEngine.Bff;

internal static class QuoteEngineSignalRServiceCollectionExtensions
{
    internal const string RedisChannelPrefix = "maliev:quote-engine:signalr";

    internal static ISignalRServerBuilder AddQuoteEngineSignalR(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var signalR = services.AddSignalR();
        var redisConnectionString = configuration.GetConnectionString("redis");
        if (string.IsNullOrWhiteSpace(redisConnectionString))
        {
            return signalR;
        }

        var redisConfiguration = ConfigurationOptions.Parse(redisConnectionString.Trim());
        redisConfiguration.AbortOnConnectFail = false;
        redisConfiguration.ConnectRetry = 3;
        redisConfiguration.ConnectTimeout = 5_000;
        redisConfiguration.AsyncTimeout = 5_000;
        redisConfiguration.SyncTimeout = 5_000;
        redisConfiguration.KeepAlive = 30;
        redisConfiguration.ChannelPrefix = RedisChannel.Literal(RedisChannelPrefix);

        return signalR.AddStackExchangeRedis(options =>
        {
            options.Configuration = redisConfiguration;
        });
    }
}
