# Agent 3D Preview — Robustness, Telemetry & Best-Effort Fidelity

**Date:** 2026-07-03
**Status:** Approved design, pending spec review
**Supersedes operational assumptions in:** `2026-06-17-agent-3d-preview-generation.md` (that spec described a server-side SharpGLTF/GLB + BabylonJS design; the shipped implementation instead uses **agent-emitted CAD commands → client-side replicad (OpenCASCADE WASM) web worker → three.js**. This spec targets the shipped architecture.)

---

## Problem

The agent's inline 3D preview generator is not robust and not faithful:

1. **It dead-ends in an error state.** A "fix the design" request produced `number 25 is not iterable (cannot read property Symbol(Symbol.iterator))`. The preview showed an error with a "Retry preview" button that re-runs the **identical** commands, so it fails identically forever. The generation is left hanging.
2. **It produces low-fidelity output.** A hand-sketch of a "Hand" keychain rendered as a generic square box with a hole.
3. **Thumbs feedback has no aggregate view.** Thumb up/down already loops into per-customer memory to self-correct, but there is no cross-session performance signal to inform a keep-vs-replace decision on the whole feature.

### Root causes (confirmed by code trace)

- **Dead-end error:** CAD commands that pass server-side validation can still throw *inside the replicad worker* (client-side). Prime suspect: `buildProfile` in `Maliev.QuoteEngine.Client/js-src/replicad-worker.js:199-202` calls `sketch.threePointsArc(p[0], p[1], p[2], p[3])` with four scalars; if replicad expects point **arrays** there, an `arc` segment (which a curved "hand" redesign naturally introduces) throws exactly the non-iterable error. `ValidateCadCommands` validates profile presence/height but **not** per-segment arc arity/shape, so the bad command ships to the client. `RetryPreview()` re-runs identical commands, guaranteeing repeat failure.
- **Low fidelity:** When the external agent returns no usable content on a preview request, the BFF's hardcoded `TryGenerateFallbackPreview` (`Maliev.QuoteEngine.Bff/Services/QuoteAgentService.cs:8314`) **always** emits a box-with-4-corner-holes regardless of the request. A single brittle special-case (`BuildHandKeychainPreviewCommands`, fires only on literal "hand"+"keychain" **and** an emitted box) is the only organic-shape handling, and it missed this case.
- **No aggregate telemetry:** `RecordPreviewFeedbackAsync` writes artifact metadata + per-customer memory + one info log, but emits no metric. `BffMetrics` exists but has no preview counters.

---

## Constraints & context (what this repo controls)

- The `quote_generate_3d_preview` tool is **executed** here (`ExecuteToolAsync`) but its **input schema, parameter descriptions, and few-shot examples live in the external ChatbotService** (`ChatbotServiceClient` advertises no tool schema). We cannot rewrite the schema; we *can* influence the agent via the natural-language guidance in `ComposeAgentMessage`.
- **There is no server-side geometry build.** replicad is WASM-in-browser; the BFF's `client-geometry-runtime.worker.js` only analyzes uploaded meshes for DFM. So "validate by building" is unavailable server-side. Robustness must come from (a) tighter static validation + coercion and (b) a client-side graceful-degradation guarantee — not build-to-validate.
- **Honesty caveat:** faithful "sketch → matching 3D shape" is bounded by the external LLM + the primitive CAD vocabulary. Workstream 4 *improves* fidelity; it does not *guarantee* it. The durable deliverables are: never dead-ends, honest previews, and telemetry to make the keep/replace call with data.
- **Source-pinning tests:** `QuoteEngineSourceTests.cs` and `QuoteAgentArtifactSourceTests.cs` assert exact CSS/markup/JS substrings. Every UI/JS edit here must update the corresponding pins and be verified visually.

---

## Approach

**Harden the existing CAD-commands → replicad pipeline.** Two alternatives were considered and rejected:

- **Build server-side to validate** — not viable without adding a JS/WASM runtime (Node sidecar / Jint) to the .NET BFF; too heavy for the payoff.
- **Swap to image-based previews** — an image model renders "a hand keychain" easily but yields a *picture*, not a manufacturable 3D file. The feature's goal is to create 3D files for customers, so this fails the goal. (It remains the "move to something else" option the new telemetry would inform.)

---

## Workstream 1 — Robustness core ("must not hang in error")

Two layers; **reproduction-first.**

**1a. Reproduce the failing command class (test-first).**
Add a focused test that feeds an `arc`-bearing (and `bezier`-bearing) profile through the client worker path / a Node harness to confirm the exact throw. Verify replicad's real `threePointsArc` / `quadraticBezierCurveTo` / sketch-point signatures via Context7 (`replicad`) before changing arg-passing. The reproduction pins the true culprit rather than guessing.

