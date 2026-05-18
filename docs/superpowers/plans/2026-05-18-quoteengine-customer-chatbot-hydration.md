# QuoteEngine Customer Chatbot Hydration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the customer assistant button and drawer to `Maliev.QuoteEngine`, and hydrate the same customer chatbot conversation when a browser moves from `Maliev.Web` to QuoteEngine.

**Architecture:** QuoteEngine gets its own BFF chatbot boundary at `/quote/v1/chatbot/*`; the browser never calls ChatbotService directly. Web continues to create and update the customer assistant session, Web sets a signed cross-subdomain handoff cookie, and QuoteEngine uses that cookie to restore the ChatbotService session and visible message history. Account-specific responses are generated only after QuoteEngine confirms the customer is authenticated in the current browser session.

**Tech Stack:** .NET 10, Blazor WASM, MudBlazor, ASP.NET Core BFF controllers, ChatbotService HTTP client, Data Protection or shared HMAC cookie signing, xUnit, Playwright E2E in `Maliev.Aspire.Tests`.

---

## Context From Current Code

- Web chatbot UI lives in `B:\maliev\Maliev.Web\Maliev.Web.Client\Components\CustomerChatbot.razor`.
- Web shared browser helper lives in `B:\maliev\Maliev.Web\Maliev.Web.Bff\wwwroot\js\maliev-chatbot.js`.
- Web chatbot API boundary is `POST /web/v1/chatbot/messages` in `B:\maliev\Maliev.Web\Maliev.Web.Bff\Controllers\ChatbotController.cs`.
- Web writes `maliev.customerAssistant.session.v1` to localStorage and `maliev_customer_assistant_session` to a `.maliev.com` cookie.
- localStorage is origin-scoped, so it cannot hydrate `quote.maliev.com` from `www.maliev.com`; the cross-app bridge must be a cookie or server-side handoff.
- Intranet uses a topbar `MudIconButton` and a right-side `MudDrawer` in `B:\maliev\Maliev.Intranet\Maliev.Intranet.Client\Layout\MainLayout.razor`.
- QuoteEngine topbar lives in `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Client\Layout\MainLayout.razor`; it currently has no chatbot button or drawer.
- QuoteEngine BFF currently has no ChatbotService client, no chatbot controller, and no ChatbotService Aspire reference.
- ChatbotService supports anonymous `POST /chatbot/v1/sessions/initiate` and `POST /chatbot/v1/messages`, but existing history read `GET /chatbot/v1/sessions/{sessionId}/messages` is user-profile-owned and cannot hydrate anonymous Web sessions by session id alone.

## Product Rules

- The QuoteEngine assistant button should feel like the Intranet assistant affordance: a topbar chat icon with tooltip, opening a right-side drawer.
- The active page must remain in place during sign-in. The chatbot uses a popup or secondary auth window, then updates the drawer after auth polling confirms the session.
- Anonymous users can ask general manufacturing and quoting questions.
- Anonymous users asking account-specific questions must be told to sign in before quotes, orders, receipts, profile, address, or personal information is shown.
- Signed-in users can receive account-aware summaries and links, but mutating actions such as address updates must open the relevant account page for explicit confirmation.
- The same ChatbotService session id must continue across Web and QuoteEngine.
- Visible history hydration must not rely on browser localStorage crossing subdomains.
- The browser must never call ChatbotService directly.

---

## File Structure

### Maliev.QuoteEngine

