# QuoteEngine + ChatbotService — Remediation Plan

**Date:** 2026-06-18
**Source:** [2026-06-18-quoteengine-chatbot-audit.md](2026-06-18-quoteengine-chatbot-audit.md)
**Status:** Partially implemented. Each item lists files, approach, tests, and acceptance criteria.

### Implementation status (2026-06-18)

Landed in `Maliev.ChatbotService` and verified by the **full test suite: 445 passed, 0 failed, 6 skipped** (count dropped from 456 after removing 11 tests for the deleted dead `IInputValidationService`/`IResponseTimeoutService`) (the skips are pre-existing `GeminiClientTests` that need a live Gemini key). `dotnet build` of the whole solution is clean (0/0), and the integration suite (Testcontainers Postgres/Redis/RabbitMQ) was run with the ChatbotService Aspire resource stopped (stop the single `ChatbotService` resource via the Aspire dashboard/MCP — killing the process alone fails because the AppHost respawns it and re-locks `Api/bin`).

| Item | Status | What changed |
|---|---|---|
| **S6 (webhook reliability + durability)** | ✅ Done | The fire-and-forget `Task.Run` debounce is gone. New `IWebhookBufferQueue`/`RedisWebhookBufferQueue` holds all state in Redis (buffer list + reply-context blob + a global `due` ZSET) and uses a **lease/visibility-timeout**: `ClaimDueSessionsAsync` pushes a claimed session's due-time forward (it does **not** delete), `PeekAsync` reads without removing, and the buffer is only `LPOP×N`-trimmed on a successful `AcknowledgeAsync`. So a crash mid-processing drops nothing — the lease expires and a later poll (this process or after restart) reclaims it (**at-least-once**). `ProcessWebhookCommandHandler` now just enqueues; a hosted `WebhookBufferProcessorBackgroundService` polls → claims → processes each in a fresh scope via `IWebhookBufferProcessor` → replies. **Lease = 360s ≥ the 330s session lock (C2)** so a slow-but-live turn isn't reclaimed; attempt cap (3) at claim time drops a poison batch; reply is **best-effort** (a stale LINE token never re-runs the paid turn); `PlatformUserId` is persisted for a future push-API reply. Poller gated off under Testing (1s loop vs per-test Redis flush). New tests: 6 `WebhookBufferQueueTests` (incl. the deterministic **lease-then-reclaim recovery** acceptance test, the **enqueue-during-lease ⇒ no-duplicate-reply** test that pins the `ZADD GT` fix, and the attempt-cap drop) + 1 `WebhookBufferProcessorTests` end-to-end. Caveats (documented): at-least-once retry isn't idempotent; delayed-recovery LINE reply tokens may be stale (turn still completes). |
| **S2 (attachment caps + daily token budget)** | ✅ Done | `MessagePipelinePolicy.TryValidateAttachmentBudget` caps attachments per message (≤8) and combined bytes (≤75MB). **Daily token budget now shipped:** new `IUsageBudgetService`/`RedisUsageBudgetService` enforces a soft per-user rolling-24h token ceiling (default 2,000,000; `UsageBudget:DailyTokenBudget`, `0`=disabled, high code default keeps the suite green). `SendMessageCommandHandler` **pre-checks** before any model call (graceful in-band "usage limit" message, no model cost, no message persisted) and **post-records** `geminiResponse.TokenUsage.TotalTokens`. **Crux fix:** `AgentChatHandler` now **sums `TokenUsage` across all loop iterations** (was reporting only the last call), so a 10-call agent turn — the QuoteEngine path S2 most needs to bound — is counted correctly. Scope note: the budget covers the response turn / full agent loop; auxiliary intent-classification + summary calls are not counted (v1). New tests: 2 `AgentChatHandlerTests` (accumulation sum + null-when-no-usage), `MessagePipelinePolicyTests` budget-message, 5 `UsageBudgetServiceTests` (Redis), 1 `MessagesApiTests` end-to-end short-circuit. |
| **C3 (attachment persistence)** | ✅ Done | Migration-free: URL/GCS attachment references are serialized into the user `Message.MetadataJson` on save (`BuildAttachmentMetadataJson`) and re-hydrated into prior turns when rebuilding history (`ParsePersistedAttachments`), so the model keeps image/PDF/CAD context across turns. Inline base64 data URLs are intentionally not persisted (store-bloat); the current turn still carries full inline data. |
| **S1** anonymous rate-limit bypass | ✅ Done | Added an ASP.NET per-IP rate limiter (`messages-per-ip` policy, fixed window) to the Chatbot API on `MessagesController`, keyed on `X-Forwarded-For`/`RemoteIpAddress` — **independent of `UserProfileId`**, so re-initiating a session no longer resets the budget. Production default 60/min (override via `RateLimiting:MessagesPerIpPerMinute`); disabled under integration tests unless a test sets it. New integration test `SendMessage_ExceedingPerIpLimit_ReturnsTooManyRequests` proves 429 across fresh sessions from one caller. *(Defense-in-depth at the Chatbot API; the BFF and per-`UserProfileId` 100/hr limit remain.)* |
| **T1** register `quote_generate_3d_preview` | ✅ Done | Added the function declaration (`description`, `process_hint`, required `cad_commands[]` matching the BFF's `Generate3DPreview` schema) to `ToolRegistry`, plus `QuoteEngineToolHandler.AllowedTools` **and** `ToolExecutorService._handlers` — **registering a quote tool requires all three**, now enforced by the data-driven `ExecuteAsync_DeclaredQuoteEngineTool_RoutesThroughToolExecutor` theory. The agent can now build/preview 3D parts (BFF + tests already existed). |
| **P2/P3** prompt completeness + tool-usage discipline | ✅ Done | `quote-engine-assistant.md` + the hardcoded fallback now cover: never reject non-CAD input / call `quote_generate_3d_preview`; handling of photos, PDFs, CAD, **audio & video**; and tool discipline (no raw JSON, no repeated calls, explain blockers as next steps). `SystemInstructionDefaultPromptTests` updated to assert both sources agree. |
| **C1** native function-call/response parts | ✅ Done | `GeminiMessage` gained structured `FunctionCalls`/`FunctionResponses`; `AgentChatHandler` populates them instead of stringifying. `GeminiClient` emits `functionCall` (role `model`) and `functionResponse` (role `user`, **object** response) parts via a shared `BuildContents`; `OpenAICompatibleModelProviderClient` emits `tool_calls` + `tool` role messages. `id` is round-tripped for Gemini 3.x. Format verified against the official Gemini REST docs + 2 new HTTP-body serialization tests. *Not yet smoke-tested against a live provider.* |
| **P1 (MVP)** channel-scoped topic/KB injection | ✅ Done | New pure helper `MessagePipelinePolicy.BuildInjectableTopicKeys` — only `Channel.Intranet` injects domain topics/KB; `SendMessageCommandHandler` now uses it. KB block already gated on `topicKeys.Any()`, so it no-ops for customers. |
| **S3** input length/null-byte guard | ✅ Done | `MessagePipelinePolicy.TryNormalizeContent` (max 8000 chars, strips `\0`, allows empty for attachment-only). Wired into `SendMessageCommandHandler` after the rate-limit check. **Deliberately does not** use the SQL-keyword `IInputValidationService` (it rejects "create a quote", "select PLA", etc.). |
| **C5** per-tool call guard | ✅ Done | `AgentChatHandler` tracks per-turn tool-call counts; stops executing a tool after 3 calls and returns a guidance result to the model. |
| **C2** session-lock TTL | ✅ Done | Lock raised from 35s → `SessionLockSeconds = 330` (covers worst-case 10×30s loop). *(Not unit-tested — Redis path.)* |
| **C4** fallback copy | ✅ Done | Replaced "unable to complete the research" with on-brand, contact-aware copy. |
| **T2** dedupe tool params | ✅ Done | Removed camelCase twins from `quote_update_account_profile` (BFF reads snake-case first). Updated `ToolRegistryTests` to assert camelCase absent. |

**New tests:** `MessagePipelinePolicyTests` (P1 + S3, incl. a regression theory that manufacturing phrases like "create a quote"/"select PLA"/"where is my order" are accepted), `GeminiClientFunctionCallSerializationTests` (C1 — asserts native parts in the HTTP body + object-wrapping of non-object results), an `AgentChatHandlerTests` case for the C5 limit, and an updated `ToolRegistryTests` case for T2.

**Done since first pass:** **T1 extrude/revolve** — the `cad_commands` schema now declares `extrude`/`revolve` with a nested `profile` (plane + segments), `axis`, and `angle`. **S1 spoofing hardening** — the per-IP limiter now keys on `RemoteIpAddress`, which `UseStandardMiddleware`'s `ForwardedHeaders` processing populates from `X-Forwarded-For`, instead of self-parsing the spoofable raw header.

**P1 severity — RESOLVED (latent, mitigated):** the `KnowledgeBase` table is created and indexed by `InitialCreate` but **never seeded** — the only migration `InsertData` is `FallbackResponseTemplates` (UnexpectedError EN/TH). KB rows are added only at runtime via the admin `KnowledgeBaseController` (POST) / `KnowledgeBaseRepository`. So P1 was never an active data-leak at deploy; it is a *latent* confidentiality risk that the shipped channel-scoping (`BuildInjectableTopicKeys` returns empty for customer channels, and KB injection is gated on `topicKeys.Any()`) already neutralizes regardless of what an admin later seeds.

**Cleanup — ✅ DONE:** removed the dead `IInputValidationService`/`InputValidationService` and `IResponseTimeoutService`/`ResponseTimeoutService` (registered but never injected in production; the SQL-keyword validator was actively unsuitable for chat, the timeout service superseded by the agent-loop's per-call timeouts) plus their tests and DI registrations. Suite stays green at 445/0/6.

**S1-extras — ✅ DONE (in `Maliev.QuoteEngine.Bff`):** the signed, HttpOnly anonymous-visitor cookie (`AnonymousVisitorCookie`, HMAC mirroring `CustomerAssistantHandoffCookie`) owns anonymous sessions and uploads, while cost-bearing ingress is limited independently by trusted client IP. The `quote-agent` policy protects messages, streaming, and speech cleanup; dedicated policies additionally protect upload initiation/finalization/handoff/streaming, estimates, sketches, browser DFM reports, and telemetry. Upload streams chain a per-minute request budget with a concurrency ceiling. Production refuses to start until trusted ingress proxies or networks are configured, and `429` responses carry lease-derived `Retry-After` metadata. Testing disables budgets unless a test opts in. Regression coverage proves cookie rotation cannot reset the IP floor, oversized bodies short-circuit with `413`, excess upload streams never reach the downstream client, estimate fan-out is bounded, and telemetry cannot mutate another visitor's upload.

> **Surfaced follow-up (S1-IP-behind-BFF):** the BFF→ChatbotService call does **not** propagate the client IP, so ChatbotService's shipped per-IP limiter (60/min) sees *all* QuoteEngine traffic as the single BFF IP — a collective cap, not per-customer. This makes the new BFF limiter the **primary** abuse control for the QuoteEngine channel (the ChatbotService per-IP limiter still protects website/webhook/direct channels). Fix later by forwarding the client IP / visitor id to ChatbotService or exempting the BFF service account from that limiter.

**Still pending (their own PRs):**
- **C1 smoke (release gate)** — one live-provider multi-tool turn before production. Correctness is already covered by `GeminiClientFunctionCallSerializationTests` (native `functionCall`/`functionResponse` parts in the HTTP body); the live smoke needs a real Gemini key and so cannot run here (the 6 skipped `GeminiClientTests` are the same class). **Manual gate:** with a real key + the fleet up, run one QuoteEngine turn that forces ≥2 sequential tool calls (e.g. estimate → DFM ack → formal quote) and confirm the model receives the prior tool results as native parts and completes the turn.

> **Lesson (recorded):** registering a config-driven default in `appsettings.json` also applies under the `Testing` environment (no `appsettings.Testing.json` exists for this service), which silently enabled the limiter for the whole integration suite and produced 38 spurious 429s. Keep environment-varying defaults in the code path (`isIntegrationTest ? …`) or in the test factory, not base `appsettings.json`.

---

## Guiding principles

- **Build + test every change** (`dotnet build` clean, then `dotnet test`) before it's considered done; add a regression test per fix so the bug can't silently return.
- **Channel-aware by default** — the root cause behind several findings is that customer (website/quote-engine) and internal (intranet) flows share unscoped machinery. Fixes should make the channel boundary explicit, not patch symptoms.
- **Backward compatible** — `Channel`, tool profiles, and the confirmation-action pattern already exist; extend them rather than re-architect.
- **Ship in risk order** — public-abuse and confidentiality first, then reliability, then capability, then strategic.

## Effort key
S = < ½ day · M = 1–2 days · L = 3–5 days · XL = separate design + multi-PR initiative.

---

## Phase 0 — Pre-flight (do first, ~S)

| Task | Why |
|---|---|
| **0.1** ✅ DONE — Read the seeded `KnowledgeBase` rows. **Finding:** KB is **not seeded** (the `InitialCreate.cs:273` `InsertData` is `FallbackResponseTemplates`, not KB); KB is populated only at runtime via the admin controller. P1 is therefore a latent risk, already mitigated by channel-scoping. | Sets P1's final severity (scope-confusion vs real data-leak) and tells us whether P1 is a "today" incident or a latent risk. |
| **0.2** Add **characterization tests** that capture today's behavior of the agent loop payload and the prompt-assembly output for a `quote-engine` session. | Gives a safety net before touching C1 and P1. |
| **0.3** Confirm production `Llm:Provider` value. | Decides whether the OpenAI path (C1, attachment-drop) is live or dormant. |

---

## Phase 1 — Stop the bleeding: abuse & safety (highest priority)

### 1.1 — S1: Close the anonymous rate-limit bypass — **M, CRITICAL**
**Problem:** anonymous web/quote-engine sessions each mint a fresh `UserProfileId`, and the limit keys on it, so re-initiating a session resets the budget. Endpoints are anonymous end-to-end.
**Approach:**
1. Issue a **signed, HttpOnly anonymous-visitor cookie** at the BFF on first contact (Make Studio) and on `InitiateSession`; carry a stable `visitorId` claim through to ChatbotService.
2. Add **IP + visitorId sliding-window limiting** independent of `UserProfileId`, using ASP.NET `RateLimiter` middleware at the **BFF `AgentController`** (the public ingress) — partition key = `visitorId ?? clientIp`. Keep the existing per-`UserProfileId` counter as a secondary signal.
3. Cap **sessions created per IP/hour**.
4. Optionally gate anonymous bursts behind a lightweight challenge (Turnstile/hCaptcha) above a threshold.
**Files:** `Maliev.QuoteEngine.Bff/Program.cs` (rate-limiter + cookie), `AgentController.cs`, `Maliev.QuoteEngine.Bff/Services/CustomerSessionResolver.cs`; `Maliev.ChatbotService.Api/Program.cs` (defense-in-depth limiter), `RateLimitService.cs` (key strategy).
**Tests:** integration — N+1 requests from one IP/visitor → 429; new session does **not** reset the IP/visitor window.
**Acceptance:** a script that loops `InitiateSession`+`SendMessage` is throttled within the configured window.

### 1.2 — S3: Wire input validation into the pipeline — **S, HIGH**
**Problem:** `IInputValidationService` is registered but never invoked.
**Approach:** inject it into `SendMessageCommandHandler`; call right after the rate-limit check and before the Gemini request — enforce max content length, strip/refuse control characters, and run the existing abuse/injection heuristics; on failure throw the existing `InvalidOperationException` → 400. Decide `IResponseTimeoutService`: wire it (wrap the agent loop) or delete it.
**Files:** `SendMessageCommandHandler.cs` (ctor + call site ~line 175), DI in `Program.cs`.
**Tests:** unit — oversized/abusive content is rejected before any `IGeminiClient` call (assert the fake client was never called).
**Acceptance:** validation runs on every message; an over-long payload returns 400, not a Gemini call.

### 1.3 — S2: Cost / compute guardrails — **M, HIGH**
**Problem:** message-count ≠ cost; one message → up to 10 Gemini calls + 50 MB payloads, no token/byte/attachment-count ceiling.
**Approach:** add per-visitor/session/day **token budget** (use `GeminiTokenUsage` already returned) and short-circuit when exceeded; cap **attachment count** and **total bytes** per message (today only per-attachment size is checked, `SendMessageCommandHandler.cs:180-191`); make `MaxIterations` configurable and consider lowering from 10; add a cost-aware early stop.
**Files:** `SendMessageCommandHandler.cs`, `AgentChatHandler.cs` (`MaxIterations` → config), a small `IUsageBudgetService`.
**Tests:** unit — exceeding the daily token budget yields a graceful "limit reached" response; >N attachments → 400.

---

## Phase 2 — Confidentiality & prompt correctness

### 2.1 — P1: Channel-scope intent, topic prompts, and KB facts — **M, CRITICAL**
**Problem:** the intranet-only intent classifier runs on every channel and drives **unscoped** topic-prompt + KB-fact injection, so internal finance/sales/HR operational instructions (and any seeded KB facts) can enter a public customer prompt.
**Approach (minimum viable, then full):**
- *MVP:* in `SendMessageCommandHandler`, only populate `topicKeys` from a **per-channel customer-safe allowlist**; for `Website`/`QuoteEngine`, never add intranet topics, and skip KB injection for non-intranet channels (or restrict to a customer-safe topic set). Pass `Channel` into `GetMergedInstructionsAsync` and `GetByTopicAsync`.
- *Full:* add a `Scope`/`Channel` column to `SystemInstruction` and `KnowledgeBase`; filter `GetActiveByTopicsAsync`/`GetByTopicAsync` by it; give website/quote-engine their own intent taxonomy + classifier prompt (a customer-facing `intent-classification` variant).
**Files:** `SendMessageCommandHandler.cs:314-364`, `SystemInstructionService.cs:138-218`, `SystemInstructionRepository.cs`, `KnowledgeBaseRepository.cs`, `Prompts/extraction/intent-classification.md` (+ a customer variant), migration for the scope column.
**Tests:** a `quote-engine`/`website` session with a "pricing" message **never** merges `sales`/`finance`/`hr`/`inventory` topic text or intranet KB facts (assert on the assembled system prompt).
**Acceptance:** no intranet topic/KB string can appear in a customer-channel prompt — enforced by test.

### 2.2 — P3: Inject real tool-usage rules for agent channels — **S, MEDIUM**
**Problem:** `tool-usage-rules.md` is never injected (it's only reachable as an intent topic, which the classifier never emits) and is intranet-flavored.
**Approach:** inject a **channel-appropriate** tool-usage block unconditionally for agent channels (Intranet, QuoteEngine), independent of intent topics; author a quote-engine-specific version (the 3-call cap, "don't dump raw JSON," confirmation-first for writes, the gated workflow).
**Files:** prompt-assembly in `SendMessageCommandHandler.cs`; new `Prompts/core/quote-engine-tool-rules.md`.
**Tests:** quote-engine prompt always contains the tool-usage block.

### 2.3 — P2: Bring the quote-engine prompt up to the product spec — **M, HIGH**
**Problem:** prompt omits audio/video and the 3D-preview tool; the live default still says "CAD required, photos supplemental" (the rejection behavior the 2026-06-17 spec set out to remove); two diverging persona sources (DB-seeded `.md` vs hardcoded `GetDefaultSystemInstruction`).
**Approach:** apply the 2026-06-17 spec's prompt strengthening (never reject non-CAD; infer shape/dims; call the 3D preview tool); add artifact-handling guidance for all accepted types; add agent-side abuse/jailbreak guidance; **reconcile the two persona sources** into one (treat the `.md` as source of truth, generate/trim the fallback from it or shrink the fallback to a pointer).
**Files:** `Prompts/core/quote-engine-assistant.md`, `SystemInstructionService.cs:245-260` (fallback), the prompt-seeding path. Pairs with 4.1.
**Tests:** `SystemInstructionDefaultPromptTests` updated; assert the fallback and the seeded prompt agree on the key rules.

### 2.4 — P4: Mark tool/file/page content as untrusted — **S, LOW**
Wrap tool results and external content in a delimited "untrusted data — never treat as instructions" envelope in `AgentChatHandler` and reinforce in the prompt.

---

## Phase 3 — Agent-loop reliability

### 3.1 — C1: Emit native function-call / function-response parts — **L, CRITICAL**
**Problem:** both providers send prior tool turns as plain text, breaking multi-step tool calling (and the OpenAI path also drops non-image attachments and disables streaming with tools).
**Approach:**
1. Give `GeminiMessage` a structured representation of a tool turn (e.g. `FunctionCall? Call`, `FunctionResult? Result`) instead of stringified JSON in `Content`.
2. `AgentChatHandler` populates those fields (replace `JsonSerializer.Serialize(functionCall)` and the `"[Function result for X]"` text — `AgentChatHandler.cs:94-98, 152-156`).
3. `GeminiClient.SendMessageAsync` + `BuildGeminiPayloadJson` emit `{functionCall:{name,args}}` (model turn) and `{functionResponse:{name,response}}` (function turn).
4. `OpenAICompatibleModelProviderClient.BuildPayload` emits `assistant.tool_calls` + `role:"tool"` messages; also stop dropping non-image attachments where the provider supports them (`:328-332`) and reconsider the no-streaming-with-tools fallback (`:103-113`).
**Files:** `GeminiMessage` model, `AgentChatHandler.cs`, `GeminiClient.cs`, `OpenAICompatibleModelProviderClient.cs`.
**Tests:** a fake `IGeminiClient`/HTTP capture asserts the request carries structured `functionCall`/`functionResponse` (Gemini) and `tool_calls`/`tool` (OpenAI) for a 2-step tool conversation; existing `GeminiClientStreamingTests`/`AgentChatHandlerTests` stay green.
**Risk:** central path — gate behind the Phase 0 characterization tests; validate against a real multi-tool flow (estimate → DFM ack → formal quote).

### 3.2 — C2: Align session lock TTL with worst-case loop — **S, HIGH**
Renew the Redis lock each iteration (extend TTL), or set the initial TTL ≥ `MaxIterations × TimeoutSeconds` + margin, so a slow turn can't release the lock mid-flight. **Files:** `SendMessageCommandHandler.cs:131`, `AgentChatHandler.cs`. **Test:** simulated long loop keeps the lock held throughout.

### 3.3 — C5: Per-tool call guard — **S, MEDIUM**
Track per-tool invocation counts within a turn in `AgentChatHandler`; dedupe identical calls and cap repeats (enforce the "3 per tool" rule in code, not just prompt).

### 3.4 — C3: Persist + re-hydrate attachments across turns — **M, MEDIUM**
Persist attachment references (GCS URIs) with the `Message`, and re-attach them to the relevant turns when rebuilding history (`SendMessageCommandHandler.cs:308, 403-415`) so the model retains earlier image/PDF/CAD context. **Test:** turn 2 still "sees" the turn-1 image.

### 3.5 — C4: Fix the fallback copy — **S, LOW**
Replace "unable to complete the research" (`AgentChatHandler.cs:196`) with a localized, domain-appropriate message.

---

## Phase 4 — Capability unlock

### 4.1 — T1: Register `quote_generate_3d_preview` so the agent can call it — **S, HIGH**
**Problem:** the tool is built + tested in the BFF but absent from the agent's surface, so it's unreachable.
**Approach:** add the function declaration to `ToolRegistry.GetQuoteEngineFunctionDeclarations()` and the name to `QuoteEngineToolHandler.AllowedTools`; verify the BFF route handles it in `AgentController`/`QuoteAgentService`; pair with the prompt change in 2.3 so the agent knows when to call it; wire the inline BabylonJS preview per the spec.
**Files:** `ToolRegistry.cs`, `QuoteEngineToolHandler.cs:22-55`, `Prompts/core/quote-engine-assistant.md`, client viewer component.
**Tests:** `QuoteEngineToolHandler`/`ToolRegistry` tests include the new tool; an agent test exercises an inferred-primitive request → GLB.
**Acceptance:** "make a 50×30 mm bracket with two holes" produces a previewable GLB in chat.

### 4.2 — T2: De-duplicate `quote_update_account_profile` params — **S, MEDIUM**
Keep snake_case only (the handler already serializes `SnakeCaseLower`, `QuoteEngineToolHandler.cs:19`); normalize incoming casing in the BFF; remove the camelCase twins from `ToolRegistry.cs:332-350`. **Test:** schema has no duplicate fields; BFF still accepts either casing.

### 4.3 — T4: Guard tool-profile isolation — **S, LOW**
Add a test asserting the `quote-engine` profile never returns any intranet tool name.

---

## Phase 5 — Reliability & hardening

- **5.1 — S6 (M):** move webhook buffered processing off fire-and-forget `Task.Run` onto a durable hosted-service/queue with retry + observability (`ProcessWebhookCommandHandler.cs:162-210`).
- **5.2 — S4 (S):** constant-time signature compare in `LineClient`; add webhook event-id idempotency.
- **5.3 — S5 (S):** bind the session to the anonymous visitor cookie so a leaked session GUID alone can't be posted to.

---

## Phase 6 — Strategic / vision (separate design efforts)

- **6.1 — "Team of engineers" multi-agent (XL).** Today it's a single Gemini loop. To deliver the vision, design a small orchestration: specialist roles (DFM reviewer, materials/process advisor, pricing engineer, checkout concierge) as either sub-agents or distinct tool/prompt phases, with a coordinator. Needs its own brainstorming + spec.
- **6.2 — Audio/video artifact analysis (L).** Today audio is dictation-only and video is accepted but unanalyzed. Define what "analyze a video/audio of a part" should produce (frames → geometry hints, spoken requirements → structured inputs) and add a tool + prompt + pipeline. The OpenAI provider's attachment-drop (C1/3.1) must be fixed first for non-Gemini providers.

---

## Recommended first PR

Bundle the cheap, high-impact safety wins that don't touch the central agent loop:
**1.2 (wire input validation) + 1.1 MVP (IP/visitor rate limiting at the BFF) + 2.1 MVP (channel-scope topic/KB injection).**
These three remove the public-abuse and confidentiality exposure with contained blast radius and clear tests, and they don't depend on the riskier C1 rework. Tackle **C1 (3.1)** as its own PR behind the Phase 0 characterization tests, then **T1 (4.1)** to unlock the 3D-preview capability.

## Sequencing / dependencies

- 2.3 (prompt) and 4.1 (register 3D tool) should land together — prompt tells the agent to use the tool that 4.1 exposes.
- 3.1 (C1) should precede 6.2 (audio/video) on non-Gemini providers (the attachment-drop is part of the same fix).
- 0.2 characterization tests should precede 2.1 and 3.1.
- 1.1 and 5.3 share the anonymous-visitor-cookie mechanism — implement the cookie once.
