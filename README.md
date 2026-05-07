# Maliev.QuoteEngine

Customer-facing quote engine and custom manufacturing order portal for MALIEV.

[![CI - Develop](https://github.com/MALIEV-Co-Ltd/Maliev.QuoteEngine/actions/workflows/ci-develop.yml/badge.svg)](https://github.com/MALIEV-Co-Ltd/Maliev.QuoteEngine/actions/workflows/ci-develop.yml)
[![CI - Main](https://github.com/MALIEV-Co-Ltd/Maliev.QuoteEngine/actions/workflows/ci-main.yml/badge.svg)](https://github.com/MALIEV-Co-Ltd/Maliev.QuoteEngine/actions/workflows/ci-main.yml)

This repo is intentionally separate from `Maliev.Web`.

- `Maliev.Web` owns the public SSR marketing site, service information, blog, contact, and standard product shop.
- `Maliev.QuoteEngine` owns custom manufacturing workflows: CAD upload, part configuration, geometry/DFM status, deterministic pricing, quote approval, manufacturing orders, NDAs, documents, and customer profile management.

## Local Development

```powershell
dotnet restore Maliev.QuoteEngine.slnx
dotnet run --project Maliev.QuoteEngine.Bff/Maliev.QuoteEngine.Bff.csproj
```

The BFF hosts the Blazor WebAssembly client and exposes prototype APIs under `/quote/v1/*`.

## Architecture

```text
Customer Browser
  -> Maliev.QuoteEngine.Client (Blazor WASM, MudBlazor, upload workspace)
  -> Maliev.QuoteEngine.Bff (API aggregation, session/customer resolution, SignalR hub)
  -> MALIEV backend services
```

Backend services used by the final quote engine:

- AuthService and IAMService for customer sign-in, Google SSO, permissions, and service authentication.
- CustomerService for customer profile, credential validation, Google identity linkage, NDA, and document ownership.
- UploadService and GeometryService for resumable uploads, geometry metrics, GLB previews, thumbnails, and DFM events.
- MaterialService and PricingService for material/process options and deterministic quote estimates.
- ProjectService, QuotationService, PdfService, OrderService, PaymentService, and DeliveryService for formal quote and manufacturing order lifecycle.

## API Endpoints

| Endpoint | Purpose | Prototype auth |
|---|---|---|
| `GET /quote/v1/reference-data` | Customer-visible process, material, lead-time, and file extension data. | Public draft |
| `POST /quote/v1/uploads/resumable` | Initiate a customer quote upload session. | Public draft |
| `PUT /quote/v1/uploads/resumable/{uploadId}` | Stream raw upload bytes with `Content-Range`. | Public draft |
| `POST /quote/v1/uploads/resumable/{uploadId}/complete` | Mark upload complete and publish analysis state. | Public draft |
| `GET /quote/v1/uploads/{uploadId}/analysis-status` | Read part-scoped geometry, DFM, viewer, and thumbnail status. | Public draft |
| `POST /quote/v1/estimate` | Calculate a deterministic prototype estimate from part geometry and selections. | Public draft |
| `POST /quote/v1/projects/draft` | Create the customer draft project boundary. | Customer session |
| `POST /quote/v1/quotes/formal` | Generate a formal quote boundary. | Customer session |
| `POST /quote/v1/quotes/{quoteId}/approve` | Approve a formal quote. | Customer session |
| `POST /quote/v1/orders` | Create a custom manufacturing order from an approved quote. | Customer session |
| `GET /quote/v1/account/profile` | Current customer profile. | Customer session |
| `GET /quote/v1/account/quotes` | Customer quote history. | Customer session |
| `GET /quote/v1/account/orders` | Customer manufacturing order history. | Customer session |
| `GET /quote/v1/account/ndas` | Customer NDA records. | Customer session |
| `GET /quote/v1/account/documents` | Customer document records. | Customer session |
| `POST /quote/v1/auth/sign-in` | Customer email/password sign-in placeholder. | Public |
| `POST /quote/v1/auth/sign-up` | Customer account registration placeholder. | Public |
| `POST /quote/v1/auth/google/exchange` | Customer Google SSO exchange placeholder. | Public |
| `POST /quote/v1/auth/sign-out` | Clear the quote customer session cookie. | Customer session |
| `GET /hubs/quote-notifications` | SignalR hub for file analysis and quote events. | Customer session |

## Permissions Model

The prototype exposes public draft quote endpoints so anonymous customers can start an estimate before signing in. Formal quotes, orders, account data, NDAs, and documents are customer-session scoped. The production backend gap is tracked explicitly: AuthService and CustomerService must provide customer-safe credential validation, Google identity linkage, and IAM principal resolution before these endpoints are backed by durable service calls.

## Boundaries

The browser never supplies a trusted customer id. The BFF resolves the current customer from auth/session context before forwarding to backend services. The current prototype keeps this behavior represented in the API shape and tests while backend customer auth gaps are implemented.
