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
    IHostEnvironment environment,
    ILogger<QuoteController> logger) : ControllerBase
{
    private const string PrototypeUploadPrefix = "prototype-local:";

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
            var downstreamUploadId = await uploadClient.InitiateResumableUploadAsync(
                upload.FileName,
                upload.ContentType,
                upload.ExpectedSizeBytes,
                upload.StoragePath,
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
    public ActionResult<QuoteAnalysisStatusResponse> GetAnalysisStatus(string uploadId)
    {
        var upload = store.GetUpload(uploadId);
        return upload is null ? NotFound() : Ok(upload.ToAnalysisStatus());
    }

    [HttpPost("estimate")]
    public ActionResult<QuoteEstimateResponse> Estimate([FromBody] QuoteEstimateRequest request)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        return Ok(store.Estimate(request));
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
            request.Amount,
            request.Currency,
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
    public ActionResult<GenerateFormalQuoteResponse> ApproveQuote(Guid quoteId)
    {
        if (!sessionResolver.TryResolveCustomerId(out _))
        {
            return Unauthorized();
        }

        return Ok(new GenerateFormalQuoteResponse(quoteId, $"MQ-{DateTime.UtcNow:yyyyMMdd}-APPROVED", "/quote/v1/account/quotes/sample.pdf", "Approved"));
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
            Requirements = request.Notes
        };
        // Default to 3D Printing (FDM); actual process comes from the quotation line items
        orderRequest.SetProcessFromCode("fdm");

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

    private bool CanUsePrototypeUploadFallback =>
        environment.IsDevelopment() || environment.IsEnvironment("Testing");

    private static string CreatePrototypeUploadId(string uploadId) => $"{PrototypeUploadPrefix}{uploadId}";

    private static bool IsPrototypeUpload(UploadState upload) =>
        upload.DownstreamUploadId?.StartsWith(PrototypeUploadPrefix, StringComparison.Ordinal) == true;
}