- Create `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Shared\Chatbot\CustomerChatbotDtos.cs` for QuoteEngine-facing request, response, action, hydration, and handoff DTOs.
- Create `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Bff\Clients\ChatbotServiceClient.cs` for ChatbotService wire calls using snake_case JSON.
- Create `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Bff\Services\CustomerChatbotService.cs` for account gating, context enrichment, and hydration orchestration.
- Create `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Bff\Controllers\ChatbotController.cs` for `/quote/v1/chatbot/messages`, `/quote/v1/chatbot/hydrate`, and `/quote/v1/chatbot/session`.
- Create `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Bff\Security\CustomerAssistantHandoffCookie.cs` for parsing and validating the signed Web-to-QuoteEngine handoff cookie.
- Create `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Client\Components\CustomerAssistantDrawer.razor` for the drawer conversation UI.
- Create `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Client\Components\CustomerAssistantDrawer.razor.css` for drawer-specific UI.
- Create `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Client\Pages\AuthChatbotComplete.razor` for popup completion.
- Create `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Client\wwwroot\js\maliev-chatbot.js` with the shared helper functions QuoteEngine needs.
- Modify `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Bff\Program.cs` to register ChatbotService client, chatbot service, and static script.
- Modify `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Client\Program.cs` only if new client service registration is split from `QuoteEngineApiClient`.
- Modify `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Client\Services\QuoteEngineApiClient.cs` to add chatbot methods.
- Modify `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Client\Layout\MainLayout.razor` to add topbar chat button and right drawer.
- Modify `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Client\wwwroot\index.html` to load `js/maliev-chatbot.js`.
- Modify `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Client\wwwroot\css\app.css` only for topbar spacing and responsive drawer integration that cannot live in component CSS.
- Modify `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Tests\QuoteEngineSourceTests.cs` for source-level UI/script contracts.
- Modify `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Tests\QuoteEngineEndpointTests.cs` for BFF endpoint and wire-shape coverage.

### Maliev.Web

- Modify `B:\maliev\Maliev.Web\Maliev.Web.Bff\Controllers\ChatbotController.cs` so every successful chatbot response also sets a signed handoff cookie.
- Create `B:\maliev\Maliev.Web\Maliev.Web.Bff\Security\CustomerAssistantHandoffCookie.cs` with the same payload and signing rules as QuoteEngine.
- Modify `B:\maliev\Maliev.Web\Maliev.Web.Tests\CustomerChatbotBoundaryTests.cs` or `WebBffEndpointTests.cs` to verify the handoff cookie is set.

### Maliev.ChatbotService

- Create an internal BFF-only history endpoint, for example `GET /chatbot/v1/internal/sessions/{sessionId}/messages`.
- Add a controller test proving the internal endpoint returns messages for the requested session when the caller has `chatbot.sessions.read`, without requiring the session to belong to the caller's user profile.
- Keep the existing user-owned `GET /chatbot/v1/sessions/{sessionId}/messages` behavior unchanged.

### Maliev.Aspire

- Modify `B:\maliev\Maliev.Aspire\Maliev.Aspire.AppHost\AppHost.cs` to add `.WithReference(chatbotService)` to `QuoteEngineBff`.
- Modify `B:\maliev\Maliev.Aspire\Maliev.Aspire.Tests\AppHostReferenceTests.cs` to assert QuoteEngineBff references ChatbotService.
- Modify `B:\maliev\Maliev.Aspire\Maliev.Aspire.Tests\E2E\BrowserJourneyGateTests.cs` to unskip and complete `QuoteEngine_CustomerChatbotWindow_RetainsWebConversation`.
- Update `B:\maliev\Maliev.Aspire\Maliev.Aspire.Tests\specs\E2E_USER_JOURNEY_RUN_RESULTS.md` after verification.

---

## Task 1: Contract Tests First

**Files:**
- Modify: `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Tests\QuoteEngineSourceTests.cs`
- Modify: `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Tests\QuoteEngineEndpointTests.cs`
- Create: `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Shared\Chatbot\CustomerChatbotDtos.cs`

- [ ] **Step 1: Add source tests for the topbar button, drawer, helper script, and completion route**

Add a test named `Customer_assistant_drawer_is_integrated_into_quote_layout` that asserts:

```csharp
Assert.Contains("CustomerAssistantDrawer", layout, StringComparison.Ordinal);
Assert.Contains("topbar-chat-toggle", layout, StringComparison.Ordinal);
Assert.Contains("MudDrawer", layout, StringComparison.Ordinal);
Assert.Contains("chatbot-open", styles, StringComparison.Ordinal);
Assert.Contains("js/maliev-chatbot.js", index, StringComparison.Ordinal);
Assert.Contains("@page \"/auth/chatbot-complete\"", authComplete, StringComparison.Ordinal);
```

