# Make Studio — E2E Deployment Readiness

_Status reconciliation as of 2026-06-20. Evidence-grounded review across QuoteEngine, ChatbotService,
OrderService, ProjectService, PaymentService, JobService, Intranet, and Aspire E2E._

> A multi-agent audit was attempted but aborted on a hard account rate limit ("session limit · resets
> 12pm Asia/Bangkok") after ~1.49M subagent tokens, returning no findings. This report is the manual,
> inline continuation, cross-checked against committed git history and source.

## TL;DR

The Make Studio quote→paid-order→production handoff is **wired and internally consistent in code, and
already committed.** The working trees of QuoteEngine, ChatbotService, OrderService, ProjectService,
PaymentService, Intranet, and Aspire are **clean** as of this reconciliation. The remaining deployment
gate is therefore **green E2E proof + targeted audit coverage**, not known uncommitted feature code.

## Architecture (verified)

```
Customer ─▶ QuoteEngine.Client (Blazor WASM, QuoteAgentLaunchShell + workbench)
         ─▶ QuoteEngine.Bff (AgentController → QuoteAgentService → ChatbotService;
                              8 RabbitMQ consumers; SignalR QuoteNotificationsHub;
                              12 backend service clients; auth handoff cookies/tokens)
         ─▶ ChatbotService (AgentChatHandler loop; ToolRegistry + QuoteEngineToolHandler;
                              SystemInstructionService; Gemini / OpenAI-compatible providers)
Backend event mesh (Maliev.MessagingContracts):
   PaymentService ─(paid)▶ OrderService ─OrderPaidEvent▶ JobService
   JobService ─JobCreatedEvent▶ ProjectService (links ProjectPart.JobId)
   OrderService ─OrderCreatedEvent (with source project part ids)▶ ProjectService
   OrderService ─OrderStatusChangedEvent▶ QuoteEngine.Bff ─SignalR▶ Customer
   (Project/Order/DFM/File/Price events)▶ Intranet.Bff consumers (staff surfaces)
```

Important structural facts:
- **The BFF publishes no events** (`grep IPublishEndpoint|IBus|.Publish|.Send` → no matches). All
  project-data-to-Intranet flow depends on the **backend services** being called and publishing.
- **`QuoteController` is a hybrid**: it injects both `QuoteEnginePrototypeStore` (in-memory) **and** the
  real clients (`IQuotationServiceClient`, `IOrderServiceClient`, `IPaymentServiceClient`,
  `IQePricingServiceClient`, `ICustomerServiceClient`). The upload path demonstrates the pattern: call
  the real service, fall back to the prototype store on failure (`QuoteController.cs:96-122`). Whether
  each mutating endpoint resolves to the real client or the store in production config is the key
  "backing" question and must be locked down before go-live (see Risks).

## The order→job→project→Intranet linkage (verified coherent)

The single linchpin is the deterministic **`OrderItemId`**, which has **one origin** (OrderService) and
flows into both the project part and the job — no divergent recomputation:

| Step | Code | Mechanism |
|---|---|---|
| Order identity | `OrderServiceClient.cs:79-83` (QE) / `OrderManagementService.cs:328-335` (OS) | Both compute order GUID = `MD5(orderNumber)` — **aligned** |
| Order item identity | `OrderManagementService.cs:353` | `MD5("order-item:{orderId}:{sourceProjectPartId}")` |
| Project part stamped | OrderService `OrderCreatedEvent` (`af13231`) + QE `a85e277` | part gets `OrderId` + `OrderItemId` from the order |
| Paid → job | `OrderPaidEventConsumer.cs` (JobService) | `IConsumer<OrderPaidEvent>` → `CreateJobsForPaidOrderAsync` |
| Job item identity | `IOrderServiceClient.GetOrderItemsAsync` → `OrderItemDto{OrderItemId,SourceProjectPartId}` | job reuses OrderService's `OrderItemId` (single source of truth) |
| Job created | `JobService.cs:1523-1541` | publishes `JobCreatedEvent{JobId,OrderId,OrderItemId}` |
| Part ← job link | `JobCreatedEventConsumer.cs:50-69` (ProjectService) | matches `OrderId && OrderItemId && JobId==null` → `LinkJobAsync` |
| Intranet exposes | `ProjectServiceClient.cs:637`, `ProjectsController.cs:564` | `part.JobId` (+ OrderId/OrderItemId via `585baf8`) |
| Customer status | `QuoteOrderStatusChangedConsumer.cs` | `OrderStatusChangedEvent` → SignalR per-order group |

Conclusion: the chain exists, is in the Aspire topology (`AppHost.cs:965` adds JobService), and is
**satisfiable**. The risk that remains is integration timing/routing (event `ConsumedBy` targeting,
ordering, retries) — exactly what the E2E proves.

## Reconciliation of the 10 stated deployment gaps

| # | Stated gap | Actual state | Evidence |
|---|---|---|---|
| 1 | Project/order linkage proof pending | **Code wired; proof-pending** | deterministic order/item ids align (table above) |
| 2 | Intranet project-part DTO must be committed/validated | **Committed** | Intranet `585baf8 fix: expose project part order links`; tree clean |
| 3 | Order ID contract across QE/OS/PS | **Resolved in code** | `MD5(orderNumber)` both sides; QE `c64f543` |
| 4 | OrderService GUID-preservation dirty change | **Committed, not dirty** | OS `2a28b02 fix: preserve order event guid identities`; tree clean |
| 5 | Payment→production/job lifecycle unproven | **Code wired end-to-end; proof-pending** | OrderPaid→JobCreated→part.JobId chain (table above) |
| 6 | Customer order-status tracking | **Wired** | `QuoteOrderStatusChangedConsumer.cs` → SignalR per-order group |
| 7 | Login redirect restore stricter gate | **Base committed; stricter E2E pending** | QE `7e78489 fix: restore make studio chat after auth` |
| 8 | Real browser upload→DFM→reupload proof | **Real path exists (w/ fallback); browser E2E pending** | `QuoteController.cs:96-122`; DFM consumers + SignalR |
| 9 | ChatbotService tool/prompt schema full audit | **Core registry/dispatch verified; compatibility hardening committed**: 32 tools consistent across `ToolRegistry` (declared) ↔ `QuoteEngineToolHandler.AllowedTools` ↔ BFF `QuoteAgentService` dispatch; customer channel exposes only `quote-engine` tools; BFF tool endpoint requires signed `QuoteAgentContextToken`; QuoteEngine now accepts common model-emitted CAD numeric strings such as `"50 mm"`, object-shaped `params`, correctly ordered object-shaped primitive params, and the singular `command` argument alias in generated preview tool payloads; generated-preview feedback is sanitized before durable memory or next-turn prompt context | `ToolRegistry.cs:30`, `QuoteEngineToolHandler.cs:22-56`, `AgentController.cs:244`; QE `47ca397`, `2ecf184`, `875cd28`, `dbd07ae`, `b5e49ae` |
| 10 | Payment non-happy paths gating | **QuoteEngine relay coverage strengthened**: 5 consumers committed; unit tests cover completed, pending, failed, expired, cancelled mapping, shared order-group routing, completed order-status update failure, and null-payload skip behavior for all states. Duplicate-webhook/idempotency remains a PaymentService/OrderService cross-service proof item. | BFF `QuotePayment{Completed,Pending,Failed,Expired,Cancelled}Consumer.cs`; `PaymentNotificationConsumerTests.cs`; QE `402c468` |

Net: 4 of 10 are simply **already done** (2,3,4,6); 4 are **code-complete, proof-pending** (1,5,7,8);
2 need **targeted release-gate evidence** (9 residual tool authorization/schema coverage, 10 payment
non-happy-path sign-off).

## Current uncommitted work

No uncommitted work was present in QuoteEngine, ChatbotService, OrderService, ProjectService,
PaymentService, Intranet, or Aspire at this reconciliation point.

## True remaining gate (prioritized)

- **P0 — Green Aspire Make Studio E2E** with the stricter (`jobId`) assertion: DFM correction → quote
  approval → order creation → payment completion → Intranet order/project visibility → **project-part
  ↔ order ↔ production-job linkage** → restored chat/workbench. This is the single proof that closes
  gaps 1,5,7,8.
- **P0 — Backing config (VERIFIED this session)**: the mutating money/order/quote endpoints resolve to
  **real** clients and fail-closed in production:
  - `quotes/formal` → `quotationClient.CreateAsync` (QuoteController.cs:709); `quotes/{id}/approve` →
    `quotationClient.GetByIdAsync` (1106); `payments` → `paymentClient.InitiateAsync` + `orderClient.*`
    (925, 911); `orders` → `orderClient.CreateAsync` + `AddStatusAsync` (1159, 1171).
  - The prototype-store `GenerateQuote`/`CreateOrder`/`StartPayment` methods are **not called by the
    controller** (dead code). Upload→prototype fallback is gated to dev/test only (1378-1379), so prod
    fails-closed (502).
  - Residual (product decision, not a blocker): `estimate` falls back unconditionally to `store.Estimate`
    when PricingService returns null (`pricingEstimate ?? store.Estimate`, line 310). Estimates are
    non-binding, so this is graceful degradation; decide whether prod should instead surface a "pricing
    unavailable" state. Draft projects are in-memory only (no `ProjectServiceClient`); the durable
    project record is created Intranet-side on quotation acceptance.
- **P1 — ChatbotService Make Studio tool-schema residual audit (gap 9)**: core registry/dispatch is
  verified, and `quote_generate_3d_preview` compatibility coverage is committed for numeric strings
  (`47ca397`), the singular `command` argument alias (`2ecf184`), object-shaped `params`
  (`875cd28`), and primitive ordering for object-shaped cylinder params (`dbd07ae`).
  Preview feedback prompt-injection hygiene is also covered: prompt-override text is stripped before
  CustomerService memory observation or next-turn ChatbotService context (`b5e49ae`).
  Release evidence still needs focused coverage for the highest-risk customer tools — upload registration,
  DFM acknowledgement, checkout details, payment start, project resume, project summary, order creation —
  proving schema match and customer-vs-employee authorization scoping.
- **P1 — Payment duplicate/idempotency release evidence (gap 10 residual)**: QuoteEngine now has focused
  relay tests for completed, pending, failed, expired, cancelled, status-update failure, group routing,
  and null payloads (`402c468`). The remaining proof is cross-service duplicate-webhook/idempotency:
  PaymentService should not emit duplicate terminal side effects, and OrderService must keep repeated
  payment/status writes idempotent.

## Validation lanes (per AGENTS.md)

- Focused builds/tests for any touched repo before commit.
- The acceptance proof for the linkage gaps is the Aspire E2E passing with the project-part↔order↔job
  assertion — not unit tests alone.
- Commit each validated slice separately (Aspire SDK bump; Aspire E2E assertion; this report).