**1b. Fix arg-passing in the worker so it can never hand replicad a non-iterable.**
In `replicad-worker.js`, correct the segment→replicad call shapes (e.g. pass point arrays where replicad wants points), and wrap each segment/command translation so a bad shape throws a **descriptive, catchable** error (`Profile segment 'arc' ...`) instead of an opaque `25 is not iterable`. The client `quote-inline-viewer-three.js` already normalizes scalar→array for `params`/`size`; extend `normalizeNumericArray` coverage to any remaining fields the worker spreads.

**1c. Reject residual un-buildable commands at generation time (server).**
Extend `ValidateCadCommands` to validate **per-segment arity/shape** for every profile segment type (`move`/`line`/`hLine`/`vLine`/`arc`/`bezier`), matching the worker's `requireSegmentParams` contract, plus any newly-identified class from 1a. A validation failure returns `{ error }` from `Generate3DPreview` → surfaced to the agent tool call → **agent regenerates in-turn**. This means malformed geometry is never stored as a viewer artifact, so it never reaches the client.

**Guarantee:** after 1b+1c, the only way to reach the client is a buildable command set; and if replicad still fails for an unforeseen reason, Workstream 2 ensures no dead-end.

---

## Workstream 2 — Client safety-net (graceful degradation)

`QeInlinePartViewer` today catches `JSException`, shows an error, and offers a useless identical retry.

- Add an `EventCallback<PreviewBuildOutcome>` `OnBuildOutcome` parameter. On success/failure, the viewer reports `{ success, errorClass, errorMessage }` up to `QuoteAgentLaunchShell`.
- Replace the "Retry preview" button behavior: instead of re-running identical commands, the recovery action is **"Ask the agent to rebuild"** — the shell (which owns `SessionId`, the failing `ArtifactId`, and the chat send path) sends a regeneration request to the agent, including the error class, so the next turn produces new geometry. (Retry-identical is removed; it is theater for deterministic failures.)
- The shell reports the build outcome to the BFF for telemetry (Workstream 3) via a small endpoint: `POST /quote/v1/agent/sessions/{sessionId}/artifacts/{artifactId}/preview-build` with `{ success, errorClass }`.

`errorClass` is low-cardinality (e.g. `worker_unavailable`, `build_timeout`, `invalid_geometry`, `empty_mesh`, `other`) derived from the error message — never the raw message (cardinality/PII hygiene).

---

## Workstream 3 — Telemetry (keep-vs-replace signal)

Add to `BffMetrics` (existing `quote-engine` meter), **keeping the existing self-correction memory loop intact**:

| Counter | Tags | Emitted from |
|---|---|---|
| `quote_agent_preview_generations` | `outcome` = `generated` \| `validation_rejected` \| `fallback_used`, `process_family` | `Generate3DPreview` / `TryGenerateFallbackPreview` |
| `quote_agent_preview_build_outcomes` | `outcome` = `success` \| `build_failed`, `error_class` | new `preview-build` report endpoint (Workstream 2) |
| `quote_agent_preview_feedback` | `sentiment` = `up` \| `down` | `RecordPreviewFeedbackAsync` |

Derived signals: **build failure-rate** (robustness health) and **thumbs-up-rate** (fidelity/satisfaction) — the decisive keep-vs-replace metrics. Tag values are validated against fixed allow-lists to keep cardinality bounded. Structured info logs remain for traceability.

---

## Workstream 4 — Fidelity (best-effort, sequenced last)

- **Prompt guidance:** In `ComposeAgentMessage`, strengthen the preview guidance to steer the agent toward **profile-`extrude` silhouettes** (outline via `move`/`line`/`arc` segments) for organic/outline shapes (hands, letters, logos, animals), and away from defaulting to `box`. Explicitly instruct: for a recognizable object, emit an extruded 2D silhouette, not a bounding primitive.
- **Silhouette registry:** Replace the one-off `BuildHandKeychainPreviewCommands` + `ShouldRewriteBoxLikeHandKeychainCommands` with a small **keyword → parametric-silhouette** registry (starting set: hand, star, heart, letter/initial, plus "generic tag"). `NormalizeKnownGeneratedPreviewCommands` consults the registry so a box-with-known-keyword becomes the matching silhouette. This generalizes the existing band-aid instead of adding more one-offs.
- **Honest fallback:** When `TryGenerateFallbackPreview` must emit a generic shape, label the artifact/description as a **placeholder** ("rough placeholder — tell me the shape to refine it") so a hand request never *silently* becomes a box. If the message keywords match a registry silhouette, the fallback uses it instead of the box.