- [ ] **Step 2: Add endpoint tests for QuoteEngine chatbot routes**

Add tests that call:

```csharp
await client.PostAsJsonAsync("/quote/v1/chatbot/messages", new CustomerChatbotRequest
{
    Message = "Can you help with CNC aluminum fixtures?",
    Language = "en"
});

await client.GetAsync("/quote/v1/chatbot/session");
await client.PostAsJsonAsync("/quote/v1/chatbot/hydrate", new CustomerChatbotHydrateRequest
{
    SessionId = knownSessionId
});
```

Expected assertions:

```csharp
Assert.Equal(HttpStatusCode.OK, response.StatusCode);
Assert.Equal("assistant", body.Role);
Assert.NotEqual(Guid.Empty, body.SessionId);
```

- [ ] **Step 3: Add DTO wire-shape assertions**

The public QuoteEngine DTOs must use the same browser-facing JSON names as Web:

```csharp
Assert.Contains("\"sessionId\"", json, StringComparison.Ordinal);
Assert.Contains("\"customerContext\"", json, StringComparison.Ordinal);
Assert.Contains("\"suggestedActions\"", json, StringComparison.Ordinal);
```

The ChatbotService client request must use snake_case for downstream calls:

```csharp
Assert.Contains("\"session_id\"", capturedJson, StringComparison.Ordinal);
Assert.Contains("\"response_mime_type\"", capturedJson, StringComparison.Ordinal);
```

- [ ] **Step 4: Run the failing tests**

Run:

```powershell
dotnet test B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Tests\Maliev.QuoteEngine.Tests.csproj --filter "FullyQualifiedName~QuoteEngineSourceTests|FullyQualifiedName~QuoteEngineEndpointTests" -p:UseSharedCompilation=false -m:1 /nr:false --logger "console;verbosity=minimal"
```

Expected before implementation: source and endpoint tests fail because the component, script, DTOs, and routes do not exist.

- [ ] **Step 5: Commit test scaffold**

```powershell
git -C B:\maliev\Maliev.QuoteEngine add Maliev.QuoteEngine.Tests Maliev.QuoteEngine.Shared
git -C B:\maliev\Maliev.QuoteEngine commit -m "test: define quote chatbot hydration contract"
```

---

## Task 2: QuoteEngine BFF Chatbot Boundary

**Files:**
- Create: `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Bff\Clients\ChatbotServiceClient.cs`
- Create: `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Bff\Services\CustomerChatbotService.cs`
- Create: `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Bff\Controllers\ChatbotController.cs`
- Create: `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Bff\Security\CustomerAssistantHandoffCookie.cs`
- Modify: `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Bff\Program.cs`

- [ ] **Step 1: Implement `CustomerChatbotRequest` and response DTOs**

Use browser-facing camelCase properties that match `Maliev.Web.Shared.Chatbot`:

```csharp
public sealed class CustomerChatbotRequest
{
    public Guid? SessionId { get; set; }
    [Required]
    [StringLength(1000, MinimumLength = 1)]
    public string Message { get; set; } = string.Empty;
    [StringLength(1600)]
    public string? CustomerContext { get; set; }
    [RegularExpression("^(en|th)?$")]
    public string? Language { get; set; }
}
```

- [ ] **Step 2: Implement ChatbotService client**

Use these downstream endpoints:

```csharp
POST /chatbot/v1/sessions/initiate
POST /chatbot/v1/messages
GET  /chatbot/v1/internal/sessions/{sessionId}/messages
```

The initiate payload uses `channel = "website"` and adds `surface = "quote-engine"` only inside message context, because ChatbotService currently has no `QuoteEngine` channel enum.

- [ ] **Step 3: Implement account-specific gate**

In `CustomerChatbotService.SendAsync`, apply this decision order:

```csharp
if (IsAccountSpecificTopic(message) && !quoteAuth.IsAuthenticated)
{
    return SignInRequiredResponse(sessionId, language);
}

if (IsAccountSpecificTopic(message) && quoteAuth.IsAuthenticated)
{
    return await CreateQuoteAccountResponseAsync(message, quoteAuth, cancellationToken);
}

return await SendToChatbotServiceAsync(message, sessionId, BuildQuoteContext(), cancellationToken);
```

