# Maliev.QuoteEngine + Maliev.ChatbotService — Interaction & Safety Audit

**Date:** 2026-06-18
**Scope:** How the Make Studio (QuoteEngine) frontend and the ChatbotService agent ("Mali / น้องมะลิ") work together to let customers price parts, manage projects, order, pay, and track — with focus on: (1) correctness & interactivity, (2) tool-call validation, (3) system-prompt quality & gaps, (4) anti-spam safeguards. Read-only analysis; no code was changed.

## How it works today (verified)

```
Browser (Make Studio, Blazor)
   │  POST /quote/v1/agent/messages[/stream]            (QuoteEngine BFF: AgentController)
   ▼
QuoteEngine BFF  ── QuoteAgentService ── issues signed X-Maliev-Agent-Context token
   │  POST /chatbot/v1/messages[/stream]  (forwards user Bearer + agent-context token)
   ▼
ChatbotService  ── SendMessageCommandHandler (pipeline spine):
   Redis per-session lock (35s)
   → rate limit (100 msg/hr, key = UserProfileId)
   → attachment size check
   → language detect → intent classify → assemble system prompt (channel core + topics + KB facts)
   → AgentChatHandler loop (Gemini function-calling, MaxIterations=10, 30s each)
        └─ tool call → ToolExecutorService → QuoteEngineToolHandler → BFF /quote/v1/agent/tools/{name}
               └─ QuoteAgentService.ExecuteToolAsync / ConfirmActionAsync (real ownership/gating)
   → SignalR / NDJSON stream of text deltas + "thinking steps" back to the browser
```

The **architecture is sound**: a thin, allowlisted tool proxy in ChatbotService; a **signed-context-gated** tool boundary in the BFF; a **server-side confirmation-action** pattern for any state change; auth-gated customer-data access. The problems below are in the *wiring and safeguards around* that architecture, not the shape of it.

---

## 1. Correctness & Interactivity

### C1 — [CRITICAL] Multi-turn tool calling is sent to Gemini as plain text, not native function parts
**Runtime path (verified):** DI resolves `IGeminiClient` → `ProviderRoutingGeminiClient` (`Program.cs:160`), which routes by `Llm:Provider` (default `"gemini"`) to `GeminiModelProviderClient`, which **wraps and delegates directly to `GeminiClient`** (`GeminiModelProviderClient.cs:14,29,36-44`). So for the default provider the code below is the live path. *(The alternate `OpenAICompatibleModelProviderClient` path was not read; if `Llm:Provider="openai"` is ever used, re-verify its serialization separately.)*

`GeminiClient` serializes **every** message as a single text part (`new { text = message.Content }`, role `assistant→model` else `user`) — see `GeminiClient.cs:61-81` and `GeminiClient.cs:459-479`. But `AgentChatHandler` records the model's tool call as `GeminiMessage{Role="assistant", Content=JsonSerializer.Serialize(functionCall)}` (`AgentChatHandler.cs:94-98`) and each tool result as `GeminiMessage{Role="user", Content="[Function result for X]: …"}` (`AgentChatHandler.cs:152-156`). **No request path ever emits Gemini `functionCall` / `functionResponse` parts.**

**Both providers affected (verified):** `OpenAICompatibleModelProviderClient` has the *same* defect and worse — it maps tool turns to plain `assistant`/`user` `content` strings instead of OpenAI `tool_calls`/`tool` roles (`OpenAICompatibleModelProviderClient.cs:218-225`), drops every non-image attachment to a `"Attached file available as supplemental context"` stub (`:328-332`), and disables token streaming whenever tools are present (`:103-113`).

- **Impact:** Gemini's function-calling contract expects the prior model turn to carry a `functionCall` part and the next turn a `functionResponse` part. Feeding them as free text degrades multi-step tool use — the model can re-issue calls, lose grounding on results, or hallucinate. It "works by luck" for a single call but is brittle for the exact multi-step chains this product needs (estimate → DFM ack → formal quote → order → payment).
- **Fix:** Extend `GeminiMessage` to carry structured tool turns and emit native parts: model turn → `parts:[{functionCall:{name,args}}]`; result turn → `parts:[{functionResponse:{name,response}}]`. Apply to both `SendMessageAsync` and `BuildGeminiPayloadJson` (streaming).

