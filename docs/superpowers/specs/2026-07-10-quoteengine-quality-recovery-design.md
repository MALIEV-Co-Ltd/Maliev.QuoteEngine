# QuoteEngine quality recovery design

**Date:** 2026-07-10
**Primary surface:** `Maliev.QuoteEngine`
**Related services:** `Maliev.ChatbotService`, `Maliev.PricingService`, `Maliev.AuthService`, `Maliev.Intranet`, `Maliev.Web`, `Maliev.RegistryService`, `Maliev.SearchService`, `Maliev.ProjectService`, `Maliev.DeliveryService`, and the existing `Maliev.GeometryService` browser runtime contract.

## Outcome

The Make Studio experience behaves like one coherent technical workspace:

- the composer has deterministic voice, upload, ready, and sending states;
- uploaded and sent files use one square thumbnail language and open the shared workbench;
- navigation, summaries, projects, auth, and workbench surfaces use compact regular-weight typography with contrast hierarchy;
- model tool calls are never shown as assistant prose, tool results always re-enter the model loop, and the final answer is grounded against server state;
- prices, formal quotes, shipping choices, package data, and addresses cannot be claimed unless their authoritative state exists;
- Google entry points are rendered by Google Identity Services (GIS), while MALIEV owns the resulting secure application session;
- customer project search is owner-scoped before SearchService results can be exposed to QuoteEngine.

## Evidence-backed root causes

1. Gemini can emit compact textual calls such as `tools.quotecalculateestimate()` and `tools.quotegetshippingrates(...)`. `LeakedToolCallParser` only canonicalizes declared underscore names, so these calls are returned as final assistant text instead of executed.
2. QuoteEngine's `StripToolTraces` only recognizes headers such as `Tool Call:`; it does not remove a bare `tools.quote...` invocation.
3. `GroundAssistantText` protects viewer/auth claims but not estimate amounts or formal-quote artifact claims.
4. PricingService applies `MinimumOrderPriceFloor` as a per-unit floor and then multiplies by quantity. The domain field is an order floor, so this makes larger quantities artificially more expensive.
5. Runtime logs also show PricingService returning zero because current MaterialService IDs do not match the seeded pricing-configuration IDs. Stable material/process codes exist in the request but are not available on `PricingConfiguration` for a safe fallback lookup.
6. QuoteEngine's development prototype estimator repeats setup cost per unit instead of amortizing it across the line quantity.
7. QuoteWorkspace copies an immutable `Uploading` attachment into the shell. Its failure path updates only the part model, so the composer can remain permanently disabled.
8. Immediate object URLs exist only for images. The GeometryService browser worker can extract mesh buffers for STL, OBJ, 3MF, glTF, and GLB, while the host renderer needed to create a PNG exists only in Intranet.
9. Shipping DTOs already preserve courier logo, package weight, dimensions, and package lines; QuoteAgent drops those fields and displays every SHIPPOP product as a generic confirmation action.
10. Registry lookup exists, but shipping-rate execution does not independently require an exact Thai district/province/postcode match. The Google grounding trigger also misses common Thai address-component language.
11. Project detail and customer-scoped ProjectService search already exist, but the Projects page consumes neither. SearchService project documents currently have no customer owner field and are unsafe for customer search.
12. The three apps use valid server-side Google OAuth handlers, but render imitation Google buttons. AuthService's exchange endpoints trust caller-asserted Google identity fields instead of independently validating an ID token.

## Chosen architecture

### 1. Composer and file state

Model the submit slot from state, not styling:

```text
uploading -> disabled Uploading
sending   -> disabled spinner
empty + voice available + no ready attachments -> Use voice waveform
otherwise -> Send
```

The waveform reuses the existing dictation pointer/keyboard lifecycle. Attachment-only turns retain a Send control. Upload changes update the shell by stable client/upload/storage identity, including failed and cancelled states.

Create one visual file-tile projection for composer attachments, sent-message attachments, uploaded files, Drive imports, and generated artifacts. Tiles are square, show a pending blur/pulse/spinner, put the filename below the preview, and reveal remove/actions on hover or keyboard focus. Clicking a sent tile selects the matching uploaded part/artifact and opens the workbench.

### 2. Browser thumbnail boundary

Port Intranet's proven Three.js host rendering pattern to QuoteEngine and continue using the GeometryService runtime manifest and `extract_mesh` worker operation. Mesh formats receive an immediate transparent PNG thumbnail. STEP/STP/IGES/IGS remain in an explicit pending state until server tessellation/GLB arrives; the UI must not pretend those formats are locally rendered.

### 3. Workspace information architecture

The rail uses two-line project rows: name/actions on the first line, status/date on the second. Chats start collapsed and use chat-specific iconography. Per-project action state disables only the active operation and renders a spinner.

The artifact drawer becomes the shared Workbench file explorer. A single list contains local uploads, Drive imports, sketches, and generated artifacts, with type/status/source metadata and local/Drive add actions. The preview remains the selected-item surface; internal statuses such as `DfmAnalysisReady` are mapped to customer copy.

The Projects page adds a debounced search field and expandable project rows. Expansion loads the existing customer-owned detail endpoint, caches the result, and shows parts/files, creation/update dates, project status, quote/order references when present, and explicit empty document states when a quote, invoice, or receipt does not yet exist.

### 4. Agent execution and truth boundary

ChatbotService canonicalizes leaked function names by comparing an alphanumeric lowercase key (`tools.quotecalculateestimate` -> `quote_calculate_estimate`) against declared tools. It preserves the canonical declaration name, executes it, appends a native function-response turn, and requires a subsequent model answer. Compact argument aliases used in observed shipping calls are normalized at the QuoteEngine boundary.

