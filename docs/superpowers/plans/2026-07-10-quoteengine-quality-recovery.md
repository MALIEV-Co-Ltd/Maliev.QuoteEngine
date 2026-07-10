# QuoteEngine quality recovery implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Resolve the 20 browser-reported QuoteEngine defects with deterministic UI state, truthful agent/tool behavior, correct pricing and shipping, official Google Identity Services entry points, and owner-scoped project search.

**Architecture:** QuoteEngine remains the customer orchestration and presentation boundary. Existing platform services continue to own geometry extraction, registry data, delivery rates, pricing, projects, search, and identity; only the proven contract gaps are changed. Every behavior slice starts with a failing regression and ends with focused validation plus a repo-local commit.

**Tech Stack:** .NET 10, ASP.NET Core BFFs, Blazor WebAssembly, xUnit, Three.js, Gemini function calling, PostgreSQL/EF Core, Google Identity Services/FedCM.

## Global constraints

- Preserve unrelated dirty files and stage only the validated slice.
- Never expose a customer ID supplied by the browser as trusted ownership input.
- Tool names remain allowlisted and context-token bound.
- Browser-local geometry is advisory; server geometry remains authoritative.
- Google buttons are rendered by GIS; MALIEV does not imitate Google UI.
- Do not push any repository without explicit user approval.

---

### Task 1: Recover compact textual tool calls and continue the agent loop

**Files:**
- Modify: `B:\maliev\Maliev.ChatbotService\Maliev.ChatbotService.Application\Handlers\LeakedToolCallParser.cs`
- Modify: `B:\maliev\Maliev.ChatbotService\Maliev.ChatbotService.Tests\Unit\LeakedToolCallParserTests.cs`
- Modify: `B:\maliev\Maliev.ChatbotService\Maliev.ChatbotService.Tests\Unit\AgentChatHandlerTests.cs`

**Interfaces:**
- Consumes: declared canonical tool names from `GeminiToolDeclaration`.
- Produces: `GeminiFunctionCall.Name` using the exact declared name and parsed keyword arguments.

- [ ] **Step 1: Add exact failing leak regressions**

```csharp
[Theory]
[InlineData("tools.quotecalculateestimate()", "quote_calculate_estimate")]
[InlineData("tools.quotegetshippingrates(addressline1='36/1', postalcode='12345')", "quote_get_shipping_rates")]
[InlineData("tools.quoteprepareformal_quote()", "quote_prepare_formal_quote")]
public void Parse_CompactToolsPrefix_ResolvesDeclaredCanonicalName(string text, string expected)
{
    var calls = LeakedToolCallParser.Parse(text,
        ["quote_calculate_estimate", "quote_get_shipping_rates", "quote_prepare_formal_quote"]);
    Assert.Single(calls);
    Assert.Equal(expected, calls[0].Name);
}
```

- [ ] **Step 2: Run the focused tests and confirm the expected empty-call failure**

Run:

```powershell
dotnet test Maliev.ChatbotService.Tests\Maliev.ChatbotService.Tests.csproj --configuration Release --filter "FullyQualifiedName~LeakedToolCallParserTests|FullyQualifiedName~AgentChatHandlerTests.ExecuteAsync_Leaked"
```

Expected before implementation: the three compact-name cases fail because no calls are recovered.

- [ ] **Step 3: Canonicalize only against declared tools**

Add a canonical key that removes a leading `tools`, punctuation, and underscores, then lowercases invariantly. Build a dictionary from canonical key to exact declaration name; reject missing or ambiguous keys. Keep the existing five-call cap and keyword-only argument parser.

```csharp
private static string CanonicalToolKey(string value) =>
    new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
```

- [ ] **Step 4: Prove result re-entry**

Add an `AgentChatHandlerTests` case whose first response is `tools.quotecalculateestimate()`, whose executor returns a price JSON object, and whose second response is a customer answer. Assert two model calls, one tool execution, one function-response message, and no `tools.` text in the result.

- [ ] **Step 5: Run focused and full unit validation**

```powershell
dotnet test Maliev.ChatbotService.Tests\Maliev.ChatbotService.Tests.csproj --configuration Release --filter "FullyQualifiedName~LeakedToolCallParserTests|FullyQualifiedName~AgentChatHandlerTests"
dotnet build Maliev.ChatbotService.slnx --configuration Release
```

- [ ] **Step 6: Commit**