### C2 — [HIGH] Per-session lock (35s) is shorter than the worst-case turn (~300s)
`SendMessageCommandHandler.cs:131` takes the Redis lock for 35s, but the loop is `MaxIterations=10` (`AgentChatHandler.cs:17`) × `TimeoutSeconds=30` (`AgentChatHandler.cs:61`) ≈ 300s, with **no lock renewal** mid-loop.
- **Impact:** On a slow multi-tool turn the lock expires, a concurrent message for the same session acquires it, and two turns run in parallel — interleaved history writes and racing session/state updates, undermining the "sequential per session" guarantee.
- **Fix:** Renew the lock each iteration (or set TTL ≥ MaxIterations×timeout + margin); align the constants.

### C3 — [MEDIUM] History is text-only, capped at 10, and attachments aren't persisted
Only the last 10 messages are loaded (`SendMessageCommandHandler.cs:308`), `Message` stores text only, and attachments are re-attached **only for the current turn** (`SendMessageCommandHandler.cs:403-415`).
- **Impact:** On the next turn the model loses the photo/PDF/CAD it just analyzed, forcing re-asks and inconsistency across a quote conversation. (Partially mitigated for QuoteEngine because `quote_get_state` / `quote_get_project_summary` can re-fetch server state.)
- **Fix:** Persist attachment references (GCS URIs) and re-hydrate them into the relevant turns; consider history summarization beyond 10.

### C4 — [LOW] Wrong-domain fallback copy
The max-iteration fallback says *"unable to complete the research"* (`AgentChatHandler.cs:196`) — off-brand for a manufacturing quote agent, and English-only.
- **Fix:** Domain-appropriate, localized (TH/EN) message.

### C5 — [MEDIUM] No loop guard against repeated tool calls
The loop executes whatever the model asks, up to 10 iterations, with no per-tool counter. The "max 3 calls per tool per turn" rule lives only in `tool-usage-rules.md:34`, which is **never injected** (see P3).
- **Fix:** Track per-tool invocation counts within a turn and short-circuit/deduplicate.

---

## 2. Tool Calls — Analysis & Validation

The tool **security model is a strength** (see T3). Issues are about coverage and schema hygiene.

### T1 — [HIGH] The 3D-preview generation tool is implemented but unreachable by the agent
The 2026-06-17 spec (`docs/superpowers/specs/2026-06-17-agent-3d-preview-generation.md`) adds `quote_generate_3d_preview` + `QuoteModelPrimitiveDto`/`QuoteGenerateModelRequest`, and the implementation + tests exist in the BFF (`QuoteAgentService.cs`, `QuoteAgentEndpointTests.cs`). **But the tool is missing from `ToolRegistry.GetQuoteEngineFunctionDeclarations()` (the 31 declared `quote_*` tools) and from `QuoteEngineToolHandler.AllowedTools` (`QuoteEngineToolHandler.cs:22-55`).** The handler hard-rejects any unlisted name as `"Unknown QuoteEngine tool"`.
- **Impact:** This is the **single biggest gap vs the "create 3D files for quote" goal** — the capability is built and tested server-side but the agent cannot invoke it.
- **Fix:** Register the function declaration in `ToolRegistry` (quote-engine profile) and add the name to `AllowedTools`; apply the spec's prompt strengthening (see P2).

### T2 — [MEDIUM] `quote_update_account_profile` declares duplicate snake_case + camelCase params
`ToolRegistry.cs:332-350` declares `display_name`+`displayName`, `company_name`+`companyName`, `vat_number`+`vatNumber`, `preferred_language`+`preferredLanguage`, `preferred_currency`+`preferredCurrency`.
- **Impact:** Confuses the model (which to fill), bloats the schema, and signals casing inconsistency downstream.
- **Fix:** The tool handler already serializes args as `SnakeCaseLower` (`QuoteEngineToolHandler.cs:19`) — keep snake_case canonically, normalize in the BFF, and drop the camelCase duplicates from the declaration.

