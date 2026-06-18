# Maliev.QuoteEngine Agent Guidelines

This repository owns the customer-facing custom manufacturing quote experience. It is separate from `Maliev.Web`, which owns the marketing site and standard storefront pages.

## Build, Test, And Run

Run commands from `B:\maliev\Maliev.QuoteEngine`.

```powershell
dotnet build Maliev.QuoteEngine.slnx --configuration Release
dotnet test Maliev.QuoteEngine.slnx --configuration Release
dotnet format Maliev.QuoteEngine.slnx --verify-no-changes
dotnet run --project Maliev.QuoteEngine.Bff\Maliev.QuoteEngine.Bff.csproj
```

Use focused tests while iterating:

```powershell
dotnet test Maliev.QuoteEngine.slnx --configuration Release --filter "FullyQualifiedName~QuoteEngineEndpointTests"
```

## Repository Structure

- `Maliev.QuoteEngine.Bff`: ASP.NET Core BFF, prototype API endpoints, session resolution, SignalR hub.
- `Maliev.QuoteEngine.Client`: Blazor WebAssembly UI for quote upload and customer workflows.
- `Maliev.QuoteEngine.Shared`: DTOs shared between BFF and client.
- `Maliev.QuoteEngine.Tests`: source and endpoint regression tests.

## Product And Service Boundaries

- QuoteEngine owns custom manufacturing quote intake: CAD upload, part configuration, geometry/DFM status, deterministic estimate UX, customer draft project creation, formal quote approval, order handoff, customer documents, and NDA surfaces.
- QuoteEngine does not own public marketing pages, blog, or standard shop catalog. Those belong to `Maliev.Web` and `Maliev.CommerceService`.
- Durable backend ownership stays in the platform services: `CustomerService`, `UploadService`, `GeometryService`, `MaterialService`, `PricingService`, `ProjectService`, `QuotationService`, `PdfService`, `OrderService`, `PaymentService`, and `DeliveryService`.

## Security And Authorization Rules

- Public draft quote endpoints are allowed only for prototype estimate flow and must not be treated as a durable customer boundary.
- Formal quotes, orders, account data, NDAs, and documents must be customer-session scoped.
- The browser never supplies a trusted customer ID. Resolve the customer from the session/auth context and verify downstream object ownership before returning or mutating records.
- Upload and file endpoints must enforce resumable upload range validation, file size/type limits, storage-path canonicalization, and ownership checks before exposing signed URLs or artifact status.
- Do not add arbitrary callback URLs or public service-to-service endpoints without a signed/token-bound callback control.
- Before changing a controller, shared DTO, downstream client, message contract, or JSON payload, verify the entire wire contract: client request DTO, BFF payload, service controller DTO, downstream DTO, JSON property names, and regression tests.

## Frontend Rules

- Follow MALIEV Blazor/MudBlazor conventions already present in the repo.
- Keep the first screen as the actual quote workflow, not a marketing landing page.
- Use feature-complete states for upload, analysis, pricing, formal quote, approval, and account history flows.
- Validate responsive layouts and avoid text overflow in compact quote and upload controls.

## Testing Rules

- Use xUnit with standard `Assert.*`; do not use FluentAssertions.
- Add source or endpoint tests for route metadata, session/customer scoping, upload contract changes, and cross-boundary DTO changes.
- For UI/session changes, run targeted tests and browser verification when the affected flow is runnable locally.

## Goal Execution Workflow

### Automatic Active Goal Setup

Every user request in this repository must be translated into an active working goal before making file edits. This is a standing repo instruction to use goal recording automatically; do not wait for the user to separately ask for `/goal`.

Before any file edit, record an active goal using the first available mechanism:

1. If a callable `/goal` tool is explicitly available in the active tool list, call it.
2. Otherwise, if a Codex goal-recording tool is callable, such as `create_goal`, use it to create the active goal.
3. Otherwise, write a visible `Active goal` block in chat.

When using a tool-based goal recorder, keep the tool objective concise and still make the required MALIEV goal details visible in chat before editing. When using the chat fallback, the `Active goal` block is the active goal record.

The goal record must include:

- Outcome: the observable state that should be true when complete.
- Scope: the repo, files, components, services, or user flow affected.
- Boundary/contract: the client, BFF, service, message contract, DTO, JSON payload, auth/session, UI state, or downstream boundary being checked.
- Validation: the focused tests, build checks, browser checks, log checks, or manual verification that will prove the goal.
- Commit boundary: the coherent validated slice that should be committed.

Keep the goal narrow and tied to the user's current request. If investigation proves the original goal is incomplete or wrong, record a `Revised active goal` before continuing.

Use the lightest validation lane that still protects the affected behavior. Do not run every gate for every checkpoint.

### Lane 1: UI Polish And Interaction

Use this lane for layout, visual styling, copy, focus handling, panel sizing, composer behavior, and other browser-visible refinements that do not change server contracts.

- Prefer browser-first verification: inspect the target component/CSS, make the scoped edit, run the app when needed, and verify the real interaction with a screenshot or concise browser evidence.
- Do not add brittle source-string tests for simple visual details such as spacing, border radius, colors, or copy unless the behavior is a durable product contract.
- Use focused component/source checks only when the UI behavior has non-trivial state, keyboard, upload, or accessibility logic.
- Batch related visual tweaks into one coherent local commit instead of committing every tiny CSS adjustment.

### Lane 2: Product Behavior And Agent Contracts

Use this lane for chat turns, streaming, attachment payloads, artifact state, project actions, auth handoffs, pricing/lead-time configuration, and agent tool contracts.

- Use TDD or an equivalent failing regression first when changing behavior.
- Verify the relevant DTO and JSON wire shape across client, BFF, shared contracts, and downstream service clients before editing.
- Run focused endpoint/source tests for the touched behavior before broader validation.
- Keep gates visible in code and tests, but do not expose internal completion gates in the customer UI unless explicitly required.

### Lane 3: Security, Money, Orders, And Durable Account Data

Use this lane for authentication, account mutation, ownership checks, formal quotes, order creation, payment initiation, document generation, upload authorization, and connector handoffs.

- Require strict contract tests, ownership/authorization tests, and focused service/client validation.
- Confirm write actions are confirmation-backed and customer-session scoped.
- Run build, relevant tests, format verification, and pre-commit review before committing.

### Checkpoint Discipline

- A checkpoint should usually finish in 10-30 minutes. If it cannot, split it into a smaller slice.
- Each checkpoint should have one explicit outcome, one validation plan, and one local commit when it changes repo files.
- Avoid re-reading large generated files or broad `rg` output when a targeted search or line-range read will answer the question.
- Preserve unrelated dirty files from parallel agents. Stage and commit only files that belong to the validated slice.
- The full `dotnet build`, broad `dotnet test`, and full `dotnet format --verify-no-changes` gates remain required for release-sized or high-risk slices, but they are not mandatory after every small UI polish edit.

## Banned Libraries And Practices

- AutoMapper is banned; use explicit mapping.
- FluentValidation is banned; use DataAnnotations or manual validation.
- Swashbuckle/Swagger is banned; use Scalar if API docs are added.
- Do not commit secrets, sample private keys, provider credentials, or customer files.

## Git Rules

- This is an independent git repo. Run git commands from `B:\maliev\Maliev.QuoteEngine`.
- Commit every meaningful repo-local change after validation.
- Do not push unless the user explicitly asks.
