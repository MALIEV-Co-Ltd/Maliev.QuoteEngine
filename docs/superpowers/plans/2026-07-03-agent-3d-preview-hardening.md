# Agent 3D Preview Hardening — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (inline) to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the agent's inline 3D preview never dead-end in an error, instrument build/feedback outcomes as keep-vs-replace telemetry, and improve organic-shape fidelity within external-agent limits.

**Architecture:** Agent-emitted CAD commands → BFF normalize/validate → client replicad (OpenCASCADE WASM) worker → three.js render. Fixes target the shipped pipeline: a confirmed worker arg-passing bug, defense-in-depth server validation, `BffMetrics` counters, a client build-outcome callback + regeneration recovery, and a keyword→silhouette registry.

**Tech Stack:** .NET 9 / Blazor WASM, xUnit, `System.Diagnostics.Metrics`, replicad 0.23, esbuild.

**Confirmed root cause (reproduce-first done):** replicad `lineTo(point: Point2D)` takes one `[x,y]` array; the worker passes two scalars at `replicad-worker.js:185,188,243`, so replicad throws `number <n> is not iterable`. Every line-based silhouette and every cone is affected. Server commands are valid (a `CadDoubleArrayJsonConverter` coerces scalar→array), so validation never caught it.

**Reference spec:** `docs/superpowers/specs/2026-07-03-agent-3d-preview-hardening-design.md`

**Execution order:** Task 1 (worker fix) → 2 (server validation) → 3 (telemetry) → 4 (client safety-net) → 5 (fidelity). Build + test green after each; commit each.

---

## Task 1: Fix worker `lineTo` arg-passing (the confirmed dead-end fix)

**Files:**
- Modify: `Maliev.QuoteEngine.Client/js-src/replicad-worker.js:185,188,243`
- Rebuild: `Maliev.QuoteEngine.Client/wwwroot/js/replicad-worker.bundle.js` (via esbuild)
- Test: `Maliev.QuoteEngine.Tests/QuoteAgentArtifactSourceTests.cs` (source-pin regression)
- Behavioral smoke: throwaway Node script (proves fix; not committed)

- [ ] **Step 1: Behavioral repro (prove the bug, then the fix).** Write `scratchpad/replicad-smoke.mjs` that `setOC`s the WASM, builds `new Sketcher('XY').movePointerTo([0,0]).lineTo([10,0]).lineTo([10,10]).close().extrude(5)` and meshes it. First run with the buggy `lineTo(10,0)` form to observe `... is not iterable`, then the `lineTo([10,0])` form to observe a non-empty mesh. Run: `node scratchpad/replicad-smoke.mjs`. (If WASM init is not feasible headlessly, record that and rely on Steps 2–5.)

- [ ] **Step 2: Fix the three call sites.**
  - `:185` `sketch.lineTo(p[2], p[3]);` → `sketch.lineTo([p[2], p[3]]);`
  - `:188` `sketch.lineTo(p[0], p[1]);` → `sketch.lineTo([p[0], p[1]]);`
  - `:243` `.lineTo(radiusBottom, 0)` → `.lineTo([radiusBottom, 0])`

- [ ] **Step 3: Rebuild the bundle.** Run: `npm run build-worker` in `Maliev.QuoteEngine.Client`. Expected: esbuild writes `wwwroot/js/replicad-worker.bundle.js`, exit 0.

- [ ] **Step 4: Add source-pin regression test.** In `QuoteAgentArtifactSourceTests.cs`, assert the worker source contains `lineTo([p[0], p[1]])`, `lineTo([p[2], p[3]])`, and `lineTo([radiusBottom, 0])`, and does NOT contain the scalar form `lineTo(p[0], p[1])`. (Mirror the file-reading pattern already used in that test class.)

- [ ] **Step 5: Build + test.** `dotnet test --filter FullyQualifiedName~QuoteAgentArtifactSourceTests`. Expected: PASS.

- [ ] **Step 6: Commit.** `git add` the worker source, bundle, and test → `git commit -m "fix: replicad worker lineTo expects a Point2D array, not two scalars"`.

---

## Task 2: Per-segment validation (defense-in-depth)

**Files:**
- Modify: `Maliev.QuoteEngine.Bff/Services/QuoteAgentService.cs` (`ValidateProfileCommand` / segment checks)
- Test: `Maliev.QuoteEngine.Tests/QuoteAgentEndpointTests.cs`