### T3 — [STRENGTH, verified] Writes are gated by signed context + server-side confirmation, not model trust
- `AgentController.ExecuteTool` requires a valid signed `X-Maliev-Agent-Context` token → 401 otherwise (`AgentController.cs:170-177`).
- State changes are executed only via a **separate** `POST actions/{actionId}/confirm` endpoint (`AgentController.cs:186-208`).
- `SearchCustomerData` and `ConfirmAction` throw → 401 when sign-in is required; `UploadSketch` enforces `image/*` + 10MB (`AgentController.cs:213-243`).
- `QuoteEngineToolHandler` enforces an allowlist and requires the context token (`QuoteEngineToolHandler.cs:22-83`).
- **Confirm-time logic verified (STRENGTH):** `QuoteAgentService` rejects a model-supplied `amount` that doesn't match the server estimate — `Math.Abs(requestedAmount - state.Estimate.Total) > 0.01m` → `"Payment amount does not match the current server-side estimate."` (`QuoteAgentService.cs:1285-1291`), and exposes `expectedAmount = state.Estimate?.Total` (`:1310`). At confirm time, `ConfirmActionAsync` resolves `customerId` server-side and `ExecuteCreateOrder`/`ExecuteStartPayment` use `state.FormalQuote.QuoteId` / `state.Order.OrderId` / `state.Estimate.Total` — never model input (`:458-494, 2302-2356`). The "never trust customer-supplied IDs/amounts" claim holds.

### T4 — [LOW] Intranet + quote-engine tools share one registry; only profile gating separates them
`ToolRegistry.GetToolDeclarationsForProfile` returns quote_* only for `quote-engine` and the full intranet set for `intranet`. Add a regression test asserting the quote-engine profile never exposes intranet tools.

---

## 3. System Prompt / Instruction Quality & Missing Points

### P1 — [HIGH→CRITICAL] Cross-channel injection: intranet-only intent classifier feeds unscoped internal topic prompts (and KB facts) into customer chats
`intent-classification.md` is hardcoded as *"an intent classifier for the Maliev **Intranet** Operations Assistant"* with internal topics (`customers, sales, finance, hr, analytics, inventory`). It runs for **every** channel (`SendMessageCommandHandler.cs:315`). Its output is added to `topicKeys` (`:319-331`), which then drives:
- topic-prompt merge with **no channel filter** — `SystemInstructionService.cs:179` `GetActiveByTopicsAsync(topics)`, and
- KB-fact injection with **no channel filter** — `SendMessageCommandHandler.cs:347` `GetByTopicAsync(topic)`.

- **Mechanism (proven):** a customer asking "how much does this cost?" classifies as `sales`/`finance`, so the internal topic prompt is merged into the public customer prompt. The topic content is internal *operations behavior*, not a raw secret dump — e.g. `finance.md` tells the agent it can "Record payments — mark invoices as paid", "View payment history for customer or invoice", and that "Credit notes require manager approval" (`Prompts/topics/finance.md:9-44`). Injecting this into **Mali (customer agent)** tells a public assistant it has internal finance/sales/HR *capabilities* it must never offer customers — direct **scope confusion** that contradicts `customer-website-assistant.md` rule #4.
- **Leak dimension (severity-determining, confirm):** the actual *data* exposure depends on whether sensitive `KnowledgeBase` facts are seeded for these topics (injected verbatim via `GetByTopicAsync`). If sensitive internal facts exist in the KB, this becomes a true confidentiality breach; if the KB is empty/benign it stays "wrong-domain capability injection." **Read the seeded KB rows to set final severity.** Rated CRITICAL pending that check because the blast radius (public exposure of internal operational instructions/facts) is high.
- **Fix:** Make intent classification and topic/KB injection channel-aware. Tag `SystemInstruction`/`KnowledgeBase` rows with an allowed scope/channel and filter on it; give website/quote-engine their own customer-safe topic taxonomy. Add a regression test that no intranet topic/KB can enter a customer channel.

### P2 — [HIGH] The quote-engine prompt under-specifies the product's own capabilities and safety
- **No audio/video guidance** though the vision says "analyze all artifacts incl. audio/video"; the prompt limits inputs to CAD/3D + PDF/photo/sketch.
- **No 3D-preview-generation guidance** — the live default still says *"CAD and 3D files are required … PDFs/photos/sketches are supplemental requirement context only"* (`SystemInstructionService.cs:255`), which is the exact "rejection" behavior the 2026-06-17 spec set out to eliminate.
- **No agent-side anti-abuse/jailbreak guidance.**
- **Two diverging sources of truth** for the persona: the DB-seeded `quote-engine-assistant.md` vs the hardcoded fallback `GetDefaultSystemInstruction` (`SystemInstructionService.cs:245-260`). The fallback is terser and omits the "engage with the photo first, never reject" rule.
- **Fix:** Expand the prompt to all artifact types + the 3D-preview tool + abuse handling; reconcile the seeded prompt and the hardcoded fallback into one source of truth.

