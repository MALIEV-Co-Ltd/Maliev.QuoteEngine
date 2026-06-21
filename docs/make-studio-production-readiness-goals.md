# Make Studio Production Readiness Goals

_Created 2026-06-21 as the finite gate list for the QuoteEngine / Make Studio deployment objective._

## Scope Boundary

This gate is for **Make Studio**: the authenticated customer manufacturing quote workflow in `Maliev.QuoteEngine`, its `Maliev.ChatbotService` agent channel, and the downstream service path into quotation, order, payment, project, job, and Intranet visibility.

This gate does **not** require every wider MALIEV customer-portal story to be complete. Email verification, password-reset email delivery, NDA lifecycle, generic document management, broad Web storefront checkout, and unrelated Intranet modules remain separate product gates unless they block Make Studio.

## Completion Rule

Make Studio is production-ready only when every **P0** and **P1** gate below is either:

- **Passed** with named automated evidence and a committed change set; or
- **Explicitly accepted** as a documented product risk with owner, reason, and rollback/operational mitigation.

P2 items are deployment-adjacent hardening/backlog and must not be hidden, but they do not block the Make Studio launch unless Product or Security raises them to P1.

## Gate Matrix

| Gate | Priority | Definition of Done | Current Evidence | Status |
|---|---:|---|---|---|
| G1. Launch UX and sign-in panel | P1 | `/quote/new` opens to a centered "What do you want to make today?" prompt; left-panel sign-in card is compact; no mobile/desktop overlap. | `Maliev.QuoteEngine` commit `73ac32a`; QuoteEngine build passed. | Passed |
| G2. Auth handoff and active chat restore | P0 | Login/sign-up returns to the active Make Studio chat; signed customer chat messages/workbench state restore after redirect/refresh. | Aspire tests `QuoteEngine_MakeStudioAgent_LoginRedirectRestoresActiveChat`, `QuoteEngine_MakeStudioAgent_RestoresSignedCustomerConversation`, and full Make Studio E2E restore assertions. | Passed |
| G3. Attachment and hand-sketch handling | P0 | Sketch/image/document attachments do not trigger Gemini invalid `fileData` failures; uploaded media is passed only in supported forms; unlabeled sketches ask for dimensions instead of fabricating geometry. | `Maliev.ChatbotService` commit `3035357`; Chatbot Gemini serialization tests; QuoteEngine attachment stream tests; system prompt/tool schema wording. | Passed |
| G4. ChatbotService tool contract | P0 | QuoteEngine channel exposes only customer-safe `quote_*` tools; tool declarations match BFF dispatch; every write tool is confirmation-gated; missing QuoteEngine context fails closed; transport failures return safe tool errors. | `ToolRegistryTests`, `QuoteEngineToolHandlerTests`, `SystemInstructionDefaultPromptTests`, BFF `AgentController` signed context-token requirement. | Mostly passed; P1 residual is negative replay/auth-scope release coverage for high-risk write tools. |
| G5. Generated preview compatibility | P1 | Model-emitted CAD commands with common aliases, numeric strings, shorthand primitives, object params, and square/plate/hole/slot/boss/standoff shapes are accepted or produce safe clarification. | QuoteEngine preview commits through `065bb24`; ChatbotService schema commit `f379013`; focused tests in both repos. | Passed |
| G6. Upload, DFM, and corrected reupload | P0 | Real/customer-scoped uploads are accepted; DFM-blocked revision blocks progression; corrected reupload supersedes the old revision; stale pricing/quote/order/payment artifacts are cleared. | QuoteEngine focused tests for upload ownership, DFM acknowledgement, supersede, and commercial reset; full Aspire Make Studio E2E exercises blocked upload and corrected reupload. | Passed |
| G7. Pricing and formal quote | P0 | Estimate uses PricingService in production with no prototype fallback; formal quote is immutable, versioned, PDF-backed, and owner-scoped. | QuoteEngine pricing no-fallback tests; formal quote artifact download owner/path tests; full Aspire Make Studio E2E formal quote and PDF artifact assertions. | Passed |
| G8. Checkout, payment, and order creation | P0 | Checkout uses customer-owned billing/shipping addresses; order creation is confirmation-gated; payment handoff uses QuoteEngine return URLs, idempotency, and fail-closed error handling. | QuoteEngine checkout/payment tests; PaymentService client contract tests; Aspire commit `16fb9f8` seeds real CustomerService addresses; full Make Studio E2E passes. | Passed |
| G9. Payment event and duplicate safety | P0 | Payment completion updates customer-visible order tracking; duplicate provider/completed events do not republish terminal events or create duplicate paid transitions. | QuoteEngine `PaymentNotificationConsumerTests`; PaymentService commit `c0eef0d`; OrderService duplicate completed-payment test; full paid-order Aspire path passed. | Passed |
| G10. Paid order to production and Intranet | P0 | Payment completion creates/links production jobs; ProjectService parts expose `orderId`, `orderItemId`, and `jobId`; Intranet sees the paid order and linked project parts. | Aspire `QuoteEngine_MakeStudioAgentTools_CorrectsDfmCompletesPaymentAndLinksProductionJob` passed on 2026-06-21 with TRX `make-studio-e2e-20260621-addresses.trx`. | Passed |
| G11. Customer order status tracking | P0 | Customer can see order status, payment status, manufacturing milestone, and restored workbench payment/order artifact after payment. | Full Aspire Make Studio E2E order page, project summary, and restored workbench assertions. | Passed |
| G12. Customer and artifact isolation | P0 | Cross-customer access to quotes, orders, PDFs, project artifacts, uploaded attachments, and documents is denied or not found. | Aspire cross-customer quote/order/document tests; QuoteEngine owner-scoped PDF/download tests; analysis-status ownership tests. | Passed for Make Studio critical artifacts; keep generic portal routes covered separately. |
| G13. Production backing/fail-closed behavior | P0 | Production does not silently use `QuoteEnginePrototypeStore` for mutating/money/order paths; production service unavailability surfaces a controlled error. | QuoteEngine production no-fallback tests for upload, project detail, estimate, agent estimate/profile/project management; readiness source audit. | Passed for current Make Studio flow; recheck before release config freeze. |
| G14. Observability and deploy evidence | P1 | Release candidate has named build/test/E2E evidence, clean worktrees, committed run notes, and no unexplained stale service/process blockers. | Latest run notes in `Maliev.Aspire.Tests/specs/E2E_USER_JOURNEY_RUN_RESULTS.md`; focused builds/tests across touched repos. | Mostly passed; final release candidate should rerun the green E2E once after config freeze. |

