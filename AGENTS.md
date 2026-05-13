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

## Banned Libraries And Practices

- AutoMapper is banned; use explicit mapping.
- FluentValidation is banned; use DataAnnotations or manual validation.
- Swashbuckle/Swagger is banned; use Scalar if API docs are added.
- Do not commit secrets, sample private keys, provider credentials, or customer files.

## Git Rules

- This is an independent git repo. Run git commands from `B:\maliev\Maliev.QuoteEngine`.
- Commit every meaningful repo-local change after validation.
- Do not push unless the user explicitly asks.