### P3 — [MEDIUM] `tool-usage-rules.md` is effectively dead and intranet-flavored
It's a Topic prompt keyed `tool-usage-rules`, but `topicKeys` is only ever populated from intent-classifier outputs (`customers/sales/finance/hr/analytics/inventory/General`), so it is **never injected**. Its content ("search customers/orders/invoices; never guess customer names/order numbers") is also meaningless for the quote-engine toolset.
- **Impact:** General tool discipline (the 3-call cap, "don't dump raw JSON", "cite the source") never reaches the model.
- **Fix:** Inject a channel-appropriate tool-usage block unconditionally for agent channels (not via intent topics); write a quote-engine-specific version.

### P4 — [LOW] Prompt-injection resistance is text-only
Tool results are concatenated as `user`-role text (`AgentChatHandler.cs:155`) and page/file content flows into the model. The "only trust backend skill prompts" rule is good but there is no structural separation of untrusted data.
- **Fix:** Wrap tool/file/page content in clearly delimited "untrusted data" envelopes and reinforce in the prompt.

---

## 4. Anti-Spam / Abuse Safeguards

### S1 — [CRITICAL] The 100 msg/hr limit is trivially bypassable for anonymous customers
`/chatbot/v1/messages` and `/messages/stream` are `[AllowAnonymous]` (`MessagesController.cs:67,115`). `RateLimitService` keys on `UserProfileId` (`chatbot:ratelimit:{userProfileId}`). For an anonymous website/quote-engine visit (no social `externalUserId`, no authenticated id), `InitiateSessionCommandHandler` mints a brand-new profile with `Guid.NewGuid()` (`InitiateSessionCommandHandler.cs:209-227`; the lookup switch at `:198-205` only covers Line/FB/IG/WhatsApp). The BFF in front does **not** add an auth gate either: `QuoteEngine.Bff/Program.cs:64` is bare `AddAuthorization()` with **no fallback policy**, and `AgentController` carries no `[Authorize]` — so the public agent endpoints are anonymous end-to-end.
- **Impact:** Re-initiate a session → new `UserProfileId` → fresh 100-message budget. The limit only constrains authenticated/social users with stable IDs. **This is the headline spam hole for the public frontend.**
- **Fix:** Add IP-based + anonymous-cookie + global rate limiting independent of `UserProfileId` (ASP.NET `RateLimiter` middleware), cap sessions-per-IP/hour, and consider a lightweight challenge (CAPTCHA / proof-of-work) for anonymous bursts.

### S2 — [HIGH] Message-count limiting ≠ cost/compute limiting
One message can fan out to up to 10 Gemini calls (`AgentChatHandler`) plus large multimodal payloads (image 10MB / PDF 20MB / video & audio 50MB — `SendMessageCommandHandler.cs:180-191`). There is no per-token budget, no per-message attachment **count** cap, and no daily cost ceiling.
- **Impact:** Even inside 100 messages, a user can drive very high Gemini spend; combined with S1, cost exposure is effectively unbounded.
- **Fix:** Add per-user/session/day token & cost budgets; cap attachment count and total bytes per message; lower `MaxIterations` or add cost-aware early stop.

### S3 — [HIGH] `IInputValidationService` (and `IResponseTimeoutService`) are registered but never used
`IInputValidationService` is referenced only in `Program.cs` (DI), its implementation, and tests — not in `SendMessageCommandHandler` / `MessagesController` / `AgentChatHandler` (it's not even in the handler's constructor). `ResponseTimeoutService` appears only in its own files.
- **Impact:** No input length/abuse/injection validation runs before content reaches Gemini — a safeguard that exists on paper but provides no protection.
- **Fix:** Invoke `IInputValidationService` early in the pipeline (length, abuse, injection heuristics); wire or remove `IResponseTimeoutService`.