QuoteEngine remains the final customer-output guard:

- bare tool syntax is removed;
- estimate amounts described as totals or unit prices must match `QuoteAgentStateResponse.Estimate`;
- a formal quote may be described as available only when a `formal_quote` artifact exists;
- a pending `formal_quote` action is described as awaiting confirmation, never as an existing file;
- when sanitization leaves no usable answer, a deterministic state-derived answer is returned.

### 5. Pricing boundary

PricingService treats minimum order price as a line/order total:

```text
raw total = surcharged unit price * quantity
floored total = max(raw total, minimum order price)
returned unit price = floored total / quantity
```

Pricing configurations gain stable `MaterialCode` and `ManufacturingProcessCode` keys. Exact IDs remain the first lookup; a code fallback is accepted only for an active unambiguous configuration and is audited. QuoteEngine's development estimator amortizes setup once per line and labels prototype output in tool/state metadata.

### 6. Address and shipping boundary

Before calling DeliveryService, QuoteEngine validates the destination's subdistrict/district/province/postcode combination against RegistryService. A mismatch returns structured suggestions and does not fetch rates. ChatbotService enables Google grounding for Thai address component language and instructs the model to reconcile public-place grounding with registry results.

Rate presentation filters non-positive, food/fresh/frozen/fruit-specific, and inapplicable oversize products; deduplicates equivalent courier products; and limits the initial list to a compact useful set. Selection cards include the courier logo, price, lead time, total package weight, box dimensions, and package count. Clicking a card selects it directly; shipping choices are not shown under a generic durable-confirmation heading and do not expose the gateway brand.

### 7. Google identity boundary

All three apps load `https://accounts.google.com/gsi/client` and let GIS render a standard large button with FedCM enabled. The personalized returning-account state is therefore owned by Google, not simulated by MALIEV.

GIS posts `credential` and `g_csrf_token` to each app's same-origin BFF. The BFF verifies the double-submit CSRF token and forwards the credential over the authenticated service boundary. AuthService validates Google's signature, audience, issuer, expiry, `email_verified`, `sub`, and (for Intranet employees) `hd`; it then creates the existing MALIEV session/token response. Existing server OAuth routes remain as compatibility fallbacks until the GIS paths are deployed everywhere.

### 8. Search ownership boundary

SearchService project documents gain `OwnerType` and `OwnerId`. ProjectService publishes/reindexes those fields from the owning customer. QuoteEngine queries only with the authenticated customer ID and then hydrates candidate project IDs through ProjectService ownership checks before returning them. No ownerless SearchService hit is customer-visible.

## Visual language

- Use weights 400 and 500 for the Make Studio shell; reserve 600 for the MALIEV wordmark and exceptional numeric emphasis.
- Use full-contrast ink for primary labels and muted ink for supporting text rather than heavier glyphs.
- Prefer filled/tonal surfaces, whitespace, and shadow over nested borders.
- Use compact 32-36 px controls, 8-12 px internal gaps, and stable square media previews.
- Animate state changes with opacity and transform/grid expansion; respect `prefers-reduced-motion`.
- Hover-revealed actions remain reachable with `:focus-within`, have accessible names, and never rely on color alone.

## Error handling

- Upload failure replaces the pending snapshot and re-enables send; the failed tile remains removable and announces the error.
- Thumbnail failure falls back to a neutral CAD tile and never fails the upload.
- Tool recovery executes only a name matching an allowlisted declared tool and remains capped by the existing per-turn limits.
- Pricing configuration ambiguity returns no price rather than selecting an arbitrary configuration.
- Registry mismatch blocks rate retrieval and returns customer-safe correction options.
- GIS verification failure creates no session and returns a generic sign-in error without logging the token.
- Project detail/search failures preserve the list and show an inline retry state.

## Acceptance matrix

| Comments | Proof |
| --- | --- |
| 1, 6, 7, 10 | Composer behavior tests plus browser pending -> thumbnail -> ready -> sent -> selected journey |
| 2, 3, 4, 5, 11, 13 | Desktop/mobile browser screenshots, keyboard checks, reduced-motion check, CSS diff review |
| 8, 9, 20 | Project ownership/action endpoint tests plus browser rail and expandable-project journeys |
| 12, 14, 15, 18 | Chatbot leaked-call red/green tests, QuoteEngine grounded-output tests, live/fake tool-loop test |
| 16, 17, 19 | Shipping/action/helper tests and browser workbench/preview/selection checks |
| Google sign-in | GIS markup/config tests, CSRF/token validation tests, success/failure/hd/session tests in every touched repo |
| SearchService | owner-filter query tests, ProjectService index mapping tests, QuoteEngine ownership hydration tests |

## Commit boundaries

1. Design and plan only.
2. ChatbotService textual-tool recovery.
3. PricingService minimum-order and stable configuration lookup.
4. QuoteEngine agent truth, registry, and shipping contracts.
5. QuoteEngine composer/thumbnail/file-tile behavior.
6. QuoteEngine navigation, workbench, projects, and visual language.
7. AuthService Google credential verification.
8. One GIS integration commit in each of QuoteEngine, Intranet, and Web.
9. Owner-scoped SearchService and ProjectService indexing commits, followed by QuoteEngine consumption.

## Approaches considered

- **Surface patch:** CSS plus prompt wording. Rejected because it leaves tool calls unexecuted and facts ungrounded.
- **QuoteEngine-first state and contract repair (chosen):** fixes the visible product while strengthening only the service boundaries proven deficient.
- **Shared workspace/auth rewrite:** rejected for this pass because it expands risk without improving the reported flows faster.

