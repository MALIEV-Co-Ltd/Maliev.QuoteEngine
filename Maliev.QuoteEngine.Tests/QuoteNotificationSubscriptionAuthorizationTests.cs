using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Maliev.QuoteEngine.Bff.Hubs;
using Maliev.QuoteEngine.Bff.Security;
using Maliev.QuoteEngine.Bff.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Maliev.QuoteEngine.Tests;

[Collection(QuoteEngineEndpointTestCollection.Name)]
public sealed class QuoteNotificationSubscriptionAuthorizationTests(
    QuoteEngineWebApplicationFactory factory) : IClassFixture<QuoteEngineWebApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Signed_visitor_can_subscribe_to_its_session_and_upload_but_another_visitor_cannot()
    {
        await using var scopedFactory = factory.WithWebHostBuilder(_ => { });
        using var scope = scopedFactory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var ownerStore = services.GetRequiredService<IQuoteAgentSessionOwnerStore>();
        var uploads = services.GetRequiredService<QuoteEnginePrototypeStore>();
        var authorizer = services.GetRequiredService<QuoteNotificationSubscriptionAuthorizer>();
        var ownerVisitor = Guid.NewGuid();
        var otherVisitor = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        const string storagePath = "quote-agent/visitor-a/bracket.stl";

        Assert.True(await ownerStore.TryCreateAsync(
            sessionId,
            new QuoteAgentSessionOwner(CustomerId: null, ownerVisitor),
            CancellationToken.None));
        uploads.TrackAgentUpload(
            "upload-a",
            Guid.NewGuid(),
            "bracket.stl",
            "model/stl",
            128,
            storagePath,
            sessionId,
            customerId: null,
            visitorId: ownerVisitor);

        var ownerContext = CreateVisitorContext(ownerVisitor, DateTimeOffset.UtcNow.AddDays(1));
        var otherContext = CreateVisitorContext(otherVisitor, DateTimeOffset.UtcNow.AddDays(1));

        Assert.True(authorizer.CanConnect(ownerContext));
        Assert.Equal(
            QuoteNotificationsHub.QuoteSessionGroup(sessionId),
            await authorizer.AuthorizeSessionAsync(ownerContext, sessionId, CancellationToken.None));
        Assert.Equal(
            QuoteNotificationsHub.FileGroup(storagePath),
            await authorizer.AuthorizeFileAsync(ownerContext, storagePath, CancellationToken.None));
        Assert.Null(await authorizer.AuthorizeSessionAsync(otherContext, sessionId, CancellationToken.None));
        Assert.Null(await authorizer.AuthorizeFileAsync(otherContext, storagePath, CancellationToken.None));
    }

    [Fact]
    public async Task Unsigned_or_expired_visitor_cannot_connect_or_replay_a_subscription()
    {
        await using var scopedFactory = factory.WithWebHostBuilder(_ => { });
        using var scope = scopedFactory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var ownerStore = services.GetRequiredService<IQuoteAgentSessionOwnerStore>();
        var authorizer = services.GetRequiredService<QuoteNotificationSubscriptionAuthorizer>();
        var visitorId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        Assert.True(await ownerStore.TryCreateAsync(
            sessionId,
            new QuoteAgentSessionOwner(CustomerId: null, visitorId),
            CancellationToken.None));

        var unsigned = new DefaultHttpContext();
        var expired = CreateVisitorContext(visitorId, DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.False(authorizer.CanConnect(unsigned));
        Assert.False(authorizer.CanConnect(expired));
        Assert.Null(await authorizer.AuthorizeSessionAsync(unsigned, sessionId, CancellationToken.None));
        Assert.Null(await authorizer.AuthorizeSessionAsync(expired, sessionId, CancellationToken.None));
    }

    [Fact]
    public async Task Only_the_authenticated_order_customer_can_subscribe_to_an_order()
    {
        await using var scopedFactory = factory.WithWebHostBuilder(_ => { });
        using var scope = scopedFactory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var authorizer = services.GetRequiredService<QuoteNotificationSubscriptionAuthorizer>();
        var prototypeCustomerId = services.GetRequiredService<QuoteEnginePrototypeStore>()
            .PrototypeCustomer.CustomerId;
        const string orderNumber = "MO-DEMO-0001";

        var owner = CreateCustomerContext(prototypeCustomerId);
        var otherCustomer = CreateCustomerContext(Guid.NewGuid());

        Assert.True(authorizer.CanConnect(owner));
        Assert.Equal(
            QuoteNotificationsHub.OrderGroup(orderNumber),
            await authorizer.AuthorizeOrderAsync(owner, orderNumber, CancellationToken.None));
        Assert.Null(await authorizer.AuthorizeOrderAsync(otherCustomer, orderNumber, CancellationToken.None));
    }

    [Fact]
    public void Hub_exposes_only_subscription_methods_to_browser_callers()
    {
        var publicMethods = typeof(QuoteNotificationsHub)
            .GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "JoinFileGroup",
                "JoinOrderGroup",
                "JoinQuoteSessionGroup",
                "LeaveFileGroup",
                "LeaveOrderGroup",
                "LeaveQuoteSessionGroup",
                "OnConnectedAsync",
                "OnDisconnectedAsync"
            ],
            publicMethods);
    }

    [Fact]
    public async Task Hub_adds_only_an_authorized_connection_to_a_server_derived_group()
    {
        await using var scopedFactory = factory.WithWebHostBuilder(_ => { });
        using var scope = scopedFactory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var ownerStore = services.GetRequiredService<IQuoteAgentSessionOwnerStore>();
        var sessionId = Guid.NewGuid();
        var visitorId = Guid.NewGuid();
        Assert.True(await ownerStore.TryCreateAsync(
            sessionId,
            new QuoteAgentSessionOwner(CustomerId: null, visitorId),
            CancellationToken.None));

        var groups = Substitute.For<IGroupManager>();
        groups.AddToGroupAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        groups.RemoveFromGroupAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var hub = CreateHub(CreateVisitorContext(visitorId, DateTimeOffset.UtcNow.AddDays(1), services), groups);

        await hub.JoinQuoteSessionGroup(sessionId);
        await hub.OnDisconnectedAsync(exception: null);

        await groups.Received(1).AddToGroupAsync(
            "connection-a",
            QuoteNotificationsHub.QuoteSessionGroup(sessionId),
            Arg.Any<CancellationToken>());
        await groups.Received(1).RemoveFromGroupAsync(
            "connection-a",
            QuoteNotificationsHub.QuoteSessionGroup(sessionId),
            CancellationToken.None);
    }

    [Fact]
    public async Task Hub_rejects_a_foreign_connection_without_adding_a_group_membership()
    {
        await using var scopedFactory = factory.WithWebHostBuilder(_ => { });
        using var scope = scopedFactory.Services.CreateScope();
        var ownerStore = scope.ServiceProvider.GetRequiredService<IQuoteAgentSessionOwnerStore>();
        var sessionId = Guid.NewGuid();
        Assert.True(await ownerStore.TryCreateAsync(
            sessionId,
            new QuoteAgentSessionOwner(CustomerId: null, Guid.NewGuid()),
            CancellationToken.None));

        var groups = Substitute.For<IGroupManager>();
        var hub = CreateHub(
            CreateVisitorContext(Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1), scope.ServiceProvider),
            groups);

        await Assert.ThrowsAsync<HubException>(() => hub.JoinQuoteSessionGroup(sessionId));
        await groups.DidNotReceive().AddToGroupAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    private static DefaultHttpContext CreateVisitorContext(
        Guid visitorId,
        DateTimeOffset expiresAt,
        IServiceProvider? requestServices = null)
    {
        var context = new DefaultHttpContext();
        if (requestServices is not null)
        {
            context.RequestServices = requestServices;
        }

        context.Request.Headers.Cookie = $"{AnonymousVisitorCookie.CookieName}=" +
            CreateSignedVisitorCookie(visitorId, DateTimeOffset.UtcNow.AddMinutes(-1), expiresAt);
        return context;
    }

    private static DefaultHttpContext CreateCustomerContext(Guid customerId)
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("customer_id", customerId.ToString("D"))],
            authenticationType: "test"));
        return context;
    }

    private static string CreateSignedVisitorCookie(
        Guid visitorId,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt)
    {
        var payload = new AnonymousVisitorPayload(visitorId, issuedAt, expiresAt);
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var encodedPayload = Base64UrlTextEncoder.Encode(Encoding.UTF8.GetBytes(json));
        using var hmac = new HMACSHA256(
            Encoding.UTF8.GetBytes("maliev-local-development-anonymous-visitor-key"));
        var signature = Base64UrlTextEncoder.Encode(
            hmac.ComputeHash(Encoding.UTF8.GetBytes(encodedPayload)));
        return $"{encodedPayload}.{signature}";
    }

    private static QuoteNotificationsHub CreateHub(HttpContext httpContext, IGroupManager groups)
    {
        var features = new FeatureCollection();
        var httpContextFeature = Substitute.For<IHttpContextFeature>();
        httpContextFeature.HttpContext.Returns(httpContext);
        features.Set(httpContextFeature);
        var callerContext = Substitute.For<HubCallerContext>();
        callerContext.ConnectionId.Returns("connection-a");
        callerContext.Features.Returns(features);
        callerContext.Items.Returns(new Dictionary<object, object?>());

        return new QuoteNotificationsHub
        {
            Context = callerContext,
            Groups = groups
        };
    }
}