```powershell
git add Maliev.ChatbotService.Application\Handlers\LeakedToolCallParser.cs Maliev.ChatbotService.Tests\Unit\LeakedToolCallParserTests.cs Maliev.ChatbotService.Tests\Unit\AgentChatHandlerTests.cs
git commit -m "fix: recover compact agent tool calls"
```

### Task 2: Correct PricingService quantity semantics and stable configuration lookup

**Files:**
- Modify: `B:\maliev\Maliev.PricingService\Maliev.PricingService.Domain\Entities\PricingConfiguration.cs`
- Modify: `B:\maliev\Maliev.PricingService\Maliev.PricingService.Application\Services\PricingOrchestrator.cs`
- Modify: `B:\maliev\Maliev.PricingService\Maliev.PricingService.Infrastructure\Data\SeedData\PricingCatalogSeedData.cs`
- Create: a normal EF Core migration under `Maliev.PricingService.Infrastructure\Migrations`
- Create: `B:\maliev\Maliev.PricingService\Maliev.PricingService.Tests\Unit\PricingOrchestratorQuantityTests.cs`

**Interfaces:**
- Consumes: `PricingRequest.MaterialId`, `MaterialCode`, `ManufacturingProcessId`, `ManufacturingProcessName`, and `Quantity`.
- Produces: a line-total minimum floor with `UnitPrice = TotalAmount / Quantity`.

- [ ] **Step 1: Add failing quantity and configuration regressions**

Create tests proving: a 500 THB minimum produces 500 total/500 unit at quantity 1 and 500 total/100 unit at quantity 5 when raw cost is below the floor; a higher raw total is not floored; an exact ID lookup wins; an unambiguous code lookup works when IDs drift; ambiguous code rows return no price.

- [ ] **Step 2: Run the tests and verify the per-unit-floor failure**

```powershell
dotnet test Maliev.PricingService.Tests\Maliev.PricingService.Tests.csproj --configuration Release --filter "FullyQualifiedName~PricingOrchestratorQuantityTests"
```

- [ ] **Step 3: Add stable codes and query logic**

Add normalized `MaterialCode` and `ManufacturingProcessCode` columns to `PricingConfiguration`, populate every seed row, create indexes, and query exact IDs first. Query active code matches only when exact IDs have no row; accept exactly one match and log the fallback.

- [ ] **Step 4: Apply the order floor correctly**

```csharp
var rawTotalThb = surchargedUnitPrice * request.Quantity;
var flooredTotalThb = Math.Max(rawTotalThb, breakdown.MinimumOrderPriceFloor);
var flooredUnitPriceThb = flooredTotalThb / request.Quantity;
```

Derive FX totals and discount amounts from these values without changing the response JSON property names.

- [ ] **Step 5: Validate migration, tests, build, and format**

```powershell
dotnet test Maliev.PricingService.Tests\Maliev.PricingService.Tests.csproj --configuration Release --filter "FullyQualifiedName~PricingOrchestratorQuantityTests|FullyQualifiedName~VolumeDiscountResolverTests|FullyQualifiedName~PricingOrchestratorPublishTests"
dotnet build Maliev.PricingService.slnx --configuration Release
dotnet format Maliev.PricingService.slnx --verify-no-changes
```

- [ ] **Step 6: Commit the pricing slice**

Stage only the entity, orchestrator, seed, migration, snapshot, and tests; commit `fix: correct quantity pricing floors`.

### Task 3: Ground QuoteEngine price, artifact, address, and shipping state

**Files:**
- Modify: `Maliev.QuoteEngine.Bff/Services/QuoteAgentService.cs`
- Modify: `Maliev.QuoteEngine.Bff/Services/QuoteEnginePrototypeStore.cs`
- Modify: `Maliev.QuoteEngine.Shared/Agent/QuoteAgentDtos.cs`
- Modify: `Maliev.QuoteEngine.Tests/QuoteAgentEndpointTests.cs`
- Modify: `Maliev.QuoteEngine.Tests/QuoteAgentUiHelperTests.cs`

**Interfaces:**
- Consumes: Chatbot final content, `QuoteAgentStateResponse.Estimate`, artifacts/actions, RegistryService suggestions, DeliveryService rates/packages.
- Produces: customer-safe final text and compact structured shipping actions.

- [ ] **Step 1: Add failing regressions for the observed transcript**

Cover bare `tools.quotecalculateestimate()`, a 120 THB model claim when state says 2,500 THB/500 unit, a claimed formal quote without `formal_quote`, registry mismatch before rates, fruit/zero-rate filtering, and preservation of logo/package metadata.

