using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    public IReadOnlyList<string> PaymentIdempotencyKeys => FakePaymentServiceClient.IdempotencyKeys.ToArray();

    public void ClearPaymentIdempotencyKeys()
    {
        while (FakePaymentServiceClient.IdempotencyKeys.TryDequeue(out _))
        {
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            // Replace the real UploadService HTTP client with a no-op test double.
            services.RemoveAll<QuoteUploadServiceClient>();
            services.AddSingleton<QuoteUploadServiceClient>(new NoOpQuoteUploadServiceClient());

            services.RemoveAll<IQuoteGeometryRuntimeClient>();
            services.AddSingleton<IQuoteGeometryRuntimeClient>(new FakeQuoteGeometryRuntimeClient());

            // Replace real downstream service clients with in-memory fakes.
            services.RemoveAll<IMaterialCatalogClient>();
            services.AddSingleton<IMaterialCatalogClient>(new FakeMaterialCatalogClient());

            services.RemoveAll<IQuotationServiceClient>();
            services.AddSingleton<IQuotationServiceClient>(new FakeQuotationServiceClient());

            services.RemoveAll<IOrderServiceClient>();
            services.AddSingleton<IOrderServiceClient>(new FakeOrderServiceClient());

            // Returns null → AccountController falls back to PrototypeStore for profile
            services.RemoveAll<ICustomerServiceClient>();
            services.AddSingleton<ICustomerServiceClient>(new FakeCustomerServiceClient());

            services.RemoveAll<ICountryServiceClient>();
            services.AddSingleton<ICountryServiceClient>(new FakeCountryServiceClient());

            services.RemoveAll<IRegistryServiceClient>();
            services.AddSingleton<IRegistryServiceClient>(new FakeRegistryServiceClient());

            // Returns a fixed hosted payment URL
            services.RemoveAll<IPaymentServiceClient>();
            services.AddSingleton<IPaymentServiceClient>(new FakePaymentServiceClient());

            services.RemoveAll<IQePricingServiceClient>();
            services.AddSingleton<IQePricingServiceClient>(new FakePricingServiceClient());

            // Test-only sign-in endpoint: issues the shared identity cookie with customer_id claim.
            // Replaces the removed /quote/v1/auth/sign-in endpoint for test authentication.
            services.AddTransient<IStartupFilter, TestSignInStartupFilter>();
        });
    }

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

        public Task<QuotationCreatedResult?> CreateAsync(QuotationCreateRequest request, CancellationToken ct = default)
        {
            var result = new QuotationCreatedResult
            {
                Id = Guid.NewGuid(),
                CustomerId = request.CustomerId,
                QuotationNumber = $"MQ-TEST-{Guid.NewGuid():N}"[..16],
                Status = "Draft",
                Total = request.LineItems.Sum(x => x.UnitPrice * x.Quantity),
                CurrencyCode = "THB",
                UpdatedAt = DateTime.UtcNow
            };
            _quotes[result.Id] = result;
            return Task.FromResult<QuotationCreatedResult?>(result);
        }

        public Task<QuotationCreatedResult?> GetByIdAsync(Guid quotationId, CancellationToken ct = default) =>
            Task.FromResult(_quotes.TryGetValue(quotationId, out var r) ? r : null);

        public Task<IReadOnlyList<CustomerQuoteSummaryDto>> GetByCustomerAsync(Guid customerId, CancellationToken ct = default)
        {
            IReadOnlyList<CustomerQuoteSummaryDto> result = _quotes.Values
                .Where(q => q.CustomerId == customerId)
                .Select(q => new CustomerQuoteSummaryDto(
                    q.Id, q.QuotationNumber, q.Status, q.Total, q.CurrencyCode,
                    new DateTimeOffset(q.UpdatedAt, TimeSpan.Zero), string.Empty))
                .ToArray();
            return Task.FromResult(result);
        }
    }

    private sealed class FakeOrderServiceClient : IOrderServiceClient
    {
        private readonly ConcurrentDictionary<string, List<CustomerOrderSummaryDto>> _ordersByCustomer = new();
        private readonly ConcurrentDictionary<string, CustomerOrderDetailDto> _ordersByNumber = new();

        public Task<OrderCreatedResult?> CreateAsync(OrderCreateRequest request, CancellationToken ct = default)
        {
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

            return Task.FromResult<OrderCreatedResult?>(new OrderCreatedResult
            {
                OrderId = orderId,
                OrderNumber = orderNumber,
                Status = "Pending"
            });
        }

        public Task<IReadOnlyList<CustomerOrderSummaryDto>> GetByCustomerAsync(string customerId, CancellationToken ct = default)
        {
            IReadOnlyList<CustomerOrderSummaryDto> result =
                _ordersByCustomer.TryGetValue(customerId, out var list) ? [.. list] : [];
            return Task.FromResult(result);
        }

        public Task<CustomerOrderDetailDto?> GetDetailAsync(string orderNumber, CancellationToken ct = default) =>
            Task.FromResult(_ordersByNumber.TryGetValue(orderNumber, out var detail) ? detail : null);

        public Task<bool> AddStatusAsync(string orderId, string status, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    private sealed class FakeCustomerServiceClient : ICustomerServiceClient
    {
        private static readonly Guid DefaultBillingAddressId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        private static readonly Guid DefaultShippingAddressId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        private readonly ConcurrentDictionary<Guid, List<CustomerAddressDto>> _addressesByCustomer = new();
        private uint _nextVersion = 1;

        public Task<CustomerProfileResponse?> GetByIdAsync(Guid customerId, CancellationToken ct = default) =>
            Task.FromResult<CustomerProfileResponse?>(null);

        public Task<CustomerProfileResponse?> GetByEmailAsync(string email, CancellationToken ct = default) =>
            Task.FromResult<CustomerProfileResponse?>(CreateProfile(email));

        public Task<CustomerProfileResponse?> EnsureCustomerAsync(
            string email,
            string displayName,
            string phone = "",
            CancellationToken ct = default) =>
            Task.FromResult<CustomerProfileResponse?>(CreateProfile(email, displayName, phone));

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

        private static CustomerProfileResponse CreateProfile(string email, string displayName = "Quote customer", string phone = "")
        {
            var normalizedEmail = string.IsNullOrWhiteSpace(email) ? "customer@example.com" : email.Trim().ToLowerInvariant();
            var idBytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(normalizedEmail));
            return new CustomerProfileResponse(
                new Guid(idBytes),
                string.IsNullOrWhiteSpace(displayName) ? normalizedEmail : displayName,
                normalizedEmail,
                phone,
                string.Empty,
                "en");
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

        public Task<PaymentInitiatedResult?> InitiateAsync(
            string customerId, string orderId, string orderNumber,
            decimal amount, string currency,
            string returnUrl, string cancelUrl, string idempotencyKey,
            Guid? billingAddressId, Guid? shippingAddressId, bool acceptedTerms,
            CancellationToken ct = default)
        {
            IdempotencyKeys.Enqueue(idempotencyKey);
            return Task.FromResult<PaymentInitiatedResult?>(new PaymentInitiatedResult
            {
                TransactionId = Guid.NewGuid(),
                PaymentUrl = $"https://pay.test.example.com/hosted/{Guid.NewGuid():N}",
                Status = "1"
            });
        }
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
}

public sealed class QuoteEngineEndpointTests(QuoteEngineWebApplicationFactory factory)
    : IClassFixture<QuoteEngineWebApplicationFactory>
{
    private static readonly Guid TestBillingAddressId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TestShippingAddressId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task Root_landing_is_server_rendered_without_loading_wasm_bundle()
    {
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/");

        Assert.Contains("class=\"landing-shell\"", html, StringComparison.Ordinal);
        Assert.Contains("data-landing-appbar", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/demo\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/demo\" data-wasm-entry", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/auth/sign-in?returnUrl=/quote/new\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/auth/sign-in?returnUrl=/quote/new\" data-wasm-entry", html, StringComparison.Ordinal);
        Assert.Contains("sessionStorage.setItem(\"maliev.quote.workspace.handoff\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("_framework/blazor.webassembly.js", html, StringComparison.Ordinal);
        Assert.DoesNotContain("_content/MudBlazor", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Auth_pages_redirect_to_web_sign_in_without_loading_wasm_bundle()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var signIn = await client.GetAsync("/auth/sign-in?returnUrl=/quote/new");
        var signUp = await client.GetAsync("/auth/sign-up?returnUrl=/quote/new");

        // Auth pages redirect to Maliev.Web — QuoteEngine has no own sign-in surface.
        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
        Assert.NotNull(signIn.Headers.Location);
        Assert.Contains("/auth/sign-in", signIn.Headers.Location.OriginalString, StringComparison.Ordinal);
        Assert.Contains("returnUrl=", signIn.Headers.Location.OriginalString, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.Redirect, signUp.StatusCode);
        Assert.NotNull(signUp.Headers.Location);
        Assert.Contains("/auth/sign-up", signUp.Headers.Location.OriginalString, StringComparison.Ordinal);
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
        Assert.Contains("\"runtimeVersion\":\"1.0.0\"", body, StringComparison.Ordinal);
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
        Assert.Contains("\"runtimeVersion\":\"1.0.0\"", body, StringComparison.Ordinal);
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

        response.EnsureSuccessStatusCode();
        var handoff = await response.Content.ReadFromJsonAsync<QuoteUploadHandoffResponse>();
        Assert.NotNull(handoff);
        Assert.Equal("web-session-1", handoff.QuoteSessionId);
        var part = Assert.Single(handoff.Parts);
        Assert.Equal("web-upload-1", part.UploadId);
        Assert.Equal("Analyzed", part.Status);
        Assert.True(part.VolumeCc > 0);
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
        Assert.Equal("THB", profile.PreferredCurrency);
        Assert.NotEmpty(profile.Timezone);
        Assert.Equal("Active", profile.NdaStatus);
        Assert.NotNull(profile.NdaExpiresAt);
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
        Assert.False(duplicatedPart.ViewerSettings.EdgesEnabled);
        Assert.False(duplicatedPart.ViewerSettings.GridEnabled);
        Assert.True(duplicatedPart.ViewerSettings.DfmOverlayEnabled);
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
        Assert.Contains(customerAQuotes, item => item.QuoteId == quote.QuoteId);
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

        var quoteResponse = await client.PostAsJsonAsync(
            "/quote/v1/quotes/formal",
            new GenerateFormalQuoteRequest(Guid.NewGuid(), "session-configured-order", [part], "Configured order quote."));
        quoteResponse.EnsureSuccessStatusCode();
        var quote = await quoteResponse.Content.ReadFromJsonAsync<GenerateFormalQuoteResponse>();
        Assert.NotNull(quote);

        var orderResponse = await client.PostAsJsonAsync(
            "/quote/v1/orders",
            new CreateManufacturingOrderRequest(
                quote.QuoteId,
                "PO-CONFIGURED",
                "Customer accepted configured quote.")
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
        Assert.Equal("Pending", detailResp.CurrentStatus);
        Assert.NotEmpty(detailResp.StatusHistory);
        Assert.Contains(detailResp.StatusHistory, s => s.Status == "Pending");
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
            AcceptedTerms = true
        });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<InitiatePaymentResponse>();
        Assert.NotNull(body);
        Assert.NotEqual(Guid.Empty, body.TransactionId);
        Assert.StartsWith("https://pay.test.example.com/hosted/", body.PaymentUrl);
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
        Assert.Contains(keys, key => key.EndsWith(firstAttemptId.ToString("D"), StringComparison.Ordinal));
        Assert.Contains(keys, key => key.EndsWith(secondAttemptId.ToString("D"), StringComparison.Ordinal));
        Assert.NotEqual(keys[0], keys[1]);
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

    [Fact]
    public async Task Chatbot_message_routes_through_quote_boundary()
    {
        using var chatbotFactory = CreateChatbotFactory();
        using var client = chatbotFactory.CreateClient();

        var response = await client.PostAsJsonAsync("/quote/v1/chatbot/messages", new CustomerChatbotRequest
        {
            Message = "Can you help with CNC aluminum fixtures?",
            Language = "en"
        });
        var body = await response.Content.ReadFromJsonAsync<CustomerChatbotResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("assistant", body.Role);
        Assert.NotEqual(Guid.Empty, body.SessionId);
    }

    [Fact]
    public async Task Chatbot_session_endpoint_reports_quote_auth_state()
    {
        using var client = await CreateSignedInClientAsync("quote-chat@example.com");

        var session = await client.GetFromJsonAsync<CustomerChatbotSessionResponse>("/quote/v1/chatbot/session");

        Assert.NotNull(session);
        Assert.True(session.IsAuthenticated);
        Assert.Equal("quote-chat@example.com", session.Email);
        Assert.NotNull(session.CustomerId);
    }

    [Fact]
    public async Task Chatbot_hydrate_validates_shared_session_request()
    {
        using var client = factory.CreateClient();
        var knownSessionId = Guid.NewGuid();

        var response = await client.PostAsJsonAsync("/quote/v1/chatbot/hydrate", new CustomerChatbotHydrateRequest
        {
            SessionId = knownSessionId
        });
        var body = await response.Content.ReadFromJsonAsync<CustomerChatbotHydrateResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal(knownSessionId, body.SessionId);
        Assert.False(body.Hydrated);
        Assert.NotNull(body.ContinuationMessage);
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
                    var normalizedEmail = email.Trim().ToLowerInvariant();

                    var idBytes = MD5.HashData(Encoding.UTF8.GetBytes(normalizedEmail));
                    var customerId = new Guid(idBytes);

                    var store = context.RequestServices.GetRequiredService<QuoteEnginePrototypeStore>();
                    store.UpsertCustomer(customerId, normalizedEmail, "Test Customer", string.Empty, string.Empty, "en");

                    var claims = new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, customerId.ToString()),
                        new Claim("customer_id", customerId.ToString()),
                        new Claim("user_type", "customer"),
                        new Claim(ClaimTypes.Email, normalizedEmail),
                        new Claim(ClaimTypes.Name, "Test Customer"),
                        new Claim("email_verified", "true")
                    };
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