## Remaining Work Before Calling The Goal Complete

1. **Add a focused P1 auth/replay test slice** for highest-risk tool actions: `quote_acknowledge_dfm`, `quote_update_checkout_details`, `quote_create_order`, `quote_start_payment`, `quote_request_employee_review`, `quote_resume_project`, and `quote_get_project_summary`.
2. **Run final release-candidate validation** after any remaining edits: focused QuoteEngine/ChatbotService tests, focused Aspire Make Studio E2E, and build checks for touched repos.
3. **Write final production-readiness report** with pass/fail status for G1-G14, commits, commands, residual accepted risks, and rollback/monitoring notes.

## Product Owner Review

**Phase Alignment:** Phase 2 QuoteEngine readiness, with Phase 1 Intranet/production handoff dependencies.

**Business Logic Check:**

- 3-Second Rule: Applicable to Intranet follow-up surfaces, not customer chat wait time. The Intranet paid-order/project visibility gate is covered; shop-floor mobile execution remains outside this Make Studio launch gate.
- Determinism Rule: Passed for Make Studio pricing only if PricingService remains the production price source and prototype fallback stays disabled in production.
- 2x Markup Rule: Not directly exercised by the Make Studio FDM/PLA E2E; keep outsourced/China Switch pricing as a separate pricing release gate.

**Completeness Check:** Complete for Make Studio quote-to-paid-production readiness. Broader customer portal gaps are explicitly out of scope unless they block this flow.

**Technical Plan Alignment:** The gates follow service ownership: QuoteEngine BFF orchestrates, ChatbotService owns agent model/tool transport, Pricing/Quotation/Order/Payment/Project/Job services own durable domain state, and Intranet reads service-owned project/order records.

**Verdict:** Collaborative Approval for this finite gate definition.
