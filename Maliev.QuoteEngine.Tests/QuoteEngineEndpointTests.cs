using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Maliev.QuoteEngine.Shared.Agent;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Services;
using Maliev.QuoteEngine.Shared.Account;
using Maliev.QuoteEngine.Shared.Chatbot;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Polly.Timeout;

namespace Maliev.QuoteEngine.Tests;

/// <summary>
/// Custom factory that sets environment to "Testing" so that:
/// - MassTransit:UseInMemory=true takes effect (no RabbitMQ required)
/// - appsettings.Testing.json is loaded (DemoMode.GlbUrl configured)
/// - All real service HTTP clients are replaced with in-memory fakes
/// </summary>
public sealed class QuoteEngineWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly FakeQuotationServiceClient _fakeQuotationServiceClient = new();
    private readonly FakeOrderServiceClient _fakeOrderServiceClient = new();
    private readonly FakeProjectServiceClient _fakeProjectServiceClient = new();
    private readonly FakeInvoiceServiceClient _fakeInvoiceServiceClient = new();

    public IReadOnlyList<string> PaymentIdempotencyKeys => FakePaymentServiceClient.IdempotencyKeys.ToArray();

    public IReadOnlyList<CapturedPaymentInitiation> PaymentInitiations => FakePaymentServiceClient.Initiations.ToArray();

    public CapturedOrderDeliverySnapshot? LastOrderDeliverySnapshot => _fakeOrderServiceClient.LastDeliverySnapshot;

    public OrderCreateRequest? LastOrderCreateRequest => _fakeOrderServiceClient.LastCreateRequest;

    public IReadOnlyList<OrderCreateRequest> OrderCreateRequests => _fakeOrderServiceClient.CreateRequests.ToArray();

    public IReadOnlyList<CapturedOrderStatusUpdate> OrderStatusUpdates => _fakeOrderServiceClient.StatusUpdates.ToArray();

    public QuotationCreateRequest? LastQuotationCreateRequest => _fakeQuotationServiceClient.LastCreateRequest;

    public InvoiceCreateForOrderRequest? LastInvoiceCreateRequest => _fakeInvoiceServiceClient.LastCreateRequest;

    public InvoicePreparedResult? LastInvoicePreparedResult => _fakeInvoiceServiceClient.LastPreparedResult;

    public CapturedProjectDraftCreate? LastProjectDraftCreate => _fakeProjectServiceClient.LastCreate;

    public IReadOnlyList<CapturedProjectPartCreate> LastProjectPartCreates => _fakeProjectServiceClient.PartCreates.ToArray();

    public string? GetProjectStatus(Guid projectId) => _fakeProjectServiceClient.GetProjectStatus(projectId);

    public string? GetProjectReviewNote(Guid projectId) => _fakeProjectServiceClient.GetReviewNote(projectId);

    public void FailNextOrderStatus(string status) => _fakeOrderServiceClient.FailNextStatus(status);

    public void MarkOrderPaid(string orderNumber) => _fakeOrderServiceClient.MarkPaid(orderNumber);

    public void DelayOrderCreateBy(TimeSpan delay) => _fakeOrderServiceClient.CreateDelay = delay;

    public void SupersedeQuoteVersion(Guid quotationId) => _fakeQuotationServiceClient.SupersedeQuoteVersion(quotationId);

    public void ExpireQuote(Guid quotationId) => _fakeQuotationServiceClient.ExpireQuote(quotationId);

    public void ClearPaymentIdempotencyKeys()
    {
        while (FakePaymentServiceClient.IdempotencyKeys.TryDequeue(out _))
        {
        }

        while (FakePaymentServiceClient.Initiations.TryDequeue(out _))
        {
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureTestServices(services =>
        {
            RemoveMassTransitHostedService(services);

            // Replace the real UploadService HTTP client with a no-op test double.
            services.RemoveAll<QuoteUploadServiceClient>();
            services.AddSingleton<QuoteUploadServiceClient>(new NoOpQuoteUploadServiceClient());

            services.RemoveAll<IQuoteGeometryRuntimeClient>();
            services.AddSingleton<IQuoteGeometryRuntimeClient>(new FakeQuoteGeometryRuntimeClient());

            // Replace real downstream service clients with in-memory fakes.
            services.RemoveAll<IMaterialCatalogClient>();
            services.AddSingleton<IMaterialCatalogClient>(new FakeMaterialCatalogClient());

            services.RemoveAll<IQuotationServiceClient>();
            services.AddSingleton<IQuotationServiceClient>(_fakeQuotationServiceClient);

            services.RemoveAll<IOrderServiceClient>();
            services.AddSingleton<IOrderServiceClient>(_fakeOrderServiceClient);

            // Returns null → AccountController falls back to PrototypeStore for profile
            services.RemoveAll<ICustomerServiceClient>();
            services.AddSingleton<ICustomerServiceClient>(new FakeCustomerServiceClient());

            services.RemoveAll<IProjectServiceClient>();
            services.AddSingleton<IProjectServiceClient>(_fakeProjectServiceClient);

            services.RemoveAll<ICountryServiceClient>();
            services.AddSingleton<ICountryServiceClient>(new FakeCountryServiceClient());

            services.RemoveAll<IRegistryServiceClient>();
            services.AddSingleton<IRegistryServiceClient>(new FakeRegistryServiceClient());

            services.RemoveAll<IInvoiceServiceClient>();
            services.AddSingleton<IInvoiceServiceClient>(_fakeInvoiceServiceClient);

            // Returns a fixed hosted payment URL
            services.RemoveAll<IPaymentServiceClient>();
            services.AddSingleton<IPaymentServiceClient>(new FakePaymentServiceClient());

            services.RemoveAll<IQePricingServiceClient>();
            services.AddSingleton<IQePricingServiceClient>(new FakePricingServiceClient());

            // Test-only sign-in endpoint: issues the shared identity cookie with customer identity claims.
            // Replaces the removed /quote/v1/auth/sign-in endpoint for test authentication.
            services.AddTransient<IStartupFilter, TestSignInStartupFilter>();
        });
    }

    private static void RemoveMassTransitHostedService(IServiceCollection services)
    {
        for (var index = services.Count - 1; index >= 0; index--)
        {
            var descriptor = services[index];
            if (descriptor.ServiceType == typeof(IHostedService) &&
                descriptor.ImplementationType?.FullName == "MassTransit.MassTransitHostedService")
            {
                services.RemoveAt(index);
            }
        }
    }

    public sealed record CapturedOrderDeliverySnapshot(
        string OrderNumber,
        Guid BillingAddressId,
        Guid ShippingAddressId,
        string? ShippingAddressLine1,
        string? ShippingAddressLine2,
        string? ShippingCity,
        string? ShippingProvince,
        string? ShippingPostalCode,
        string? ShippingCountry,
        string? BillingCompanyName,
        string? BillingVatNumber,
        string? DeliveryContactName,
        string? DeliveryContactPhone,
        string? DeliveryContactEmail);

    public sealed record CapturedOrderStatusUpdate(string OrderNumber, string Status);

    public sealed record CapturedPaymentInitiation(
        string CustomerId,
        string OrderId,
        string OrderNumber,
        decimal Amount,
        string Currency,
        string ReturnUrl,
        string CancelUrl,
        string IdempotencyKey,
        Guid? BillingAddressId,
        Guid? ShippingAddressId,
        string? BillingCompanyName,
        string? BillingVatNumber,
        string? DeliveryContactName,
        string? DeliveryContactPhone,
        string? DeliveryContactEmail);

    public sealed record CapturedProjectDraftCreate(
        Guid CustomerId,
        string CustomerName,
        string QuoteSessionId,
        string Title,
        string Notes,
        Guid ProjectServiceProjectId,
        string ProjectServiceProjectNumber,
        Guid? SourceProjectId = null,
        string? SourceProjectNumber = null);

    public sealed record CapturedProjectPartCreate(
        Guid ProjectServiceProjectId,
        Guid ProjectServicePartId,
        string FileName,
        Guid? MaterialId,
        int Quantity,
        string ProcessId,
        bool DfmAcknowledged,
        bool HasDfmWarnings,
        string? StoragePath,
        IReadOnlyList<QuotePartAttachmentDto> DrawingFiles);

    // ── Upload no-op ──────────────────────────────────────────────────────────

    private sealed class NoOpQuoteUploadServiceClient()
        : QuoteUploadServiceClient(new HttpClient(), NullLogger<QuoteUploadServiceClient>.Instance)
    {
        public override Task<string> InitiateResumableUploadAsync(
            string fileName,
            string contentType,
            long totalSize,
            string storagePath,
            IReadOnlyDictionary<string, string>? metadataTags,
            CancellationToken ct) => Task.FromResult($"downstream-{Guid.NewGuid():N}");

        public override Task StreamUploadAsync(Stream body, string contentType, long contentLength,
            string contentRange, string downstreamUploadId, string storagePath, CancellationToken ct) => Task.CompletedTask;

        public override Task<string> GetDownloadUrlByPathAsync(string storagePath,
            int expirationMinutes = 60, CancellationToken ct = default)
            => Task.FromResult($"https://test-cdn.example.com/{Uri.EscapeDataString(storagePath)}");
    }

    internal sealed class RecordingQuoteUploadServiceClient()
        : QuoteUploadServiceClient(new HttpClient(), NullLogger<QuoteUploadServiceClient>.Instance)
    {
        public IReadOnlyDictionary<string, string>? LastMetadataTags { get; private set; }
        public string? LastInitiatedFileName { get; private set; }
        public string? LastInitiatedContentType { get; private set; }
        public long LastInitiatedTotalSize { get; private set; }
        public string? LastInitiatedStoragePath { get; private set; }
        public string? LastStreamedContentRange { get; private set; }
        public string? LastStreamedUploadId { get; private set; }
        public string? LastStreamedStoragePath { get; private set; }
        public long LastStreamedContentLength { get; private set; }
        public byte[] LastStreamedBytes { get; private set; } = [];

        public override Task<string> InitiateResumableUploadAsync(
            string fileName,
            string contentType,
            long totalSize,
            string storagePath,
            IReadOnlyDictionary<string, string>? metadataTags,
            CancellationToken ct)
        {
            LastInitiatedFileName = fileName;
            LastInitiatedContentType = contentType;
            LastInitiatedTotalSize = totalSize;
            LastInitiatedStoragePath = storagePath;
            LastMetadataTags = metadataTags;
            return Task.FromResult("downstream-document-upload");
        }

        public override async Task StreamUploadAsync(
            Stream body,
            string contentType,
            long contentLength,
            string contentRange,
            string downstreamUploadId,
            string storagePath,
            CancellationToken ct)
        {
            LastStreamedContentRange = contentRange;
            LastStreamedUploadId = downstreamUploadId;
            LastStreamedStoragePath = storagePath;
            LastStreamedContentLength = contentLength;
            using var memory = new MemoryStream();
            await body.CopyToAsync(memory, ct);
            LastStreamedBytes = memory.ToArray();
        }
    }

    private sealed class FakeQuoteGeometryRuntimeClient : IQuoteGeometryRuntimeClient
    {
        public Task<HttpResponseMessage> GetRuntimeManifestAsync(CancellationToken ct = default)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"runtimeVersion\":\"0.1.0\",\"assets\":{\"worker\":\"/geometry/client-runtime/assets/client-geometry-runtime.abc.worker.js\"}}",
                    Encoding.UTF8,
                    "application/json")
            };
            response.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            return Task.FromResult(response);
        }

        public Task<HttpResponseMessage> GetRuntimeAssetAsync(string assetName, CancellationToken ct = default)
        {
            if (assetName == "missing.worker.js")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("{\"detail\":\"Runtime asset not found\"}", Encoding.UTF8, "application/json")
                });
            }

            var content = new ByteArrayContent([0x00, 0x61, 0xFF, 0x7F]);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/wasm");
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            };
            response.Headers.CacheControl = new CacheControlHeaderValue
            {
                Public = true,
                MaxAge = TimeSpan.FromDays(365),
            };
            response.Headers.CacheControl.Extensions.Add(new NameValueHeaderValue("immutable"));
            return Task.FromResult(response);
        }
    }

    internal sealed class TimeoutQuoteGeometryRuntimeClient : IQuoteGeometryRuntimeClient
    {
        public Task<HttpResponseMessage> GetRuntimeManifestAsync(CancellationToken ct = default)
        {
            throw new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout of 30 seconds elapsing.",
                new TimeoutException("The operation was canceled."));
        }

        public Task<HttpResponseMessage> GetRuntimeAssetAsync(string assetName, CancellationToken ct = default)
        {
            throw new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout of 30 seconds elapsing.",
                new TimeoutException("The operation was canceled."));
        }
    }

    internal sealed class PollyTimeoutQuoteGeometryRuntimeClient : IQuoteGeometryRuntimeClient
    {
        public Task<HttpResponseMessage> GetRuntimeManifestAsync(CancellationToken ct = default) =>
            Task.FromException<HttpResponseMessage>(new TimeoutRejectedException(
                "The operation didn't complete within the allowed timeout of '00:01:00'."));

        public Task<HttpResponseMessage> GetRuntimeAssetAsync(string assetName, CancellationToken ct = default) =>
            Task.FromException<HttpResponseMessage>(new TimeoutRejectedException(
                "The operation didn't complete within the allowed timeout of '00:01:00'."));
    }

    internal sealed class ThrowIfCalledQuoteGeometryRuntimeClient : IQuoteGeometryRuntimeClient
    {
        public Task<HttpResponseMessage> GetRuntimeManifestAsync(CancellationToken ct = default) =>
            Task.FromException<HttpResponseMessage>(
                new InvalidOperationException("Packaged runtime assets must not call GeometryService."));

        public Task<HttpResponseMessage> GetRuntimeAssetAsync(string assetName, CancellationToken ct = default) =>
            Task.FromException<HttpResponseMessage>(
                new InvalidOperationException("Packaged runtime assets must not call GeometryService."));
    }

    // ── Fake service clients ──────────────────────────────────────────────────

    private sealed class FakeMaterialCatalogClient : IMaterialCatalogClient
    {
        private static readonly Guid FdmProcessId = Guid.Parse("11111111-0000-0000-0000-000000000001");
        private static readonly Guid SlaProcessId = Guid.Parse("11111111-0000-0000-0000-000000000002");
        private static readonly Guid CncProcessId = Guid.Parse("11111111-0000-0000-0000-000000000003");
        private static readonly Guid DefaultMatId = Guid.Parse("22222222-0000-0000-0000-000000000001");

        public Task<Guid> ResolveProcessIdAsync(string processCode, CancellationToken ct = default) =>
            Task.FromResult(processCode.ToLowerInvariant() switch
            {
                "sla" => SlaProcessId,
                "cnc" => CncProcessId,
                _ => FdmProcessId
            });

        public Task<Guid> ResolveMaterialIdAsync(string processCode, string materialCode, CancellationToken ct = default) =>
            Task.FromResult(DefaultMatId);
    }

    private sealed class FakeQuotationServiceClient : IQuotationServiceClient
    {
        private readonly ConcurrentDictionary<Guid, QuotationCreatedResult> _quotes = new();

        public QuotationCreateRequest? LastCreateRequest { get; private set; }

        public Task<QuotationCreatedResult?> CreateAsync(QuotationCreateRequest request, CancellationToken ct = default)
        {
            LastCreateRequest = request;
            var result = new QuotationCreatedResult
            {
                Id = Guid.NewGuid(),
                CustomerId = request.CustomerId,
                SourceProjectId = request.SourceProjectId,
                SourceProjectNumber = request.SourceProjectNumber,
                QuotationNumber = $"MQ-TEST-{Guid.NewGuid():N}"[..16],
                Status = "Draft",
                CurrentVersionNumber = 1,
                ValidityPeriodEnd = request.ValidityPeriodEnd,
                Total = request.LineItems.Sum(x => x.UnitPrice * x.Quantity),
                CurrencyCode = "THB",
                UpdatedAt = DateTime.UtcNow,
                QuoteVersionId = Guid.NewGuid(),
                QuoteVersionNumber = 1,
                PdfArtifactUrl = $"https://files.example.test/quotations/{request.CustomerId:N}/formal-quote.pdf",
                PdfArtifactStoragePath = $"quotations/{request.CustomerId:N}/{Guid.NewGuid():N}/formal-quote.pdf"
            };
            _quotes[result.Id] = result;
            return Task.FromResult<QuotationCreatedResult?>(result);
        }

        public Task<QuotationCreatedResult?> CreateOrReviseProjectQuoteAsync(QuotationCreateRequest request, CancellationToken ct = default)
        {
            if (request.SourceProjectId is not { } sourceProjectId)
            {
                return CreateAsync(request, ct);
            }

            var existing = _quotes.Values.FirstOrDefault(quote =>
                quote.CustomerId == request.CustomerId &&
                quote.SourceProjectId == sourceProjectId);
            if (existing is null)
            {
                return CreateAsync(request, ct);
            }

            LastCreateRequest = request;
            var result = new QuotationCreatedResult
            {
                Id = existing.Id,
                CustomerId = existing.CustomerId,
                SourceProjectId = existing.SourceProjectId,
                SourceProjectNumber = existing.SourceProjectNumber,
                QuotationNumber = existing.QuotationNumber,
                Status = existing.Status,
                CurrentVersionNumber = existing.QuoteVersionNumber.GetValueOrDefault(1) + 1,
                ValidityPeriodEnd = existing.ValidityPeriodEnd ?? DateTime.UtcNow.AddDays(14),
                Total = request.LineItems.Sum(x => x.UnitPrice * x.Quantity),
                CurrencyCode = existing.CurrencyCode,
                UpdatedAt = DateTime.UtcNow,
                QuoteVersionId = Guid.NewGuid(),
                QuoteVersionNumber = existing.QuoteVersionNumber.GetValueOrDefault(1) + 1,
                PdfArtifactUrl = $"https://files.example.test/quotations/{request.CustomerId:N}/formal-quote-v{existing.QuoteVersionNumber.GetValueOrDefault(1) + 1}.pdf",
                PdfArtifactStoragePath = $"quotations/{request.CustomerId:N}/{Guid.NewGuid():N}/formal-quote-v{existing.QuoteVersionNumber.GetValueOrDefault(1) + 1}.pdf"
            };
            _quotes[result.Id] = result;
            return Task.FromResult<QuotationCreatedResult?>(result);
        }

        public Task<QuotationCreatedResult?> GetByIdAsync(Guid quotationId, CancellationToken ct = default) =>
            Task.FromResult(_quotes.TryGetValue(quotationId, out var r) ? r : null);

        public void SupersedeQuoteVersion(Guid quotationId)
        {
            if (!_quotes.TryGetValue(quotationId, out var existing))
            {
                return;
            }

            var nextVersionNumber = existing.QuoteVersionNumber.GetValueOrDefault(1) + 1;
            _quotes[quotationId] = CopyQuote(existing, newVersionId: Guid.NewGuid(), newVersionNumber: nextVersionNumber);
        }

        public void ExpireQuote(Guid quotationId)
        {
            if (!_quotes.TryGetValue(quotationId, out var existing))
            {
                return;
            }

            _quotes[quotationId] = CopyQuote(
                existing,
                newStatus: "Expired",
                newValidityPeriodEnd: DateTime.UtcNow.AddDays(-1));
        }

        public Task<QuotationCreatedResult?> GetBySourceProjectAsync(Guid customerId, Guid sourceProjectId, CancellationToken ct = default)
        {
            var result = _quotes.Values.FirstOrDefault(quote =>
                quote.CustomerId == customerId &&
                quote.SourceProjectId == sourceProjectId);
            return Task.FromResult<QuotationCreatedResult?>(result);
        }

        public Task<IReadOnlyList<CustomerQuoteSummaryDto>> GetByCustomerAsync(Guid customerId, CancellationToken ct = default)
        {
            IReadOnlyList<CustomerQuoteSummaryDto> result = _quotes.Values
                .Where(q => q.CustomerId == customerId)
                .Select(q => new CustomerQuoteSummaryDto(
                    q.Id, q.QuotationNumber, q.Status, q.Total, q.CurrencyCode,
                    new DateTimeOffset(q.UpdatedAt, TimeSpan.Zero),
                    q.PdfArtifactUrl ?? string.Empty,
                    [
                        new CustomerQuoteVersionSummaryDto(
                            q.QuoteVersionId ?? Guid.NewGuid(),
                            2,
                            q.Total,
                            q.CurrencyCode,
                            "Updated test quote",
                            q.PdfArtifactUrl,
                            q.PdfArtifactStoragePath,
                            "Make Studio",
                            new DateTimeOffset(q.UpdatedAt.AddMinutes(5), TimeSpan.Zero)),
                        new CustomerQuoteVersionSummaryDto(
                            Guid.NewGuid(),
                            1,
                            q.Total - 250m,
                            q.CurrencyCode,
                            "Initial test quote",
                            null,
                            null,
                            "Make Studio",
                            new DateTimeOffset(q.UpdatedAt, TimeSpan.Zero))
                    ]))
                .ToArray();
            return Task.FromResult(result);
        }

        private static QuotationCreatedResult CopyQuote(
            QuotationCreatedResult existing,
            string? newStatus = null,
            Guid? newVersionId = null,
            int? newVersionNumber = null,
            DateTime? newValidityPeriodEnd = null)
        {
            return new QuotationCreatedResult
            {
                Id = existing.Id,
                CustomerId = existing.CustomerId,
                SourceProjectId = existing.SourceProjectId,
                SourceProjectNumber = existing.SourceProjectNumber,
                QuotationNumber = existing.QuotationNumber,
                Status = newStatus ?? existing.Status,
                CurrentVersionNumber = newVersionNumber ?? existing.CurrentVersionNumber,
                ValidityPeriodEnd = newValidityPeriodEnd ?? existing.ValidityPeriodEnd,
                Total = existing.Total,
                CurrencyCode = existing.CurrencyCode,
                UpdatedAt = DateTime.UtcNow,
                QuoteVersionId = newVersionId ?? existing.QuoteVersionId,
                QuoteVersionNumber = newVersionNumber ?? existing.QuoteVersionNumber,
                PdfArtifactUrl = existing.PdfArtifactUrl,
                PdfArtifactStoragePath = existing.PdfArtifactStoragePath
            };
        }
    }

    private sealed class FakeProjectServiceClient : IProjectServiceClient
    {
        private readonly ConcurrentDictionary<Guid, CapturedProjectDraftCreate> _projects = new();
        private readonly ConcurrentDictionary<Guid, List<CapturedProjectPartCreate>> _partsByProject = new();
        private readonly ConcurrentDictionary<Guid, bool> _archivedProjects = new();
        private readonly ConcurrentDictionary<Guid, bool> _pinnedProjects = new();
        private readonly ConcurrentDictionary<Guid, string> _projectStatuses = new();
        private readonly ConcurrentDictionary<Guid, string> _reviewNotes = new();

        public ConcurrentQueue<CapturedProjectPartCreate> PartCreates { get; } = new();

        public CapturedProjectDraftCreate? LastCreate { get; private set; }

        public async Task<ProjectServiceDraftProjectResult?> CreateDraftProjectAsync(
            Guid customerId,
            string customerName,
            CreateDraftProjectRequest request,
            Func<QuotePartDraftDto, CancellationToken, Task<Guid?>> resolveMaterialIdAsync,
            CancellationToken ct = default)
        {
            var projectId = Guid.NewGuid();
            var projectNumber = $"PRJ-TEST-{projectId:N}"[..17];
            while (PartCreates.TryDequeue(out _))
            {
            }

            LastCreate = new CapturedProjectDraftCreate(
                customerId,
                customerName,
                request.QuoteSessionId,
                request.Title,
                request.Notes,
                projectId,
                projectNumber);

            _projects[projectId] = LastCreate;
            _projectStatuses[projectId] = "Draft";
            var projectParts = new List<CapturedProjectPartCreate>();
            foreach (var part in request.Parts)
            {
                var partId = Guid.NewGuid();
                part.PartId = partId;
                var capturedPart = new CapturedProjectPartCreate(
                    projectId,
                    partId,
                    part.FileName,
                    await resolveMaterialIdAsync(part, ct),
                    part.Quantity,
                    part.ProcessId,
                    part.DfmAcknowledged,
                    part.Findings.Count > 0 ||
                    part.FdmReport?.Issues.Count > 0 ||
                    part.SlaReport?.Issues.Count > 0 ||
                    part.CncReport?.Issues.Count > 0 ||
                    (!part.IsManifold && !string.IsNullOrWhiteSpace(part.NonManifoldReason)),
                    part.StoragePath ?? part.ViewerStoragePath ?? part.UploadId,
                    part.DrawingFiles.ToArray());
                projectParts.Add(capturedPart);
                PartCreates.Enqueue(capturedPart);
            }

            _partsByProject[projectId] = projectParts;
            return new ProjectServiceDraftProjectResult(projectId, projectNumber, "Draft");
        }

        public async Task<DuplicateDraftProjectResponse?> DuplicateDraftProjectAsync(
            Guid customerId,
            string customerName,
            Guid projectId,
            DuplicateDraftProjectRequest request,
            Func<QuotePartDraftDto, CancellationToken, Task<Guid?>> resolveMaterialIdAsync,
            CancellationToken ct = default)
        {
            if (!_projects.TryGetValue(projectId, out var source) || source.CustomerId != customerId)
            {
                return null;
            }

            var duplicateId = Guid.NewGuid();
            var duplicateNumber = $"PRJ-TEST-{duplicateId:N}"[..17];
            var title = string.IsNullOrWhiteSpace(request.Title)
                ? $"Copy of {source.Title}"
                : request.Title.Trim();
            LastCreate = new CapturedProjectDraftCreate(
                customerId,
                customerName,
                source.QuoteSessionId,
                title,
                source.Notes,
                duplicateId,
                duplicateNumber,
                projectId,
                source.ProjectServiceProjectNumber);
            _projects[duplicateId] = LastCreate;
            _projectStatuses[duplicateId] = "Draft";

            var sourceParts = _partsByProject.TryGetValue(projectId, out var parts)
                ? parts
                : [];
            var duplicateParts = new List<CapturedProjectPartCreate>();
            while (PartCreates.TryDequeue(out _))
            {
            }

            foreach (var part in sourceParts)
            {
                var draft = ToQuotePartDraft(part);
                var partId = Guid.NewGuid();
                draft.PartId = partId;
                var captured = new CapturedProjectPartCreate(
                    duplicateId,
                    partId,
                    part.FileName,
                    await resolveMaterialIdAsync(draft, ct),
                    part.Quantity,
                    part.ProcessId,
                    part.DfmAcknowledged,
                    part.HasDfmWarnings,
                    part.StoragePath,
                    part.DrawingFiles);
                duplicateParts.Add(captured);
                PartCreates.Enqueue(captured);
            }

            _partsByProject[duplicateId] = duplicateParts;
            return new DuplicateDraftProjectResponse(
                duplicateId,
                duplicateNumber,
                "Draft",
                title,
                duplicateParts.Select(ToQuotePartDraft).ToArray());
        }

        public Task<IReadOnlyList<CustomerProjectNavItemDto>> GetProjectNavigationAsync(
            Guid customerId,
            CancellationToken ct = default)
        {
            IReadOnlyList<CustomerProjectNavItemDto> result = _projects.Values
                .Where(project => project.CustomerId == customerId && !IsArchived(project.ProjectServiceProjectId))
                .Select(project => new CustomerProjectNavItemDto(
                    project.ProjectServiceProjectId,
                    project.ProjectServiceProjectNumber,
                    project.Title,
                    GetStatus(project.ProjectServiceProjectId),
                    IsPinned: IsPinned(project.ProjectServiceProjectId),
                    IsArchived: false,
                    DateTimeOffset.UtcNow))
                .OrderByDescending(project => project.IsPinned)
                .ToArray();

            return Task.FromResult(result);
        }

        public Task<CustomerProjectDetailResponse?> GetProjectDetailAsync(
            Guid customerId,
            Guid projectId,
            CancellationToken ct = default)
        {
            if (!_projects.TryGetValue(projectId, out var project) || project.CustomerId != customerId)
            {
                return Task.FromResult<CustomerProjectDetailResponse?>(null);
            }

            var parts = _partsByProject.TryGetValue(projectId, out var projectParts)
                ? projectParts.Select(ToQuotePartDraft).ToArray()
                : [];
            var detail = new CustomerProjectDetailResponse(
                project.ProjectServiceProjectId,
                project.ProjectServiceProjectNumber,
                GetStatus(projectId),
                project.Title,
                IsPinned(projectId),
                IsArchived(projectId),
                DateTimeOffset.UtcNow,
                parts);

            return Task.FromResult<CustomerProjectDetailResponse?>(detail);
        }

        public Task<ProjectManagementResponse?> SetProjectPinnedAsync(
            Guid customerId,
            Guid projectId,
            bool isPinned,
            CancellationToken ct = default)
        {
            if (!_projects.TryGetValue(projectId, out var project) || project.CustomerId != customerId)
            {
                return Task.FromResult<ProjectManagementResponse?>(null);
            }

            _pinnedProjects[projectId] = isPinned;
            return Task.FromResult<ProjectManagementResponse?>(ToManagementResponse(project));
        }

        public Task<ProjectManagementResponse?> ArchiveProjectAsync(
            Guid customerId,
            Guid projectId,
            CancellationToken ct = default)
        {
            if (!_projects.TryGetValue(projectId, out var project) || project.CustomerId != customerId)
            {
                return Task.FromResult<ProjectManagementResponse?>(null);
            }

            _archivedProjects[projectId] = true;
            return Task.FromResult<ProjectManagementResponse?>(ToManagementResponse(project));
        }

        public Task<ProjectManagementResponse?> RequestProjectReviewAsync(
            Guid customerId,
            Guid projectId,
            string note,
            CancellationToken ct = default)
        {
            if (!_projects.TryGetValue(projectId, out var project) || project.CustomerId != customerId)
            {
                return Task.FromResult<ProjectManagementResponse?>(null);
            }

            _projectStatuses[projectId] = "CustomerReview";
            _reviewNotes[projectId] = note;
            return Task.FromResult<ProjectManagementResponse?>(ToManagementResponse(project));
        }

        public Task<IReadOnlyList<QuoteAgentSearchResultDto>> SearchProjectResultsAsync(
            Guid customerId,
            string? query,
            int limit,
            CancellationToken ct = default)
        {
            var normalizedQuery = query?.Trim() ?? string.Empty;
            var normalizedLimit = Math.Clamp(limit, 1, 50);
            IReadOnlyList<QuoteAgentSearchResultDto> result = _projects.Values
                .Where(project => project.CustomerId == customerId)
                .Select(project => new QuoteAgentSearchResultDto
                {
                    ResourceType = "project",
                    ResourceId = project.ProjectServiceProjectId.ToString("D"),
                    Title = project.Title,
                    Detail = $"{project.ProjectServiceProjectNumber} · {GetStatus(project.ProjectServiceProjectId)} · {_partsByProject.GetValueOrDefault(project.ProjectServiceProjectId)?.Count ?? 0} part(s)",
                    ActionHint = "resume_project",
                    Url = $"/quotes?projectId={project.ProjectServiceProjectId:D}",
                    Metadata = new(StringComparer.OrdinalIgnoreCase)
                    {
                        ["projectNumber"] = project.ProjectServiceProjectNumber,
                        ["status"] = GetStatus(project.ProjectServiceProjectId),
                        ["isPinned"] = IsPinned(project.ProjectServiceProjectId).ToString().ToLowerInvariant(),
                        ["isArchived"] = IsArchived(project.ProjectServiceProjectId).ToString().ToLowerInvariant(),
                        ["source"] = "project_service"
                    }
                })
                .Where(result => string.IsNullOrWhiteSpace(normalizedQuery) ||
                    result.Title.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                    result.Detail.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
                .Take(normalizedLimit)
                .ToArray();

            return Task.FromResult(result);
        }

        private ProjectManagementResponse ToManagementResponse(CapturedProjectDraftCreate project) =>
            new(
                project.ProjectServiceProjectId,
                project.ProjectServiceProjectNumber,
                IsArchived(project.ProjectServiceProjectId) ? "Archived" : GetStatus(project.ProjectServiceProjectId),
                project.Title,
                IsPinned(project.ProjectServiceProjectId),
                IsArchived(project.ProjectServiceProjectId));

        private string GetStatus(Guid projectId) =>
            _projectStatuses.GetValueOrDefault(projectId, "Draft");

        public string? GetProjectStatus(Guid projectId) =>
            _projectStatuses.GetValueOrDefault(projectId);

        public string? GetReviewNote(Guid projectId) =>
            _reviewNotes.GetValueOrDefault(projectId);

        private bool IsPinned(Guid projectId) =>
            _pinnedProjects.TryGetValue(projectId, out var isPinned) && isPinned;

        private bool IsArchived(Guid projectId) =>
            _archivedProjects.TryGetValue(projectId, out var isArchived) && isArchived;

        private static QuotePartDraftDto ToQuotePartDraft(CapturedProjectPartCreate part) =>
            new()
            {
                PartId = part.ProjectServicePartId,
                FileId = Guid.Empty,
                UploadId = part.StoragePath ?? part.FileName,
                FileName = part.FileName,
                ProcessId = part.ProcessId,
                MaterialId = part.MaterialId?.ToString("D") ?? string.Empty,
                Quantity = part.Quantity,
                VolumeCc = 1m,
                SurfaceAreaCm2 = 1m,
                StoragePath = part.StoragePath,
                Status = "Draft",
                IsManifold = true,
                DfmAcknowledged = part.DfmAcknowledged,
                DrawingFiles = part.DrawingFiles.ToList()
            };
    }

    public sealed class EmptyProjectServiceClient : IProjectServiceClient
    {
        public Task<ProjectServiceDraftProjectResult?> CreateDraftProjectAsync(
            Guid customerId,
            string customerName,
            CreateDraftProjectRequest request,
            Func<QuotePartDraftDto, CancellationToken, Task<Guid?>> resolveMaterialIdAsync,
            CancellationToken ct = default) =>
            Task.FromResult<ProjectServiceDraftProjectResult?>(null);

        public Task<DuplicateDraftProjectResponse?> DuplicateDraftProjectAsync(
            Guid customerId,
            string customerName,
            Guid projectId,
            DuplicateDraftProjectRequest request,
            Func<QuotePartDraftDto, CancellationToken, Task<Guid?>> resolveMaterialIdAsync,
            CancellationToken ct = default) =>
            Task.FromResult<DuplicateDraftProjectResponse?>(null);

        public Task<IReadOnlyList<CustomerProjectNavItemDto>> GetProjectNavigationAsync(
            Guid customerId,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CustomerProjectNavItemDto>>([]);

        public Task<CustomerProjectDetailResponse?> GetProjectDetailAsync(
            Guid customerId,
            Guid projectId,
            CancellationToken ct = default) =>
            Task.FromResult<CustomerProjectDetailResponse?>(null);

        public Task<ProjectManagementResponse?> SetProjectPinnedAsync(
            Guid customerId,
            Guid projectId,
            bool isPinned,
            CancellationToken ct = default) =>
            Task.FromResult<ProjectManagementResponse?>(null);

        public Task<ProjectManagementResponse?> ArchiveProjectAsync(
            Guid customerId,
            Guid projectId,
            CancellationToken ct = default) =>
            Task.FromResult<ProjectManagementResponse?>(null);

        public Task<ProjectManagementResponse?> RequestProjectReviewAsync(
            Guid customerId,
            Guid projectId,
            string note,
            CancellationToken ct = default) =>
            Task.FromResult<ProjectManagementResponse?>(null);

        public Task<IReadOnlyList<QuoteAgentSearchResultDto>> SearchProjectResultsAsync(
            Guid customerId,
            string? query,
            int limit,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<QuoteAgentSearchResultDto>>([]);
    }

    public sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Maliev.QuoteEngine.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FakeOrderServiceClient : IOrderServiceClient
    {
        private readonly ConcurrentDictionary<string, List<CustomerOrderSummaryDto>> _ordersByCustomer = new();
        private readonly ConcurrentDictionary<string, CustomerOrderDetailDto> _ordersByNumber = new();
        private string? _statusToFailOnce;

        public ConcurrentQueue<CapturedOrderStatusUpdate> StatusUpdates { get; } = new();

        public ConcurrentQueue<OrderCreateRequest> CreateRequests { get; } = new();

        public CapturedOrderDeliverySnapshot? LastDeliverySnapshot { get; private set; }

        public OrderCreateRequest? LastCreateRequest { get; private set; }

        public TimeSpan CreateDelay { get; set; }

        public void FailNextStatus(string status) => _statusToFailOnce = status;

        public void MarkPaid(string orderNumber)
        {
            if (!_ordersByNumber.TryGetValue(orderNumber, out var detail))
            {
                return;
            }

            var paidAt = DateTimeOffset.UtcNow;
            _ordersByNumber[orderNumber] = detail with
            {
                CurrentStatus = "Paid",
                PaymentStatus = "Paid",
                UpdatedAt = paidAt,
                StatusHistory =
                [
                    .. detail.StatusHistory,
                    new OrderStatusEntryDto("Paid", "Payment confirmed.", paidAt)
                ],
                ManufacturingMilestones =
                [
                    new CustomerManufacturingMilestoneDto(
                        "order-received",
                        "Order received",
                        "We have received the order and attached customer requirements.",
                        "complete",
                        15,
                        detail.CreatedAt),
                    new CustomerManufacturingMilestoneDto(
                        "quote-payment",
                        "Quote and payment",
                        "Formal quote and payment confirmation are tracked before production starts.",
                        "complete",
                        35,
                        paidAt),
                    new CustomerManufacturingMilestoneDto(
                        "manufacturing",
                        "Manufacturing",
                        "The parts are queued or active on the selected manufacturing process.",
                        "current",
                        55,
                        null)
                ]
            };
        }

        public async Task<OrderCreatedResult?> CreateAsync(OrderCreateRequest request, CancellationToken ct = default)
        {
            if (CreateDelay > TimeSpan.Zero)
            {
                await Task.Delay(CreateDelay, ct);
            }

            LastCreateRequest = request;
            CreateRequests.Enqueue(request);
            var orderId = Guid.NewGuid();
            var orderNumber = $"ORD-TEST-{orderId:N}"[..16];
            var summary = new CustomerOrderSummaryDto(
                orderId, orderNumber, "Pending", DateTimeOffset.UtcNow, orderNumber);

            _ordersByCustomer.AddOrUpdate(
                request.CustomerId,
                _ => [summary],
                (_, list) => { lock (list) { list.Add(summary); return list; } });

            var receivedAt = DateTimeOffset.UtcNow;
            var detail = new CustomerOrderDetailDto(
                OrderId: orderId,
                OrderNumber: orderNumber,
                CurrentStatus: "Pending",
                PaymentStatus: "Unpaid",
                QuotedAmount: 1500m,
                QuoteCurrency: "THB",
                PromisedDeliveryDate: null,
                ActualDeliveryDate: null,
                CustomerPoNumber: request.CustomerPoNumber,
                Requirements: request.Requirements,
                CreatedAt: receivedAt,
                UpdatedAt: receivedAt,
                StatusHistory: [new OrderStatusEntryDto("Pending", "Your order has been received.", receivedAt)])
            {
                QuoteId = request.QuoteId,
                QuoteNumber = request.QuoteNumber,
                QuoteVersionId = request.QuoteVersionId,
                QuoteVersionNumber = request.QuoteVersionNumber,
                OrderFiles =
                [
                    new CustomerOrderFileDto(
                        1001,
                        "make-studio-manufacturing-packet.pdf",
                        "Supporting",
                        "Document",
                        $"orders/{orderNumber}/files/make-studio-manufacturing-packet.pdf",
                        "application/pdf",
                        18_432,
                        receivedAt)
                ],
                ManufacturingMilestones =
                [
                    new CustomerManufacturingMilestoneDto(
                        "order-received",
                        "Order received",
                        "We have received the order and attached customer requirements.",
                        "complete",
                        15,
                        receivedAt),
                    new CustomerManufacturingMilestoneDto(
                        "quote-payment",
                        "Quote and payment",
                        "Formal quote and payment confirmation are tracked before production starts.",
                        "current",
                        35,
                        null),
                    new CustomerManufacturingMilestoneDto(
                        "manufacturing",
                        "Manufacturing",
                        "The parts are queued or active on the selected manufacturing process.",
                        "pending",
                        55,
                        null)
                ]
            };
            _ordersByNumber[orderNumber] = detail;

            return new OrderCreatedResult
            {
                OrderId = orderId,
                OrderNumber = orderNumber,
                Status = "Pending"
            };
        }

        public Task<IReadOnlyList<CustomerOrderSummaryDto>> GetByCustomerAsync(string customerId, CancellationToken ct = default)
        {
            IReadOnlyList<CustomerOrderSummaryDto> result =
                _ordersByCustomer.TryGetValue(customerId, out var list) ? [.. list] : [];
            return Task.FromResult(result);
        }

        public Task<CustomerOrderDetailDto?> GetDetailAsync(string orderNumber, CancellationToken ct = default) =>
            Task.FromResult(_ordersByNumber.TryGetValue(orderNumber, out var detail) ? detail : null);

        public Task<bool> AddStatusAsync(string orderId, string status, CancellationToken ct = default)
        {
            if (string.Equals(_statusToFailOnce, status, StringComparison.OrdinalIgnoreCase))
            {
                _statusToFailOnce = null;
                return Task.FromResult(false);
            }

            StatusUpdates.Enqueue(new CapturedOrderStatusUpdate(orderId, status));
            if (_ordersByNumber.TryGetValue(orderId, out var detail))
            {
                var updatedAt = DateTimeOffset.UtcNow;
                var paymentStatus = string.Equals(status, "Paid", StringComparison.OrdinalIgnoreCase)
                    ? "Paid"
                    : string.Equals(status, "Accepted", StringComparison.OrdinalIgnoreCase)
                        ? "Unpaid"
                        : detail.PaymentStatus;
                _ordersByNumber[orderId] = detail with
                {
                    CurrentStatus = status,
                    PaymentStatus = paymentStatus,
                    UpdatedAt = updatedAt,
                    StatusHistory =
                    [
                        .. detail.StatusHistory,
                        new OrderStatusEntryDto(status, $"Order advanced to {status}.", updatedAt)
                    ]
                };
            }

            return Task.FromResult(true);
        }

        public Task<bool> UpdateDeliverySnapshotAsync(
            OrderDeliverySnapshotRequest request,
            CancellationToken ct = default)
        {
            LastDeliverySnapshot = new CapturedOrderDeliverySnapshot(
                request.OrderNumber,
                request.BillingAddressId,
                request.ShippingAddressId,
                request.ShippingAddressLine1,
                request.ShippingAddressLine2,
                request.ShippingCity,
                request.ShippingProvince,
                request.ShippingPostalCode,
                request.ShippingCountry,
                request.BillingCompanyName,
                request.BillingVatNumber,
                request.DeliveryContactName,
                request.DeliveryContactPhone,
                request.DeliveryContactEmail);

            return Task.FromResult(true);
        }
    }

    public sealed class FakeCustomerServiceClient(bool allowProfileLookup = true) : ICustomerServiceClient
    {
        private static readonly Guid DefaultBillingAddressId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        private static readonly Guid DefaultShippingAddressId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        private readonly ConcurrentDictionary<Guid, CustomerProfileResponse> _profilesById = new();
        private readonly ConcurrentDictionary<Guid, List<CustomerAddressDto>> _addressesByCustomer = new();
        private readonly ConcurrentDictionary<Guid, List<CustomerDocumentDto>> _documentsByCustomer = new();
        private readonly ConcurrentDictionary<Guid, List<CustomerNdaDto>> _ndasByCustomer = new();
        private uint _nextVersion = 1;

        public Task<CustomerProfileResponse?> GetByIdAsync(Guid customerId, CancellationToken ct = default) =>
            Task.FromResult(allowProfileLookup && _profilesById.TryGetValue(customerId, out var profile) ? profile : null);

        public Task<CustomerProfileResponse?> GetByEmailAsync(string email, CancellationToken ct = default) =>
            Task.FromResult<CustomerProfileResponse?>(StoreProfile(CreateProfile(email)));

        public Task<CustomerProfileResponse?> EnsureCustomerAsync(
            string email,
            string displayName,
            string phone = "",
            CancellationToken ct = default) =>
            Task.FromResult<CustomerProfileResponse?>(StoreProfile(CreateProfile(email, displayName, phone)));

        public Task<CustomerProfileResponse?> EnsureCompanyBillingIdentityAsync(
            Guid customerId,
            string companyName,
            string? vatNumber,
            string? phone,
            CancellationToken ct = default)
        {
            var existing = _profilesById.TryGetValue(customerId, out var profile)
                ? profile
                : CreateProfile($"customer-{customerId:N}@example.com", companyName, phone ?? string.Empty);
            var updated = existing with
            {
                CompanyName = string.IsNullOrWhiteSpace(companyName) ? "MALIEV Buyer Co." : companyName,
                VatNumber = string.IsNullOrWhiteSpace(vatNumber) ? "1234567890123" : vatNumber
            };
            _profilesById[customerId] = updated;
            return Task.FromResult<CustomerProfileResponse?>(updated);
        }

        public Task<CustomerProfileResponse?> UpdateCustomerProfileAsync(
            Guid customerId,
            string displayName,
            string? phone,
            string? companyName,
            string? vatNumber,
            string preferredLanguage,
            string timezone,
            CancellationToken ct = default)
        {
            var existing = _profilesById.TryGetValue(customerId, out var profile)
                ? profile
                : CreateProfile($"customer-{customerId:N}@example.com", displayName, phone ?? string.Empty);
            var updated = existing with
            {
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? existing.DisplayName : displayName,
                Phone = string.IsNullOrWhiteSpace(phone) ? existing.Phone : phone,
                CompanyName = string.IsNullOrWhiteSpace(companyName) ? existing.CompanyName : companyName,
                PreferredLanguage = string.IsNullOrWhiteSpace(preferredLanguage) ? existing.PreferredLanguage : preferredLanguage,
                Timezone = string.IsNullOrWhiteSpace(timezone) ? existing.Timezone : timezone,
                VatNumber = string.IsNullOrWhiteSpace(vatNumber) ? existing.VatNumber : vatNumber
            };
            _profilesById[customerId] = updated;
            return Task.FromResult<CustomerProfileResponse?>(updated);
        }

        public Task<HttpResponseMessage> GetCustomerAddressesAsync(Guid customerId, CancellationToken cancellationToken)
        {
            var list = _addressesByCustomer.GetOrAdd(customerId, _ => CreateDefaultAddresses());
            IReadOnlyList<CustomerAddressDto> addresses = [.. list];
            return Task.FromResult(JsonResponse(addresses));
        }

        public Task<HttpResponseMessage> CreateCustomerAddressAsync(object request, CancellationToken cancellationToken)
        {
            var root = JsonSerializer.SerializeToElement(request, JsonOptions);
            var ownerId = GetGuid(root, "ownerId", "OwnerId") ?? Guid.Empty;
            var address = new CustomerAddressDto
            {
                Id = Guid.NewGuid(),
                Type = GetString(root, "type", "Type") ?? "Shipping",
                IsDefault = GetBool(root, "isDefault", "IsDefault"),
                PlaceLabel = GetString(root, "placeLabel", "PlaceLabel"),
                PlaceLabelOther = GetString(root, "placeLabelOther", "PlaceLabelOther"),
                AddressLine1 = GetString(root, "addressLine1", "AddressLine1") ?? string.Empty,
                AddressLine2 = GetString(root, "addressLine2", "AddressLine2"),
                AddressLine3 = GetString(root, "addressLine3", "AddressLine3"),
                District = GetString(root, "district", "District"),
                City = GetString(root, "city", "City") ?? string.Empty,
                StateProvince = GetString(root, "stateProvince", "StateProvince") ?? string.Empty,
                PostalCode = GetString(root, "postalCode", "PostalCode") ?? string.Empty,
                CountryId = GetGuid(root, "countryId", "CountryId") ?? FakeCountryServiceClient.ThailandCountryId,
                RecipientName = GetString(root, "recipientName", "RecipientName"),
                RecipientPhone = GetString(root, "recipientPhone", "RecipientPhone"),
                DriverNote = GetString(root, "driverNote", "DriverNote"),
                AddressSource = GetString(root, "addressSource", "AddressSource") ?? "Manual",
                GooglePlaceId = GetString(root, "googlePlaceId", "GooglePlaceId"),
                FormattedAddress = GetString(root, "formattedAddress", "FormattedAddress"),
                Latitude = GetDecimal(root, "latitude", "Latitude"),
                Longitude = GetDecimal(root, "longitude", "Longitude"),
                Version = _nextVersion++
            };

            var addresses = _addressesByCustomer.GetOrAdd(ownerId, _ => []);
            lock (addresses)
            {
                ResetSameRoleDefault(addresses, address);
                addresses.Add(address);
            }

            return Task.FromResult(JsonResponse(address, HttpStatusCode.Created));
        }

        public Task<HttpResponseMessage> UpdateCustomerAddressAsync(Guid addressId, object request, CancellationToken cancellationToken)
        {
            var root = JsonSerializer.SerializeToElement(request, JsonOptions);
            foreach (var addresses in _addressesByCustomer.Values)
            {
                lock (addresses)
                {
                    var address = addresses.FirstOrDefault(item => item.Id == addressId);
                    if (address is null)
                    {
                        continue;
                    }

                    address.Type = GetString(root, "type", "Type") ?? address.Type;
                    address.IsDefault = GetBool(root, "isDefault", "IsDefault");
                    address.PlaceLabel = GetString(root, "placeLabel", "PlaceLabel");
                    address.PlaceLabelOther = GetString(root, "placeLabelOther", "PlaceLabelOther");
                    address.AddressLine1 = GetString(root, "addressLine1", "AddressLine1") ?? address.AddressLine1;
                    address.AddressLine2 = GetString(root, "addressLine2", "AddressLine2");
                    address.AddressLine3 = GetString(root, "addressLine3", "AddressLine3");
                    address.District = GetString(root, "district", "District");
                    address.City = GetString(root, "city", "City") ?? address.City;
                    address.StateProvince = GetString(root, "stateProvince", "StateProvince") ?? address.StateProvince;
                    address.PostalCode = GetString(root, "postalCode", "PostalCode") ?? address.PostalCode;
                    address.RecipientName = GetString(root, "recipientName", "RecipientName");
                    address.RecipientPhone = GetString(root, "recipientPhone", "RecipientPhone");
                    address.DriverNote = GetString(root, "driverNote", "DriverNote");
                    address.AddressSource = GetString(root, "addressSource", "AddressSource") ?? address.AddressSource;
                    address.GooglePlaceId = GetString(root, "googlePlaceId", "GooglePlaceId");
                    address.FormattedAddress = GetString(root, "formattedAddress", "FormattedAddress");
                    address.Latitude = GetDecimal(root, "latitude", "Latitude");
                    address.Longitude = GetDecimal(root, "longitude", "Longitude");
                    address.Version = _nextVersion++;
                    ResetSameRoleDefault(addresses, address);
                    return Task.FromResult(JsonResponse(address));
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        public Task<HttpResponseMessage> DeleteCustomerAddressAsync(Guid addressId, object request, CancellationToken cancellationToken)
        {
            foreach (var addresses in _addressesByCustomer.Values)
            {
                lock (addresses)
                {
                    var address = addresses.FirstOrDefault(item => item.Id == addressId);
                    if (address is null)
                    {
                        continue;
                    }

                    addresses.Remove(address);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        public Task<IReadOnlyList<CustomerDocumentDto>?> GetCustomerDocumentsAsync(
            Guid customerId,
            CancellationToken cancellationToken)
        {
            var documents = _documentsByCustomer.GetOrAdd(customerId, _ => []);
            lock (documents)
            {
                return Task.FromResult<IReadOnlyList<CustomerDocumentDto>?>(documents.ToList());
            }
        }

        public Task<CustomerDocumentDto?> CreateCustomerDocumentAsync(
            Guid customerId,
            CustomerDocumentUploadRequest request,
            CancellationToken cancellationToken)
        {
            var document = new CustomerDocumentDto(
                Guid.NewGuid(),
                request.FileName.Trim(),
                request.Kind.Trim(),
                DateTimeOffset.UtcNow,
                request.StoragePath.Trim(),
                request.ContentType.Trim(),
                request.FileSizeBytes,
                string.IsNullOrWhiteSpace(request.OrderNumber) ? null : request.OrderNumber.Trim());
            var documents = _documentsByCustomer.GetOrAdd(customerId, _ => []);
            lock (documents)
            {
                documents.Add(document);
            }

            return Task.FromResult<CustomerDocumentDto?>(document);
        }

        public Task<IReadOnlyList<CustomerNdaDto>?> GetCustomerNdasAsync(
            Guid customerId,
            CancellationToken cancellationToken)
        {
            var ndas = _ndasByCustomer.GetOrAdd(customerId, _ =>
            [
                new CustomerNdaDto(
                    Guid.Parse("f3bfdbb3-7078-4dd8-b9d7-7b7068995821"),
                    "Mutual NDA",
                    "Active",
                    DateTimeOffset.UtcNow.AddDays(-7),
                    DateTimeOffset.UtcNow.AddDays(83))
            ]);
            lock (ndas)
            {
                return Task.FromResult<IReadOnlyList<CustomerNdaDto>?>(ndas.ToList());
            }
        }

        public Task<CustomerMemoryQueryResponse> GetCustomerMemoriesAsync(
            Guid customerId,
            string? query,
            int limit,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new CustomerMemoryQueryResponse
            {
                CustomerId = customerId,
                Query = query ?? string.Empty,
                Limit = limit
            });
        }

        public Task<CustomerMemoryResponse?> ObserveCustomerMemoryAsync(
            Guid customerId,
            CustomerMemoryObserveRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<CustomerMemoryResponse?>(null);
        }

        private static CustomerProfileResponse CreateProfile(string email, string displayName = "Quote customer", string phone = "")
        {
            var normalizedEmail = string.IsNullOrWhiteSpace(email) ? "customer@example.com" : email.Trim().ToLowerInvariant();
            var idBytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(normalizedEmail));
            return new CustomerProfileResponse(
                new Guid(idBytes),
                string.IsNullOrWhiteSpace(displayName) ? normalizedEmail : displayName,
                normalizedEmail,
                phone,
                "MALIEV Test Buyer Co., Ltd.",
                "en",
                NdaExpiresAt: DateTimeOffset.UtcNow.AddDays(90),
                VatNumber: "TH-0123456789012");
        }

        private CustomerProfileResponse StoreProfile(CustomerProfileResponse profile)
        {
            _profilesById[profile.CustomerId] = profile;
            return profile;
        }

        private static void ResetSameRoleDefault(List<CustomerAddressDto> addresses, CustomerAddressDto current)
        {
            if (!current.IsDefault)
            {
                return;
            }

            foreach (var address in addresses.Where(item => item.Id != current.Id && string.Equals(item.Type, current.Type, StringComparison.OrdinalIgnoreCase)))
            {
                address.IsDefault = false;
            }
        }

        private static List<CustomerAddressDto> CreateDefaultAddresses()
        {
            return
            [
                new CustomerAddressDto
                {
                    Id = DefaultBillingAddressId,
                    Type = "Billing",
                    IsDefault = true,
                    AddressLine1 = "12 Billing Road",
                    City = "Bangkok",
                    StateProvince = "Bangkok",
                    PostalCode = "10110",
                    CountryId = FakeCountryServiceClient.ThailandCountryId,
                    RecipientName = "Accounts Payable",
                    RecipientPhone = "+66810000001",
                    AddressSource = "Manual",
                    Version = 1
                },
                new CustomerAddressDto
                {
                    Id = DefaultShippingAddressId,
                    Type = "Shipping",
                    IsDefault = true,
                    AddressLine1 = "34 Shipping Road",
                    City = "Bangkok",
                    StateProvince = "Bangkok",
                    PostalCode = "10110",
                    CountryId = FakeCountryServiceClient.ThailandCountryId,
                    RecipientName = "Receiving",
                    RecipientPhone = "+66810000002",
                    AddressSource = "Manual",
                    Version = 1
                }
            ];
        }

        private static string? GetString(JsonElement root, params string[] names)
        {
            foreach (var name in names)
            {
                if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString();
                }
            }

            return null;
        }

        private static Guid? GetGuid(JsonElement root, params string[] names)
        {
            foreach (var name in names)
            {
                if (root.TryGetProperty(name, out var value) &&
                    value.ValueKind == JsonValueKind.String &&
                    Guid.TryParse(value.GetString(), out var id))
                {
                    return id;
                }
            }

            return null;
        }

        private static bool GetBool(JsonElement root, params string[] names)
        {
            foreach (var name in names)
            {
                if (root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    return value.GetBoolean();
                }
            }

            return false;
        }

        private static decimal? GetDecimal(JsonElement root, params string[] names)
        {
            foreach (var name in names)
            {
                if (root.TryGetProperty(name, out var value) &&
                    value.ValueKind == JsonValueKind.Number &&
                    value.TryGetDecimal(out var number))
                {
                    return number;
                }
            }

            return null;
        }

        private static HttpResponseMessage JsonResponse<T>(T value, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            return new HttpResponseMessage(statusCode)
            {
                Content = JsonContent.Create(value, options: JsonOptions)
            };
        }

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    }

    private sealed class FakeCountryServiceClient : ICountryServiceClient
    {
        internal static readonly Guid ThailandCountryId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        public Task<HttpResponseMessage> GetCountryByIso2Async(string iso2, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { id = ThailandCountryId }, options: new JsonSerializerOptions(JsonSerializerDefaults.Web))
            });
        }
    }

    private sealed class FakeRegistryServiceClient : IRegistryServiceClient
    {
        public Task<HttpResponseMessage> SearchThaiLocationsAsync(string query, int limit, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    data = new[]
                    {
                        new
                        {
                            id = Guid.Parse("1f54cb83-cfa2-4e4c-baa8-e902e458019b"),
                            postalCode = "11120",
                            subDistrictTh = "คลองข่อย",
                            districtTh = "ปากเกร็ด",
                            provinceTh = "นนทบุรี",
                            subDistrictEn = "Khlong Khoi",
                            districtEn = "Pak Kret",
                            provinceEn = "Nonthaburi"
                        }
                    }
                }, options: new JsonSerializerOptions(JsonSerializerDefaults.Web))
            });
        }
    }

    private sealed class FakePaymentServiceClient : IPaymentServiceClient
    {
        public static ConcurrentQueue<string> IdempotencyKeys { get; } = new();

        public static ConcurrentQueue<CapturedPaymentInitiation> Initiations { get; } = new();

        public Task<PaymentInitiatedResult?> InitiateAsync(
            string customerId, string orderId, string orderNumber,
            decimal amount, string currency,
            string returnUrl, string cancelUrl, string idempotencyKey,
            Guid? billingAddressId, Guid? shippingAddressId,
            string? billingCompanyName, string? billingVatNumber,
            string? deliveryContactName, string? deliveryContactPhone, string? deliveryContactEmail,
            bool acceptedTerms,
            CancellationToken ct = default)
        {
            IdempotencyKeys.Enqueue(idempotencyKey);
            Initiations.Enqueue(new CapturedPaymentInitiation(
                customerId,
                orderId,
                orderNumber,
                amount,
                currency,
                returnUrl,
                cancelUrl,
                idempotencyKey,
                billingAddressId,
                shippingAddressId,
                billingCompanyName,
                billingVatNumber,
                deliveryContactName,
                deliveryContactPhone,
                deliveryContactEmail));
            return Task.FromResult<PaymentInitiatedResult?>(new PaymentInitiatedResult
            {
                TransactionId = Guid.NewGuid(),
                PaymentUrl = $"https://pay.test.example.com/hosted/{Guid.NewGuid():N}",
                Status = "1"
            });
        }
    }

    private sealed class FakeInvoiceServiceClient : IInvoiceServiceClient
    {
        public InvoiceCreateForOrderRequest? LastCreateRequest { get; private set; }

        public InvoicePreparedResult? LastPreparedResult { get; private set; }

        public Task<InvoicePreparedResult?> CreateAndFinalizeForOrderAsync(
            InvoiceCreateForOrderRequest request,
            CancellationToken ct = default)
        {
            LastCreateRequest = request;
            LastPreparedResult = new InvoicePreparedResult
            {
                InvoiceId = Guid.NewGuid(),
                InvoiceNumber = $"INV-{DateTime.UtcNow:yyyyMMdd}-000001",
                Status = "Finalized",
                GrandTotal = request.Lines.Sum(line => line.Quantity * line.UnitPrice),
                Currency = request.Currency
            };
            return Task.FromResult<InvoicePreparedResult?>(LastPreparedResult);
        }
    }

    public sealed class EmptyPricingServiceClient : IQePricingServiceClient
    {
        public Task<PricingCalculationResult?> CalculateAsync(
            QuotePartDraftDto part,
            Guid customerId,
            Guid materialId,
            Guid manufacturingProcessId,
            string leadTimeCode,
            decimal? toleranceAdditionalCostPercent,
            CancellationToken ct = default) =>
            Task.FromResult<PricingCalculationResult?>(null);
    }

    public sealed class ZeroPricingServiceClient : IQePricingServiceClient
    {
        public Task<PricingCalculationResult?> CalculateAsync(
            QuotePartDraftDto part,
            Guid customerId,
            Guid materialId,
            Guid manufacturingProcessId,
            string leadTimeCode,
            decimal? toleranceAdditionalCostPercent,
            CancellationToken ct = default) =>
            Task.FromResult<PricingCalculationResult?>(new PricingCalculationResult
            {
                UnitPrice = 0m,
                TotalAmount = 0m,
                UnitPriceBeforeVolumeDiscount = 0m,
                VolumeDiscountUnitAmount = 0m,
                VolumeDiscountPercent = 0m,
                ConfidenceScore = 0m,
                EngineName = "zero-pricing",
                AuditId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
                EstimatedLeadTimeDays = 0
            });
    }

    private sealed class FakePricingServiceClient : IQePricingServiceClient
    {
        public Task<PricingCalculationResult?> CalculateAsync(
            QuotePartDraftDto part,
            Guid customerId,
            Guid materialId,
            Guid manufacturingProcessId,
            string leadTimeCode,
            decimal? toleranceAdditionalCostPercent,
            CancellationToken ct = default)
        {
            var processRate = part.ProcessId.Equals("cnc", StringComparison.OrdinalIgnoreCase)
                ? 520m
                : part.ProcessId.Equals("sla", StringComparison.OrdinalIgnoreCase) ? 180m : 95m;
            var setup = part.ProcessId.Equals("cnc", StringComparison.OrdinalIgnoreCase) ? 850m : 120m;
            var leadTimeMultiplier = leadTimeCode.Equals("EXPRESS", StringComparison.OrdinalIgnoreCase)
                ? 1.35m
                : leadTimeCode.Equals("ECONOMY", StringComparison.OrdinalIgnoreCase) ? 0.90m : 1m;
            var toleranceMultiplier = 1m + ((toleranceAdditionalCostPercent ?? 0m) / 100m);
            var unitPrice = Math.Round((setup + Math.Max(part.VolumeCc, 1m) * processRate) * leadTimeMultiplier * toleranceMultiplier, 2);
            return Task.FromResult<PricingCalculationResult?>(new PricingCalculationResult
            {
                UnitPrice = unitPrice,
                TotalAmount = Math.Round(unitPrice * part.Quantity, 2),
                UnitPriceBeforeVolumeDiscount = unitPrice,
                VolumeDiscountUnitAmount = 0m,
                VolumeDiscountPercent = 0m,
                ConfidenceScore = 0.92m,
                EngineName = "test-pricing",
                AuditId = Guid.NewGuid(),
                EstimatedLeadTimeDays = leadTimeCode.Equals("EXPRESS", StringComparison.OrdinalIgnoreCase) ? 3 : 6
            });
        }
    }

    public sealed class SentinelPricingServiceClient : IQePricingServiceClient
    {
        public Task<PricingCalculationResult?> CalculateAsync(
            QuotePartDraftDto part,
            Guid customerId,
            Guid materialId,
            Guid manufacturingProcessId,
            string leadTimeCode,
            decimal? toleranceAdditionalCostPercent,
            CancellationToken ct = default) =>
            Task.FromResult<PricingCalculationResult?>(new PricingCalculationResult
            {
                UnitPrice = 99_999m,
                TotalAmount = 99_999m * part.Quantity,
                UnitPriceBeforeVolumeDiscount = 99_999m,
                VolumeDiscountUnitAmount = 0m,
                VolumeDiscountPercent = 0m,
                ConfidenceScore = 1m,
                EngineName = "sentinel-pricing",
                AuditId = Guid.Parse("99999999-9999-9999-9999-999999999999"),
                EstimatedLeadTimeDays = 99
            });
    }
}