---

## Data flow (after this work)

```
Customer: "make a Hand keychain" (+ sketch)
  → agent calls quote_generate_3d_preview { description, cad_commands|primitives }
  → BFF Generate3DPreview:
       NormalizeCadCommandsForBrowserWorker (coerce fields)
       NormalizeKnownGeneratedPreviewCommands (silhouette registry)   [W4]
       ValidateCadCommands (now incl. per-segment arity)              [W1c]
         ├─ invalid → { error } → agent regenerates in-turn (no client dead-end)
         └─ valid   → store viewer artifact (cad_commands) + metric generations:generated  [W3]
  → Client QeInlinePartViewer builds via replicad worker (fixed arg-passing) [W1b]
       ├─ success → render + report build:success                     [W2/W3]
       └─ failure → honest message + "Ask agent to rebuild" + report build:build_failed(error_class) [W2/W3]
  → Thumbs up/down → RecordPreviewFeedbackAsync → memory loop (kept) + metric feedback:up|down  [W3]
```

---

## Testing

**Unit / integration (BFF, xUnit):**
- `ValidateCadCommands` rejects malformed `arc`/`bezier`/segment-arity commands (the reproduction case from 1a) with a clear agent-facing error.
- `NormalizeCadCommandsForBrowserWorker` coerces newly-covered fields.
- Silhouette registry: known keyword → extruded silhouette (not a box); unknown → honest placeholder.
- `Generate3DPreview` / `TryGenerateFallbackPreview` emit the correct `generations` metric outcome.
- `RecordPreviewFeedbackAsync` emits the `feedback` metric **and** still writes the memory loop (regression guard).
- New `preview-build` endpoint records `build_outcomes` with a validated `error_class` and rejects unknown classes.
- `BffMetrics` counters assert tag allow-lists (cardinality guard).

**Client (worker/JS + component):**
- Worker reproduction test (1a) goes from throwing `25 is not iterable` → building a valid mesh or throwing a descriptive, classified error.
- `QeInlinePartViewer` raises `OnBuildOutcome` on success and failure; failure surfaces the "Ask agent to rebuild" recovery (not identical-retry).

**Source-pin tests:** update `QuoteEngineSourceTests.cs` / `QuoteAgentArtifactSourceTests.cs` for changed CSS/markup/JS substrings; verify the inline viewer + recovery UI visually.

**Full suite:** `dotnet build` (zero warnings) then `dotnet test` green before completion.

---

## Files changed (anticipated)

| File | Change |
|---|---|
| `Client/js-src/replicad-worker.js` (+ rebuilt `wwwroot/js/replicad-worker.bundle.js`) | Fix segment→replicad arg-passing; descriptive classified errors [W1b] |
| `Client/wwwroot/js/quote-inline-viewer-three.js` | Extend `normalizeNumericArray` field coverage [W1b] |
| `Bff/Services/QuoteAgentService.cs` | Per-segment validation [W1c]; silhouette registry + honest fallback [W4]; prompt guidance [W4]; generation + feedback metrics [W3] |
| `Bff/BffMetrics.cs` | 3 new counters + tag allow-lists [W3] |
| `Bff/Controllers/AgentController.cs` | New `preview-build` report endpoint [W2/W3] |
| `Shared/Agent/QuoteAgentDtos.cs` | `PreviewBuildOutcome` request/response DTO [W2] |
| `Client/Components/QuoteAgent/QeInlinePartViewer.razor` | `OnBuildOutcome` callback; remove identical-retry [W2] |
| `Client/Components/QuoteAgent/QuoteAgentLaunchShell.razor` | Handle build outcome, report telemetry, drive "rebuild" recovery [W2] |
| `Tests/*` | New + updated tests; source-pin updates |

---

## Sequencing

**1 (robustness) → 3 (telemetry) → 2 (client safety-net) → 4 (fidelity).** Robustness + telemetry land the must-have guarantee and the decision data first; the client safety-net closes the residual dead-end; fidelity is the incremental, external-bound improvement last.

---

## Out of scope

- Rewriting the external `quote_generate_3d_preview` tool schema/examples (owned by ChatbotService).
- Server-side geometry building / CSG boolean holes beyond current support.
- Image-based or ML sketch-to-3D previews (the "move to something else" path telemetry would inform).
- A metrics dashboard/DB table — counters are emitted to the existing metrics backend for querying.