- [ ] **Step 2: Verify all new tests fail for the intended reasons**

```powershell
dotnet test Maliev.QuoteEngine.slnx --configuration Release --filter "FullyQualifiedName~QuoteAgentEndpointTests.Agent_grounding|FullyQualifiedName~QuoteAgentEndpointTests.Agent_shipping"
```

- [ ] **Step 3: Add defensive output grounding**

Extend `StripToolTraces` for single-line `tools.<call>(...)`. Add `GroundEstimateText` and `GroundFormalQuoteArtifactText`; when a mismatch exists, replace the unsupported claim with deterministic state-derived text. Do not mutate internal status/estimate DTOs.

- [ ] **Step 4: Validate address at the BFF boundary**

Before `GetShippingRatesAsync`, query RegistryService and require a normalized exact match for subdistrict/district/province/postcode. Return `success=false`, `addressValidation=needs_customer_review`, and suggestions when mismatched.

- [ ] **Step 5: Filter and enrich shipping choices**

Remove non-positive and food/fresh/frozen/fruit rates; include oversize products only for an oversized planned package; deduplicate equivalent products; limit the compact initial set. Preserve `CourierLogoUrl`, `PackageCount`, `TotalWeight`, and `Packages` in tool rows and action arguments. Do not include `Provider` in customer-facing copy.

- [ ] **Step 6: Amortize prototype setup**

Calculate prototype line total as one setup charge plus variable configured cost times quantity; derive unit price from the line total. Add `pricingSource=prototype` metadata so the model/UI can call it an estimate rather than an authoritative formal quote.

- [ ] **Step 7: Validate and commit**

Run the focused endpoint tests, `PricingServiceClientContractTests`, Release build, and format verification. Commit `fix: ground quote agent commercial state`.

### Task 4: Implement immediate mesh thumbnails and deterministic upload state

**Files:**
- Create: `Maliev.QuoteEngine.Client/wwwroot/js/quote-cad-thumbnails.js`
- Modify: `Maliev.QuoteEngine.Client/Pages/QuoteWorkspace.razor`
- Modify: `Maliev.QuoteEngine.Client/Components/QuoteAgent/QuoteAgentLaunchShell.razor`
- Modify: `Maliev.QuoteEngine.Client/wwwroot/css/app.css`
- Modify: `Maliev.QuoteEngine.Tests/QuoteEngineSourceTests.cs`
- Add a Node behavior test under `Maliev.QuoteEngine.Client/js-tests`.

**Interfaces:**
- Consumes: `quoteEngineUploads.getObjectUrl(clientFileId)` and GeometryService runtime manifest/`extract_mesh`.
- Produces: transparent PNG data URL and stable attachment status updates.

- [ ] **Step 1: Add failing source/behavior tests**

Assert runtime routes, `extract_mesh`, transparent PNG output, mesh extension allowlist, and a shell update on upload failure. Add helper tests for `Uploading`, `Processing`, `Ready`, `Upload failed`, and removed states.

- [ ] **Step 2: Port the proven Intranet renderer**

Use self-hosted Three.js, the QuoteEngine runtime routes, 256 px isometric output, transparent background, timeout/worker termination, and mesh formats only. Return null on unsupported STEP/IGES so server GLB can complete later.

- [ ] **Step 3: Update status snapshots by identity**

Replace `NotifyUploadCompletedAsync` with a general update method that upserts by client file ID/upload ID/storage path/file name. Invoke it at upload start, durable processing, ready, and failure. `HasPendingComposerUpload` must not latch after failure.

- [ ] **Step 4: Add pending/ready tile behavior**

Square tiles show the generated thumbnail as soon as available; pending tiles use blur/pulse/spinner; remove appears on hover/focus; filename remains below. Uploading disables send and changes its accessible tooltip to `Uploading`.

- [ ] **Step 5: Validate and commit**

Run Node tests, focused source/helper tests, Release build, then browser-test STL pending -> thumbnail -> ready and failure -> removable/re-enabled. Commit `feat: add immediate quote file thumbnails`.

### Task 5: Unify composer, message, and workbench file interactions