Account-specific terms must include orders, quotes, receipts, invoices, tax invoice, profile, personal information, address book, shipping address, billing address, update address, and the Thai equivalents already used by Web.

- [ ] **Step 4: Implement account response summaries**

For signed-in account questions, summarize from existing QuoteEngine BFF/account sources:

```csharp
var profile = store.GetProfile(customerId);
var quotes = store.GetQuotes(customerId).Take(3).ToList();
var orders = store.GetOrders(customerId).Take(3).ToList();
```

Responses must include links, not silent mutations:

```csharp
new CustomerChatbotActionDto { Label = "Open orders", Action = "link", Data = "/orders" }
new CustomerChatbotActionDto { Label = "Open profile", Action = "link", Data = "/profile" }
new CustomerChatbotActionDto { Label = "Open documents", Action = "link", Data = "/documents" }
```

- [ ] **Step 5: Implement hydration**

`POST /quote/v1/chatbot/hydrate` must:

1. Read the signed handoff cookie.
2. Reject the request if `request.SessionId` does not match the signed cookie session id.
3. Call ChatbotService internal history endpoint.
4. Return at most 24 messages, ordered oldest to newest.
5. Return a non-failing empty message list when ChatbotService history is unavailable, while still returning the session id so the next message continues the session.

- [ ] **Step 6: Register dependencies**

In `Program.cs`:

```csharp
builder.AddAuthenticatedServiceClient<IChatbotServiceClient, ChatbotServiceClient>("ChatbotService")
    .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(45));
builder.Services.AddScoped<ICustomerChatbotService, CustomerChatbotService>();
builder.Services.AddScoped<CustomerAssistantHandoffCookie>();
```

Use the authenticated client because history hydration uses a protected internal ChatbotService endpoint.

- [ ] **Step 7: Run QuoteEngine endpoint tests**

Run:

```powershell
dotnet test B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Tests\Maliev.QuoteEngine.Tests.csproj --filter "FullyQualifiedName~QuoteEngineEndpointTests" -p:UseSharedCompilation=false -m:1 /nr:false --logger "console;verbosity=minimal"
```

Expected after this task: endpoint tests pass; source tests still fail until the client UI is added.

- [ ] **Step 8: Commit BFF boundary**

```powershell
git -C B:\maliev\Maliev.QuoteEngine add Maliev.QuoteEngine.Bff Maliev.QuoteEngine.Shared Maliev.QuoteEngine.Tests
git -C B:\maliev\Maliev.QuoteEngine commit -m "feat: add quote chatbot bff boundary"
```

---

## Task 3: QuoteEngine Client Drawer And Button

**Files:**
- Create: `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Client\Components\CustomerAssistantDrawer.razor`
- Create: `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Client\Components\CustomerAssistantDrawer.razor.css`
- Create: `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Client\Pages\AuthChatbotComplete.razor`
- Create: `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Client\wwwroot\js\maliev-chatbot.js`
- Modify: `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Client\Layout\MainLayout.razor`
- Modify: `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Client\Services\QuoteEngineApiClient.cs`
- Modify: `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Client\wwwroot\index.html`
- Modify: `B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Client\wwwroot\css\app.css`

- [ ] **Step 1: Add client API methods**

Add methods:

```csharp
public Task<CustomerChatbotResponse> SendChatbotMessageAsync(CustomerChatbotRequest request, CancellationToken cancellationToken = default)
    => PostAsync<CustomerChatbotRequest, CustomerChatbotResponse>("quote/v1/chatbot/messages", request, cancellationToken);

public Task<CustomerChatbotHydrateResponse> HydrateChatbotAsync(CustomerChatbotHydrateRequest request, CancellationToken cancellationToken = default)
    => PostAsync<CustomerChatbotHydrateRequest, CustomerChatbotHydrateResponse>("quote/v1/chatbot/hydrate", request, cancellationToken);

public async Task<CustomerChatbotSessionResponse> GetChatbotSessionAsync(CancellationToken cancellationToken = default)
    => await httpClient.GetFromJsonAsync<CustomerChatbotSessionResponse>("quote/v1/chatbot/session", cancellationToken)
        ?? new CustomerChatbotSessionResponse();
```