- [ ] **Step 1: Read `ValidateProfileCommand`** (grep in `QuoteAgentService.cs`) to extend without duplicating.
- [ ] **Step 2: Write failing test** — a `quote_generate_3d_preview` call whose profile has a `line` segment with a single param (arity 1) is rejected with a clear error mentioning the segment; a well-formed silhouette passes. Assert via the tool-execution/endpoint path used by existing tests.
- [ ] **Step 3: Run test — expect FAIL.**
- [ ] **Step 4: Implement** per-segment arity checks matching the worker's `requireSegmentParams` contract (`move`:2, `line`:2 or ≥4, `hLine`:1, `vLine`:1, `arc`:4, `bezier`:4); reject with `Profile segment '<type>' requires <n> parameter(s)`. Keep it bounded — do not reimplement replicad geometry.
- [ ] **Step 5: Run test — expect PASS**, then `dotnet build` (zero warnings).
- [ ] **Step 6: Commit** `fix: reject malformed profile segments before they reach the preview worker`.

---

## Task 3: Telemetry counters (keep-vs-replace signal)

**Files:**
- Modify: `Maliev.QuoteEngine.Bff/BffMetrics.cs` (3 counters + record methods, tag allow-lists)
- Modify: `Maliev.QuoteEngine.Bff/Services/QuoteAgentService.cs` (inject `BffMetrics metrics` into primary ctor; emit `generations` at the `ExecuteToolAsync` call site; emit `feedback` in `RecordPreviewFeedbackAsync`)
- Test: `Maliev.QuoteEngine.Tests/BffMetricsTests.cs` (new, using `MetricCollector<long>`)

- [ ] **Step 1: Confirm test deps** — check `Maliev.QuoteEngine.Tests.csproj` for `Microsoft.Extensions.Diagnostics.Testing` (provides `MetricCollector`). If absent, add the package (matching the pinned .NET version).
- [ ] **Step 2: Write failing `BffMetricsTests`** — `RecordPreviewGeneration("generated","fdm")`, `RecordPreviewBuildOutcome(false,"invalid_geometry")`, `RecordPreviewFeedback("up")` each emit one measurement with the expected tags; unknown tag values normalize to `other`/allow-listed fallback.
- [ ] **Step 3: Run — expect FAIL** (methods undefined).
- [ ] **Step 4: Implement** three counters on the existing `quote-engine` meter: `quote_agent_preview_generations` (tags `outcome`,`process_family`), `quote_agent_preview_build_outcomes` (tags `outcome`,`error_class`), `quote_agent_preview_feedback` (tag `sentiment`). Validate each tag against a fixed allow-list (reuse `NormalizeMarker`/`NormalizeProcessFamily`; add allow-list helpers for `outcome`/`error_class`/`sentiment`).
- [ ] **Step 5: Wire emission** — add `BffMetrics metrics` to the `QuoteAgentService` primary constructor; in `ExecuteToolAsync`, after `Generate3DPreview` returns, emit `generated` vs `validation_rejected` from the result shape; in `TryGenerateFallbackPreview` success emit `fallback_used`; in `RecordPreviewFeedbackAsync` emit `feedback` with the normalized sentiment (leave the existing memory loop untouched — regression-guard it).
- [ ] **Step 6: Run tests + `dotnet build`.** Expected: PASS, zero warnings.
- [ ] **Step 7: Commit** `feat: emit 3D preview generation/build/feedback metrics`.

---

## Task 4: Client safety-net — build outcome + regeneration recovery

**Files:**
- Modify: `Maliev.QuoteEngine.Shared/Agent/QuoteAgentDtos.cs` (`QuoteAgentPreviewBuildRequest`)
- Modify: `Maliev.QuoteEngine.Bff/Controllers/AgentController.cs` (new `preview-build` endpoint, mirroring the `feedback` endpoint at `:251`)
- Modify: `Maliev.QuoteEngine.Bff/Services/QuoteAgentService.cs` (+`RecordPreviewBuildOutcomeAsync` → emits `build_outcomes`)
- Modify: `Maliev.QuoteEngine.Client/Components/QuoteAgent/QeInlinePartViewer.razor` (`OnBuildOutcome` EventCallback; remove identical-retry)
- Modify: `Maliev.QuoteEngine.Client/Components/QuoteAgent/QuoteAgentLaunchShell.razor` (report outcome; "Ask agent to rebuild" recovery)
- Tests: `QuoteAgentEndpointTests.cs` (endpoint), source-pin updates