[Collection(QuoteEngineEndpointTestCollection.Name)]
public sealed class QuoteEngineEndpointTests(QuoteEngineWebApplicationFactory factory)
    : IClassFixture<QuoteEngineWebApplicationFactory>
{
    private static readonly Guid TestBillingAddressId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TestShippingAddressId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task Root_loads_chat_workspace_app_shell_anonymously()
    {
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/");

        Assert.Contains("<div id=\"app\"></div>", html, StringComparison.Ordinal);
        Assert.Contains("<div id=\"quote-startup\"", html, StringComparison.Ordinal);
        Assert.Contains("_framework/blazor.webassembly.js", html, StringComparison.Ordinal);
        Assert.Contains("window.getMalievAuth=function(){return {\"isSignedIn\":false", html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"landing-shell\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Get manufacturing quotes", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Quotes_route_loads_chat_workspace_app_shell_anonymously()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/quotes");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<div id=\"app\"></div>", html, StringComparison.Ordinal);
        Assert.Contains("<div id=\"quote-startup\"", html, StringComparison.Ordinal);
        Assert.Contains("_framework/blazor.webassembly.js", html, StringComparison.Ordinal);
        Assert.Contains("window.getMalievAuth=function(){return {\"isSignedIn\":false", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Quotes_route_prehydrates_signed_in_customer_from_name_identifier_claim()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var signIn = await client.GetAsync("/test/sign-in?email=nameid-only@example.com&omitCustomerId=true");
        signIn.EnsureSuccessStatusCode();
        var session = await client.GetFromJsonAsync<QuoteAuthStatusResponse>("/quote/v1/auth/session");
        var response = await client.GetAsync("/quotes");
        var html = await response.Content.ReadAsStringAsync();

        Assert.NotNull(session);
        Assert.True(session!.IsSignedIn);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("window.getMalievAuth=function(){return {\"isSignedIn\":true", html, StringComparison.Ordinal);
        Assert.Contains(session.CustomerId!.Value.ToString("D"), html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Quotes_route_injects_static_asset_map_for_fingerprinted_dotnet_module()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/quotes");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("window.malievStaticAssetMap", html, StringComparison.Ordinal);
        Assert.Contains("\"_framework/dotnet.js\"", html, StringComparison.Ordinal);
        Assert.Contains("\"_framework/dotnet.", html, StringComparison.Ordinal);

        var mapJsonStart = html.IndexOf("window.malievStaticAssetMap=", StringComparison.Ordinal);
        Assert.True(mapJsonStart >= 0);
        mapJsonStart += "window.malievStaticAssetMap=".Length;
        var mapJsonEnd = html.IndexOf(";window.getMalievAuth", mapJsonStart, StringComparison.Ordinal);
        Assert.True(mapJsonEnd > mapJsonStart);
        using var document = JsonDocument.Parse(html[mapJsonStart..mapJsonEnd]);
        var dotnetModule = document.RootElement.GetProperty("_framework/dotnet.js").GetString();
        Assert.False(string.IsNullOrWhiteSpace(dotnetModule));

        var moduleResponse = await client.GetAsync($"/{dotnetModule}");
        var moduleBody = await moduleResponse.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, moduleResponse.StatusCode);
        Assert.Equal("text/javascript", moduleResponse.Content.Headers.ContentType?.MediaType);
        Assert.True(moduleBody.Length > 10_000);
    }

    [Fact]
    public async Task Quote_detail_route_stays_customer_scoped_for_anonymous_users()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync($"/quotes/{Guid.NewGuid():D}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.StartsWith("/quotes?auth=sign-in", response.Headers.Location.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Auth_pages_redirect_to_studio_dialog_without_loading_wasm_bundle()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var signIn = await client.GetAsync("/auth/sign-in?returnUrl=/quote/new");
        var signUp = await client.GetAsync("/auth/sign-up?returnUrl=/quote/new");

        // Auth pages open the studio sign-in dialog; sign-in completes against
        // AuthService inside this BFF, never via the Maliev.Web frontend.
        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
        Assert.NotNull(signIn.Headers.Location);
        Assert.StartsWith("/quotes?auth=sign-in", signIn.Headers.Location.OriginalString, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.Redirect, signUp.StatusCode);
        Assert.NotNull(signUp.Headers.Location);
        Assert.StartsWith("/quotes?auth=sign-up", signUp.Headers.Location.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeometryRuntime_manifest_is_public_and_preserves_no_cache()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/quote/v1/geometry/runtime/manifest");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"runtimeVersion\":\"0.1.0\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeometryRuntime_manifest_falls_back_to_packaged_runtime_when_geometry_service_times_out()
    {
        await using var timeoutFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IQuoteGeometryRuntimeClient>();
                services.AddSingleton<IQuoteGeometryRuntimeClient>(
                    new QuoteEngineWebApplicationFactory.TimeoutQuoteGeometryRuntimeClient());
            });
        });
        using var client = timeoutFactory.CreateClient();

        var response = await client.GetAsync("/quote/v1/geometry/runtime/manifest");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains($"\"runtimeVersion\":\"{EmbeddedRuntimeWorkerVersion()}\"", body, StringComparison.Ordinal);
        Assert.Contains("\"runtimeKind\":\"browser-first-geometry\"", body, StringComparison.Ordinal);
        Assert.Contains(
            "\"directBrowserViewerExtensions\":[\".3mf\",\".glb\",\".gltf\",\".obj\",\".stl\"]",
            body,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"interactiveServerDfmFallbackForBrowserPrimaryUploads\":false",
            body,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeometryRuntime_manifest_falls_back_to_packaged_runtime_when_geometry_service_polly_times_out()
    {
        await using var timeoutFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IQuoteGeometryRuntimeClient>();
                services.AddSingleton<IQuoteGeometryRuntimeClient>(
                    new QuoteEngineWebApplicationFactory.PollyTimeoutQuoteGeometryRuntimeClient());
            });
        });
        using var client = timeoutFactory.CreateClient();

        var response = await client.GetAsync("/quote/v1/geometry/runtime/manifest");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains($"\"runtimeVersion\":\"{EmbeddedRuntimeWorkerVersion()}\"", body, StringComparison.Ordinal);
        Assert.Contains("\"runtimeKind\":\"browser-first-geometry\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeometryRuntime_asset_falls_back_to_packaged_runtime_when_geometry_service_times_out()
    {
        await using var timeoutFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IQuoteGeometryRuntimeClient>();
                services.AddSingleton<IQuoteGeometryRuntimeClient>(
                    new QuoteEngineWebApplicationFactory.TimeoutQuoteGeometryRuntimeClient());
            });
        });
        using var client = timeoutFactory.CreateClient();

        var manifest = await client.GetFromJsonAsync<JsonElement>("/quote/v1/geometry/runtime/manifest");
        var workerPath = manifest.GetProperty("assets").GetProperty("worker").GetString();
        Assert.NotNull(workerPath);
        var workerName = workerPath.Split('/').Last();

        var response = await client.GetAsync($"/quote/v1/geometry/runtime/assets/{workerName}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("immutable", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.Equal("text/javascript; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Contains(
            "MALIEV_BROWSER_GEOMETRY_RUNTIME_VERSION",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeometryRuntime_asset_falls_back_to_packaged_runtime_when_geometry_service_polly_times_out()
    {
        await using var timeoutFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IQuoteGeometryRuntimeClient>();
                services.AddSingleton<IQuoteGeometryRuntimeClient>(
                    new QuoteEngineWebApplicationFactory.PollyTimeoutQuoteGeometryRuntimeClient());
            });
        });
        using var client = timeoutFactory.CreateClient();

        var manifest = await client.GetFromJsonAsync<JsonElement>("/quote/v1/geometry/runtime/manifest");
        var workerPath = manifest.GetProperty("assets").GetProperty("worker").GetString();
        Assert.NotNull(workerPath);
        var workerName = workerPath.Split('/').Last();

        var response = await client.GetAsync($"/quote/v1/geometry/runtime/assets/{workerName}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("immutable", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.Equal("text/javascript; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Contains(
            "MALIEV_BROWSER_GEOMETRY_RUNTIME_VERSION",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeometryRuntime_asset_serves_packaged_runtime_without_calling_geometry_service_when_asset_name_matches()
    {
        var provider = new GeometryRuntimeFallbackProvider();
        using var manifestDocument = JsonDocument.Parse(Encoding.UTF8.GetString(provider.GetManifest().Content));
        var workerPath = manifestDocument.RootElement.GetProperty("assets").GetProperty("worker").GetString();
        Assert.NotNull(workerPath);
        var workerName = workerPath.Split('/').Last();
        await using var noDownstreamFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IQuoteGeometryRuntimeClient>();
                services.AddSingleton<IQuoteGeometryRuntimeClient>(
                    new QuoteEngineWebApplicationFactory.ThrowIfCalledQuoteGeometryRuntimeClient());
            });
        });
        using var client = noDownstreamFactory.CreateClient();

        var response = await client.GetAsync($"/quote/v1/geometry/runtime/assets/{workerName}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("immutable", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.Equal("text/javascript; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Contains(
            "MALIEV_BROWSER_GEOMETRY_RUNTIME_VERSION",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeometryRuntime_asset_is_public_and_preserves_immutable_cache()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            "/quote/v1/geometry/runtime/assets/client-geometry-runtime.abc.worker.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("immutable", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.Equal("application/wasm", response.Content.Headers.ContentType?.ToString());
        Assert.Equal([0x00, 0x61, 0xFF, 0x7F], await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task GeometryRuntime_missing_asset_returns404()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/quote/v1/geometry/runtime/assets/missing.worker.js");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(
            "Runtime asset not found",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeometryRuntime_telemetry_accepts_browser_local_completion_without_auth()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/quote/v1/geometry/runtime/telemetry",
            new
            {
                processCode = "CNC_MILL",
                runtimeVersion = "1.0.0",
                algorithmVersion = "browser-first-dfm-v1",
                authority = "local_primary",
                executionMode = "primary_interactive",
                accepted = true,
                issueCount = 1,
                warningCount = 1,
                faceCount = 27122,
                inputHash = "abc123"
            });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task GeometryRuntime_telemetry_when_accepted_metrics_hydrates_analysis_status()
    {
        using var client = factory.CreateClient();
        var initResponse = await client.PostAsJsonAsync("/quote/v1/uploads/resumable", new InitiateQuoteUploadRequest
        {
            QuoteSessionId = "local-metrics-session",
            FileName = "local-metrics-part.stl",
            ContentType = "model/stl",
            FileSizeBytes = 12
        });
        initResponse.EnsureSuccessStatusCode();
        var upload = await initResponse.Content.ReadFromJsonAsync<InitiateQuoteUploadResponse>();
        Assert.NotNull(upload);

        var telemetryResponse = await client.PostAsJsonAsync(
            "/quote/v1/geometry/runtime/telemetry",
            new
            {
                storagePath = upload.StoragePath,
                processCode = "CNC_MILL",
                authority = "local_primary",
                executionMode = "primary_interactive",
                accepted = true,
                metrics = new
                {
                    volumeMm3 = 12_500,
                    surfaceAreaMm2 = 6_200,
                    isManifold = false,
                    nonManifoldEdgeCount = 4
                }
            });

        Assert.Equal(HttpStatusCode.NoContent, telemetryResponse.StatusCode);
        var status = await client.GetFromJsonAsync<QuoteAnalysisStatusResponse>(
            $"/quote/v1/uploads/{upload.UploadId}/analysis-status");
        Assert.NotNull(status);
        Assert.Equal(12.5m, status.VolumeCc);
        Assert.Equal(62m, status.SurfaceAreaCm2);
        Assert.False(status.IsManifold);
        Assert.Equal("Browser local DFM found 4 non-manifold edge(s).", status.NonManifoldReason);
    }

    [Fact]
    public async Task GeometryRuntime_telemetry_accepts_browser_local_terminal_unavailable_without_auth()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/quote/v1/geometry/runtime/telemetry",
            new
            {
                processCode = "CNC_MILL",
                status = "unavailable",
                reason = "input_too_large",
                authority = "local_primary",
                executionMode = "primary_interactive"
            });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task GeometryRuntime_telemetry_accepts_browser_local_start_without_auth()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/quote/v1/geometry/runtime/telemetry",
            new
            {
                processCode = "CNC_MILL",
                status = "started",
                authority = "local_primary",
                executionMode = "primary_interactive",
                inputByteCount = 84,
                inputTriangleCount = 1
            });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task ReferenceData_exposes_customer_visible_processes_and_materials()
    {
        using var client = factory.CreateClient();

        var response = await client.GetFromJsonAsync<QuoteReferenceDataResponse>("/quote/v1/reference-data");

        Assert.NotNull(response);
        Assert.Contains(response.Processes, process => process.Id == "fdm");
        Assert.Contains(response.Materials, material => material.ProcessId == "cnc");
        Assert.Contains(response.ProcessOptions, option => option.ProcessId == "cnc" && option.ConfigKey == "deburr_edges");
        Assert.Contains(response.ProcessOptions, option => option.ProcessId == "fdm" && option.ConfigKey == "print_orientation");
        Assert.Contains("step", response.SupportedExtensions);
    }

    [Fact]
    public async Task DemoProject_is_non_mutating_sample_journey()
    {
        using var client = factory.CreateClient();

        var demo = await client.GetFromJsonAsync<QuoteEngineDemoProjectResponse>("/quote/v1/demo/project");

        Assert.NotNull(demo);
        Assert.Contains("sample", demo.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Single(demo.Parts);
        Assert.Contains("does not create", demo.Notice, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/models/sample.glb", demo.ViewerUrl);
        Assert.StartsWith("/", demo.ViewerUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResumableUpload_allows_anonymous_temporary_workspace_upload()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/quote/v1/uploads/resumable", new InitiateQuoteUploadRequest
        {
            QuoteSessionId = "session-unsigned",
            FileName = "part.stl",
            ContentType = "model/stl",
            FileSizeBytes = 12
        });

        response.EnsureSuccessStatusCode();
        var upload = await response.Content.ReadFromJsonAsync<InitiateQuoteUploadResponse>();
        Assert.NotNull(upload);
        Assert.StartsWith("quotes/temp/session-unsigned/", upload.StoragePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResumableUpload_marks_browser_primary_mesh_uploads_for_downstream_upload()
    {
        var recordingUploadClient = new QuoteEngineWebApplicationFactory.RecordingQuoteUploadServiceClient();
        using var recordingFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<QuoteUploadServiceClient>();
                services.AddSingleton<QuoteUploadServiceClient>(recordingUploadClient);
            });
        });
        using var client = recordingFactory.CreateClient();

        var response = await client.PostAsJsonAsync("/quote/v1/uploads/resumable", new InitiateQuoteUploadRequest
        {
            QuoteSessionId = "session-browser-primary",
            FileName = "part.stl",
            ContentType = "model/stl",
            FileSizeBytes = 12
        });

        response.EnsureSuccessStatusCode();

        Assert.NotNull(recordingUploadClient.LastMetadataTags);
        Assert.Equal("browser_primary", recordingUploadClient.LastMetadataTags!["geometry.executionPolicy"]);
        Assert.Equal("required", recordingUploadClient.LastMetadataTags["geometry.browserRuntime"]);
        Assert.Equal("skip_for_browser_viewable", recordingUploadClient.LastMetadataTags["geometry.serverGlbExport"]);
    }

    [Fact]
    public async Task UploadHandoff_imports_web_uploaded_files_into_active_workspace()
    {
        using var client = factory.CreateClient();
        var quoteSessionId = Guid.NewGuid();
        var token = CreateSignedWebUploadHandoffToken(new
        {
            quoteSessionId = quoteSessionId.ToString("D"),
            files = new[]
            {
                new
                {
                    uploadId = "web-upload-1",
                    fileId = Guid.NewGuid(),
                    fileName = "web-dropped-part.step",
                    storagePath = $"quotes/temp/{quoteSessionId:N}/123/web-dropped-part.step",
                    contentType = "application/step",
                    fileSizeBytes = 420_000,
                    status = "Completed"
                }
            },
            issuedAt = DateTimeOffset.UtcNow,
            expiresAt = DateTimeOffset.UtcNow.AddMinutes(15)
        });

        var response = await client.PostAsJsonAsync("/quote/v1/uploads/handoff", new QuoteUploadHandoffRequest
        {
            HandoffToken = token
        });

        response.EnsureSuccessStatusCode();
        var handoff = await response.Content.ReadFromJsonAsync<QuoteUploadHandoffResponse>();
        Assert.NotNull(handoff);
        Assert.Equal(quoteSessionId.ToString("D"), handoff.QuoteSessionId);
        var part = Assert.Single(handoff.Parts);
        Assert.Equal("web-upload-1", part.UploadId);
        Assert.Equal("Analyzed", part.Status);
        Assert.True(part.VolumeCc > 0);

        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var partJson = document.RootElement.GetProperty("parts")[0];
        Assert.True(partJson.TryGetProperty("viewerGlbUrl", out var viewerGlbUrl), "Handoff parts must include the viewer URL used by the canvas.");
        Assert.Equal("/models/sample.glb", viewerGlbUrl.GetString());
        Assert.True(partJson.TryGetProperty("viewerStoragePath", out var viewerStoragePath), "Handoff parts must include the viewer source storage path.");
        Assert.Equal($"quotes/temp/{quoteSessionId:N}/123/web-dropped-part.step", viewerStoragePath.GetString());
        Assert.True(partJson.TryGetProperty("viewerFileExtension", out var viewerFileExtension), "Handoff parts must include the viewer source extension.");
        Assert.Equal(".step", viewerFileExtension.GetString());
        Assert.True(partJson.TryGetProperty("thumbnailUrl", out var thumbnailUrl), "Handoff parts must include a thumbnail for the imported part list.");
        Assert.Equal("/images/generated/sample-part.svg", thumbnailUrl.GetString());
        Assert.True(partJson.TryGetProperty("contentType", out var contentType), "Handoff parts must include the MIME type for agent attachment registration.");
        Assert.Equal("application/step", contentType.GetString());
        Assert.True(partJson.TryGetProperty("fileSizeBytes", out var fileSizeBytes), "Handoff parts must include the original file size for agent attachment registration.");
        Assert.Equal(420_000, fileSizeBytes.GetInt64());
    }

    [Fact]
    public async Task UploadHandoff_rejects_unsigned_web_uploaded_files()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/quote/v1/uploads/handoff", new QuoteUploadHandoffRequest
        {
            QuoteSessionId = "web-session-1",
            Files =
            [
                new QuoteUploadHandoffFileDto
                {
                    UploadId = "web-upload-1",
                    FileId = Guid.NewGuid(),
                    FileName = "web-dropped-part.step",
                    StoragePath = "quotes/temp/web-session-1/123/web-dropped-part.step",
                    ContentType = "application/step",
                    FileSizeBytes = 420_000,
                    Status = "Completed"
                }
            ]
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UploadHandoff_rejects_signed_storage_paths_outside_web_quote_session()
    {
        using var client = factory.CreateClient();
        var quoteSessionId = Guid.NewGuid();
        var token = CreateSignedWebUploadHandoffToken(new
        {
            quoteSessionId = quoteSessionId.ToString("D"),
            files = new[]
            {
                new
                {
                    uploadId = "web-upload-1",
                    fileId = Guid.NewGuid(),
                    fileName = "web-dropped-part.step",
                    storagePath = "customer-documents/web-dropped-part.step",
                    contentType = "application/step",
                    fileSizeBytes = 420_000,
                    status = "Completed"
                }
            },
            issuedAt = DateTimeOffset.UtcNow,
            expiresAt = DateTimeOffset.UtcNow.AddMinutes(15)
        });

        var response = await client.PostAsJsonAsync("/quote/v1/uploads/handoff", new QuoteUploadHandoffRequest
        {
            HandoffToken = token
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static string CreateSignedWebUploadHandoffToken<TPayload>(TPayload payload)
    {
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var encodedPayload = Microsoft.AspNetCore.WebUtilities.Base64UrlTextEncoder.Encode(Encoding.UTF8.GetBytes(json));
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes("maliev-local-development-quote-upload-handoff-key"));
        var encodedSignature = Microsoft.AspNetCore.WebUtilities.Base64UrlTextEncoder.Encode(hmac.ComputeHash(Encoding.UTF8.GetBytes(encodedPayload)));
        return $"{encodedPayload}.{encodedSignature}";
    }

    [Fact]
    public async Task ResumeUpload_validates_content_range_and_streams_chunk()
    {
        using var client = await CreateSignedInClientAsync();
        var initiation = await client.PostAsJsonAsync("/quote/v1/uploads/resumable", new InitiateQuoteUploadRequest
        {
            QuoteSessionId = "session-1",
            FileName = "part.stl",
            ContentType = "model/stl",
            FileSizeBytes = 12
        });
        initiation.EnsureSuccessStatusCode();
        var upload = await initiation.Content.ReadFromJsonAsync<InitiateQuoteUploadResponse>();
        Assert.NotNull(upload);

        // Missing Content-Range → 400
        using var badContent = new ByteArrayContent([1, 2, 3]);
        var badPut = await client.PutAsync(upload.ProxyUploadUrl, badContent);
        Assert.Equal(HttpStatusCode.BadRequest, badPut.StatusCode);

        // Valid Content-Range → 204 (no-op stream accepted)
        using var goodContent = new ByteArrayContent([1, 2, 3, 4]);
        goodContent.Headers.ContentType = MediaTypeHeaderValue.Parse("model/stl");
        goodContent.Headers.ContentRange = new ContentRangeHeaderValue(0, 3, 4);
        var goodPut = await client.PutAsync(upload.ProxyUploadUrl, goodContent);
        Assert.Equal(HttpStatusCode.NoContent, goodPut.StatusCode);
    }

    [Fact]
    public async Task Analysis_status_rejects_upload_owned_by_another_customer()
    {
        using var owner = await CreateSignedInClientAsync("analysis-owner@example.com");
        using var other = await CreateSignedInClientAsync("analysis-other@example.com");
        var initiation = await owner.PostAsJsonAsync("/quote/v1/uploads/resumable", new InitiateQuoteUploadRequest
        {
            QuoteSessionId = "analysis-owner-session",
            FileName = "owner-private-analysis.step",
            ContentType = "application/step",
            FileSizeBytes = 512
        });
        initiation.EnsureSuccessStatusCode();
        var upload = await initiation.Content.ReadFromJsonAsync<InitiateQuoteUploadResponse>();
        Assert.NotNull(upload);

        var ownerStatus = await owner.GetAsync($"/quote/v1/uploads/{upload.UploadId}/analysis-status");
        ownerStatus.EnsureSuccessStatusCode();

        var otherStatus = await other.GetAsync($"/quote/v1/uploads/{upload.UploadId}/analysis-status");

        Assert.Equal(HttpStatusCode.Forbidden, otherStatus.StatusCode);
    }

    [Fact]
    public async Task Upload_uses_local_prototype_fallback_when_upload_service_is_unavailable_in_testing()
    {
        await using var fallbackFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<QuoteUploadServiceClient>();
                services.AddSingleton<QuoteUploadServiceClient>(new FailingQuoteUploadServiceClient());
            });
        });
        using var client = fallbackFactory.CreateClient();

        var initiation = await client.PostAsJsonAsync("/quote/v1/uploads/resumable", new InitiateQuoteUploadRequest
        {
            QuoteSessionId = "session-local-fallback",
            FileName = "local-only-part.step",
            ContentType = "application/step",
            FileSizeBytes = 1024
        });
        initiation.EnsureSuccessStatusCode();
        var upload = await initiation.Content.ReadFromJsonAsync<InitiateQuoteUploadResponse>();
        Assert.NotNull(upload);

        using var chunk = new ByteArrayContent(new byte[1024]);
        chunk.Headers.ContentType = MediaTypeHeaderValue.Parse("application/step");
        chunk.Headers.ContentRange = new ContentRangeHeaderValue(0, 1023, 1024);
        var put = await client.PutAsync(upload.ProxyUploadUrl, chunk);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var complete = await client.PostAsync($"/quote/v1/uploads/resumable/{upload.UploadId}/complete", null);
        complete.EnsureSuccessStatusCode();
        var completed = await complete.Content.ReadFromJsonAsync<CompleteQuoteUploadResponse>();
        Assert.NotNull(completed);
        Assert.Equal("Analyzed", completed.Status);

        var status = await client.GetFromJsonAsync<QuoteAnalysisStatusResponse>($"/quote/v1/uploads/{upload.UploadId}/analysis-status");
        Assert.NotNull(status);
        Assert.Equal("Analyzed", status.Status);
        Assert.True(status.VolumeCc > 0);
    }

    [Fact]
    public async Task Upload_does_not_use_local_prototype_fallback_when_upload_service_is_unavailable_in_production()
    {
        await using var productionFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AnonymousVisitor:SigningKey"] = "quote-upload-production-test-signing-key"
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostEnvironment>();
                services.AddSingleton<IHostEnvironment>(new QuoteEngineWebApplicationFactory.TestHostEnvironment(Environments.Production));

                services.RemoveAll<QuoteUploadServiceClient>();
                services.AddSingleton<QuoteUploadServiceClient>(new FailingQuoteUploadServiceClient());
            });
        });
        using var client = productionFactory.CreateClient();

        var initiation = await client.PostAsJsonAsync("/quote/v1/uploads/resumable", new InitiateQuoteUploadRequest
        {
            QuoteSessionId = "session-production-upload-fallback",
            FileName = "production-upload.step",
            ContentType = "application/step",
            FileSizeBytes = 1024
        });

        Assert.Equal(HttpStatusCode.BadGateway, initiation.StatusCode);
        using var body = JsonDocument.Parse(await initiation.Content.ReadAsStringAsync());
        Assert.Equal("Upload service unavailable.", body.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task CompleteUpload_demo_filename_returns_analyzed_status()
    {
        using var client = factory.CreateClient();

        var initResp = await client.PostAsJsonAsync("/quote/v1/uploads/resumable", new InitiateQuoteUploadRequest
        {
            FileName = "sample.step",
            ContentType = "application/octet-stream",
            FileSizeBytes = 1024,
            QuoteSessionId = "test-session-demo"
        });
        initResp.EnsureSuccessStatusCode();
        var initiated = await initResp.Content.ReadFromJsonAsync<InitiateQuoteUploadResponse>();
        Assert.NotNull(initiated);

        var chunkContent = new ByteArrayContent(new byte[1024]);
        chunkContent.Headers.TryAddWithoutValidation("Content-Range", "bytes 0-1023/1024");
        chunkContent.Headers.TryAddWithoutValidation("Content-Type", "application/octet-stream");
        await client.PutAsync(initiated.ProxyUploadUrl, chunkContent);

        var completeResp = await client.PostAsync(
            $"/quote/v1/uploads/resumable/{initiated.UploadId}/complete", null);
        completeResp.EnsureSuccessStatusCode();
        var completed = await completeResp.Content.ReadFromJsonAsync<CompleteQuoteUploadResponse>();

        Assert.NotNull(completed);
        Assert.Equal("Analyzed", completed.Status);
        Assert.Equal("sample.step", completed.FileName);
    }

    [Fact]
    public async Task CompleteUpload_non_demo_file_returns_processing_status()
    {
        using var client = factory.CreateClient();

        var initResp = await client.PostAsJsonAsync("/quote/v1/uploads/resumable", new InitiateQuoteUploadRequest
        {
            FileName = "my-custom-bracket.step",
            ContentType = "application/octet-stream",
            FileSizeBytes = 512,
            QuoteSessionId = "test-session-live"
        });
        initResp.EnsureSuccessStatusCode();
        var initiated = await initResp.Content.ReadFromJsonAsync<InitiateQuoteUploadResponse>();
        Assert.NotNull(initiated);

        var completeResp = await client.PostAsync(
            $"/quote/v1/uploads/resumable/{initiated.UploadId}/complete", null);
        completeResp.EnsureSuccessStatusCode();
        var completed = await completeResp.Content.ReadFromJsonAsync<CompleteQuoteUploadResponse>();

        Assert.NotNull(completed);
        Assert.Equal("Processing", completed.Status);
        Assert.Equal("my-custom-bracket.step", completed.FileName);
    }

    [Fact]
    public async Task Estimate_uses_deterministic_demo_sample_pricing_before_pricing_service()
    {
        await using var demoFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IQePricingServiceClient>();
                services.AddSingleton<IQePricingServiceClient>(new QuoteEngineWebApplicationFactory.SentinelPricingServiceClient());
            });
        });
        using var client = demoFactory.CreateClient();

        var demo = await client.GetFromJsonAsync<QuoteEngineDemoProjectResponse>("/quote/v1/demo/project");
        Assert.NotNull(demo);
        var part = Assert.Single(demo.Parts);
        part.ProcessId = "fdm";
        part.MaterialId = "pla";
        part.FinishId = "fdm-matte";
        part.FinishCode = "MATTE";
        part.ToleranceId = "fdm-standard";
        part.ToleranceCode = "FDM_STANDARD";
        part.InspectionLevel = "STANDARD";
        part.Quantity = 2;

        var standard = await client.PostAsJsonAsync("/quote/v1/estimate", new QuoteEstimateRequest
        {
            QuoteSessionId = demo.DemoSessionId,
            LeadTimeCode = "STANDARD",
            Parts = [part]
        });
        standard.EnsureSuccessStatusCode();
        var standardBody = await standard.Content.ReadFromJsonAsync<QuoteEstimateResponse>();

        Assert.NotNull(standardBody);
        Assert.Equal(2_596.00m, standardBody.Total);
        var standardLine = Assert.Single(standardBody.Lines);
        Assert.Equal(1_298.00m, standardLine.UnitPrice);

        var express = await client.PostAsJsonAsync("/quote/v1/estimate", new QuoteEstimateRequest
        {
            QuoteSessionId = demo.DemoSessionId,
            LeadTimeCode = "EXPRESS",
            Parts = [part]
        });
        express.EnsureSuccessStatusCode();
        var expressBody = await express.Content.ReadFromJsonAsync<QuoteEstimateResponse>();

        Assert.NotNull(expressBody);
        Assert.Equal(3_504.60m, expressBody.Total);

        part.Quantity = 3;
        var quantityThree = await client.PostAsJsonAsync("/quote/v1/estimate", new QuoteEstimateRequest
        {
            QuoteSessionId = demo.DemoSessionId,
            LeadTimeCode = "EXPRESS",
            Parts = [part]
        });
        quantityThree.EnsureSuccessStatusCode();
        var quantityThreeBody = await quantityThree.Content.ReadFromJsonAsync<QuoteEstimateResponse>();

        Assert.NotNull(quantityThreeBody);
        Assert.Equal(5_256.90m, quantityThreeBody.Total);
    }

    [Fact]
    public async Task Estimate_uses_part_geometry_and_requires_sign_in_for_formal_quote()
    {
        using var client = factory.CreateClient();
        var estimate = await client.PostAsJsonAsync("/quote/v1/estimate", new QuoteEstimateRequest
        {
            QuoteSessionId = "session-2",
            LeadTimeCode = "STANDARD",
            Parts =
            [
                new QuotePartDraftDto
                {
                    FileId = Guid.NewGuid(),
                    UploadId = "upload-1",
                    FileName = "bracket.step",
                    ProcessId = "cnc",
                    MaterialId = "al6061",
                    Quantity = 2,
                    VolumeCc = 8.5m,
                    DfmAcknowledged = true
                }
            ]
        });
        estimate.EnsureSuccessStatusCode();

        var body = await estimate.Content.ReadFromJsonAsync<QuoteEstimateResponse>();
        Assert.NotNull(body);
        Assert.True(body.Total > 0);
        Assert.True(body.RequiresSignIn);
        Assert.Single(body.Lines);
    }

    [Fact]
    public async Task Estimate_does_not_fall_back_to_prototype_pricing_in_production()
    {
        using var productionFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostEnvironment>();
                services.AddSingleton<IHostEnvironment>(new QuoteEngineWebApplicationFactory.TestHostEnvironment(Environments.Production));

                services.RemoveAll<IQePricingServiceClient>();
                services.AddSingleton<IQePricingServiceClient>(new QuoteEngineWebApplicationFactory.EmptyPricingServiceClient());
            });
        });
        using var client = productionFactory.CreateClient();

        var response = await client.PostAsJsonAsync("/quote/v1/estimate", new QuoteEstimateRequest
        {
            QuoteSessionId = "production-pricing-unavailable",
            LeadTimeCode = "STANDARD",
            Parts =
            [
                new QuotePartDraftDto
                {
                    FileId = Guid.NewGuid(),
                    UploadId = "upload-production-pricing",
                    FileName = "production-bracket.step",
                    ProcessId = "cnc",
                    MaterialId = "al6061",
                    Quantity = 2,
                    VolumeCc = 8.5m,
                    DfmAcknowledged = true
                }
            ]
        });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Pricing is temporarily unavailable.", body.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Reference_data_and_estimate_support_projectnew_customer_configuration()
    {
        using var client = factory.CreateClient();

        var reference = await client.GetFromJsonAsync<QuoteReferenceDataResponse>("/quote/v1/reference-data");

        Assert.NotNull(reference);
        Assert.NotEmpty(reference.Finishes);
        Assert.NotEmpty(reference.Tolerances);
        Assert.NotEmpty(reference.InspectionLevels);
        Assert.NotEmpty(reference.RoughnessOptions);
        Assert.NotEmpty(reference.Colors);
        Assert.Contains(reference.Finishes, item => item.ProcessId == "cnc" && item.Code == "BEAD_BLAST_CLEAR");
        Assert.Contains(reference.Tolerances, item => item.Code == "ISO2768_M");
        Assert.Contains(reference.InspectionLevels, item => item.Code == "STANDARD");
        Assert.Contains(reference.RoughnessOptions, item => item.Code == "RA_1_6");

        var baseEstimateResponse = await client.PostAsJsonAsync("/quote/v1/estimate", new QuoteEstimateRequest
        {
            QuoteSessionId = "projectnew-parity-base",
            LeadTimeCode = "STANDARD",
            Parts =
            [
                new QuotePartDraftDto
                {
                    FileId = Guid.NewGuid(),
                    UploadId = "upload-base",
                    FileName = "fixture.step",
                    ProcessId = "cnc",
                    MaterialId = "al6061",
                    Quantity = 1,
                    VolumeCc = 10m,
                    SurfaceAreaCm2 = 60m
                }
            ]
        });
        baseEstimateResponse.EnsureSuccessStatusCode();
        var baseEstimate = await baseEstimateResponse.Content.ReadFromJsonAsync<QuoteEstimateResponse>();

        var configuredEstimateResponse = await client.PostAsJsonAsync("/quote/v1/estimate", new QuoteEstimateRequest
        {
            QuoteSessionId = "projectnew-parity-configured",
            LeadTimeCode = "STANDARD",
            Parts =
            [
                new QuotePartDraftDto
                {
                    FileId = Guid.NewGuid(),
                    UploadId = "upload-configured",
                    FileName = "fixture.step",
                    ProcessId = "cnc",
                    MaterialId = "al6061",
                    FinishId = "cnc-bead-blast-clear",
                    FinishCode = "BEAD_BLAST_CLEAR",
                    ToleranceId = "iso-2768-m",
                    ToleranceCode = "ISO2768_M",
                    InspectionLevel = "STANDARD",
                    RoughnessCode = "RA_1_6",
                    Color = "Natural",
                    HasThreadedHoles = true,
                    ThreadSpecification = "M3x0.5",
                    ThreadedHoleCount = 4,
                    InsertType = "HeatSet",
                    InsertCount = 2,
                    Quantity = 1,
                    VolumeCc = 10m,
                    SurfaceAreaCm2 = 60m,
                    BodyCount = 2,
                    SelectedBodyIndex = 1,
                    DrawingFiles =
                    [
                        new QuotePartAttachmentDto("fixture.pdf", "customers/c/q/fixture.pdf", "application/pdf", 2048, "Drawing")
                    ]
                }
            ]
        });
        configuredEstimateResponse.EnsureSuccessStatusCode();
        var configuredEstimate = await configuredEstimateResponse.Content.ReadFromJsonAsync<QuoteEstimateResponse>();

        Assert.NotNull(baseEstimate);
        Assert.NotNull(configuredEstimate);
        Assert.True(configuredEstimate.Total > baseEstimate.Total);
        var line = Assert.Single(configuredEstimate.Lines);
        Assert.Contains("finish", line.Notes, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tolerance", line.Notes, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("inspection", line.Notes, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("thread", line.Notes, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Account_profile_is_resolved_server_side_from_session_boundary()
    {
        using var client = await CreateSignedInClientAsync("profile-owner@example.com");

        var profile = await client.GetFromJsonAsync<CustomerProfileResponse>("/quote/v1/account/profile");

        Assert.NotNull(profile);
        Assert.NotEqual(Guid.Empty, profile.CustomerId);
        Assert.Equal("profile-owner@example.com", profile.Email);
        Assert.Equal("MALIEV Test Buyer Co., Ltd.", profile.CompanyName);
        Assert.Equal("TH-0123456789012", profile.VatNumber);
        Assert.Equal("THB", profile.PreferredCurrency);
        Assert.NotEmpty(profile.Timezone);
        Assert.Equal("Active", profile.NdaStatus);
        Assert.NotNull(profile.NdaExpiresAt);
    }

    [Fact]
    public async Task Account_profile_does_not_fall_back_to_prototype_store_in_production()
    {
        await using var scopedFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostEnvironment>();
                services.AddSingleton<IHostEnvironment>(new QuoteEngineWebApplicationFactory.TestHostEnvironment(Environments.Production));

                services.RemoveAll<ICustomerServiceClient>();
                services.AddSingleton<ICustomerServiceClient>(
                    new QuoteEngineWebApplicationFactory.FakeCustomerServiceClient(allowProfileLookup: false));
            });
        });
        using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var signIn = await client.GetAsync("/test/sign-in?email=production-profile-missing@example.com");
        signIn.EnsureSuccessStatusCode();

        var response = await client.GetAsync("/quote/v1/account/profile");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Account service unavailable", body.RootElement.GetProperty("title").GetString());
        Assert.Equal("Customer profile is temporarily unavailable.", body.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Unauthenticated_request_is_signed_out_with_no_demo_customer_leakage()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        // No identity cookie at all — session endpoint returns not-signed-in.
        var sessionResponse = await client.GetAsync("/quote/v1/auth/session");
        sessionResponse.EnsureSuccessStatusCode();
        var session = await sessionResponse.Content.ReadFromJsonAsync<QuoteAuthStatusResponse>();

        Assert.NotNull(session);
        Assert.False(session.IsSignedIn);
        Assert.Null(session.CustomerId);

        // Profile endpoint rejects unauthenticated requests.
        var profileResponse = await client.GetAsync("/quote/v1/account/profile");
        Assert.Equal(HttpStatusCode.Unauthorized, profileResponse.StatusCode);
    }

    [Fact]
    public async Task Auth_session_returns_profile_image_url_from_session_claim()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        const string profileImageUrl = "https://cdn.example.test/avatar/profile.jpg";
        var signIn = await client.GetAsync(
            $"/test/sign-in?email={Uri.EscapeDataString("profile-image@example.com")}&profileImageUrl={Uri.EscapeDataString(profileImageUrl)}");
        signIn.EnsureSuccessStatusCode();

        var session = await client.GetFromJsonAsync<QuoteAuthStatusResponse>("/quote/v1/auth/session");

        Assert.NotNull(session);
        Assert.True(session.IsSignedIn);
        Assert.Equal(profileImageUrl, session.ProfileImageUrl);
    }

    [Fact]
    public async Task Duplicate_project_preserves_customer_owned_files_settings_drawings_and_viewer_settings()
    {
        using var client = await CreateSignedInClientAsync("duplicate-owner@example.com");
        var partId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var request = new CreateDraftProjectRequest(
            QuoteSessionId: "duplicate-session",
            Parts:
            [
                new QuotePartDraftDto
                {
                    PartId = partId,
                    FileId = fileId,
                    UploadId = "duplicate-upload",
                    FileName = "duplicate-fixture.step",
                    ProcessId = "cnc",
                    MaterialId = "al6061",
                    FinishId = "cnc-bead-blast-clear",
                    FinishCode = "BEAD_BLAST_CLEAR",
                    ToleranceId = "iso-2768-f",
                    ToleranceCode = "ISO2768_F",
                    InspectionLevel = "DIMENSIONAL_REPORT",
                    RoughnessCode = "RA_1_6",
                    Quantity = 4,
                    VolumeCc = 42.5m,
                    SurfaceAreaCm2 = 120.25m,
                    HasThreadedHoles = true,
                    ThreadSpecification = "M4x0.7",
                    ThreadedHoleCount = 6,
                    InsertType = "Helicoil",
                    InsertCount = 3,
                    BodyCount = 2,
                    SelectedBodyIndex = 1,
                    DfmAcknowledged = true,
                    PartNotes = "Carry this exact customer configuration.",
                    DrawingFiles =
                    [
                        new QuotePartAttachmentDto("duplicate-fixture.pdf", "customers/owner/q/drawing.pdf", "application/pdf", 4096, "Drawing")
                    ],
                    ViewerSettings = new QuotePartViewerSettingsDto("right", false, false, true)
                }
            ],
            Notes: "Original draft notes.");

        var createResponse = await client.PostAsJsonAsync("/quote/v1/projects/draft", request);
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<CreateDraftProjectResponse>();
        Assert.NotNull(created);
        Assert.NotNull(created.ProjectServiceProjectId);
        var projectServiceProjectId = created.ProjectServiceProjectId.Value;
        Assert.False(string.IsNullOrWhiteSpace(created.ProjectServiceProjectNumber));
        var projectCreate = factory.LastProjectDraftCreate;
        Assert.NotNull(projectCreate);
        Assert.Equal("duplicate-session", projectCreate.QuoteSessionId);
        Assert.Equal("Untitled quote", projectCreate.Title);
        Assert.Equal("Original draft notes.", projectCreate.Notes);
        Assert.Equal(created.ProjectServiceProjectId, projectCreate.ProjectServiceProjectId);
        var projectPart = Assert.Single(factory.LastProjectPartCreates);
        Assert.Equal(projectCreate.ProjectServiceProjectId, projectPart.ProjectServiceProjectId);
        Assert.NotEqual(Guid.Empty, projectPart.ProjectServicePartId);
        Assert.Equal("duplicate-fixture.step", projectPart.FileName);
        Assert.Equal(4, projectPart.Quantity);
        Assert.Equal("cnc", projectPart.ProcessId);
        Assert.True(projectPart.DfmAcknowledged);
        Assert.NotEqual(Guid.Empty, projectPart.MaterialId);

        var navigation = await client.GetFromJsonAsync<List<CustomerProjectNavItemDto>>("/quote/v1/projects/nav");
        Assert.NotNull(navigation);
        Assert.Contains(navigation, project =>
            project.ProjectId == created.ProjectServiceProjectId &&
            project.ProjectNumber == created.ProjectServiceProjectNumber);

        var durableDetail = await client.GetFromJsonAsync<CustomerProjectDetailResponse>(
            $"/quote/v1/projects/{created.ProjectServiceProjectId:D}");
        Assert.NotNull(durableDetail);
        Assert.Equal(created.ProjectServiceProjectId, durableDetail.ProjectId);
        Assert.Equal(created.ProjectServiceProjectNumber, durableDetail.ProjectNumber);
        var durablePart = Assert.Single(durableDetail.Parts);
        Assert.Equal(projectPart.ProjectServicePartId, durablePart.PartId);
        Assert.Equal("duplicate-fixture.step", durablePart.FileName);
        Assert.Equal("cnc", durablePart.ProcessId);
        Assert.Equal(4, durablePart.Quantity);
        Assert.True(durablePart.DfmAcknowledged);

        var durableDuplicateResponse = await client.PostAsJsonAsync(
            $"/quote/v1/projects/{projectServiceProjectId:D}/duplicate",
            new DuplicateDraftProjectRequest("Durable duplicate fixture copy"));
        durableDuplicateResponse.EnsureSuccessStatusCode();
        var durableDuplicate = await durableDuplicateResponse.Content.ReadFromJsonAsync<DuplicateDraftProjectResponse>();
        Assert.NotNull(durableDuplicate);
        Assert.NotEqual(projectServiceProjectId, durableDuplicate.ProjectId);
        Assert.Equal("Durable duplicate fixture copy", durableDuplicate.Title);
        Assert.Equal(projectServiceProjectId, factory.LastProjectDraftCreate?.SourceProjectId);
        Assert.Equal(created.ProjectServiceProjectNumber, factory.LastProjectDraftCreate?.SourceProjectNumber);
        var durableDuplicatePart = Assert.Single(durableDuplicate.Parts);
        Assert.Equal("duplicate-fixture.step", durableDuplicatePart.FileName);
        Assert.Equal("cnc", durableDuplicatePart.ProcessId);
        Assert.Equal(4, durableDuplicatePart.Quantity);

        var duplicateResponse = await client.PostAsJsonAsync(
            $"/quote/v1/projects/{created.ProjectId:D}/duplicate",
            new DuplicateDraftProjectRequest("Duplicate fixture copy"));
        duplicateResponse.EnsureSuccessStatusCode();
        var duplicated = await duplicateResponse.Content.ReadFromJsonAsync<DuplicateDraftProjectResponse>();

        Assert.NotNull(duplicated);
        Assert.NotEqual(created.ProjectId, duplicated.ProjectId);
        Assert.Equal("Duplicate fixture copy", duplicated.Title);
        Assert.Equal("Draft", duplicated.Status);
        var duplicatedPart = Assert.Single(duplicated.Parts);
        Assert.NotEqual(partId, duplicatedPart.PartId);
        Assert.Equal(fileId, duplicatedPart.FileId);
        Assert.Equal("duplicate-upload", duplicatedPart.UploadId);
        Assert.Equal("duplicate-fixture.step", duplicatedPart.FileName);
        Assert.Equal("cnc", duplicatedPart.ProcessId);
        Assert.Equal("al6061", duplicatedPart.MaterialId);
        Assert.Equal("cnc-bead-blast-clear", duplicatedPart.FinishId);
        Assert.Equal("ISO2768_F", duplicatedPart.ToleranceCode);
        Assert.Equal("DIMENSIONAL_REPORT", duplicatedPart.InspectionLevel);
        Assert.Equal("RA_1_6", duplicatedPart.RoughnessCode);
        Assert.Equal(4, duplicatedPart.Quantity);
        Assert.True(duplicatedPart.HasThreadedHoles);
        Assert.Equal("M4x0.7", duplicatedPart.ThreadSpecification);
        Assert.Equal(6, duplicatedPart.ThreadedHoleCount);
        Assert.Equal("Helicoil", duplicatedPart.InsertType);
        Assert.Equal(3, duplicatedPart.InsertCount);
        Assert.Equal(2, duplicatedPart.BodyCount);
        Assert.Equal(1, duplicatedPart.SelectedBodyIndex);
        Assert.True(duplicatedPart.DfmAcknowledged);
        Assert.Equal("Carry this exact customer configuration.", duplicatedPart.PartNotes);
        var drawing = Assert.Single(duplicatedPart.DrawingFiles);
        Assert.Equal("duplicate-fixture.pdf", drawing.FileName);
        Assert.Equal("customers/owner/q/drawing.pdf", drawing.StoragePath);
        Assert.Equal("right", duplicatedPart.ViewerSettings.CameraPreset);
        Assert.False(duplicatedPart.ViewerSettings.EdgesEnabled.GetValueOrDefault());
        Assert.False(duplicatedPart.ViewerSettings.GridEnabled);
        Assert.True(duplicatedPart.ViewerSettings.DfmOverlayEnabled);
    }

    [Fact]
    public async Task Project_navigation_requires_signed_in_customer()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var response = await client.GetAsync("/quote/v1/projects/nav");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Project_navigation_lists_customer_projects_and_updates_pin_state()
    {
        using var client = await CreateSignedInClientAsync("project-nav-owner@example.com");

        var first = await CreateDraftProjectAsync(client, "Regular project");
        var second = await CreateDraftProjectAsync(client, "Pinned project");
        var firstProjectServiceId = Assert.IsType<Guid>(first.ProjectServiceProjectId);
        var secondProjectServiceId = Assert.IsType<Guid>(second.ProjectServiceProjectId);

        var pinResponse = await client.PostAsync($"/quote/v1/projects/{secondProjectServiceId:D}/pin", null);
        pinResponse.EnsureSuccessStatusCode();
        var pinned = await pinResponse.Content.ReadFromJsonAsync<ProjectManagementResponse>();
        Assert.NotNull(pinned);
        Assert.True(pinned.IsPinned);

        var navResponse = await client.GetAsync("/quote/v1/projects/nav");
        navResponse.EnsureSuccessStatusCode();
        var projects = await navResponse.Content.ReadFromJsonAsync<List<CustomerProjectNavItemDto>>();

        Assert.NotNull(projects);
        Assert.Equal(2, projects.Count);
        Assert.Equal(secondProjectServiceId, projects[0].ProjectId);
        Assert.True(projects[0].IsPinned);
        Assert.Equal("Pinned project", projects[0].Title);
        Assert.Contains(projects, project => project.ProjectId == firstProjectServiceId && !project.IsPinned);

        var unpinResponse = await client.DeleteAsync($"/quote/v1/projects/{secondProjectServiceId:D}/pin");
        unpinResponse.EnsureSuccessStatusCode();
        var unpinned = await unpinResponse.Content.ReadFromJsonAsync<ProjectManagementResponse>();
        Assert.NotNull(unpinned);
        Assert.False(unpinned.IsPinned);

        var updatedNav = await client.GetFromJsonAsync<List<CustomerProjectNavItemDto>>("/quote/v1/projects/nav");
        Assert.NotNull(updatedNav);
        Assert.Contains(updatedNav, project => project.ProjectId == secondProjectServiceId && !project.IsPinned);
    }

    [Fact]
    public async Task Project_detail_does_not_fall_back_to_prototype_store_in_production()
    {
        const string email = "project-production-fallback@example.com";
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var customerId = new Guid(MD5.HashData(Encoding.UTF8.GetBytes(normalizedEmail)));
        await using var scopedFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostEnvironment>();
                services.AddSingleton<IHostEnvironment>(new QuoteEngineWebApplicationFactory.TestHostEnvironment(Environments.Production));
                services.RemoveAll<IProjectServiceClient>();
                services.AddSingleton<IProjectServiceClient>(new QuoteEngineWebApplicationFactory.EmptyProjectServiceClient());
            });
        });
        var store = scopedFactory.Services.GetRequiredService<QuoteEnginePrototypeStore>();
        var prototypeProject = store.CreateDraftProject(
            customerId,
            new CreateDraftProjectRequest(
                "prototype-only-production",
                [],
                "Prototype-only project should not be exposed in production.",
                "Prototype-only production project"));
        using var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var signIn = await client.GetAsync($"/test/sign-in?email={Uri.EscapeDataString(email)}");
        signIn.EnsureSuccessStatusCode();

        var response = await client.GetAsync($"/quote/v1/projects/{prototypeProject.ProjectId:D}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Project_pin_rejects_project_owned_by_another_customer()
    {
        using var owner = await CreateSignedInClientAsync("project-nav-owner-a@example.com");
        var project = await CreateDraftProjectAsync(owner, "Private fixture");
        var projectServiceId = Assert.IsType<Guid>(project.ProjectServiceProjectId);

        using var other = await CreateSignedInClientAsync("project-nav-owner-b@example.com");
        var pinResponse = await other.PostAsync($"/quote/v1/projects/{projectServiceId:D}/pin", null);
        var unpinResponse = await other.DeleteAsync($"/quote/v1/projects/{projectServiceId:D}/pin");

        Assert.Equal(HttpStatusCode.NotFound, pinResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unpinResponse.StatusCode);
    }

    [Fact]
    public async Task Project_archive_removes_project_from_navigation_and_rejects_other_customers()
    {
        using var owner = await CreateSignedInClientAsync("project-archive-owner@example.com");
        var activeProject = await CreateDraftProjectAsync(owner, "Active fixture");
        var archiveProject = await CreateDraftProjectAsync(owner, "Completed bracket");
        var activeProjectServiceId = Assert.IsType<Guid>(activeProject.ProjectServiceProjectId);
        var archiveProjectServiceId = Assert.IsType<Guid>(archiveProject.ProjectServiceProjectId);

        var archiveResponse = await owner.PostAsync($"/quote/v1/projects/{archiveProjectServiceId:D}/archive", null);
        archiveResponse.EnsureSuccessStatusCode();
        var archived = await archiveResponse.Content.ReadFromJsonAsync<ProjectManagementResponse>();
        Assert.NotNull(archived);
        Assert.True(archived.IsArchived);

        var navigation = await owner.GetFromJsonAsync<List<CustomerProjectNavItemDto>>("/quote/v1/projects/nav");
        Assert.NotNull(navigation);
        Assert.Contains(navigation, project => project.ProjectId == activeProjectServiceId);
        Assert.DoesNotContain(navigation, project => project.ProjectId == archiveProjectServiceId);

        using var other = await CreateSignedInClientAsync("project-archive-other@example.com");
        var crossCustomerArchive = await other.PostAsync($"/quote/v1/projects/{activeProjectServiceId:D}/archive", null);
        Assert.Equal(HttpStatusCode.NotFound, crossCustomerArchive.StatusCode);
    }

    [Fact]
    public async Task Project_achieve_marks_project_complete_and_rejects_other_customers()
    {
        using var owner = await CreateSignedInClientAsync("project-achieve-owner@example.com");
        var activeProject = await CreateDraftProjectAsync(owner, "Active fixture");
        var achievedProject = await CreateDraftProjectAsync(owner, "Released bracket");
        var activeProjectServiceId = Assert.IsType<Guid>(activeProject.ProjectServiceProjectId);
        var achievedProjectServiceId = Assert.IsType<Guid>(achievedProject.ProjectServiceProjectId);

        var achieveResponse = await owner.PostAsync($"/quote/v1/projects/{achievedProjectServiceId:D}/achieve", null);
        achieveResponse.EnsureSuccessStatusCode();
        var achieved = await achieveResponse.Content.ReadFromJsonAsync<ProjectManagementResponse>();
        Assert.NotNull(achieved);
        Assert.True(achieved.IsArchived);

        var navigation = await owner.GetFromJsonAsync<List<CustomerProjectNavItemDto>>("/quote/v1/projects/nav");
        Assert.NotNull(navigation);
        Assert.Contains(navigation, project => project.ProjectId == activeProjectServiceId);
        Assert.DoesNotContain(navigation, project => project.ProjectId == achievedProjectServiceId);

        using var other = await CreateSignedInClientAsync("project-achieve-other@example.com");
        var crossCustomerAchieve = await other.PostAsync($"/quote/v1/projects/{activeProjectServiceId:D}/achieve", null);
        Assert.Equal(HttpStatusCode.NotFound, crossCustomerAchieve.StatusCode);
    }

    [Fact]
    public async Task Draft_project_create_and_duplicate_preserve_browser_local_dfm_payload()
    {
        using var client = await CreateSignedInClientAsync("local-dfm-owner@example.com");
        var request = new
        {
            quoteSessionId = "local-dfm-session",
            parts = new[]
            {
                new
                {
                    partId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    fileId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    uploadId = "upload-local-dfm",
                    fileName = "local-bracket.stl",
                    processId = "cnc",
                    materialId = "al6061",
                    quantity = 1,
                    volumeCc = 27.122m,
                    surfaceAreaCm2 = 84.5m,
                    storagePath = "projects/session/local-bracket.stl",
                    status = "DfmAnalysisReady",
                    viewerGlbUrl = "blob:https://quote.local/viewer",
                    viewerStoragePath = "projects/session/local-bracket.stl",
                    viewerFileExtension = ".stl",
                    thumbnailUrl = "blob:https://quote.local/thumb",
                    findings = new[]
                    {
                        new
                        {
                            severity = "warning",
                            code = "LOCAL_PRIMARY",
                            message = "Browser local DFM warning."
                        }
                    },
                    isManifold = false,
                    nonManifoldReason = "Open edge detected locally.",
                    cncReport = new
                    {
                        sharpCornerCount = 1,
                        hasUndercuts = false,
                        hasDrillHoles = true,
                        drillHoleCount = 2,
                        requiresEdm = false,
                        requiresGrinding = false,
                        isTurnable = true,
                        issues = new[]
                        {
                            new
                            {
                                severity = "warning",
                                code = "LOCAL_SHARP_CORNER",
                                message = "Local CNC warning."
                            }
                        }
                    },
                    overlayGlbUrls = new[] { "blob:https://quote.local/overlay" }
                }
            },
            notes = "Preserve local DFM analysis.",
            title = "Local DFM draft"
        };

        var createResponse = await client.PostAsJsonAsync("/quote/v1/projects/draft", request);
        createResponse.EnsureSuccessStatusCode();
        var createdJson = await createResponse.Content.ReadAsStringAsync();
        using var createdDocument = JsonDocument.Parse(createdJson);
        var created = createdDocument.RootElement;
        var createdPart = created.GetProperty("parts")[0];
        Assert.Equal("projects/session/local-bracket.stl", createdPart.GetProperty("storagePath").GetString());
        Assert.Equal("DfmAnalysisReady", createdPart.GetProperty("status").GetString());
        Assert.Equal("blob:https://quote.local/viewer", createdPart.GetProperty("viewerGlbUrl").GetString());
        Assert.Equal("LOCAL_PRIMARY", createdPart.GetProperty("findings")[0].GetProperty("code").GetString());
        Assert.Equal("LOCAL_SHARP_CORNER", createdPart.GetProperty("cncReport").GetProperty("issues")[0].GetProperty("code").GetString());
        Assert.Equal("blob:https://quote.local/overlay", createdPart.GetProperty("overlayGlbUrls")[0].GetString());

        var projectId = created.GetProperty("projectId").GetGuid();
        var duplicateResponse = await client.PostAsJsonAsync(
            $"/quote/v1/projects/{projectId:D}/duplicate",
            new DuplicateDraftProjectRequest("Local DFM copy"));
        duplicateResponse.EnsureSuccessStatusCode();
        var duplicateJson = await duplicateResponse.Content.ReadAsStringAsync();
        using var duplicateDocument = JsonDocument.Parse(duplicateJson);
        var duplicatePart = duplicateDocument.RootElement.GetProperty("parts")[0];
        Assert.Equal("projects/session/local-bracket.stl", duplicatePart.GetProperty("storagePath").GetString());
        Assert.Equal("DfmAnalysisReady", duplicatePart.GetProperty("status").GetString());
        Assert.Equal("LOCAL_PRIMARY", duplicatePart.GetProperty("findings")[0].GetProperty("code").GetString());
        Assert.Equal(1, duplicatePart.GetProperty("cncReport").GetProperty("sharpCornerCount").GetInt32());
        Assert.Equal("blob:https://quote.local/overlay", duplicatePart.GetProperty("overlayGlbUrls")[0].GetString());
    }

    [Fact]
    public async Task Account_addresses_are_read_only_in_quote_engine()
    {
        using var client = await CreateSignedInClientAsync("address-owner@example.com");

        // GET still works — read-only access for checkout address picker.
        var addresses = await client.GetFromJsonAsync<CustomerAddressDto[]>("/quote/v1/account/addresses");
        Assert.NotNull(addresses);

        // Write endpoints are removed — address editing happens in Maliev.Web.
        var postResponse = await client.PostAsJsonAsync("/quote/v1/account/addresses", new CustomerAddressUpsertRequest
        {
            Type = "Shipping",
            AddressLine1 = "12 Test Road",
            City = "Bang Phli",
            StateProvince = "Samut Prakan",
            PostalCode = "10540"
        });
        Assert.Equal(HttpStatusCode.MethodNotAllowed, postResponse.StatusCode);
    }

    [Fact]
    public async Task Account_profile_is_scoped_to_signed_in_customer()
    {
        using var owner = await CreateSignedInClientAsync("profile-scope-owner@example.com");
        using var other = await CreateSignedInClientAsync("profile-scope-other@example.com");

        var ownerProfile = await owner.GetFromJsonAsync<CustomerProfileResponse>("/quote/v1/account/profile");
        var otherProfile = await other.GetFromJsonAsync<CustomerProfileResponse>("/quote/v1/account/profile");

        Assert.NotNull(ownerProfile);
        Assert.NotNull(otherProfile);
        Assert.NotEqual(ownerProfile.CustomerId, otherProfile.CustomerId);
    }

    [Fact]
    public async Task Account_ndas_reads_customer_service_records()
    {
        using var owner = await CreateSignedInClientAsync("ndas-owner@example.com");

        var ndas = await owner.GetFromJsonAsync<CustomerNdaDto[]>("/quote/v1/account/ndas");

        Assert.NotNull(ndas);
        var nda = Assert.Single(ndas);
        Assert.Equal("Mutual NDA", nda.Title);
        Assert.Equal("Active", nda.Status);
        Assert.True(nda.ExpiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Account_documents_allow_customer_scoped_purchase_order_uploads()
    {
        using var owner = await CreateSignedInClientAsync("documents-owner@example.com");

        var postResponse = await owner.PostAsJsonAsync("/quote/v1/account/documents", new CustomerDocumentUploadRequest
        {
            FileName = "po-1001.pdf",
            Kind = "PurchaseOrder",
            StoragePath = "customers/owner/orders/ord-1001/po-1001.pdf",
            ContentType = "application/pdf",
            FileSizeBytes = 42_000,
            OrderNumber = "ORD-1001"
        });
        postResponse.EnsureSuccessStatusCode();

        var uploaded = await postResponse.Content.ReadFromJsonAsync<CustomerDocumentDto>();
        Assert.NotNull(uploaded);
        Assert.Equal("po-1001.pdf", uploaded.FileName);
        Assert.Equal("PurchaseOrder", uploaded.Kind);
        Assert.Equal("ORD-1001", uploaded.OrderNumber);
        Assert.Equal("customers/owner/orders/ord-1001/po-1001.pdf", uploaded.StoragePath);
        Assert.Equal("application/pdf", uploaded.ContentType);
        Assert.Equal(42_000, uploaded.FileSizeBytes);

        var ownerDocuments = await owner.GetFromJsonAsync<CustomerDocumentDto[]>("/quote/v1/account/documents");
        Assert.NotNull(ownerDocuments);
        Assert.Contains(ownerDocuments, document =>
            document.DocumentId == uploaded.DocumentId &&
            document.OrderNumber == "ORD-1001" &&
            document.StoragePath == "customers/owner/orders/ord-1001/po-1001.pdf");

        using var other = await CreateSignedInClientAsync("documents-other@example.com");
        var otherDocuments = await other.GetFromJsonAsync<CustomerDocumentDto[]>("/quote/v1/account/documents");
        Assert.NotNull(otherDocuments);
        Assert.DoesNotContain(otherDocuments, document => document.DocumentId == uploaded.DocumentId);
    }

    [Fact]
    public async Task Account_documents_reject_unsafe_storage_paths_and_unknown_kinds()
    {
        using var owner = await CreateSignedInClientAsync("documents-validation@example.com");

        var unsafePathResponse = await owner.PostAsJsonAsync("/quote/v1/account/documents", new CustomerDocumentUploadRequest
        {
            FileName = "po.pdf",
            Kind = "PurchaseOrder",
            StoragePath = "../private/po.pdf",
            ContentType = "application/pdf",
            FileSizeBytes = 42_000,
            OrderNumber = "ORD-1001"
        });

        Assert.Equal(HttpStatusCode.BadRequest, unsafePathResponse.StatusCode);

        var unknownKindResponse = await owner.PostAsJsonAsync("/quote/v1/account/documents", new CustomerDocumentUploadRequest
        {
            FileName = "callback.html",
            Kind = "WebhookCallback",
            StoragePath = "customer-documents/owner/callback.html",
            ContentType = "text/html",
            FileSizeBytes = 1_200,
            OrderNumber = "ORD-1001"
        });

        Assert.Equal(HttpStatusCode.BadRequest, unknownKindResponse.StatusCode);

        var documents = await owner.GetFromJsonAsync<CustomerDocumentDto[]>("/quote/v1/account/documents");
        Assert.NotNull(documents);
        Assert.DoesNotContain(documents, document => document.FileName == "po.pdf" || document.FileName == "callback.html");
    }

    [Fact]
    public async Task Account_documents_download_returns_signed_url_for_owner_only()
    {
        using var owner = await CreateSignedInClientAsync("documents-download-owner@example.com");

        var postResponse = await owner.PostAsJsonAsync("/quote/v1/account/documents", new CustomerDocumentUploadRequest
        {
            FileName = "receipt-1001.pdf",
            Kind = "Receipt",
            StoragePath = "customers/owner/orders/ord-1001/receipt-1001.pdf",
            ContentType = "application/pdf",
            FileSizeBytes = 21_000,
            OrderNumber = "ORD-1001"
        });
        postResponse.EnsureSuccessStatusCode();
        var uploaded = await postResponse.Content.ReadFromJsonAsync<CustomerDocumentDto>();
        Assert.NotNull(uploaded);

        var download = await owner.GetFromJsonAsync<CustomerDocumentDownloadResponse>(
            $"/quote/v1/account/documents/{uploaded.DocumentId:D}/download");

        Assert.NotNull(download);
        Assert.Equal(uploaded.DocumentId, download.DocumentId);
        Assert.Equal("receipt-1001.pdf", download.FileName);
        Assert.StartsWith("https://test-cdn.example.com/", download.DownloadUrl);
        Assert.Contains(Uri.EscapeDataString("customers/owner/orders/ord-1001/receipt-1001.pdf"), download.DownloadUrl);

        using var other = await CreateSignedInClientAsync("documents-download-other@example.com");
        var crossCustomerDownload = await other.GetAsync($"/quote/v1/account/documents/{uploaded.DocumentId:D}/download");
        Assert.Equal(HttpStatusCode.NotFound, crossCustomerDownload.StatusCode);
    }

    [Fact]
    public async Task Account_documents_upload_streams_file_bytes_to_upload_service()
    {
        var recordingUploadClient = new QuoteEngineWebApplicationFactory.RecordingQuoteUploadServiceClient();
        using var client = await CreateSignedInClientAsync(
            "documents-file-upload@example.com",
            services =>
            {
                services.RemoveAll<QuoteUploadServiceClient>();
                services.AddSingleton<QuoteUploadServiceClient>(recordingUploadClient);
            });

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("PurchaseOrder"), "Kind");
        content.Add(new StringContent("ORD-PO-UPLOAD"), "OrderNumber");
        content.Add(
            new ByteArrayContent("signed purchase order"u8.ToArray())
            {
                Headers = { ContentType = MediaTypeHeaderValue.Parse("application/pdf") }
            },
            "File",
            "po-upload.pdf");

        var response = await client.PostAsync("/quote/v1/account/documents/upload", content);

        response.EnsureSuccessStatusCode();
        var uploaded = await response.Content.ReadFromJsonAsync<CustomerDocumentDto>();
        Assert.NotNull(uploaded);
        Assert.Equal("PurchaseOrder", uploaded.Kind);
        Assert.Equal("po-upload.pdf", uploaded.FileName);
        Assert.Equal("ORD-PO-UPLOAD", uploaded.OrderNumber);
        Assert.StartsWith("customer-documents/", uploaded.StoragePath, StringComparison.Ordinal);
        Assert.Equal("po-upload.pdf", recordingUploadClient.LastInitiatedFileName);
        Assert.Equal("application/pdf", recordingUploadClient.LastInitiatedContentType);
        Assert.Equal("customer-document", recordingUploadClient.LastMetadataTags!["quoteEngine.documentRole"]);
        Assert.Equal("PurchaseOrder", recordingUploadClient.LastMetadataTags["quoteEngine.documentKind"]);
        Assert.Equal("ORD-PO-UPLOAD", recordingUploadClient.LastMetadataTags["quoteEngine.orderNumber"]);
        Assert.Equal("bytes 0-20/21", recordingUploadClient.LastStreamedContentRange);
        Assert.Equal("downstream-document-upload", recordingUploadClient.LastStreamedUploadId);
        Assert.Equal(uploaded.StoragePath, recordingUploadClient.LastStreamedStoragePath);
        Assert.Equal("signed purchase order"u8.ToArray(), recordingUploadClient.LastStreamedBytes);
    }

    [Fact]
    public async Task Address_google_config_is_available_for_signed_in_customer()
    {
        using var client = await CreateSignedInClientAsync("address-config@example.com");

        var config = await client.GetFromJsonAsync<GoogleAddressConfigResponse>("/quote/v1/address/google-config");

        Assert.NotNull(config);
        Assert.Equal(13.7563, config.DefaultLatitude);
        Assert.Equal(100.5018, config.DefaultLongitude);
        Assert.Contains("th", config.IncludedRegionCodes);
    }

    [Fact]
    public async Task Account_quote_and_order_history_is_scoped_to_signed_in_customer()
    {
        using var customerA = await CreateSignedInClientAsync("quote-owner-a@example.com");

        // Create formal quote via real QuotationService integration (FakeQuotationServiceClient)
        var quoteResponse = await customerA.PostAsJsonAsync(
            "/quote/v1/quotes/formal",
            new GenerateFormalQuoteRequest(Guid.NewGuid(), "session-a", [], "Customer A quote."));
        quoteResponse.EnsureSuccessStatusCode();
        var quote = await quoteResponse.Content.ReadFromJsonAsync<GenerateFormalQuoteResponse>();
        Assert.NotNull(quote);
        Assert.NotEqual(Guid.Empty, quote.QuoteId);

        // Create order referencing that quote
        var orderResponse = await customerA.PostAsJsonAsync(
            "/quote/v1/orders",
            new CreateManufacturingOrderRequest(quote.QuoteId, string.Empty, "Customer A accepted."));
        orderResponse.EnsureSuccessStatusCode();

        // Customer A sees their quote and order
        var customerAQuotes = await customerA.GetFromJsonAsync<CustomerQuoteSummaryDto[]>("/quote/v1/account/quotes");
        var customerAOrders = await customerA.GetFromJsonAsync<CustomerOrderSummaryDto[]>("/quote/v1/account/orders");
        Assert.NotNull(customerAQuotes);
        Assert.NotNull(customerAOrders);
        var customerQuote = Assert.Single(customerAQuotes, item => item.QuoteId == quote.QuoteId);
        Assert.NotNull(customerQuote.Versions);
        Assert.Equal(2, customerQuote.Versions.Count);
        var customerQuoteVersion = Assert.Single(customerQuote.Versions, version => version.VersionNumber == 2);
        Assert.Contains(customerQuote.Versions, version => version.VersionNumber == 1);
        Assert.Equal(customerQuote.Total, customerQuoteVersion.Total);
        Assert.False(string.IsNullOrWhiteSpace(customerQuoteVersion.PdfUrl));
        Assert.Single(customerAOrders);

        // Customer B sees no quotes or orders
        using var customerB = await CreateSignedInClientAsync("quote-owner-b@example.com");
        var customerBQuotes = await customerB.GetFromJsonAsync<CustomerQuoteSummaryDto[]>("/quote/v1/account/quotes");
        var customerBOrders = await customerB.GetFromJsonAsync<CustomerOrderSummaryDto[]>("/quote/v1/account/orders");
        Assert.NotNull(customerBQuotes);
        Assert.NotNull(customerBOrders);
        Assert.Empty(customerBQuotes);
        Assert.Empty(customerBOrders);

        // Customer B cannot create an order against Customer A's quote
        var crossCustomerOrder = await customerB.PostAsJsonAsync(
            "/quote/v1/orders",
            new CreateManufacturingOrderRequest(quote.QuoteId, string.Empty, "Cross-customer attempt."));
        Assert.Equal(HttpStatusCode.NotFound, crossCustomerOrder.StatusCode);
    }

    [Fact]
    public async Task Order_creation_carries_configured_part_summary_to_customer_requirements()
    {
        using var client = await CreateSignedInClientAsync("configured-order@example.com");
        var part = new QuotePartDraftDto
        {
            PartId = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            UploadId = "upload-configured-order",
            FileName = "cnc-bracket.step",
            ProcessId = "cnc",
            MaterialId = "al6061",
            FinishCode = "BEAD_BLAST_CLEAR",
            ToleranceCode = "ISO2768_M",
            InspectionLevel = "FAI",
            RoughnessCode = "RA_1_6",
            Quantity = 12,
            VolumeCc = 24.5m,
            SurfaceAreaCm2 = 88.25m,
            HasThreadedHoles = true,
            ThreadSpecification = "M3x0.5",
            ThreadedHoleCount = 4,
            InsertType = "HeatSet",
            InsertCount = 2,
            BodyCount = 2,
            SelectedBodyIndex = 1,
            DfmAcknowledged = true,
            PartNotes = "Keep cosmetic face A scratch-free."
        };
        part.ProcessOptionValues["machine"] = "3-axis";
        part.DrawingFiles.Add(new QuotePartAttachmentDto(
            "cnc-bracket-drawing.pdf",
            "customers/configured-order/drawing.pdf",
            "application/pdf",
            42_000,
            "Drawing"));

        var draftResponse = await client.PostAsJsonAsync(
            "/quote/v1/projects/draft",
            new CreateDraftProjectRequest("session-configured-order", [part], "Persist configured order project.", "Configured order project"));
        draftResponse.EnsureSuccessStatusCode();
        var draft = await draftResponse.Content.ReadFromJsonAsync<CreateDraftProjectResponse>();
        Assert.NotNull(draft);
        var projectServiceProjectId = Assert.IsType<Guid>(draft.ProjectServiceProjectId);
        var persistedPart = Assert.Single(draft.Parts ?? []);
        Assert.NotEqual(Guid.Empty, persistedPart.PartId);
        part.PartId = persistedPart.PartId;

        var quoteResponse = await client.PostAsJsonAsync(
            "/quote/v1/quotes/formal",
            new GenerateFormalQuoteRequest(projectServiceProjectId, "session-configured-order", [part], "Configured order quote."));
        quoteResponse.EnsureSuccessStatusCode();
        var quote = await quoteResponse.Content.ReadFromJsonAsync<GenerateFormalQuoteResponse>();
        Assert.NotNull(quote);

        var orderResponse = await client.PostAsJsonAsync(
            "/quote/v1/orders",
            new CreateManufacturingOrderRequest(
                quote.QuoteId,
                "PO-CONFIGURED",
                "Customer accepted configured quote.",
                projectServiceProjectId)
            {
                Parts = [part]
            });
        orderResponse.EnsureSuccessStatusCode();
        var order = await orderResponse.Content.ReadFromJsonAsync<CreateManufacturingOrderResponse>();
        Assert.NotNull(order);

        var detail = await client.GetFromJsonAsync<CustomerOrderDetailDto>(
            $"/quote/v1/account/orders/{Uri.EscapeDataString(order.OrderNumber)}");

        Assert.NotNull(detail);
        Assert.Equal("PO-CONFIGURED", detail.CustomerPoNumber);
        Assert.Contains("Customer accepted configured quote.", detail.Requirements, StringComparison.Ordinal);
        Assert.Contains("cnc-bracket.step", detail.Requirements, StringComparison.Ordinal);
        Assert.Contains("process CNC", detail.Requirements, StringComparison.Ordinal);
        Assert.Contains("material al6061", detail.Requirements, StringComparison.Ordinal);
        Assert.Contains("finish BEAD_BLAST_CLEAR", detail.Requirements, StringComparison.Ordinal);
        Assert.Contains("tolerance ISO2768_M", detail.Requirements, StringComparison.Ordinal);
        Assert.Contains("inspection FAI", detail.Requirements, StringComparison.Ordinal);
        Assert.Contains("roughness RA_1_6", detail.Requirements, StringComparison.Ordinal);
        Assert.Contains("qty 12", detail.Requirements, StringComparison.Ordinal);
        Assert.Contains("threaded holes 4 M3x0.5", detail.Requirements, StringComparison.Ordinal);
        Assert.Contains("inserts 2 HeatSet", detail.Requirements, StringComparison.Ordinal);
        Assert.Contains("drawing cnc-bracket-drawing.pdf", detail.Requirements, StringComparison.Ordinal);
        Assert.Contains("body count 2", detail.Requirements, StringComparison.Ordinal);
        Assert.Contains("selected body 1", detail.Requirements, StringComparison.Ordinal);
        Assert.Contains("DFM acknowledged", detail.Requirements, StringComparison.Ordinal);
        Assert.Contains("notes Keep cosmetic face A scratch-free.", detail.Requirements, StringComparison.Ordinal);

        var createRequest = factory.LastOrderCreateRequest;
        Assert.NotNull(createRequest);
        Assert.Equal(quote.QuoteId, createRequest.QuoteId);
        Assert.Equal(quote.QuoteNumber, createRequest.QuoteNumber);
        Assert.Equal(quote.QuoteVersionId, createRequest.QuoteVersionId);
        Assert.Equal(quote.QuoteVersionNumber, createRequest.QuoteVersionNumber);
        var productionItem = Assert.Single(createRequest.ProductionItems);
        Assert.Equal(projectServiceProjectId, productionItem.SourceProjectId);
        Assert.Equal(part.PartId, productionItem.SourceProjectPartId);
        Assert.NotEqual(Guid.Empty, productionItem.MaterialId);
        Assert.Equal("CNC", productionItem.Technology);
        Assert.Equal(part.VolumeCc, productionItem.VolumeCm3);
        Assert.Equal(part.Quantity, productionItem.Quantity);
        Assert.Contains("al6061", productionItem.MaterialSnapshotJson, StringComparison.Ordinal);
        Assert.Contains("cnc-bracket.step", productionItem.ConfigurationSnapshotJson, StringComparison.Ordinal);
        Assert.Contains("BEAD_BLAST_CLEAR", productionItem.ConfigurationSnapshotJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Order_creation_rejects_parts_with_unacknowledged_dfm_issues()
    {
        using var client = await CreateSignedInClientAsync("dfm-blocked-order@example.com");
        var part = new QuotePartDraftDto
        {
            PartId = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            UploadId = "upload-dfm-blocked-order",
            FileName = "thin-wall-bracket.stl",
            ProcessId = "fdm",
            MaterialId = "pla-black",
            Quantity = 2,
            VolumeCc = 14.25m,
            SurfaceAreaCm2 = 52.4m,
            Status = "DfmAnalysisReady",
            DfmAcknowledged = true,
            Findings = [new DfmFindingDto("warning", "THIN_WALL", "Wall thickness is below the process minimum.")],
            FdmReport = new QeFdmDfmReport(
                1,
                0,
                0m,
                false,
                0,
                [new QeDfmIssueItem("warning", "THIN_WALL", "Wall thickness is below the process minimum.")])
        };

        var quoteResponse = await client.PostAsJsonAsync(
            "/quote/v1/quotes/formal",
            new GenerateFormalQuoteRequest(Guid.NewGuid(), "session-dfm-blocked-order", [part], "DFM blocked order quote."));
        quoteResponse.EnsureSuccessStatusCode();
        var quote = await quoteResponse.Content.ReadFromJsonAsync<GenerateFormalQuoteResponse>();
        Assert.NotNull(quote);

        part.DfmAcknowledged = false;

        var orderResponse = await client.PostAsJsonAsync(
            "/quote/v1/orders",
            new CreateManufacturingOrderRequest(
                quote.QuoteId,
                "PO-DFM-BLOCK",
                "Customer attempted order before DFM acknowledgement.")
            {
                Parts = [part]
            });

        Assert.Equal(HttpStatusCode.BadRequest, orderResponse.StatusCode);
        var body = await orderResponse.Content.ReadAsStringAsync();
        Assert.Contains("DFM review is required", body, StringComparison.Ordinal);
        Assert.Contains("thin-wall-bracket.stl", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Formal_quote_for_same_project_creates_new_version_on_existing_quotation()
    {
        using var client = await CreateSignedInClientAsync("project-quote-versions@example.com");
        var projectId = Guid.NewGuid();
        var part = new QuotePartDraftDto
        {
            PartId = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            UploadId = "upload-project-quote-version",
            FileName = "versioned-bracket.stl",
            ProcessId = "fdm",
            MaterialId = "pla-black",
            Quantity = 2,
            VolumeCc = 10m,
            SurfaceAreaCm2 = 40m,
            DfmAcknowledged = true
        };

        var firstResponse = await client.PostAsJsonAsync(
            "/quote/v1/quotes/formal",
            new GenerateFormalQuoteRequest(projectId, "session-project-quote-version-1", [part], "Initial project quote."));
        firstResponse.EnsureSuccessStatusCode();
        var firstQuote = await firstResponse.Content.ReadFromJsonAsync<GenerateFormalQuoteResponse>();
        Assert.NotNull(firstQuote);
        Assert.Equal(1, firstQuote.QuoteVersionNumber);
        Assert.NotNull(firstQuote.QuoteVersionId);

        var firstCreateRequest = factory.LastQuotationCreateRequest;
        Assert.NotNull(firstCreateRequest);
        Assert.Equal(projectId, firstCreateRequest.SourceProjectId);
        Assert.False(string.IsNullOrWhiteSpace(firstCreateRequest.ProjectSnapshotJson));
        Assert.False(string.IsNullOrWhiteSpace(firstCreateRequest.ProjectSnapshotHash));

        part.Quantity = 4;
        var secondResponse = await client.PostAsJsonAsync(
            "/quote/v1/quotes/formal",
            new GenerateFormalQuoteRequest(projectId, "session-project-quote-version-2", [part], "Updated project quote."));
        secondResponse.EnsureSuccessStatusCode();
        var secondQuote = await secondResponse.Content.ReadFromJsonAsync<GenerateFormalQuoteResponse>();
        Assert.NotNull(secondQuote);
        Assert.Equal(firstQuote.QuoteId, secondQuote.QuoteId);
        Assert.Equal(firstQuote.QuoteNumber, secondQuote.QuoteNumber);
        Assert.Equal(2, secondQuote.QuoteVersionNumber);
        Assert.NotEqual(firstQuote.QuoteVersionId, secondQuote.QuoteVersionId);

        var secondCreateRequest = factory.LastQuotationCreateRequest;
        Assert.NotNull(secondCreateRequest);
        Assert.Equal(projectId, secondCreateRequest.SourceProjectId);
        Assert.Contains("Updated project quote.", secondCreateRequest.ChangeSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Order_creation_rejects_superseded_quote_version_before_order_service_call()
    {
        using var client = await CreateSignedInClientAsync("superseded-version-order@example.com");
        var projectId = Guid.NewGuid();
        var part = new QuotePartDraftDto
        {
            PartId = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            UploadId = "upload-superseded-version-order",
            FileName = "superseded-version-bracket.stl",
            ProcessId = "fdm",
            MaterialId = "pla-black",
            Quantity = 2,
            VolumeCc = 10m,
            SurfaceAreaCm2 = 40m,
            DfmAcknowledged = true
        };

        var quoteResponse = await client.PostAsJsonAsync(
            "/quote/v1/quotes/formal",
            new GenerateFormalQuoteRequest(projectId, "session-superseded-version-order", [part], "Initial quote version."));
        quoteResponse.EnsureSuccessStatusCode();
        var quote = await quoteResponse.Content.ReadFromJsonAsync<GenerateFormalQuoteResponse>();
        Assert.NotNull(quote);
        Assert.NotNull(quote.QuoteVersionId);
        Assert.Equal(1, quote.QuoteVersionNumber);

        factory.SupersedeQuoteVersion(quote.QuoteId);
        var orderCreateCount = factory.OrderCreateRequests.Count;

        var orderResponse = await client.PostAsJsonAsync(
            "/quote/v1/orders",
            new CreateManufacturingOrderRequest(
                quote.QuoteId,
                "PO-SUPERSEDED",
                "Customer attempted to accept an older quote version.")
            {
                QuoteVersionId = quote.QuoteVersionId,
                QuoteVersionNumber = quote.QuoteVersionNumber,
                Parts = [part]
            });

        Assert.Equal(HttpStatusCode.BadRequest, orderResponse.StatusCode);
        var body = await orderResponse.Content.ReadAsStringAsync();
        Assert.Contains("superseded", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(orderCreateCount, factory.OrderCreateRequests.Count);
    }

    [Fact]
    public async Task Order_creation_rejects_expired_quote_before_order_service_call()
    {
        using var client = await CreateSignedInClientAsync("expired-version-order@example.com");
        var part = new QuotePartDraftDto
        {
            PartId = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            UploadId = "upload-expired-version-order",
            FileName = "expired-version-bracket.stl",
            ProcessId = "fdm",
            MaterialId = "pla-black",
            Quantity = 1,
            VolumeCc = 8m,
            SurfaceAreaCm2 = 35m,
            DfmAcknowledged = true
        };

        var quoteResponse = await client.PostAsJsonAsync(
            "/quote/v1/quotes/formal",
            new GenerateFormalQuoteRequest(Guid.NewGuid(), "session-expired-version-order", [part], "Quote to expire."));
        quoteResponse.EnsureSuccessStatusCode();
        var quote = await quoteResponse.Content.ReadFromJsonAsync<GenerateFormalQuoteResponse>();
        Assert.NotNull(quote);

        factory.ExpireQuote(quote.QuoteId);
        var orderCreateCount = factory.OrderCreateRequests.Count;

        var orderResponse = await client.PostAsJsonAsync(
            "/quote/v1/orders",
            new CreateManufacturingOrderRequest(
                quote.QuoteId,
                "PO-EXPIRED",
                "Customer attempted to accept an expired quote.")
            {
                QuoteVersionId = quote.QuoteVersionId,
                QuoteVersionNumber = quote.QuoteVersionNumber,
                Parts = [part]
            });

        Assert.Equal(HttpStatusCode.BadRequest, orderResponse.StatusCode);
        var body = await orderResponse.Content.ReadAsStringAsync();
        Assert.Contains("expired", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(orderCreateCount, factory.OrderCreateRequests.Count);
    }

    [Fact]
    public async Task Order_creation_sends_quote_total_to_order_service()
    {
        using var client = await CreateSignedInClientAsync("quoted-total-order@example.com");
        var part = new QuotePartDraftDto
        {
            PartId = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            UploadId = "upload-quoted-total-order",
            FileName = "quoted-total-bracket.stl",
            ProcessId = "fdm",
            MaterialId = "pla-black",
            Quantity = 2,
            VolumeCc = 10m,
            SurfaceAreaCm2 = 40m,
            DfmAcknowledged = true
        };

        var quoteResponse = await client.PostAsJsonAsync(
            "/quote/v1/quotes/formal",
            new GenerateFormalQuoteRequest(Guid.NewGuid(), "session-quoted-total-order", [part], "Quoted total order."));
        quoteResponse.EnsureSuccessStatusCode();
        var quote = await quoteResponse.Content.ReadFromJsonAsync<GenerateFormalQuoteResponse>();
        Assert.NotNull(quote);

        var orderResponse = await client.PostAsJsonAsync(
            "/quote/v1/orders",
            new CreateManufacturingOrderRequest(
                quote.QuoteId,
                "PO-QUOTE-TOTAL",
                "Customer accepted quoted total.")
            {
                Parts = [part]
            });
        orderResponse.EnsureSuccessStatusCode();

        var createRequest = factory.LastOrderCreateRequest;
        Assert.NotNull(createRequest);
        Assert.Equal(quote.QuoteId, createRequest.QuoteId);
        Assert.Equal(quote.QuoteNumber, createRequest.QuoteNumber);
        Assert.Equal(quote.QuoteVersionId, createRequest.QuoteVersionId);
        Assert.Equal(quote.QuoteVersionNumber, createRequest.QuoteVersionNumber);
        Assert.Equal(2140.00m, createRequest.QuotedAmount);
        Assert.Equal("THB", createRequest.QuoteCurrency);
    }

    [Fact]
    public async Task Formal_quote_rejects_parts_with_unacknowledged_dfm_issues()
    {
        using var client = await CreateSignedInClientAsync("dfm-blocked-quote@example.com");
        var part = new QuotePartDraftDto
        {
            PartId = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            UploadId = "upload-dfm-blocked-quote",
            FileName = "non-manifold-cover.stl",
            ProcessId = "fdm",
            MaterialId = "pla-black",
            Quantity = 1,
            VolumeCc = 8.5m,
            SurfaceAreaCm2 = 31.2m,
            Status = "DfmAnalysisReady",
            DfmAcknowledged = false,
            IsManifold = false,
            NonManifoldReason = "Mesh contains non-manifold edges."
        };

        var quoteResponse = await client.PostAsJsonAsync(
            "/quote/v1/quotes/formal",
            new GenerateFormalQuoteRequest(Guid.NewGuid(), "session-dfm-blocked-quote", [part], "DFM blocked formal quote."));

        Assert.Equal(HttpStatusCode.BadRequest, quoteResponse.StatusCode);
        var body = await quoteResponse.Content.ReadAsStringAsync();
        Assert.Contains("DFM review is required", body, StringComparison.Ordinal);
        Assert.Contains("non-manifold-cover.stl", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Quote_approval_is_scoped_to_signed_in_customer()
    {
        using var customerA = await CreateSignedInClientAsync("quote-approve-owner@example.com");

        var quoteResponse = await customerA.PostAsJsonAsync(
            "/quote/v1/quotes/formal",
            new GenerateFormalQuoteRequest(Guid.NewGuid(), "session-approve", [], "Customer A quote approval."));
        quoteResponse.EnsureSuccessStatusCode();
        var quote = await quoteResponse.Content.ReadFromJsonAsync<GenerateFormalQuoteResponse>();
        Assert.NotNull(quote);

        var ownerApprove = await customerA.PostAsJsonAsync($"/quote/v1/quotes/{quote.QuoteId:D}/approve", new { });
        ownerApprove.EnsureSuccessStatusCode();
        var ownerApprovedQuote = await ownerApprove.Content.ReadFromJsonAsync<GenerateFormalQuoteResponse>();
        Assert.NotNull(ownerApprovedQuote);
        Assert.Equal(quote.QuoteId, ownerApprovedQuote.QuoteId);
        Assert.Equal("Approved", ownerApprovedQuote.Status);

        using var customerB = await CreateSignedInClientAsync("quote-approve-other@example.com");
        var crossCustomerApprove = await customerB.PostAsJsonAsync($"/quote/v1/quotes/{quote.QuoteId:D}/approve", new { });
        Assert.Equal(HttpStatusCode.NotFound, crossCustomerApprove.StatusCode);
    }

    [Fact]
    public async Task Order_detail_returns_status_history_for_signed_in_customer()
    {
        using var client = await CreateSignedInClientAsync("order-detail@example.com");

        // Create an order first
        var quoteResp = await client.PostAsJsonAsync(
            "/quote/v1/quotes/formal",
            new GenerateFormalQuoteRequest(Guid.NewGuid(), "session-detail", [], "Detail test."));
        quoteResp.EnsureSuccessStatusCode();
        var quote = await quoteResp.Content.ReadFromJsonAsync<GenerateFormalQuoteResponse>();
        Assert.NotNull(quote);

        var orderResp = await client.PostAsJsonAsync(
            "/quote/v1/orders",
            new CreateManufacturingOrderRequest(quote.QuoteId, "PO-001", "Test order detail."));
        orderResp.EnsureSuccessStatusCode();
        var order = await orderResp.Content.ReadFromJsonAsync<CreateManufacturingOrderResponse>();
        Assert.NotNull(order);

        // Fetch order detail
        var detailResp = await client.GetFromJsonAsync<CustomerOrderDetailDto>(
            $"/quote/v1/account/orders/{Uri.EscapeDataString(order.OrderNumber)}");

        Assert.NotNull(detailResp);
        Assert.Equal(order.OrderNumber, detailResp.OrderNumber);
        Assert.Equal("Quoted", detailResp.CurrentStatus);
        Assert.NotEmpty(detailResp.StatusHistory);
        Assert.Contains(detailResp.StatusHistory, s => s.Status == "Pending");
        var orderFile = Assert.Single(detailResp.OrderFiles);
        Assert.Equal("make-studio-manufacturing-packet.pdf", orderFile.FileName);
        Assert.Equal("Supporting", orderFile.FileRole);
        Assert.Equal("Document", orderFile.FileCategory);
        Assert.Equal($"orders/{order.OrderNumber}/files/make-studio-manufacturing-packet.pdf", orderFile.ObjectPath);
        Assert.Equal("application/pdf", orderFile.ContentType);
        var download = await client.GetFromJsonAsync<CustomerOrderFileDownloadResponse>(
            $"/quote/v1/account/orders/{Uri.EscapeDataString(order.OrderNumber)}/files/{orderFile.FileId}/download");
        Assert.NotNull(download);
        Assert.Equal(orderFile.FileId, download.FileId);
        Assert.Equal(orderFile.FileName, download.FileName);
        Assert.Contains(Uri.EscapeDataString(orderFile.ObjectPath), download.DownloadUrl);
        Assert.NotEmpty(detailResp.ManufacturingMilestones);
        Assert.Contains(detailResp.ManufacturingMilestones, milestone =>
            milestone.Key == "order-received" &&
            milestone.State == "complete" &&
            milestone.Percent == 15);
        Assert.Contains(detailResp.ManufacturingMilestones, milestone =>
            milestone.Key == "manufacturing" &&
            milestone.State == "pending" &&
            milestone.Percent == 55);
    }

    [Fact]
    public async Task Order_detail_is_scoped_to_signed_in_customer()
    {
        using var customerA = await CreateSignedInClientAsync("order-detail-owner@example.com");

        var quoteResp = await customerA.PostAsJsonAsync(
            "/quote/v1/quotes/formal",
            new GenerateFormalQuoteRequest(Guid.NewGuid(), "session-order-owner", [], "Customer A order detail."));
        quoteResp.EnsureSuccessStatusCode();
        var quote = await quoteResp.Content.ReadFromJsonAsync<GenerateFormalQuoteResponse>();
        Assert.NotNull(quote);

        var orderResp = await customerA.PostAsJsonAsync(
            "/quote/v1/orders",
            new CreateManufacturingOrderRequest(quote.QuoteId, "PO-OWNER", "Owner order detail."));
        orderResp.EnsureSuccessStatusCode();
        var order = await orderResp.Content.ReadFromJsonAsync<CreateManufacturingOrderResponse>();
        Assert.NotNull(order);

        var ownerDetail = await customerA.GetAsync($"/quote/v1/account/orders/{Uri.EscapeDataString(order.OrderNumber)}");
        ownerDetail.EnsureSuccessStatusCode();

        using var customerB = await CreateSignedInClientAsync("order-detail-other@example.com");
        var crossCustomerDetail = await customerB.GetAsync($"/quote/v1/account/orders/{Uri.EscapeDataString(order.OrderNumber)}");
        Assert.Equal(HttpStatusCode.NotFound, crossCustomerDetail.StatusCode);
        var crossCustomerFile = await customerB.GetAsync(
            $"/quote/v1/account/orders/{Uri.EscapeDataString(order.OrderNumber)}/files/1001/download");
        Assert.Equal(HttpStatusCode.NotFound, crossCustomerFile.StatusCode);
    }

    [Fact]
    public async Task Order_detail_returns_404_for_unknown_order_number()
    {
        using var client = await CreateSignedInClientAsync("order-detail-404@example.com");

        var response = await client.GetAsync("/quote/v1/account/orders/ORD-DOESNT-EXIST");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Order_detail_returns_401_for_anonymous_user()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/quote/v1/account/orders/ORD-2026-00001");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Payment_initiation_returns_hosted_payment_url_for_signed_in_customer()
    {
        factory.ClearPaymentIdempotencyKeys();
        using var client = await CreateSignedInClientAsync("payer@example.com");

        var quoteResp = await client.PostAsJsonAsync(
            "/quote/v1/quotes/formal",
            new GenerateFormalQuoteRequest(Guid.NewGuid(), "session-payment", [], "Payment test."));
        quoteResp.EnsureSuccessStatusCode();
        var quote = await quoteResp.Content.ReadFromJsonAsync<GenerateFormalQuoteResponse>();
        Assert.NotNull(quote);

        var orderResp = await client.PostAsJsonAsync(
            "/quote/v1/orders",
            new CreateManufacturingOrderRequest(quote.QuoteId, "PO-PAY-001", "Payment test order."));
        orderResp.EnsureSuccessStatusCode();
        var order = await orderResp.Content.ReadFromJsonAsync<CreateManufacturingOrderResponse>();
        Assert.NotNull(order);

        var response = await client.PostAsJsonAsync("/quote/v1/payments", new InitiatePaymentRequest
        {
            OrderId = order.OrderId,
            OrderNumber = order.OrderNumber,
            Amount = 1500.00m,
            Currency = "THB",
            BillingAddressId = TestBillingAddressId,
            ShippingAddressId = TestShippingAddressId,
            BillingCompanyName = "MALIEV Test Buyer Co., Ltd.",
            BillingVatNumber = "TH-0123456789012",
            AcceptedTerms = true
        });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<InitiatePaymentResponse>();
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body.TransactionId);
        Assert.StartsWith("https://pay.test.example.com/hosted/", body.PaymentUrl);
    }

    [Fact]
    public async Task Payment_initiation_persists_checkout_shipping_snapshot_on_order()
    {
        // The fake payment client records initiations in process-static state shared by every
        // test in this (non-parallel) collection; reset it first, like the sibling payment
        // tests, so Assert.Single below only sees this test's initiation.
        factory.ClearPaymentIdempotencyKeys();
        using var client = await CreateSignedInClientAsync("payer-snapshot@example.com");

        var quoteResp = await client.PostAsJsonAsync(
            "/quote/v1/quotes/formal",
            new GenerateFormalQuoteRequest(Guid.NewGuid(), "session-payment-snapshot", [], "Payment snapshot test."));
        quoteResp.EnsureSuccessStatusCode();
        var quote = await quoteResp.Content.ReadFromJsonAsync<GenerateFormalQuoteResponse>();
        Assert.NotNull(quote);

        var orderResp = await client.PostAsJsonAsync(
            "/quote/v1/orders",
            new CreateManufacturingOrderRequest(quote.QuoteId, "PO-PAY-SNAPSHOT", "Payment snapshot order."));
        orderResp.EnsureSuccessStatusCode();
        var order = await orderResp.Content.ReadFromJsonAsync<CreateManufacturingOrderResponse>();
        Assert.NotNull(order);

        var response = await client.PostAsJsonAsync("/quote/v1/payments", new InitiatePaymentRequest
        {
            OrderId = order.OrderId,
            OrderNumber = order.OrderNumber,
            Amount = 1500.00m,
            Currency = "THB",
            BillingAddressId = TestBillingAddressId,
            ShippingAddressId = TestShippingAddressId,
            BillingCompanyName = "MALIEV Test Buyer Co., Ltd.",
            BillingVatNumber = "TH-0123456789012",
            AcceptedTerms = true
        });

        response.EnsureSuccessStatusCode();
        var snapshot = factory.LastOrderDeliverySnapshot;
        Assert.NotNull(snapshot);
        Assert.Equal(order.OrderNumber, snapshot.OrderNumber);
        Assert.Equal(TestBillingAddressId, snapshot.BillingAddressId);
        Assert.Equal(TestShippingAddressId, snapshot.ShippingAddressId);
        Assert.Equal("34 Shipping Road", snapshot.ShippingAddressLine1);
        Assert.Equal("Bangkok", snapshot.ShippingCity);
        Assert.Equal("Bangkok", snapshot.ShippingProvince);
        Assert.Equal("10110", snapshot.ShippingPostalCode);
        Assert.Equal("11111111-1111-1111-1111-111111111111", snapshot.ShippingCountry);
        Assert.Equal("MALIEV Test Buyer Co., Ltd.", snapshot.BillingCompanyName);
        Assert.Equal("TH-0123456789012", snapshot.BillingVatNumber);
        Assert.Equal("Receiving", snapshot.DeliveryContactName);
        Assert.Equal("+66810000002", snapshot.DeliveryContactPhone);

        var initiation = Assert.Single(factory.PaymentInitiations);
        Assert.Equal("MALIEV Test Buyer Co., Ltd.", initiation.BillingCompanyName);
        Assert.Equal("TH-0123456789012", initiation.BillingVatNumber);
        Assert.Equal("Receiving", initiation.DeliveryContactName);
        Assert.Equal("+66810000002", initiation.DeliveryContactPhone);
    }

    [Fact]
    public async Task Payment_initiation_blocks_when_order_cannot_be_accepted_before_checkout()
    {
        factory.ClearPaymentIdempotencyKeys();
        using var client = await CreateSignedInClientAsync("payer-acceptance@example.com");

        var quoteResp = await client.PostAsJsonAsync(
            "/quote/v1/quotes/formal",
            new GenerateFormalQuoteRequest(Guid.NewGuid(), "session-payment-acceptance", [], "Payment acceptance failure test."));
        quoteResp.EnsureSuccessStatusCode();
        var quote = await quoteResp.Content.ReadFromJsonAsync<GenerateFormalQuoteResponse>();
        Assert.NotNull(quote);

        var orderResp = await client.PostAsJsonAsync(
            "/quote/v1/orders",
            new CreateManufacturingOrderRequest(quote.QuoteId, "PO-PAY-ACCEPT", "Payment acceptance failure order."));
        orderResp.EnsureSuccessStatusCode();
        var order = await orderResp.Content.ReadFromJsonAsync<CreateManufacturingOrderResponse>();
        Assert.NotNull(order);

        factory.FailNextOrderStatus("Accepted");

        var paymentResp = await client.PostAsJsonAsync("/quote/v1/payments", new InitiatePaymentRequest
        {
            OrderId = order.OrderId,
            OrderNumber = order.OrderNumber,
            Amount = 1500m,
            Currency = "THB",
            BillingAddressId = TestBillingAddressId,
            ShippingAddressId = TestShippingAddressId,
            AcceptedTerms = true
        });

        Assert.Equal(HttpStatusCode.BadGateway, paymentResp.StatusCode);
        Assert.Empty(factory.PaymentIdempotencyKeys);
    }

    [Fact]
    public async Task Payment_initiation_uses_checkout_attempt_id_in_idempotency_key()
    {
        factory.ClearPaymentIdempotencyKeys();
        using var client = await CreateSignedInClientAsync("payer-attempt@example.com");

        var quoteResp = await client.PostAsJsonAsync(
            "/quote/v1/quotes/formal",
            new GenerateFormalQuoteRequest(Guid.NewGuid(), "session-payment-attempt", [], "Payment attempt test."));
        quoteResp.EnsureSuccessStatusCode();
        var quote = await quoteResp.Content.ReadFromJsonAsync<GenerateFormalQuoteResponse>();
        Assert.NotNull(quote);

        var orderResp = await client.PostAsJsonAsync(
            "/quote/v1/orders",
            new CreateManufacturingOrderRequest(quote.QuoteId, "PO-PAY-ATTEMPT", "Payment attempt order."));
        orderResp.EnsureSuccessStatusCode();
        var order = await orderResp.Content.ReadFromJsonAsync<CreateManufacturingOrderResponse>();
        Assert.NotNull(order);

        var firstAttemptId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondAttemptId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var firstResponse = await client.PostAsJsonAsync("/quote/v1/payments", new InitiatePaymentRequest
        {
            OrderId = order.OrderId,
            OrderNumber = order.OrderNumber,
            Amount = 1500.00m,
            Currency = "THB",
            BillingAddressId = TestBillingAddressId,
            ShippingAddressId = TestShippingAddressId,
            AcceptedTerms = true,
            CheckoutAttemptId = firstAttemptId
        });
        firstResponse.EnsureSuccessStatusCode();

        var secondResponse = await client.PostAsJsonAsync("/quote/v1/payments", new InitiatePaymentRequest
        {
            OrderId = order.OrderId,
            OrderNumber = order.OrderNumber,
            Amount = 1500.00m,
            Currency = "THB",
            BillingAddressId = TestBillingAddressId,
            ShippingAddressId = TestShippingAddressId,
            AcceptedTerms = true,
            CheckoutAttemptId = secondAttemptId
        });
        secondResponse.EnsureSuccessStatusCode();

        var keys = factory.PaymentIdempotencyKeys;
        Assert.Equal(2, keys.Count);
        Assert.All(keys, key =>
        {
            Assert.StartsWith("qe:", key, StringComparison.Ordinal);
            Assert.True(key.Length <= 100);
        });
        Assert.NotEqual(keys[0], keys[1]);
    }

    [Fact]
    public async Task Payment_initiation_uses_forwarded_https_scheme_for_provider_callbacks()
    {
        factory.ClearPaymentIdempotencyKeys();
        using var client = await CreateSignedInClientAsync("payer-forwarded-scheme@example.com");
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");

        var quoteResp = await client.PostAsJsonAsync(
            "/quote/v1/quotes/formal",
            new GenerateFormalQuoteRequest(Guid.NewGuid(), "session-payment-forwarded", [], "Payment forwarded scheme test."));
        quoteResp.EnsureSuccessStatusCode();
        var quote = await quoteResp.Content.ReadFromJsonAsync<GenerateFormalQuoteResponse>();
        Assert.NotNull(quote);

        var orderResp = await client.PostAsJsonAsync(
            "/quote/v1/orders",
            new CreateManufacturingOrderRequest(quote.QuoteId, "PO-PAY-FORWARDED", "Payment forwarded scheme order."));
        orderResp.EnsureSuccessStatusCode();
        var order = await orderResp.Content.ReadFromJsonAsync<CreateManufacturingOrderResponse>();
        Assert.NotNull(order);

        var response = await client.PostAsJsonAsync("/quote/v1/payments", new InitiatePaymentRequest
        {
            OrderId = order.OrderId,
            OrderNumber = order.OrderNumber,
            Amount = 1500.00m,
            Currency = "THB",
            BillingAddressId = TestBillingAddressId,
            ShippingAddressId = TestShippingAddressId,
            AcceptedTerms = true
        });

        response.EnsureSuccessStatusCode();
        var initiation = Assert.Single(factory.PaymentInitiations);
        Assert.StartsWith("https://", initiation.ReturnUrl, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("https://", initiation.CancelUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/payment/success", initiation.ReturnUrl, StringComparison.Ordinal);
        Assert.Contains("/payment/cancel", initiation.CancelUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Payment_initiation_rejects_missing_terms_acceptance_before_checkout()
    {
        using var client = await CreateSignedInClientAsync("payer-terms@example.com");

        var quoteResp = await client.PostAsJsonAsync(
            "/quote/v1/quotes/formal",
            new GenerateFormalQuoteRequest(Guid.NewGuid(), "session-payment-terms", [], "Payment terms test."));
        quoteResp.EnsureSuccessStatusCode();
        var quote = await quoteResp.Content.ReadFromJsonAsync<GenerateFormalQuoteResponse>();
        Assert.NotNull(quote);

        var orderResp = await client.PostAsJsonAsync(
            "/quote/v1/orders",
            new CreateManufacturingOrderRequest(quote.QuoteId, "PO-PAY-TERMS", "Payment terms order."));
        orderResp.EnsureSuccessStatusCode();
        var order = await orderResp.Content.ReadFromJsonAsync<CreateManufacturingOrderResponse>();
        Assert.NotNull(order);

        var response = await client.PostAsJsonAsync("/quote/v1/payments", new
        {
            orderId = order.OrderId,
            orderNumber = order.OrderNumber,
            amount = 1500.00m,
            currency = "THB",
            billingAddressId = TestBillingAddressId,
            shippingAddressId = TestShippingAddressId,
            acceptedTerms = false
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Payment_initiation_rejects_missing_billing_or_shipping_address_before_checkout()
    {
        using var client = await CreateSignedInClientAsync("payer-address@example.com");

        var quoteResp = await client.PostAsJsonAsync(
            "/quote/v1/quotes/formal",
            new GenerateFormalQuoteRequest(Guid.NewGuid(), "session-payment-address", [], "Payment address test."));
        quoteResp.EnsureSuccessStatusCode();
        var quote = await quoteResp.Content.ReadFromJsonAsync<GenerateFormalQuoteResponse>();
        Assert.NotNull(quote);

        var orderResp = await client.PostAsJsonAsync(
            "/quote/v1/orders",
            new CreateManufacturingOrderRequest(quote.QuoteId, "PO-PAY-ADDRESS", "Payment address order."));
        orderResp.EnsureSuccessStatusCode();
        var order = await orderResp.Content.ReadFromJsonAsync<CreateManufacturingOrderResponse>();
        Assert.NotNull(order);

        var response = await client.PostAsJsonAsync("/quote/v1/payments", new
        {
            orderId = order.OrderId,
            orderNumber = order.OrderNumber,
            amount = 1500.00m,
            currency = "THB",
            billingAddressId = TestBillingAddressId,
            acceptedTerms = true
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Payment_initiation_rejects_swapped_billing_and_shipping_addresses_before_checkout()
    {
        using var client = await CreateSignedInClientAsync("payer-address-role@example.com");

        var quoteResp = await client.PostAsJsonAsync(
            "/quote/v1/quotes/formal",
            new GenerateFormalQuoteRequest(Guid.NewGuid(), "session-payment-address-role", [], "Payment address role test."));
        quoteResp.EnsureSuccessStatusCode();
        var quote = await quoteResp.Content.ReadFromJsonAsync<GenerateFormalQuoteResponse>();
        Assert.NotNull(quote);

        var orderResp = await client.PostAsJsonAsync(
            "/quote/v1/orders",
            new CreateManufacturingOrderRequest(quote.QuoteId, "PO-PAY-ADDRESS-ROLE", "Payment address role order."));
        orderResp.EnsureSuccessStatusCode();
        var order = await orderResp.Content.ReadFromJsonAsync<CreateManufacturingOrderResponse>();
        Assert.NotNull(order);

        var response = await client.PostAsJsonAsync("/quote/v1/payments", new InitiatePaymentRequest
        {
            OrderId = order.OrderId,
            OrderNumber = order.OrderNumber,
            Amount = 1500.00m,
            Currency = "THB",
            BillingAddressId = TestShippingAddressId,
            ShippingAddressId = TestBillingAddressId,
            AcceptedTerms = true
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Payment_initiation_returns_401_for_anonymous_user()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/quote/v1/payments", new InitiatePaymentRequest
        {
            OrderId = Guid.NewGuid(),
            OrderNumber = "ORD-2026-00001",
            Amount = 500m,
            Currency = "THB",
            BillingAddressId = TestBillingAddressId,
            ShippingAddressId = TestShippingAddressId,
            AcceptedTerms = true
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Payment_initiation_is_scoped_to_signed_in_customer_order()
    {
        using var customerA = await CreateSignedInClientAsync("payment-owner@example.com");

        var quoteResp = await customerA.PostAsJsonAsync(
            "/quote/v1/quotes/formal",
            new GenerateFormalQuoteRequest(Guid.NewGuid(), "session-payment-owner", [], "Owner payment scope."));
        quoteResp.EnsureSuccessStatusCode();
        var quote = await quoteResp.Content.ReadFromJsonAsync<GenerateFormalQuoteResponse>();
        Assert.NotNull(quote);

        var orderResp = await customerA.PostAsJsonAsync(
            "/quote/v1/orders",
            new CreateManufacturingOrderRequest(quote.QuoteId, "PO-PAY-OWNER", "Owner payment order."));
        orderResp.EnsureSuccessStatusCode();
        var order = await orderResp.Content.ReadFromJsonAsync<CreateManufacturingOrderResponse>();
        Assert.NotNull(order);

        using var customerB = await CreateSignedInClientAsync("payment-other@example.com");
        var crossCustomerPayment = await customerB.PostAsJsonAsync("/quote/v1/payments", new InitiatePaymentRequest
        {
            OrderId = order.OrderId,
            OrderNumber = order.OrderNumber,
            Amount = 1500.00m,
            Currency = "THB",
            BillingAddressId = TestBillingAddressId,
            ShippingAddressId = TestShippingAddressId,
            AcceptedTerms = true
        });

        Assert.Equal(HttpStatusCode.NotFound, crossCustomerPayment.StatusCode);

        var ownerPayment = await customerA.PostAsJsonAsync("/quote/v1/payments", new InitiatePaymentRequest
        {
            OrderId = order.OrderId,
            OrderNumber = order.OrderNumber,
            Amount = 1500.00m,
            Currency = "THB",
            BillingAddressId = TestBillingAddressId,
            ShippingAddressId = TestShippingAddressId,
            AcceptedTerms = true
        });

        ownerPayment.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Payment_initiation_rejects_customer_supplied_amount_that_does_not_match_order()
    {
        using var client = await CreateSignedInClientAsync("payment-amount@example.com");

        var quoteResp = await client.PostAsJsonAsync(
            "/quote/v1/quotes/formal",
            new GenerateFormalQuoteRequest(Guid.NewGuid(), "session-payment-amount", [], "Payment amount scope."));
        quoteResp.EnsureSuccessStatusCode();
        var quote = await quoteResp.Content.ReadFromJsonAsync<GenerateFormalQuoteResponse>();
        Assert.NotNull(quote);

        var orderResp = await client.PostAsJsonAsync(
            "/quote/v1/orders",
            new CreateManufacturingOrderRequest(quote.QuoteId, "PO-PAY-AMOUNT", "Payment amount order."));
        orderResp.EnsureSuccessStatusCode();
        var order = await orderResp.Content.ReadFromJsonAsync<CreateManufacturingOrderResponse>();
        Assert.NotNull(order);

        var underpayment = await client.PostAsJsonAsync("/quote/v1/payments", new InitiatePaymentRequest
        {
            OrderId = order.OrderId,
            OrderNumber = order.OrderNumber,
            Amount = 1.00m,
            Currency = "THB",
            BillingAddressId = TestBillingAddressId,
            ShippingAddressId = TestShippingAddressId,
            AcceptedTerms = true
        });

        Assert.Equal(HttpStatusCode.BadRequest, underpayment.StatusCode);

        var matchingPayment = await client.PostAsJsonAsync("/quote/v1/payments", new InitiatePaymentRequest
        {
            OrderId = order.OrderId,
            OrderNumber = order.OrderNumber,
            Amount = 1500.00m,
            Currency = "THB",
            BillingAddressId = TestBillingAddressId,
            ShippingAddressId = TestShippingAddressId,
            AcceptedTerms = true
        });

        matchingPayment.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Full_self_service_flow_quote_to_payment_succeeds_for_signed_in_customer()
    {
        using var client = await CreateSignedInClientAsync("self-service@example.com");

        // Step 1: Generate a formal quote
        var quoteResp = await client.PostAsJsonAsync(
            "/quote/v1/quotes/formal",
            new GenerateFormalQuoteRequest(Guid.NewGuid(), "session-e2e", [], "Self-service E2E test."));
        quoteResp.EnsureSuccessStatusCode();
        var quote = await quoteResp.Content.ReadFromJsonAsync<GenerateFormalQuoteResponse>();
        Assert.NotNull(quote);
        Assert.NotEqual(Guid.Empty, quote.QuoteId);

        // Step 2: Create manufacturing order (triggers self-service fast-track: New → Reviewing → Reviewed → Quoted)
        var orderResp = await client.PostAsJsonAsync(
            "/quote/v1/orders",
            new CreateManufacturingOrderRequest(quote.QuoteId, "PO-E2E-001", "Full self-service test."));
        orderResp.EnsureSuccessStatusCode();
        var order = await orderResp.Content.ReadFromJsonAsync<CreateManufacturingOrderResponse>();
        Assert.NotNull(order);
        Assert.NotEqual(Guid.Empty, order.OrderId);
        Assert.NotEmpty(order.OrderNumber);

        // Step 3: Initiate payment (advances order to Accepted, then calls PaymentService)
        var paymentResp = await client.PostAsJsonAsync("/quote/v1/payments", new InitiatePaymentRequest
        {
            OrderId = order.OrderId,
            OrderNumber = order.OrderNumber,
            Amount = 1500m,
            Currency = "THB",
            BillingAddressId = TestBillingAddressId,
            ShippingAddressId = TestShippingAddressId,
            AcceptedTerms = true
        });
        paymentResp.EnsureSuccessStatusCode();
        var payment = await paymentResp.Content.ReadFromJsonAsync<InitiatePaymentResponse>();
        Assert.NotNull(payment);
        Assert.NotEqual(Guid.Empty, payment.TransactionId);
        Assert.StartsWith("https://pay.test.example.com/hosted/", payment.PaymentUrl);
    }


    private static async Task<CreateDraftProjectResponse> CreateDraftProjectAsync(HttpClient client, string title)
    {
        var response = await client.PostAsJsonAsync(
            "/quote/v1/projects/draft",
            new CreateDraftProjectRequest(
                QuoteSessionId: Guid.NewGuid().ToString("N"),
                Parts: [],
                Notes: "Project navigation test draft.",
                Title: title));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CreateDraftProjectResponse>()
            ?? throw new InvalidOperationException("QuoteEngine returned an empty draft project response.");
    }

    private async Task<HttpClient> CreateSignedInClientAsync(string email = "customer@example.com")
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var signIn = await client.GetAsync($"/test/sign-in?email={Uri.EscapeDataString(email)}");
        signIn.EnsureSuccessStatusCode();
        return client;
    }

    private async Task<HttpClient> CreateSignedInClientAsync(
        string email,
        Action<IServiceCollection> configureServices)
    {
        var scopedFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(configureServices));
        var client = scopedFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var signIn = await client.GetAsync($"/test/sign-in?email={Uri.EscapeDataString(email)}");
        signIn.EnsureSuccessStatusCode();
        return client;
    }

    private sealed class FailingQuoteUploadServiceClient()
        : QuoteUploadServiceClient(new HttpClient(), NullLogger<QuoteUploadServiceClient>.Instance)
    {
        public override Task<string> InitiateResumableUploadAsync(
            string fileName,
            string contentType,
            long totalSize,
            string storagePath,
            IReadOnlyDictionary<string, string>? metadataTags,
            CancellationToken ct) =>
            throw new InvalidOperationException("UploadService is intentionally unavailable.");

        public override Task StreamUploadAsync(
            Stream body,
            string contentType,
            long contentLength,
            string contentRange,
            string downstreamUploadId,
            string storagePath,
            CancellationToken ct) =>
            throw new InvalidOperationException("UploadService is intentionally unavailable.");
    }

    private WebApplicationFactory<Program> CreateChatbotFactory()
    {
        return factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IChatbotServiceClient>();
                services.AddScoped<IChatbotServiceClient, FakeChatbotServiceClient>();
            });
        });
    }

    private sealed class FakeChatbotServiceClient : IChatbotServiceClient
    {
        private static readonly Guid SessionId = Guid.Parse("50d1d515-4c9b-4de2-ad10-840a19f4f64a");

        public Task<bool> CheckReadinessAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(true);
        }

        public Task<ChatbotSessionResponse?> InitiateSessionAsync(ChatbotInitiateSessionRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult<ChatbotSessionResponse?>(new ChatbotSessionResponse
            {
                SessionId = SessionId,
                Language = request.Language,
                WelcomeMessage = "Mali is ready.",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            });
        }

        public Task<ChatbotMessageResponse?> SendMessageAsync(ChatbotSendMessageRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult<ChatbotMessageResponse?>(new ChatbotMessageResponse
            {
                MessageId = Guid.Parse("20c5a8da-7a10-46da-bcf3-83f757987846"),
                Content = "Mali can help with CNC aluminum fixture quotes in Quote Engine.",
                Role = "assistant",
                Language = "en",
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        public async IAsyncEnumerable<ChatbotMessageStreamEvent> SendMessageStreamAsync(
            ChatbotSendMessageRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield return new ChatbotMessageStreamEvent { Type = "started" };
            yield return new ChatbotMessageStreamEvent
            {
                Type = "delta",
                Delta = "Mali can help with CNC aluminum "
            };
            yield return new ChatbotMessageStreamEvent
            {
                Type = "delta",
                Delta = "fixture quotes in Quote Engine."
            };
            yield return new ChatbotMessageStreamEvent
            {
                Type = "final",
                Message = new ChatbotMessageResponse
                {
                    MessageId = Guid.Parse("20c5a8da-7a10-46da-bcf3-83f757987846"),
                    Content = "Mali can help with CNC aluminum fixture quotes in Quote Engine.",
                    Role = "assistant",
                    Language = "en",
                    CreatedAt = DateTimeOffset.UtcNow
                }
            };
        }

        public Task<ChatbotConversationMessagesResponse?> GetConversationMessagesAsync(Guid sessionId, CancellationToken cancellationToken)
        {
            return Task.FromResult<ChatbotConversationMessagesResponse?>(new ChatbotConversationMessagesResponse
            {
                SessionId = sessionId,
                Language = "en",
                Messages =
                [
                    new ChatbotConversationMessageResponse
                    {
                        Role = "assistant",
                        Content = "Hydrated assistant message.",
                        CreatedAt = DateTimeOffset.UtcNow
                    }
                ]
            });
        }

        public Task<bool> TruncateLastTurnAsync(Guid sessionId, CancellationToken cancellationToken)
        {
            return Task.FromResult(true);
        }

        public Task<string?> CleanSpeechAsync(string speech, string language, CancellationToken cancellationToken)
        {
            return Task.FromResult<string?>(speech);
        }
    }

    /// <summary>
    /// Runtime version declared by the embedded fallback worker. Never pin a
    /// literal version in tests: services always pull the latest GeometryService
    /// runtime, and the fallback manifest derives its version from the synced
    /// worker source.
    /// </summary>
    private static string EmbeddedRuntimeWorkerVersion()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Maliev.QuoteEngine.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var workerPath = Path.Combine(
            dir!.FullName, "Maliev.QuoteEngine.Bff", "GeometryRuntimeFallback",
            "client-geometry-runtime.worker.js");
        var source = File.ReadAllText(workerPath);
        var match = System.Text.RegularExpressions.Regex.Match(
            source, "MALIEV_BROWSER_GEOMETRY_RUNTIME_VERSION = \"([^\"]+)\"");
        Assert.True(match.Success, "embedded worker must declare its runtime version");
        return match.Groups[1].Value;
    }

}

/// <summary>
/// Test-only startup filter that maps GET /test/sign-in?email=... to issue the shared identity
/// cookie. Registered in QuoteEngineWebApplicationFactory.ConfigureTestServices to replace the
/// removed /quote/v1/auth/sign-in endpoint for test authentication.
/// </summary>
internal sealed class TestSignInStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Method == "GET" && context.Request.Path == "/test/sign-in")
                {
                    var email = context.Request.Query["email"].ToString();
                    if (string.IsNullOrWhiteSpace(email)) email = "customer@example.com";
                    var profileImageUrl = context.Request.Query["profileImageUrl"].ToString();
                    var normalizedEmail = email.Trim().ToLowerInvariant();

                    var idBytes = MD5.HashData(Encoding.UTF8.GetBytes(normalizedEmail));
                    var customerId = new Guid(idBytes);

                    var store = context.RequestServices.GetRequiredService<QuoteEnginePrototypeStore>();
                    store.UpsertCustomer(customerId, normalizedEmail, "Test Customer", string.Empty, string.Empty, "en");
                    var customerClient = context.RequestServices.GetService<ICustomerServiceClient>();
                    if (customerClient is not null)
                    {
                        await customerClient.EnsureCustomerAsync(normalizedEmail, "Test Customer", ct: context.RequestAborted);
                    }

                    var omitCustomerId = string.Equals(
                        context.Request.Query["omitCustomerId"].ToString(),
                        "true",
                        StringComparison.OrdinalIgnoreCase);

                    var claims = new List<Claim>
                    {
                        new Claim(ClaimTypes.NameIdentifier, customerId.ToString()),
                        new Claim("user_type", "customer"),
                        new Claim(ClaimTypes.Email, normalizedEmail),
                        new Claim(ClaimTypes.Name, "Test Customer"),
                        new Claim("email_verified", "true")
                    };
                    if (!omitCustomerId)
                    {
                        claims.Add(new Claim("customer_id", customerId.ToString()));
                    }
                    if (!string.IsNullOrWhiteSpace(profileImageUrl))
                    {
                        claims.Add(new Claim("profile_image_url", profileImageUrl));
                    }

                    await context.SignInAsync(
                        CookieAuthenticationDefaults.AuthenticationScheme,
                        new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
                    context.Response.StatusCode = 200;
                    return;
                }
                await nextMiddleware(context);
            });
            next(app);
        };
    }
}