**Files:**
- Create: `Maliev.QuoteEngine.Client/Components/QuoteAgent/QuoteAgentFileTile.razor`
- Modify: `Maliev.QuoteEngine.Client/Components/QuoteAgent/QuoteAgentLaunchShell.razor`
- Modify: `Maliev.QuoteEngine.Client/wwwroot/css/app.css`
- Modify: `Maliev.QuoteEngine.Tests/QuoteEngineSourceTests.cs`

**Interfaces:**
- Produces: `OnOpen`, optional `OnRemove`, source/status/preview/filename parameters, and keyboard-operable tile markup.

- [ ] **Step 1: Add failing component/source contracts**

Pin a button-based sent attachment, shared tile usage in composer/messages/workbench, and a selection callback that opens the artifact drawer.

- [ ] **Step 2: Implement the file tile and projection**

Use the same square media/name structure for local uploads, Drive files, sketches, and generated artifacts. Map sent attachment identity to `UploadedParts` first, then visible artifacts, set selection, and open workbench.

- [ ] **Step 3: Add Workbench toolbar actions**

Provide `Add from device` and signed-in `Google Drive` actions in the drawer. Drive imports enter the same pending attachment/file projection and remain available to the agent after send.

- [ ] **Step 4: Humanize metadata**

Centralize status mapping and adaptive volume (`mm³`, `cm³`, `m³`), then use it in tiles and preview headers without changing internal `DfmAnalysisReady` values.

- [ ] **Step 5: Validate and commit**

Run source/helper/Drive tests, Release build, and browser selection journey. Commit `feat: unify quote workbench files`.

### Task 6: Repair rail, projects, summary, reasoning, and visual hierarchy

**Files:**
- Modify: `Maliev.QuoteEngine.Client/Components/QuoteAgent/QuoteAgentLaunchShell.razor`
- Modify: `Maliev.QuoteEngine.Client/wwwroot/css/app.css`
- Modify: `Maliev.QuoteEngine.Client/wwwroot/js/quote-agent-composer.js`
- Modify: `Maliev.QuoteEngine.Tests/QuoteEngineSourceTests.cs`
- Modify/add composer Node tests.

**Interfaces:**
- Composer submit slot: voice waveform or send based on derived state.
- Project actions: per-project/action busy key.
- Reasoning disclosure: explicit expanded state with animated content and reduced-motion fallback.

- [ ] **Step 1: Add behavior regressions**

Cover empty voice state, attachment-only send, upload tooltip, chats collapsed default, distinct icons, per-project busy state, message attachment open, and reasoning expanded-state persistence.

- [ ] **Step 2: Implement compact navigation/project rows**

Name/actions occupy line one; badge/date line two. Hover/focus actions include pin, archive, and duplicate; busy state disables the row action and shows a spinner.

- [ ] **Step 3: Implement the voice submit slot**

When text and ready attachments are absent and speech is supported, render the waveform button in the submit slot and route it through the existing click/toggle/hold dictation binding. Keep the secondary mic button and preserve attachment-only Send.

- [ ] **Step 4: Implement visual hierarchy and stable layout**

Use regular/medium weight plus ink/muted colors, enlarge the avatar, compact/inset the upload picker scrollbar, make the summary strip a full-width grid, remove excess auth/panel borders, and preserve focus/contrast. Animate reasoning with opacity/transform/grid size and a `prefers-reduced-motion` override.

- [ ] **Step 5: Browser verification**

Verify desktop, narrow viewport, 200% zoom, keyboard focus, hover actions, reduced motion, picker scroll track, summary width, auth dialog, and reasoning open/close.

- [ ] **Step 6: Commit**

Commit `feat: refine Make Studio workspace UX` after focused tests and Release build.

### Task 7: Add expandable customer projects and owner-scoped SearchService assistance

**Files:**
- Modify SearchService document/query contracts and tests in `B:\maliev\Maliev.SearchService`.
- Modify ProjectService search document mapper/reindex tests in `B:\maliev\Maliev.ProjectService`.
- Modify QuoteEngine project/search DTOs, BFF clients/controllers, client API, shell, and endpoint/source tests.

**Interfaces:**
- Search document: `OwnerType="customer"`, `OwnerId=<customerId>`.
- QuoteEngine query: authenticated customer ID supplied by the BFF, never the browser.
- Project candidates: rehydrated through ProjectService ownership checks.

- [ ] **Step 1: Add failing owner-isolation tests**

Prove SearchService does not return another owner's project, ProjectService emits owner fields, and QuoteEngine rejects/omits ownerless or non-owned candidates.

- [ ] **Step 2: Add owner fields and filters**