- [ ] **Step 2: Add the topbar button and drawer**

In `MainLayout.razor`, follow the Intranet pattern:

```razor
<MudTooltip Text="Mali assistant" Placement="Placement.Bottom">
    <MudIconButton Icon="@Icons.Material.Outlined.Chat"
                   Class="topbar-chat-toggle"
                   Size="Size.Medium"
                   OnClick="@ToggleChat"
                   Color="@(_chatDrawerOpen ? Color.Primary : Color.Default)" />
</MudTooltip>

<MudDrawer Open="@_chatDrawerOpen"
           OpenChanged="SetChatDrawerOpenAsync"
           Anchor="Anchor.End"
           ClipMode="DrawerClipMode.Never"
           Elevation="0"
           Variant="DrawerVariant.Temporary"
           Width="min(640px, calc(100vw - 16px))"
           Class="quote-chat-drawer">
    <CustomerAssistantDrawer OnClose="CloseChatDrawerAsync" />
</MudDrawer>
```

- [ ] **Step 3: Build drawer behavior**

The drawer component must:

1. Read the shared session id with `malievChatbot.readSharedSessionId`.
2. Call `/quote/v1/chatbot/hydrate` when a shared session exists.
3. Render hydrated messages before the greeting.
4. Persist messages to QuoteEngine localStorage under `maliev.chatbot.personalization.v1:{user-or-guest}`.
5. Write the shared session cookie after each successful send.
6. Use popup sign-in for account-specific anonymous questions.
7. Poll `/quote/v1/auth/session` after popup sign-in.

Required CSS/test selectors:

```text
.quote-chat-drawer
.customer-chatbot-panel
.customer-chatbot-messages
.customer-chatbot-composer
.customer-chatbot-auth-email
.customer-chatbot-hydrated
```

- [ ] **Step 4: Add popup completion route**

`AuthChatbotComplete.razor`:

```razor
@page "/auth/chatbot-complete"
@inject IJSRuntime JS

<PageTitle>Authentication complete - MALIEV Quote Engine</PageTitle>

<section class="auth-panel">
    <h1>Authentication complete</h1>
    <p>You can return to Mali. This window will close automatically when the browser allows it.</p>
</section>

@code {
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await JS.InvokeVoidAsync("malievChatbot.notifyAuthenticationComplete");
        }
    }
}
```

- [ ] **Step 5: Add helper script**

Copy the Web helper functions needed by QuoteEngine:

```javascript
window.malievChatbot = {
  getJson: async function (path) { /* same as Web helper */ },
  openSignInPopup: function (url) { /* same as Web helper */ },
  notifyAuthenticationComplete: function () { /* same as Web helper */ },
  readSharedSessionId: function (storageKey) { /* localStorage then cookie */ },
  writeSharedSession: function (storageKey, sessionId, userKey, language, isAuthenticated) { /* .maliev.com cookie */ },
  fitComposer: function (textarea) { /* same as Web helper */ },
  isNearBottom: function (container) { /* same as Web helper */ },
  scrollToBottom: function (container, smooth) { /* same as Web helper */ }
};
```

Load it in `index.html` before Blazor starts:

```html
<script src="js/maliev-chatbot.js"></script>
```

- [ ] **Step 6: Run source tests and client build**

Run:

```powershell
dotnet test B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Tests\Maliev.QuoteEngine.Tests.csproj --filter "FullyQualifiedName~QuoteEngineSourceTests" -p:UseSharedCompilation=false -m:1 /nr:false --logger "console;verbosity=minimal"
dotnet build B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.Client\Maliev.QuoteEngine.Client.csproj -p:UseSharedCompilation=false -m:1 /nr:false --verbosity minimal
```

Expected: source tests and client build pass.

- [ ] **Step 7: Commit UI**