- [ ] **Step 1: DTO.** Add `QuoteAgentPreviewBuildRequest { [Required] bool Success; [RegularExpression allow-list] string? ErrorClass }` mirroring `QuoteAgentPreviewFeedbackRequest` (`:539`).
- [ ] **Step 2: Failing endpoint test** — `POST sessions/{id}/artifacts/{artifactId}/preview-build {success:false,errorClass:"invalid_geometry"}` returns 200; unknown `errorClass` normalizes (no 500).
- [ ] **Step 3: Run — expect FAIL.**
- [ ] **Step 4: Implement** `AgentController.RecordPreviewBuild` (mirror `RecordPreviewFeedback` error handling) → `agentService.RecordPreviewBuildOutcomeAsync(...)` which validates the class and calls `metrics.RecordPreviewBuildOutcome`.
- [ ] **Step 5: Run test + build.** Expected PASS.
- [ ] **Step 6: Component — outcome callback.** In `QeInlinePartViewer.razor` add `[Parameter] public EventCallback<PreviewBuildOutcome> OnBuildOutcome`; invoke `success` after `createPreview` resolves and `failure(errorClass, message)` in the `catch`. Replace the identical `RetryPreview` with no-op removal of the retry button (recovery now lives in the shell). Define a small `PreviewBuildOutcome` record.
- [ ] **Step 7: Shell — report + recover.** In `QuoteAgentLaunchShell.razor`, pass `OnBuildOutcome` to `QeInlinePartViewer`; on outcome POST to the `preview-build` endpoint; on failure render an **"Ask agent to rebuild"** button that sends a canned regeneration message (reuse the shell's existing send-message path) including the error class.
- [ ] **Step 8: Update source-pin tests** for changed markup (`QuoteEngineSourceTests.cs`), then `dotnet build` + full `dotnet test`.
- [ ] **Step 9: Verify visually** in the preview server (inline viewer error → rebuild button).
- [ ] **Step 10: Commit** `feat: report 3D preview build outcomes and offer agent-rebuild recovery`.

---

## Task 5: Fidelity — silhouette registry + honest fallback + prompt guidance

**Files:**
- Modify: `Maliev.QuoteEngine.Bff/Services/QuoteAgentService.cs` (`ComposeAgentMessage` guidance; `NormalizeKnownGeneratedPreviewCommands` → registry; `TryGenerateFallbackPreview` honesty)
- Tests: `QuoteAgentEndpointTests.cs` / `QuoteEngineSourceTests.cs`

- [ ] **Step 1: Failing test** — a "star keychain" (and "heart keychain") preview request with a box primitive is rewritten to an `extrude` with a silhouette profile (Segments > 0), not a `box`; an unknown organic keyword falls back to a description prefixed `[Placeholder]`.
- [ ] **Step 2: Run — expect FAIL.**
- [ ] **Step 3: Implement registry.** Replace `ShouldRewriteBoxLikeHandKeychainCommands`/`BuildHandKeychainPreviewCommands` with a `KnownSilhouette` registry: `keyword → Func<box,hole,IReadOnlyList<CadCommandDto>>`. Seed with `hand` (reuse existing profile), `star`, `heart`. `NormalizeKnownGeneratedPreviewCommands` looks up by description keyword and rewrites a box-like command set into the matching extruded silhouette.
- [ ] **Step 4: Honest fallback.** In `TryGenerateFallbackPreview`, if the message matches a registry keyword, emit that silhouette; otherwise prefix the description `[Placeholder] ` and set part notes to invite refinement (never a silent box).
- [ ] **Step 5: Prompt guidance.** In `ComposeAgentMessage`, add guidance steering the agent to emit an extruded 2D silhouette (`move`/`line`/`arc` segments) for recognizable/organic objects rather than a bounding `box`.
- [ ] **Step 6: Run tests + build + full `dotnet test`.** Expected PASS, zero warnings.
- [ ] **Step 7: Commit** `feat: silhouette registry + honest fallback + extrude-first preview guidance`.

---

## Final verification
- [ ] `dotnet build` — zero warnings.
- [ ] `dotnet test` — full suite green; report pass counts.
- [ ] `npm run build` in Client — bundle current.
- [ ] Preview smoke: generate a preview, force a failure, confirm rebuild recovery + no dead-end.

## Self-review notes (coverage vs spec)
- W1 → Tasks 1 (worker) + 2 (validation). W2 → Task 4. W3 → Task 3. W4 → Task 5. All spec workstreams mapped.
- Honesty: worker fix is confirmed (not hypothetical). Behavioral WASM smoke is best-effort; committed guard is the source-pin + esbuild rebuild + full .NET suite.