Extend index models and query filters; update reindex/event mapping. Preserve employee/global search behavior by requiring owner filters only for customer-scoped queries.

- [ ] **Step 3: Add debounced Projects search**

Use a 250 ms cancellation-aware debounce and generation guard. SearchService supplies ranked IDs; ProjectService supplies trusted project details. Expansion caches `CustomerProjectDetailResponse` and shows loading/error/retry states.

- [ ] **Step 4: Enrich detail mapping**

Map ProjectService creation/update dates, parts/files, quotation identifiers/numbers/version, and order identifiers. Render explicit unavailable states for quote/invoice/receipt rather than inventing artifacts.

- [ ] **Step 5: Validate and commit per repo**

Run focused SearchService, ProjectService, and QuoteEngine ownership/endpoint tests plus each Release build. Commit separately in SearchService, ProjectService, and QuoteEngine.

### Task 8: Centralize verified Google credential exchange in AuthService

**Files:**
- Modify AuthService Google exchange request/handler/controller and tests.
- Add the official Google token validation package to AuthService.

**Interfaces:**
- Consumes: raw GIS ID token plus expected audience and app kind over an authenticated service call.
- Produces: existing customer/employee MALIEV auth response after independent verification.

- [ ] **Step 1: Add failing token validation tests**

Cover valid signature/audience/issuer/expiry, invalid audience, expired token, unverified email, missing/wrong employee `hd`, and unauthenticated service caller. Use a validator abstraction in tests; never create test-only production methods.

- [ ] **Step 2: Implement verified exchange**

Validate through Google's library, use `sub` as provider identity, require `email_verified`, require configured `hd` for Intranet, and issue the existing AuthService response. Deprecate caller-asserted email/user-ID exchange paths and require service authorization.

- [ ] **Step 3: Validate and commit**

Run focused auth tests, full AuthService build/test/format, and commit `fix: verify Google identity credentials`.

### Task 9: Render official GIS/FedCM buttons in QuoteEngine, Intranet, and Web

**Files:**
- QuoteEngine: client auth dialog/JS, BFF credential endpoint/CSP/config/tests.
- Intranet: login surface/JS, BFF credential endpoint/CSP/config/tests.
- Web: login surface/JS, BFF credential endpoint/CSP/config/tests.

**Interfaces:**
- GIS script: `https://accounts.google.com/gsi/client`.
- BFF POST: `credential`, `g_csrf_token`, normalized local return URL.
- AuthService: authenticated verified-credential exchange.

- [ ] **Step 1: Add failing app tests**

For each app, assert there is no custom Google-logo button, GIS renders a standard large button with width at least 200 px and FedCM enabled, the POST endpoint rejects mismatched/missing CSRF, and return URLs remain local.

- [ ] **Step 2: Add shared flow per app**

Load GIS, render into a reserved container, and use a same-origin login URI. Add required CSP `script-src`, `frame-src`, `connect-src`, and `style-src` entries for the GIS parent URLs. Keep email/password and old OAuth fallback routes.

- [ ] **Step 3: Verify sessions and failure states**

Test success, invalid credential, CSRF mismatch, expired credential, open redirect rejection, secure cookie/session creation, and employee hosted-domain enforcement.

- [ ] **Step 4: Browser verification**

Confirm Google owns the rendered button and returning-account personalization when the browser/provider state permits it; MALIEV must not synthesize that state. Verify layout in all three apps.

- [ ] **Step 5: Commit separately**

Create one validated commit in each app repo after the AuthService commit.

### Task 10: Integrated verification and final review

**Files:** none beyond fixes discovered by validation.

- [ ] **Step 1: Run full affected-repo gates**

Run Release builds and focused/full tests proportionate to each repo; run format verification for contract/auth/money repos. Record exact pass/fail counts.

- [ ] **Step 2: Run QuoteEngine browser journeys**

Verify all 20 comments at desktop and narrow viewport. Capture screenshots for composer idle/upload, auth, rail/projects, sent thumbnail/workbench, shipping selection, preview metadata, and project expansion.

- [ ] **Step 3: Review diffs and commit boundaries**

For every repo run `git status --short`, `git diff --check`, and inspect staged diffs. Do not include `.build-sweep.log` or unrelated files.

- [ ] **Step 4: Cross-check the acceptance matrix**

Map each comment to code, automated proof, and browser evidence. Report any external configuration blocker exactly; do not call a partially configured GIS/provider flow complete.