```powershell
git -C B:\maliev\Maliev.QuoteEngine add Maliev.QuoteEngine.Client Maliev.QuoteEngine.Tests
git -C B:\maliev\Maliev.QuoteEngine commit -m "feat: add quote assistant drawer"
```

---

## Task 4: Web Handoff Cookie

**Files:**
- Create: `B:\maliev\Maliev.Web\Maliev.Web.Bff\Security\CustomerAssistantHandoffCookie.cs`
- Modify: `B:\maliev\Maliev.Web\Maliev.Web.Bff\Controllers\ChatbotController.cs`
- Modify: `B:\maliev\Maliev.Web\Maliev.Web.Tests\WebBffEndpointTests.cs`

- [ ] **Step 1: Add signed handoff cookie payload**

Payload:

```csharp
public sealed record CustomerAssistantHandoffPayload(
    Guid SessionId,
    string? UserKey,
    string Language,
    bool IsAuthenticated,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);
```

Cookie names:

```text
maliev_customer_assistant_handoff
maliev_customer_assistant_session
```

The first is signed and HttpOnly for BFF hydration. The second remains JS-readable for the existing browser UI and current E2E checks.

- [ ] **Step 2: Set cookie on successful Web chatbot response**

After `chatbotService.SendAsync` succeeds:

```csharp
handoffCookie.Append(Response, result.SessionId.Value, userKey, result.Language, User.Identity?.IsAuthenticated == true);
return Ok(result);
```

Cookie settings:

```csharp
HttpOnly = true;
Secure = Request.IsHttps;
SameSite = SameSiteMode.Lax;
IsEssential = true;
Domain = Request.Host.Host.EndsWith(".maliev.com", StringComparison.OrdinalIgnoreCase) ? ".maliev.com" : null;
MaxAge = TimeSpan.FromDays(30);
```

- [ ] **Step 3: Verify Web still passes existing tests**

Run:

```powershell
dotnet test B:\maliev\Maliev.Web\Maliev.Web.Tests\Maliev.Web.Tests.csproj --filter "FullyQualifiedName~WebBffEndpointTests|FullyQualifiedName~CustomerChatbotBoundaryTests" -p:UseSharedCompilation=false -m:1 /nr:false --logger "console;verbosity=minimal"
```

- [ ] **Step 4: Commit Web handoff**

```powershell
git -C B:\maliev\Maliev.Web add Maliev.Web.Bff Maliev.Web.Tests
git -C B:\maliev\Maliev.Web commit -m "feat: issue customer assistant handoff cookie"
```

---

## Task 5: ChatbotService Internal Handoff History

**Files:**
- Create or modify: `B:\maliev\Maliev.ChatbotService\Maliev.ChatbotService.Api\Controllers\V1\InternalSessionsController.cs`
- Modify: `B:\maliev\Maliev.ChatbotService\Maliev.ChatbotService.Tests\Integration\MessagesApiTests.cs`

- [ ] **Step 1: Add internal message history endpoint**

Route:

```csharp
[ApiController]
[ApiVersion("1")]
[Route("chatbot/v{version:apiVersion}/internal/sessions")]
public sealed class InternalSessionsController : ControllerBase
{
    [HttpGet("{sessionId:guid}/messages")]
    [RequirePermission(ChatbotPermissions.SessionRead)]
    public async Task<ActionResult<ConversationMessagesResponse>> GetSessionMessages(Guid sessionId, CancellationToken cancellationToken)
}
```

Behavior:

```csharp
var messages = await messageRepository.GetMessagesBySessionIdAsync(sessionId, cancellationToken);
if (messages.Count == 0)
{
    return NotFound();
}

return Ok(new ConversationMessagesResponse
{
    SessionId = sessionId,
    Messages = messages.Select(MapMessage).TakeLast(24).ToList()
});
```

- [ ] **Step 2: Keep public user-owned history unchanged**

Existing tests for `GET /chatbot/v1/sessions/{sessionId}/messages` must still prove that users can only read their own sessions.

- [ ] **Step 3: Run ChatbotService focused tests**

Run:

```powershell
dotnet test B:\maliev\Maliev.ChatbotService\Maliev.ChatbotService.Tests\Maliev.ChatbotService.Tests.csproj --filter "FullyQualifiedName~MessagesApiTests" -p:UseSharedCompilation=false -m:1 /nr:false --logger "console;verbosity=minimal"
```

- [ ] **Step 4: Commit ChatbotService history endpoint**

```powershell
git -C B:\maliev\Maliev.ChatbotService add Maliev.ChatbotService.Api Maliev.ChatbotService.Tests
git -C B:\maliev\Maliev.ChatbotService commit -m "feat: expose internal chatbot session handoff history"
```

---

## Task 6: Aspire Wiring And E2E

**Files:**
- Modify: `B:\maliev\Maliev.Aspire\Maliev.Aspire.AppHost\AppHost.cs`
- Modify: `B:\maliev\Maliev.Aspire\Maliev.Aspire.Tests\AppHostReferenceTests.cs`
- Modify: `B:\maliev\Maliev.Aspire\Maliev.Aspire.Tests\E2E\BrowserJourneyGateTests.cs`
- Modify: `B:\maliev\Maliev.Aspire\Maliev.Aspire.Tests\specs\E2E_USER_JOURNEY_RUN_RESULTS.md`

- [ ] **Step 1: Add AppHost reference**

In the QuoteEngineBff resource:

```csharp
.WithReference(chatbotService)
```

- [ ] **Step 2: Add AppHost reference test**

Assert QuoteEngineBff includes `chatbotService` in the service references, mirroring the WebBff assertion.

- [ ] **Step 3: Complete the skipped E2E**

Replace `QuoteEngine_CustomerChatbotWindow_RetainsWebConversation` with a real test:

1. Register/sign in a Web customer.
2. Open Web home.
3. Open Web customer chatbot.
4. Send a general manufacturing message.
5. Capture `sessionId` from `/web/v1/chatbot/messages`.
6. Assert Web wrote `maliev_customer_assistant_session`.
7. Navigate same browser context to `QuoteEngineBff /projects/new`.
8. Click `.topbar-chat-toggle`.
9. Assert `.customer-chatbot-messages` contains the Web message or an explicit hydrated continuation state tied to the same `sessionId`.
10. Ask "Can you check my order status and receipt?"
11. If QuoteEngine session is anonymous, assert the drawer shows the in-chat sign-in action.
12. Complete QuoteEngine popup sign-in.
13. Assert active QuoteEngine URL remains `/projects/new`.
14. Ask again.
15. Assert the assistant returns signed-in quote/order/profile actions.

- [ ] **Step 4: Run focused Aspire checks**

Run:

```powershell
dotnet test B:\maliev\Maliev.Aspire\Maliev.Aspire.Tests\Maliev.Aspire.Tests.csproj --filter "FullyQualifiedName~AppHostReferenceTests" -p:UseSharedCompilation=false -m:1 /nr:false --logger "console;verbosity=minimal"
dotnet test B:\maliev\Maliev.Aspire\Maliev.Aspire.Tests\Maliev.Aspire.Tests.csproj --filter "FullyQualifiedName~QuoteEngine_CustomerChatbotWindow_RetainsWebConversation" -p:UseSharedCompilation=false -m:1 /nr:false --logger "console;verbosity=minimal"
```

- [ ] **Step 5: Commit Aspire updates**

```powershell
git -C B:\maliev\Maliev.Aspire add Maliev.Aspire.AppHost Maliev.Aspire.Tests
git -C B:\maliev\Maliev.Aspire commit -m "test: verify quote chatbot web handoff"
```

---

## Task 7: Full Verification

- [ ] **Step 1: QuoteEngine build and tests**

```powershell
dotnet build B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.slnx -p:UseSharedCompilation=false -m:1 /nr:false --verbosity minimal
dotnet test B:\maliev\Maliev.QuoteEngine\Maliev.QuoteEngine.slnx -p:UseSharedCompilation=false -m:1 /nr:false --logger "console;verbosity=minimal"
git -C B:\maliev\Maliev.QuoteEngine diff --check
```

- [ ] **Step 2: Web focused tests**