### S4 — [LOW, mostly resolved] Webhook ingestion surface (LINE/Meta)
Signature verification **is** implemented — `LineClient.VerifySignature` (HMAC-SHA256 + base64, `LineClient.cs:74-90`) and `MetaClient.VerifySignature` (`sha256=` HMAC, `MetaClient.cs:114-134`); README documents it on `/webhooks/line`. Residual nits: LINE compares with `signature.Equals(…, Ordinal)` (not constant-time → minor timing side-channel), and replay/idempotency isn't obviously enforced.
- **Recommendation:** Constant-time compare + event-id idempotency. Low priority.

### S6 — [MEDIUM] Webhook processing is fire-and-forget and not durable
`ProcessWebhookCommandHandler.BufferMessageAndProcessAsync` debounces then processes via `_ = Task.Run(async () => …, CancellationToken.None)` (`ProcessWebhookCommandHandler.cs:162-210`): unobserved exceptions, no retry, and work is lost on shutdown/restart — the comment itself flags "consider a dedicated scheduler or queue." (Positive: the 2s debounce is a reasonable per-session anti-flood measure, and social users are keyed on a stable `IdentityLink`, so S1's anonymous-reset hole does **not** apply to LINE/Meta.)
- **Recommendation:** Move buffered processing to a durable background queue / hosted service with retry + observability.

### S5 — [LOW] No caller↔session binding on `/messages`
Anyone with a session GUID can post to it; relies on GUID unguessability. Consider binding the session to the anonymous cookie.

---

## Capability Map vs the Vision

| Vision capability | Status | Evidence |
|---|---|---|
| Get pricing via chat | ✅ Implemented | `quote_calculate_estimate` + `PricingServiceClient`, gated workflow |
| **Create / generate 3D files for quote** | ⛔ **Blocked** | `quote_generate_3d_preview` built + tested in BFF but **not registered as an agent tool** (T1) |
| Analyze images | ✅ Implemented | image MIME → Gemini multimodal |
| Analyze PDF | ✅ Implemented | PDF MIME forwarded; transcript export via PDF service |
| Analyze 3D CAD | ✅ Implemented | upload → geometry runtime / DFM (`QuoteGeometryRuntimeClient`, `QuoteFileAnalyzed`/`QuoteDfmAnalysisReady` consumers) |
| Analyze audio | 🟡 Partial | accepted (50MB) + dictation clean-up (`clean-speech`); no audio *content* analysis in the quote flow |
| Analyze video | 🟡 Stubbed | video MIME accepted/forwarded, but no dedicated pipeline/tool/prompt; not part of quote flow |
| Create & update customer data | ✅ Implemented | `quote_update_account_profile` (confirm), addresses, checkout details |
| Create / manage projects | ✅ Implemented | `quote_prepare_draft_project` / duplicate / pin / unpin / archive / achieve / resume (confirmation cards) |
| Place order | ✅ Implemented | `quote_create_order` (auth + approved quote + checkout + confirm) |
| Make payment | ✅ Implemented | `quote_start_payment` + payment consumers |
| Track orders | ✅ Implemented | Orders/OrderDetail pages + `QuoteOrderStatusChanged` consumer + SignalR |
| "Team of engineers" multi-agent | ⛔ Missing | single Gemini agent loop; no specialist sub-agents/personas/orchestration |

---

## Priority order for fixes

1. **S1** (anonymous rate-limit bypass) + **S3** (unused input validation) — public abuse exposure.
2. **P1** (internal prompt/KB leak into customer chat) — confidentiality + correctness.
3. **C1** (native function-call parts) — reliability of every multi-step flow.
4. **T1** (register `quote_generate_3d_preview`) — unlocks the headline "create 3D for quote" goal.
5. **S2** (cost budgets), **C2** (lock/timeout alignment), **P2/P3** (prompt completeness + dead tool rules).
6. Remaining medium/low items.

*Note: this is an audit. None of the above has been implemented — each fix should be made and verified (build + targeted tests) before rollout. All originally-open checks are now closed: T3 confirm-time validation = verified strength; S4 webhook signatures = implemented (minor nits); C1 = confirmed in both providers; webhook durability = new finding S6. The single remaining read is the **content** of the seeded `KnowledgeBase` rows (`InitialCreate.cs:273 InsertData`) to set P1's final severity (scope-confusion vs data-leak).*

---

## Companion document

A phased remediation plan derived from these findings: [2026-06-18-quoteengine-chatbot-remediation-plan.md](2026-06-18-quoteengine-chatbot-remediation-plan.md).
