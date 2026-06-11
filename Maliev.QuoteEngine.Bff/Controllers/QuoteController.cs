using Asp.Versioning;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Hubs;
using Maliev.QuoteEngine.Bff.Options;
using Maliev.QuoteEngine.Bff.Services;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;


namespace Maliev.QuoteEngine.Bff.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("quote/v{version:apiVersion}")]
public sealed class QuoteController(
    QuoteEnginePrototypeStore store,
    CustomerSessionResolver sessionResolver,
    IHubContext<QuoteNotificationsHub> hubContext,
    QuoteUploadServiceClient uploadClient,
    IQuoteFileAnalysisStatusService statusService,
    IOptions<DemoModeOptions> demoOptions,
    IMaterialCatalogClient materialCatalog,
    IQuotationServiceClient quotationClient,
    IOrderServiceClient orderClient,
    IPaymentServiceClient paymentClient,
    IQePricingServiceClient pricingClient,
    IHostEnvironment environment,
    ILogger<QuoteController> logger) : ControllerBase
{
    private const string PrototypeUploadPrefix = "prototype-local:";
    private static readonly HashSet<string> BrowserViewerSourceExtensions = new(
        [".3mf", ".glb", ".gltf", ".obj", ".stl"],
        StringComparer.OrdinalIgnoreCase);

    [HttpGet("reference-data")]
    public ActionResult<QuoteReferenceDataResponse> GetReferenceData()
    {
        return Ok(store.ReferenceData);
    }

    [HttpGet("demo/project")]
    public ActionResult<QuoteEngineDemoProjectResponse> GetDemoProject()
    {
        var demo = demoOptions.Value;
        if (!demo.IsConfigured)
            return Ok(store.DemoProject);

        var project = store.DemoProject;
        return Ok(project with
        {
            ViewerUrl = demo.GlbUrl!,
            ThumbnailUrl = demo.ThumbnailUrl ?? project.ThumbnailUrl
        });
    }

    [HttpPost("uploads/resumable")]
    public async Task<ActionResult<InitiateQuoteUploadResponse>> InitiateUpload(
        [FromBody] InitiateQuoteUploadRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        if (!QuoteUploadConstraints.IsSupportedCadFileName(request.FileName))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Unsupported CAD file type.",
                Detail = $"Upload {QuoteUploadConstraints.SupportedCadExtensionLabel} files."
            });
        }

        if (request.FileSizeBytes > QuoteUploadConstraints.MaxFileSizeBytes)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "File too large.",
                Detail = $"Quote Engine accepts files up to {QuoteUploadConstraints.MaxFileSizeMegabytes} MB."
            });
        }

        var customerId = sessionResolver.TryResolveCustomerId(out var resolvedCustomerId)
            ? resolvedCustomerId
            : (Guid?)null;
        var upload = store.InitiateUpload(request, customerId);
        try
        {
            var metadataTags = BuildBrowserPrimaryUploadMetadata(upload.FileName);
            var downstreamUploadId = await uploadClient.InitiateResumableUploadAsync(
                upload.FileName,
                upload.ContentType,
                upload.ExpectedSizeBytes,
                upload.StoragePath,
                metadataTags,
                cancellationToken);
            upload = store.AttachDownstreamUpload(upload.UploadId, downstreamUploadId);
        }
        catch (Exception ex)
        {
            if (!CanUsePrototypeUploadFallback)
            {
                logger.LogError(ex, "Failed to initiate UploadService session for quote upload {UploadId}", upload.UploadId);
                return StatusCode(502, new ProblemDetails { Title = "Upload service unavailable." });
            }

            upload = store.AttachDownstreamUpload(upload.UploadId, CreatePrototypeUploadId(upload.UploadId));
            logger.LogWarning(
                ex,
                "UploadService unavailable for quote upload {UploadId}; using local prototype upload fallback.",
                upload.UploadId);
        }

        return Ok(new InitiateQuoteUploadResponse(
            upload.UploadId,
            $"/quote/v1/uploads/resumable/{upload.UploadId}",
            upload.StoragePath,
            upload.ExpectedSizeBytes));
    }

    [HttpPut("uploads/resumable/{uploadId}")]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> ResumeUpload(string uploadId, CancellationToken cancellationToken)
    {
        var contentRange = Request.Headers.ContentRange.ToString();
        if (string.IsNullOrWhiteSpace(contentRange))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Content-Range is required.",
                Detail = "Quote uploads use the resumable upload contract and must provide Content-Range."
            });
        }

        var upload = store.GetUpload(uploadId);
        if (upload is null) return NotFound();
        if (!CanAccessUpload(upload)) return Forbid();
        if (string.IsNullOrWhiteSpace(upload.DownstreamUploadId))
        {
            logger.LogError("Quote upload {UploadId} has no downstream UploadService session.", uploadId);
            return StatusCode(502, new ProblemDetails { Title = "Upload session unavailable." });
        }

        if (IsPrototypeUpload(upload))
        {
            await Request.Body.CopyToAsync(Stream.Null, cancellationToken);
            store.MarkUploaded(uploadId, Request.ContentLength ?? upload.ExpectedSizeBytes);
            return NoContent();
        }

        // Stream body bytes to UploadService (GCS-backed)
        try
        {
            await uploadClient.StreamUploadAsync(
                Request.Body, upload.ContentType,
                Request.ContentLength ?? 0, contentRange,
                upload.DownstreamUploadId,
                upload.StoragePath, cancellationToken);
        }
        catch (Exception ex)
        {
            if (CanUsePrototypeUploadFallback)
            {
                logger.LogWarning(
                    ex,
                    "UploadService stream failed for quote upload {UploadId}; using local prototype upload fallback.",
                    uploadId);
                store.AttachDownstreamUpload(uploadId, CreatePrototypeUploadId(uploadId));
                store.MarkUploaded(uploadId, Request.ContentLength ?? upload.ExpectedSizeBytes);
                return NoContent();
            }

            logger.LogError(ex, "Failed to stream upload chunk for {UploadId}", uploadId);
            return StatusCode(502, new ProblemDetails { Title = "Upload forwarding failed." });
        }

        store.MarkUploaded(uploadId, Request.ContentLength ?? 0);
        return NoContent();
    }

    [HttpPost("uploads/resumable/{uploadId}/complete")]
    public async Task<ActionResult<CompleteQuoteUploadResponse>> CompleteUpload(
        string uploadId, CancellationToken cancellationToken)
    {
        var existingUpload = store.GetUpload(uploadId);
        if (existingUpload is null) return NotFound();
        if (!CanAccessUpload(existingUpload)) return Forbid();

        // Demo short-circuit: sample bracket returns a pre-computed result immediately
        var demo = demoOptions.Value;
        if (demo.IsConfigured &&
            string.Equals(existingUpload.FileName, demo.SampleFileName, StringComparison.OrdinalIgnoreCase))
        {
            var demoUpload = store.MarkDemoAnalyzed(uploadId, demo);
            await hubContext.Clients
                .Group(QuoteNotificationsHub.FileGroup(demoUpload.StoragePath))
                .SendAsync("GlbReady", new QeGlbReadyPayload(
                    demoUpload.StoragePath, demo.GlbUrl!, demo.ThumbnailUrl,
                    1, true, false, null), cancellationToken);
            return Ok(new CompleteQuoteUploadResponse(
                demoUpload.UploadId, demoUpload.FileId, demoUpload.FileName,
                demoUpload.StoragePath, demoUpload.Status));
        }

        if (IsPrototypeUpload(existingUpload))
        {
            var prototypeUpload = store.MarkAnalyzed(uploadId);
            return Ok(new CompleteQuoteUploadResponse(
                prototypeUpload.UploadId, prototypeUpload.FileId, prototypeUpload.FileName,
                prototypeUpload.StoragePath, prototypeUpload.Status));
        }

        // Real pipeline: mark as Processing and wait for geometry events via MassTransit
        var upload = store.MarkProcessing(uploadId);
        await statusService.SetProcessingAsync(upload.StoragePath, cancellationToken);

        return Ok(new CompleteQuoteUploadResponse(
            upload.UploadId, upload.FileId, upload.FileName,
            upload.StoragePath, upload.Status));   // Status = "Processing"
    }

    [HttpPost("uploads/handoff")]
    public ActionResult<QuoteUploadHandoffResponse> ImportHandoff([FromBody] QuoteUploadHandoffRequest request)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var customerId = sessionResolver.TryResolveCustomerId(out var resolvedCustomerId)
            ? resolvedCustomerId
            : (Guid?)null;
        return Ok(store.ImportHandoff(request, customerId));
    }

    [HttpGet("uploads/{uploadId}/analysis-status")]
    public async Task<ActionResult<QuoteAnalysisStatusResponse>> GetAnalysisStatus(
        string uploadId,
        CancellationToken cancellationToken)
    {
        var upload = store.GetUpload(uploadId);
        if (upload is null)
            return NotFound();

        var response = upload.ToAnalysisStatus();
        var liveStatus = await statusService.GetStatusAsync(upload.StoragePath, cancellationToken);
        if (liveStatus is null)
            return Ok(response);

        response.Status = liveStatus.Status;
        if (liveStatus.VolumeCc.HasValue)
        {
            response.VolumeCc = liveStatus.VolumeCc.Value;
        }

        if (liveStatus.SurfaceAreaCm2.HasValue)
        {
            response.SurfaceAreaCm2 = liveStatus.SurfaceAreaCm2.Value;
        }

        response.ViewerGlbUrl = liveStatus.GlbUrl ?? response.ViewerGlbUrl;
        response.ViewerStoragePath = liveStatus.ViewerStoragePath ?? response.ViewerStoragePath;
        response.ViewerFileExtension = liveStatus.ViewerFileExtension ?? response.ViewerFileExtension;
        response.ThumbnailUrl = liveStatus.ThumbnailUrl ?? response.ThumbnailUrl;
        response.IsManifold = liveStatus.IsManifold;
        response.BodyCount = liveStatus.BodyCount;
        response.NonManifoldReason = liveStatus.NonManifoldReason;
        response.AnalysisErrorCode = liveStatus.AnalysisErrorCode;
        response.FdmReport = liveStatus.FdmReport;
        response.SlaReport = liveStatus.SlaReport;
        response.CncReport = liveStatus.CncReport;
        response.OverlayGlbUrls = liveStatus.OverlayGlbUrls;
        return Ok(response);
    }

    [HttpPost("estimate")]
    public async Task<ActionResult<QuoteEstimateResponse>> Estimate(
        [FromBody] QuoteEstimateRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var pricingEstimate = await TryEstimateWithPricingServiceAsync(request, cancellationToken);
        return Ok(pricingEstimate ?? store.Estimate(request));
    }

    private async Task<QuoteEstimateResponse?> TryEstimateWithPricingServiceAsync(
        QuoteEstimateRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Parts.Count == 0)
        {
            return null;
        }

        try
        {
            var customerId = sessionResolver.TryResolveCustomerId(out var resolvedCustomerId)
                ? resolvedCustomerId
                : Guid.Empty;
            var lines = new List<QuoteLineEstimateDto>(request.Parts.Count);

            foreach (var part in request.Parts)
            {
                var processId = await materialCatalog.ResolveProcessIdAsync(part.ProcessId, cancellationToken);
                var materialId = await materialCatalog.ResolveMaterialIdAsync(
                    part.ProcessId,
                    part.MaterialId,
                    cancellationToken);
                var toleranceAdditionalCostPercent = ResolveToleranceAdditionalCostPercent(part);
                var serviceResult = await pricingClient.CalculateAsync(
                    part,
                    customerId,
                    materialId,
                    processId,
                    request.LeadTimeCode,
                    toleranceAdditionalCostPercent,
                    cancellationToken);

                if (serviceResult is null)
                {
                    return null;
                }

                var adjustment = BuildQuoteEngineConfigurationAdjustment(part);
                var unitPrice = Math.Round(serviceResult.UnitPrice * adjustment.Multiplier + adjustment.Additive, 2);
                var lineTotal = Math.Round(unitPrice * part.Quantity, 2);
                lines.Add(new QuoteLineEstimateDto(
                    part.PartId,
                    part.FileName,
                    unitPrice,
                    lineTotal,
                    "THB",
                    adjustment.Notes));
            }

            var subtotal = lines.Sum(line => line.LineTotal);
            var discount = subtotal >= 25_000m ? Math.Round(subtotal * 0.05m, 2) : 0m;
            return new QuoteEstimateResponse(
                request.QuoteSessionId,
                subtotal,
                discount,
                subtotal - discount,
                "THB",
                true,
                lines);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PricingService estimate failed for quote session {QuoteSessionId}; using local fallback.", request.QuoteSessionId);
            return null;
        }
    }

    private decimal? ResolveToleranceAdditionalCostPercent(QuotePartDraftDto part)
    {
        var tolerance = store.ReferenceData.Tolerances.FirstOrDefault(option =>
            Matches(option.Id, part.ToleranceId) || Matches(option.Code, part.ToleranceCode));
        if (tolerance is null || tolerance.PriceMultiplier <= 1m)
        {
            return null;
        }

        return Math.Round((tolerance.PriceMultiplier - 1m) * 100m, 2);
    }

    private (decimal Multiplier, decimal Additive, string Notes) BuildQuoteEngineConfigurationAdjustment(
        QuotePartDraftDto part)
    {
        var multiplier = 1m;
        var additive = 0m;
        var notes = new List<string> { "PricingService estimate" };

        var finish = store.ReferenceData.Finishes.FirstOrDefault(option =>
            Matches(option.Id, part.FinishId) || Matches(option.Code, part.FinishCode));
        if (finish is not null && finish.PriceMultiplier != 1m)
        {
            multiplier *= finish.PriceMultiplier;
            notes.Add($"finish {finish.Name}");
        }

        var tolerance = store.ReferenceData.Tolerances.FirstOrDefault(option =>
            Matches(option.Id, part.ToleranceId) || Matches(option.Code, part.ToleranceCode));
        if (tolerance is not null)
        {
            notes.Add($"tolerance {tolerance.Code}");
        }

        var inspection = store.ReferenceData.InspectionLevels.FirstOrDefault(option =>
            Matches(option.Code, part.InspectionLevel));
        if (inspection is not null)
        {
            notes.Add($"inspection {inspection.Name}");
            if (inspection.PriceMultiplier != 1m)
            {
                multiplier *= inspection.PriceMultiplier;
            }
        }

        var roughness = store.ReferenceData.RoughnessOptions.FirstOrDefault(option =>
            Matches(option.Code, part.RoughnessCode));
        if (roughness is not null && roughness.PriceMultiplier != 1m)
        {
            multiplier *= roughness.PriceMultiplier;
            notes.Add($"roughness {roughness.Name}");
        }

        if (part.HasThreadedHoles || part.ThreadedHoleCount > 0)
        {
            var count = Math.Max(part.ThreadedHoleCount, 1);
            additive += count * 85m;
            notes.Add($"threaded holes {count}");
        }

        if (!string.IsNullOrWhiteSpace(part.InsertType) &&
            !part.InsertType.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            var count = Math.Max(part.InsertCount, 1);
            additive += count * 120m;
            notes.Add($"thread inserts {count}");
        }

        if (part.DrawingFiles.Count > 0)
        {
            notes.Add("drawing reviewed");
        }

        if (!string.IsNullOrWhiteSpace(part.PartNotes))
        {
            notes.Add("customer notes supplied");
        }

        return (multiplier, additive, string.Join("; ", notes) + ".");
    }

    [HttpPost("projects/draft")]
    public ActionResult<CreateDraftProjectResponse> CreateDraftProject([FromBody] CreateDraftProjectRequest request)
    {
        if (!sessionResolver.TryResolveCustomerId(out var customerId))
        {
            return Unauthorized(new ProblemDetails
            {
                Title = "Sign-in required.",
                Detail = "A formal customer project can only be created for a signed-in customer."
            });
        }

        return Ok(store.CreateDraftProject(customerId, request));
    }

    [HttpPost("projects/{projectId:guid}/duplicate")]
    public ActionResult<DuplicateDraftProjectResponse> DuplicateDraftProject(
        Guid projectId,
        [FromBody] DuplicateDraftProjectRequest request)
    {
        if (!sessionResolver.TryResolveCustomerId(out var customerId))
        {
            return Unauthorized(new ProblemDetails
            {
                Title = "Sign-in required.",
                Detail = "Project duplication is available only for signed-in customers."
            });
        }

        var duplicated = store.DuplicateDraftProject(customerId, projectId, request);
        return duplicated is null ? NotFound() : Ok(duplicated);
    }

    [HttpPost("quotes/formal")]
    public async Task<ActionResult<GenerateFormalQuoteResponse>> GenerateFormalQuote(
        [FromBody] GenerateFormalQuoteRequest request, CancellationToken cancellationToken)
    {
        if (!sessionResolver.TryResolveCustomerId(out var customerId))
        {
            return Unauthorized();
        }

        // Build line items: resolve material Guid via MaterialService, price via store estimate
        var lineItems = new List<QuotationLineItemCreate>();
        foreach (var part in request.Parts)
        {
            var materialGuid = await materialCatalog.ResolveMaterialIdAsync(
                part.ProcessId, part.MaterialId, cancellationToken);

            var unitPrice = EstimateUnitPrice(part);

            lineItems.Add(new QuotationLineItemCreate
            {
                MaterialServiceId = materialGuid,
                Quantity = part.Quantity,
                UnitOfMeasure = "pcs",
                UnitPrice = unitPrice,
                ManufacturingProcess = part.ProcessId.ToUpperInvariant(),
                Notes = BuildLineItemNotes(part)
            });
        }

        // QuotationService requires ≥1 line item; add placeholder when no parts submitted
        if (lineItems.Count == 0)
        {
            var fallbackMaterial = await materialCatalog.ResolveMaterialIdAsync("fdm", "pla-black", cancellationToken);
            lineItems.Add(new QuotationLineItemCreate
            {
                MaterialServiceId = fallbackMaterial,
                Quantity = 1,
                UnitOfMeasure = "pcs",
                UnitPrice = 0m,
                ManufacturingProcess = "FDM",
                Notes = "Customer self-service quote"
            });
        }

        var createRequest = new QuotationCreateRequest
        {
            CustomerId = customerId,
            BillingIdentityType = 1,
            ValidityPeriodStart = DateTime.UtcNow,
            ValidityPeriodEnd = DateTime.UtcNow.AddDays(30),
            LineItems = lineItems,
            GeneratedByDisplayName = "Customer Self-Service"
        };

        var result = await quotationClient.CreateAsync(createRequest, cancellationToken);
        if (result is null)
        {
            logger.LogError("QuotationService returned null for customerId {CustomerId}", customerId);
            return StatusCode(502, new ProblemDetails { Title = "Quotation service unavailable. Please try again." });
        }

        return Ok(new GenerateFormalQuoteResponse(result.Id, result.QuotationNumber, string.Empty, result.Status));
    }

    /// <summary>Returns an estimated unit price using the same rate table as the Estimate endpoint.</summary>
    private static decimal EstimateUnitPrice(QuotePartDraftDto part)
    {
        var baseRate = part.ProcessId.ToLowerInvariant() switch
        {
            "cnc" => 520m,
            "sla" => 180m,
            _ => 95m
        };
        var setup = part.ProcessId.Equals("cnc", StringComparison.OrdinalIgnoreCase) ? 850m : 120m;
        var multiplier = 1m;
        var additive = 0m;

        if (!string.IsNullOrWhiteSpace(part.FinishId) || !string.IsNullOrWhiteSpace(part.FinishCode))
        {
            multiplier *= 1.08m;
        }

        if (!string.IsNullOrWhiteSpace(part.ToleranceId) || !string.IsNullOrWhiteSpace(part.ToleranceCode))
        {
            multiplier *= 1.08m;
        }

        if (!string.IsNullOrWhiteSpace(part.InspectionLevel)
            && !part.InspectionLevel.Equals("STANDARD", StringComparison.OrdinalIgnoreCase))
        {
            multiplier *= 1.10m;
        }

        if (!string.IsNullOrWhiteSpace(part.RoughnessCode))
        {
            multiplier *= 1.06m;
        }

        if (part.HasThreadedHoles || part.ThreadedHoleCount > 0)
        {
            additive += Math.Max(part.ThreadedHoleCount, 1) * 85m;
        }

        if (!string.IsNullOrWhiteSpace(part.InsertType) && !part.InsertType.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            additive += Math.Max(part.InsertCount, 1) * 120m;
        }

        return Math.Round((setup + Math.Max(part.VolumeCc, 1m) * baseRate) * multiplier + additive, 2);
    }

    private static string BuildLineItemNotes(QuotePartDraftDto part)
    {
        var notes = new List<string> { part.FileName };
        if (!string.IsNullOrWhiteSpace(part.FinishCode)) notes.Add($"finish {part.FinishCode}");
        if (!string.IsNullOrWhiteSpace(part.ToleranceCode)) notes.Add($"tolerance {part.ToleranceCode}");
        if (!string.IsNullOrWhiteSpace(part.InspectionLevel)) notes.Add($"inspection {part.InspectionLevel}");
        if (!string.IsNullOrWhiteSpace(part.RoughnessCode)) notes.Add($"roughness {part.RoughnessCode}");
        if (part.ProcessOptionValues.Count > 0)
        {
            notes.Add("process options " + string.Join(", ", part.ProcessOptionValues
                .OrderBy(option => option.Key, StringComparer.OrdinalIgnoreCase)
                .Select(option => $"{option.Key}={option.Value}")));
        }

        if (part.HasThreadedHoles || part.ThreadedHoleCount > 0) notes.Add($"threaded holes {Math.Max(part.ThreadedHoleCount, 1)}");
        if (!string.IsNullOrWhiteSpace(part.InsertType) && !part.InsertType.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            notes.Add($"inserts {Math.Max(part.InsertCount, 1)}");
        }

        if (part.DrawingFiles.Count > 0) notes.Add("customer drawing supplied");
        if (!string.IsNullOrWhiteSpace(part.PartNotes)) notes.Add($"customer notes {part.PartNotes.Trim()}");
        if (part.BodyCount.HasValue) notes.Add($"body count {part.BodyCount.Value}");
        if (part.SelectedBodyIndex.HasValue) notes.Add($"selected body {part.SelectedBodyIndex.Value}");
        return string.Join("; ", notes);
    }

    private static bool Matches(string candidate, string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
            candidate.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    [HttpPost("payments")]
    public async Task<ActionResult<InitiatePaymentResponse>> InitiatePayment(
        [FromBody] InitiatePaymentRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        if (!sessionResolver.TryResolveCustomerId(out var customerId))
        {
            return Unauthorized();
        }

        var customerOrders = await orderClient.GetByCustomerAsync(customerId.ToString("D"), cancellationToken);
        if (!customerOrders.Any(order =>
            order.OrderId == request.OrderId &&
            string.Equals(order.OrderNumber, request.OrderNumber, StringComparison.OrdinalIgnoreCase)))
        {
            return NotFound();
        }

        var orderDetail = await orderClient.GetDetailAsync(request.OrderNumber, cancellationToken);
        var paymentAmount = request.Amount;
        var paymentCurrency = request.Currency;
        if (orderDetail?.QuotedAmount is decimal quotedAmount && quotedAmount > 0)
        {
            var quotedCurrency = string.IsNullOrWhiteSpace(orderDetail.QuoteCurrency)
                ? "THB"
                : orderDetail.QuoteCurrency;
            if (request.Amount != quotedAmount ||
                !string.Equals(request.Currency, quotedCurrency, StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new ProblemDetails
                {
                    Title = "Payment amount does not match the order quote.",
                    Detail = "Payment initiation must use the quoted order amount and currency.",
                    Status = StatusCodes.Status400BadRequest
                });
            }

            paymentAmount = quotedAmount;
            paymentCurrency = quotedCurrency;
        }

        // Build return/cancel URLs from the current request so the redirect lands back in the SPA.
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var returnUrl = $"{baseUrl}/payment/success?orderId={Uri.EscapeDataString(request.OrderNumber)}";
        var cancelUrl = $"{baseUrl}/payment/cancel?orderId={Uri.EscapeDataString(request.OrderNumber)}";
        var idempotencyKey = $"{customerId:D}:{request.OrderId:D}";

        // Advance order to Accepted (customer accepted the quoted price) so that when
        // PaymentService fires PaymentCompletedEvent, OrderService can apply Accepted → Paid.
        var accepted = await orderClient.AddStatusAsync(request.OrderNumber, "Accepted", cancellationToken);
        if (!accepted)
            logger.LogWarning(
                "Could not advance order {OrderNumber} to Accepted before payment initiation; PaymentCompletedEvent may fail.",
                request.OrderNumber);

        var result = await paymentClient.InitiateAsync(
            customerId.ToString("D"),
            request.OrderId.ToString("D"),
            request.OrderNumber,
            paymentAmount,
            paymentCurrency,
            returnUrl,
            cancelUrl,
            idempotencyKey,
            cancellationToken);

        if (result is null)
        {
            logger.LogError("PaymentService returned null for orderId {OrderId}", request.OrderId);
            return StatusCode(502, new ProblemDetails { Title = "Payment service unavailable. Please try again." });
        }

        return Ok(new InitiatePaymentResponse(result.TransactionId, result.PaymentUrl, result.Status));
    }

    [HttpPost("quotes/{quoteId:guid}/approve")]
    public async Task<ActionResult<GenerateFormalQuoteResponse>> ApproveQuote(Guid quoteId, CancellationToken cancellationToken)
    {
        if (!sessionResolver.TryResolveCustomerId(out var customerId))
        {
            return Unauthorized();
        }

        var quotation = await quotationClient.GetByIdAsync(quoteId, cancellationToken);
        if (quotation is null || quotation.CustomerId != customerId)
        {
            return NotFound();
        }

        return Ok(new GenerateFormalQuoteResponse(quotation.Id, quotation.QuotationNumber, string.Empty, "Approved"));
    }

    [HttpPost("orders")]
    public async Task<ActionResult<CreateManufacturingOrderResponse>> CreateOrder(
        [FromBody] CreateManufacturingOrderRequest request, CancellationToken cancellationToken)
    {
        if (!sessionResolver.TryResolveCustomerId(out var customerId))
        {
            return Unauthorized();
        }

        // Verify quote exists and belongs to the current customer
        var quotation = await quotationClient.GetByIdAsync(request.QuoteId, cancellationToken);
        if (quotation is null || quotation.CustomerId != customerId)
        {
            return NotFound();
        }

        var orderRequest = new OrderCreateRequest
        {
            CustomerId = customerId.ToString("D"),
            OrderedQuantity = 1,
            CustomerPoNumber = string.IsNullOrWhiteSpace(request.CustomerPoNumber) ? null : request.CustomerPoNumber,
            Requirements = BuildOrderRequirements(request)
        };
        orderRequest.SetProcessFromCode(request.Parts.FirstOrDefault()?.ProcessId ?? "fdm");

        var result = await orderClient.CreateAsync(orderRequest, cancellationToken);
        if (result is null)
        {
            logger.LogError("OrderService returned null for customerId {CustomerId} quoteId {QuoteId}", customerId, request.QuoteId);
            return StatusCode(502, new ProblemDetails { Title = "Order service unavailable. Please try again." });
        }

        // Self-service fast-track: advance order through internal review states so the customer
        // can proceed immediately to payment (Quoted → Accepted → PaymentService → Paid).
        // Failures are logged as warnings — the order still exists and staff can resolve manually.
        foreach (var status in new[] { "Reviewing", "Reviewed", "Quoted" })
        {
            var advanced = await orderClient.AddStatusAsync(result.OrderNumber, status, cancellationToken);
            if (!advanced)
                logger.LogWarning(
                    "Self-service fast-track: could not advance order {OrderNumber} to {Status}. Payment initiation may fail.",
                    result.OrderNumber, status);
        }

        return Ok(new CreateManufacturingOrderResponse(result.OrderId, result.OrderNumber, result.Status));
    }

    private bool CanAccessUpload(UploadState upload)
    {
        if (upload.IsTemporary)
        {
            return true;
        }

        return sessionResolver.TryResolveCustomerId(out var customerId) && upload.CustomerId == customerId;
    }

    private static string BuildOrderRequirements(CreateManufacturingOrderRequest request)
    {
        var requirements = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Notes))
        {
            requirements.Add(request.Notes.Trim());
        }

        if (request.Parts.Count > 0)
        {
            requirements.Add("Configured quote parts:");
            requirements.AddRange(request.Parts.Select(BuildConfiguredPartSummary));
        }

        return string.Join(Environment.NewLine, requirements);
    }

    private static string BuildConfiguredPartSummary(QuotePartDraftDto part)
    {
        var summary = new List<string>
        {
            part.FileName,
            $"process {part.ProcessId.ToUpperInvariant()}",
            $"material {part.MaterialId}",
            $"qty {part.Quantity}"
        };

        AddIfPresent(summary, "finish", part.FinishCode ?? part.FinishId);
        AddIfPresent(summary, "tolerance", part.ToleranceCode ?? part.ToleranceId);
        AddIfPresent(summary, "inspection", part.InspectionLevel);
        AddIfPresent(summary, "roughness", part.RoughnessCode);
        AddIfPresent(summary, "color", part.Color);

        if (part.ProcessOptionValues.Count > 0)
        {
            summary.Add("process options " + string.Join(", ", part.ProcessOptionValues
                .OrderBy(option => option.Key, StringComparer.OrdinalIgnoreCase)
                .Select(option => $"{option.Key}={option.Value}")));
        }

        if (part.HasThreadedHoles || part.ThreadedHoleCount > 0)
        {
            var threadSummary = $"threaded holes {Math.Max(part.ThreadedHoleCount, 1)}";
            if (!string.IsNullOrWhiteSpace(part.ThreadSpecification))
            {
                threadSummary += $" {part.ThreadSpecification.Trim()}";
            }

            summary.Add(threadSummary);
        }

        if (!string.IsNullOrWhiteSpace(part.InsertType) && !part.InsertType.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            summary.Add($"inserts {Math.Max(part.InsertCount, 1)} {part.InsertType.Trim()}");
        }

        foreach (var drawing in part.DrawingFiles)
        {
            summary.Add($"drawing {drawing.FileName}");
        }

        if (part.BodyCount.HasValue)
        {
            summary.Add($"body count {part.BodyCount.Value}");
        }

        if (part.SelectedBodyIndex.HasValue)
        {
            summary.Add($"selected body {part.SelectedBodyIndex.Value}");
        }

        if (part.DfmAcknowledged)
        {
            summary.Add("DFM acknowledged");
        }

        if (!part.IsManifold && !string.IsNullOrWhiteSpace(part.NonManifoldReason))
        {
            summary.Add($"DFM issue {part.NonManifoldReason.Trim()}");
        }

        AddIfPresent(summary, "notes", part.PartNotes);
        return "- " + string.Join("; ", summary);
    }

    private static void AddIfPresent(List<string> values, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values.Add($"{label} {value.Trim()}");
        }
    }

    private bool CanUsePrototypeUploadFallback =>
        environment.IsDevelopment() || environment.IsEnvironment("Testing");

    private static string CreatePrototypeUploadId(string uploadId) => $"{PrototypeUploadPrefix}{uploadId}";

    private static bool IsPrototypeUpload(UploadState upload) =>
        upload.DownstreamUploadId?.StartsWith(PrototypeUploadPrefix, StringComparison.Ordinal) == true;

    private static IReadOnlyDictionary<string, string>? BuildBrowserPrimaryUploadMetadata(string fileName)
    {
        if (!BrowserViewerSourceExtensions.Contains(Path.GetExtension(fileName)))
            return null;

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["geometry.executionPolicy"] = "browser_primary",
            ["geometry.browserRuntime"] = "required",
            ["geometry.serverGlbExport"] = "skip_for_browser_viewable"
        };
    }
}