```powershell
dotnet test B:\maliev\Maliev.Web\Maliev.Web.Tests\Maliev.Web.Tests.csproj --filter "FullyQualifiedName~WebBffEndpointTests|FullyQualifiedName~CustomerChatbotBoundaryTests|FullyQualifiedName~HeroLayoutSourceTests" -p:UseSharedCompilation=false -m:1 /nr:false --logger "console;verbosity=minimal"
git -C B:\maliev\Maliev.Web diff --check
```

- [ ] **Step 3: ChatbotService focused tests**

```powershell
dotnet test B:\maliev\Maliev.ChatbotService\Maliev.ChatbotService.Tests\Maliev.ChatbotService.Tests.csproj --filter "FullyQualifiedName~MessagesApiTests|FullyQualifiedName~SessionsApiTests" -p:UseSharedCompilation=false -m:1 /nr:false --logger "console;verbosity=minimal"
git -C B:\maliev\Maliev.ChatbotService diff --check
```

- [ ] **Step 4: Aspire focused E2E**

```powershell
dotnet test B:\maliev\Maliev.Aspire\Maliev.Aspire.Tests\Maliev.Aspire.Tests.csproj --filter "FullyQualifiedName~QuoteEngine_CustomerChatbotWindow_RetainsWebConversation|FullyQualifiedName~Web_CustomerChatbot_LoginPromptContinuesAuthenticatedConversationInPlace" -p:UseSharedCompilation=false -m:1 /nr:false --logger "console;verbosity=minimal"
git -C B:\maliev\Maliev.Aspire diff --check
```

- [ ] **Step 5: Browser visual check**

Open QuoteEngine `/projects/new` through Aspire, click the topbar chat icon, and verify:

- The drawer opens from the right without moving the current workspace.
- Text does not overlap in desktop or mobile widths.
- Hydrated messages render inside the drawer.
- The chat icon color changes while open.
- Popup sign-in does not navigate the active QuoteEngine page.

---

## Acceptance Criteria

- QuoteEngine has a topbar chat button similar to Intranet's assistant button.
- Clicking the button opens a right-side assistant drawer.
- The drawer uses the existing Mali customer-assistant persona, not the employee Intranet sidekick persona.
- General anonymous questions route through QuoteEngine BFF to ChatbotService.
- Account-specific anonymous questions show a sign-in requirement and in-chat sign-in button.
- Sign-in opens a popup and the active QuoteEngine page stays on the same route.
- After sign-in, the drawer updates to authenticated state without full-page navigation.
- Web-to-QuoteEngine switching preserves the same ChatbotService session id.
- QuoteEngine hydrates prior visible messages when a valid signed Web handoff cookie exists.
- If history hydration fails, QuoteEngine still continues the same session id and shows an explicit continuation message.
- Address/profile/order/receipt actions open QuoteEngine account pages for user confirmation; no silent mutation occurs from chat.
- Aspire E2E `QuoteEngine_CustomerChatbotWindow_RetainsWebConversation` is unskipped and passes.

## PO Review And Partnership

**Phase Alignment:** Phase 2 QuoteEngine priority; this directly supports the customer quoting platform and conversion into signed customer workflows.

**Business Logic Check:**
- 3-Second Rule: Not a shop-floor flow; drawer open and local hydration should still be immediate.
- Determinism Rule: No pricing logic changes.
- 2x Markup Rule: Not applicable.

**Completeness Check:** The plan covers UI, BFF contract, auth handoff, cross-app hydration, security boundary, and E2E evidence.

**Technical Plan Alignment:** The plan keeps browser traffic behind BFFs, avoids direct ChatbotService calls from the browser, and treats history hydration as a signed handoff rather than trusting unsigned client state.

**Verdict:** Collaborative approval with one important constraint: do not expose account-specific data until QuoteEngine has verified the customer session in the current browser.

**Proposed Value Add:** Track when a hydrated Web conversation enters QuoteEngine as a distinct analytics event so product can measure assistant-assisted quote conversion.

**This plan must be reviewed by `maliev-product-owner` for business alignment before implementation begins.**
