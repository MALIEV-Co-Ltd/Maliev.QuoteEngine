# Quote Agent tool-callback wiring — design

**Date:** 2026-06-16
**Goal context:** QuoteEngine MVP → "Quote Agent happy-path E2E" → first slice: *unblock tool callbacks*.
**Spans repos:** `Maliev.Aspire`, `Maliev.ChatbotService` (the QuoteEngine BFF side is already correct).

## Problem

When the Quote Agent tries to run any tool (e.g. *Get Project Summary*, calculate estimate,
create order), the call fails with:

```
Error Executing Get Project Summary: No Such Host Is Known (Quoteenginebff:443)
```

Because every happy-path stage past chat intake (estimate → quote → approve → order →
payment) runs through these tool calls, the whole pipeline stalls immediately after intake.

## Root cause

ChatbotService executes QuoteEngine tools in `QuoteEngineToolHandler`, which posts to a named
HttpClient `"QuoteEngineBff"`:

```csharp
var client = httpClientFactory.CreateClient("QuoteEngineBff");
// POST /quote/v1/agent/tools/{toolName}  (relative path → uses client.BaseAddress)
```

`AddServiceClient("QuoteEngineBff")` (Maliev.Aspire.ServiceDefaults `HttpClientExtensions`)
sets the base address from, in order:

1. explicit `Services:QuoteEngineBff:BaseUrl` config, else
2. `https+http://QuoteEngineBff` resolved by `.AddServiceDiscovery()`.

In ChatbotService's environment **neither is satisfied**: the Aspire AppHost registers
`ChatbotService` with references to ~25 services but **not** `quoteEngineBff`, so the
`services__quoteenginebff__*` discovery vars are never injected, and there is no explicit
`Services:QuoteEngineBff:BaseUrl`. The client falls back to the literal host `QuoteEngineBff`,
which DNS cannot resolve → `No Such Host (Quoteenginebff:443)`.

The QuoteEngine BFF already references ChatbotService (so the BFF can call it); only the
reverse edge is missing.

## Design

Three changes, none in the QuoteEngine repo:

1. **Local / Aspire** — in `Maliev.Aspire.AppHost/AppHost.cs`, add `.WithReference(quoteEngineBff)`
   to the `ChatbotService` registration block (before the health-check call). **No `.WaitFor`** —
   `quoteEngineBff` already references `chatbotService`, and adding a `WaitFor` on the reverse edge
   would create a startup-ordering cycle. `WithReference` alone (endpoint/discovery injection) is
   safe bidirectionally.
2. **AppHost test** — add `AppHost_ChatbotService_ReferencesQuoteEngineBff` to
   `Maliev.Aspire.Tests/AppHostReferenceTests.cs`, asserting `.WithReference(quoteEngineBff)` exists
   in the ChatbotService block (slice from `var chatbotService = WithSharedSecrets(` to
   `var projectService = WithSharedSecrets(`), following the existing per-block pattern.
3. **Prod / GKE** — set `Services__QuoteEngineBff__BaseUrl` in ChatbotService's deploy config to the
   QuoteEngine BFF internal URL, matching how ChatbotService's other downstream URLs are supplied.
4. **Defensive diagnostic** — in `QuoteEngineToolHandler`, when the resolved base address is
   missing/relative, return a structured, actionable error (and log a clear warning) instead of
   letting a raw DNS exception bubble into the customer chat. Add a unit test in
   `QuoteEngineToolHandlerTests`.

## Approaches considered

- **(A)** Wiring fix only (1–3). Correct but a future misconfig would again surface as a cryptic
  DNS error in chat.
- **(B, chosen)** A + the defensive diagnostic (4): fail loud and legible.
- **(C, rejected)** Have the BFF advertise the tool-callback base URL per request (like the existing
  thinking-step `CallbackUrl`) and have the tool handler prefer it. Rejected: the BFF's
  `Request.Host` is the browser/gateway host, not reliably reachable from ChatbotService internally
  (thinking callbacks are gated off by default for this reason). Service discovery is the correct
  channel.

## Non-goals

- Any change in the QuoteEngine repo (its agent tool surface + context-token auth are correct).
- The broader downstream-service E2E (Pricing/Quotation/Order/Payment live wiring) — later slices.
- Graceful in-chat degradation on tool failure — overlaps the separate "harden gating & UX" slice.

## Acceptance & verification

- `Maliev.Aspire` builds; `AppHostReferenceTests` (incl. the new test) green.
- `Maliev.ChatbotService` builds; `QuoteEngineToolHandlerTests` (incl. the new diagnostic test) green.
- **Live round-trip** (agent actually executes a QuoteEngine tool against the BFF) confirmed by the
  user in the running Aspire/GKE environment — not verifiable in this workspace without the full
  orchestration (all services + RabbitMQ/Redis/DBs).

## Risks

- *Bidirectional `WithReference` cycle* — mitigated by omitting `WaitFor` on the new edge.
- *Test brittleness* — `AppHostReferenceTests` is a source-snapshot suite; the new assertion is
  pinned to the block-slice pattern already used for other services.
