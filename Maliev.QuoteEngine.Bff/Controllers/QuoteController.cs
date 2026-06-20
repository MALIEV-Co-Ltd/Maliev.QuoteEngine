using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Asp.Versioning;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Hubs;
using Maliev.QuoteEngine.Bff.Options;
using Maliev.QuoteEngine.Bff.Security;
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
    ICustomerServiceClient customerClient,
    IProjectServiceClient projectClient,
    IPaymentServiceClient paymentClient,
    IQePricingServiceClient pricingClient,
    QuoteUploadHandoffToken handoffToken,
    IHostEnvironment environment,
    ILogger<QuoteController> logger) : ControllerBase
{
    private const string PrototypeUploadPrefix = "prototype-local:";
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new(JsonSerializerDefaults.Web);

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

        if (!QuoteUploadConstraints.IsSupportedAttachmentFileName(request.FileName))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Unsupported quote attachment type.",
                Detail = $"Upload {QuoteUploadConstraints.SupportedAttachmentExtensionLabel} files."
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

        if (!handoffToken.TryRead(request.HandoffToken, out var verifiedRequest))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid upload handoff.",
                Detail = "The website upload handoff could not be verified. Please upload the files again.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var customerId = sessionResolver.TryResolveCustomerId(out var resolvedCustomerId)
            ? resolvedCustomerId
            : (Guid?)null;
        return Ok(store.ImportHandoff(verifiedRequest, customerId));
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
    public async Task<ActionResult<CreateDraftProjectResponse>> CreateDraftProject(
        [FromBody] CreateDraftProjectRequest request,
        CancellationToken cancellationToken)
    {
        if (!sessionResolver.TryResolveCustomerId(out var customerId))
        {
            return Unauthorized(new ProblemDetails
            {
                Title = "Sign-in required.",
                Detail = "A formal customer project can only be created for a signed-in customer."
            });
        }

        var profile = store.GetProfile(customerId);
        var project = await projectClient.CreateDraftProjectAsync(
            customerId,
            profile.DisplayName,
            request,
            ResolveProjectPartMaterialIdAsync,
            cancellationToken);
        if (project is null)
        {
            logger.LogError("ProjectService did not create a draft project for customer {CustomerId}.", customerId);
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = "Project service unavailable.",
                Detail = "The draft project could not be persisted for employee handoff.",
                Status = StatusCodes.Status502BadGateway
            });
        }

        var local = store.CreateDraftProject(customerId, request);
        return Ok(local with
        {
            ProjectServiceProjectId = project.ProjectId,
            ProjectServiceProjectNumber = project.ProjectNumber,
            Parts = request.Parts
        });
    }

    private async Task<Guid?> ResolveProjectPartMaterialIdAsync(
        QuotePartDraftDto part,
        CancellationToken cancellationToken) =>
        await materialCatalog.ResolveMaterialIdAsync(part.ProcessId, part.MaterialId, cancellationToken);

    [HttpGet("projects/nav")]
    public async Task<ActionResult<IReadOnlyList<CustomerProjectNavItemDto>>> GetProjectNavigation(CancellationToken cancellationToken)
    {
        if (!sessionResolver.TryResolveCustomerId(out var customerId))
        {
            return Unauthorized(new ProblemDetails
            {
                Title = "Sign-in required.",
                Detail = "Project navigation is available only for signed-in customers."
            });
        }

        var projects = await projectClient.GetProjectNavigationAsync(customerId, cancellationToken);
        return projects.Count > 0 || !CanUsePrototypeProjectFallback()
            ? Ok(projects)
            : Ok(store.GetProjectNavigation(customerId));
    }

    [HttpGet("projects/{projectId:guid}")]
    public async Task<ActionResult<CustomerProjectDetailResponse>> GetProjectDetail(Guid projectId, CancellationToken cancellationToken)
    {
        if (!sessionResolver.TryResolveCustomerId(out var customerId))
        {
            return Unauthorized(new ProblemDetails
            {
                Title = "Sign-in required.",
                Detail = "Project details are available only for signed-in customers."
            });
        }

        var project = await projectClient.GetProjectDetailAsync(customerId, projectId, cancellationToken);
        if (project is not null)
        {
            return Ok(project);
        }

        project = CanUsePrototypeProjectFallback()
            ? store.GetProjectDetail(customerId, projectId)
            : null;
        return project is null ? NotFound() : Ok(project);
    }

    [HttpPost("projects/{projectId:guid}/duplicate")]
    public async Task<ActionResult<DuplicateDraftProjectResponse>> DuplicateDraftProject(
        Guid projectId,
        [FromBody] DuplicateDraftProjectRequest request,
        CancellationToken cancellationToken)
    {
        if (!sessionResolver.TryResolveCustomerId(out var customerId))
        {
            return Unauthorized(new ProblemDetails
            {
                Title = "Sign-in required.",
                Detail = "Project duplication is available only for signed-in customers."
            });
        }

        var durableDuplicate = await projectClient.DuplicateDraftProjectAsync(
            customerId,
            store.GetProfile(customerId).DisplayName,
            projectId,
            request,
            ResolveProjectPartMaterialIdAsync,
            cancellationToken);
        if (durableDuplicate is not null)
        {
            return Ok(durableDuplicate);
        }

        var duplicated = CanUsePrototypeProjectFallback()
            ? store.DuplicateDraftProject(customerId, projectId, request)
            : null;
        return duplicated is null ? NotFound() : Ok(duplicated);
    }

    [HttpPost("projects/{projectId:guid}/pin")]
    public async Task<ActionResult<ProjectManagementResponse>> PinProject(Guid projectId, CancellationToken cancellationToken)
    {
        if (!sessionResolver.TryResolveCustomerId(out var customerId))
        {
            return Unauthorized(new ProblemDetails
            {
                Title = "Sign-in required.",
                Detail = "Project pinning is available only for signed-in customers."
            });
        }

        var pinned = await projectClient.SetProjectPinnedAsync(customerId, projectId, isPinned: true, cancellationToken)
            ?? (CanUsePrototypeProjectFallback() ? store.SetProjectPinned(customerId, projectId, isPinned: true) : null);
        return pinned is null ? NotFound() : Ok(pinned);
    }

    [HttpDelete("projects/{projectId:guid}/pin")]
    public async Task<ActionResult<ProjectManagementResponse>> UnpinProject(Guid projectId, CancellationToken cancellationToken)
    {
        if (!sessionResolver.TryResolveCustomerId(out var customerId))
        {
            return Unauthorized(new ProblemDetails
            {
                Title = "Sign-in required.",
                Detail = "Project unpinning is available only for signed-in customers."
            });
        }

        var unpinned = await projectClient.SetProjectPinnedAsync(customerId, projectId, isPinned: false, cancellationToken)
            ?? (CanUsePrototypeProjectFallback() ? store.SetProjectPinned(customerId, projectId, isPinned: false) : null);
        return unpinned is null ? NotFound() : Ok(unpinned);
    }

    [HttpPost("projects/{projectId:guid}/archive")]
    public async Task<ActionResult<ProjectManagementResponse>> ArchiveProject(Guid projectId, CancellationToken cancellationToken)
    {
        if (!sessionResolver.TryResolveCustomerId(out var customerId))
        {
            return Unauthorized(new ProblemDetails
            {
                Title = "Sign-in required.",
                Detail = "Project archiving is available only for signed-in customers."
            });
        }

        var archived = await projectClient.ArchiveProjectAsync(customerId, projectId, cancellationToken)
            ?? (CanUsePrototypeProjectFallback() ? store.SetProjectArchived(customerId, projectId, isArchived: true) : null);
        return archived is null ? NotFound() : Ok(archived);
    }

    [HttpPost("projects/{projectId:guid}/achieve")]
    public async Task<ActionResult<ProjectManagementResponse>> AchieveProject(Guid projectId, CancellationToken cancellationToken)
    {
        if (!sessionResolver.TryResolveCustomerId(out var customerId))
        {
            return Unauthorized(new ProblemDetails
            {
                Title = "Sign-in required.",
                Detail = "Project completion is available only for signed-in customers."
            });
        }

        var achieved = await projectClient.ArchiveProjectAsync(customerId, projectId, cancellationToken)
            ?? (CanUsePrototypeProjectFallback() ? store.SetProjectAchieved(customerId, projectId) : null);
        return achieved is null ? NotFound() : Ok(achieved);
    }

    private bool CanUsePrototypeProjectFallback()
    {
        return environment.IsDevelopment() || environment.IsEnvironment("Testing");
    }

    [HttpPost("quotes/formal")]
    public async Task<ActionResult<GenerateFormalQuoteResponse>> GenerateFormalQuote(
        [FromBody] GenerateFormalQuoteRequest request, CancellationToken cancellationToken)
    {
        if (!sessionResolver.TryResolveCustomerId(out var customerId))
        {
            return Unauthorized();
        }

        var dfmReviewError = ValidateDfmReviewAcknowledgement(request.Parts);
        if (dfmReviewError is not null)
        {
            return dfmReviewError;
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
            SourceProjectId = request.ProjectId,
            ProjectSnapshotJson = BuildFormalQuoteProjectSnapshotJson(customerId, request),
            ChangeSummary = string.IsNullOrWhiteSpace(request.Notes)
                ? "Customer self-service formal quote"
                : request.Notes.Trim(),
            GeneratedByDisplayName = "Customer Self-Service"
        };
        createRequest.ProjectSnapshotHash = ComputeSha256Hex(createRequest.ProjectSnapshotJson);

        var result = await quotationClient.CreateOrReviseProjectQuoteAsync(createRequest, cancellationToken);
        if (result is null)
        {
            logger.LogError("QuotationService returned null for customerId {CustomerId}", customerId);
            return StatusCode(502, new ProblemDetails { Title = "Quotation service unavailable. Please try again." });
        }

        return Ok(new GenerateFormalQuoteResponse(result.Id, result.QuotationNumber, result.PdfArtifactUrl ?? string.Empty, result.Status)
        {
            QuoteVersionId = result.QuoteVersionId,
            QuoteVersionNumber = result.QuoteVersionNumber,
            PdfArtifactStoragePath = result.PdfArtifactStoragePath
        });
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

    private static string BuildFormalQuoteProjectSnapshotJson(
        Guid customerId,
        GenerateFormalQuoteRequest request)
    {
        return JsonSerializer.Serialize(new
        {
            customerId,
            sourceProjectId = request.ProjectId,
            quoteSessionId = request.QuoteSessionId,
            notes = request.Notes,
            parts = request.Parts.Select(part => new
            {
                part.PartId,
                part.FileId,
                part.UploadId,
                part.FileName,
                part.ProcessId,
                part.MaterialId,
                part.FinishId,
                part.FinishCode,
                part.Color,
                part.Quantity,
                part.VolumeCc,
                part.SurfaceAreaCm2,
                part.ToleranceId,
                part.ToleranceCode,
                part.InspectionLevel,
                part.RoughnessCode,
                part.ProcessOptionValues,
                part.HasThreadedHoles,
                part.ThreadSpecification,
                part.ThreadedHoleCount,
                part.InsertType,
                part.InsertCount,
                part.BodyCount,
                part.SelectedBodyIndex,
                part.DfmAcknowledged,
                part.Findings,
                part.StoragePath,
                part.ViewerStoragePath,
                part.ViewerFileExtension,
                part.DrawingFiles
            })
        }, SnapshotJsonOptions);
    }

    private static string ComputeSha256Hex(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

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

        if (!request.AcceptedTerms)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Terms acceptance is required before checkout.",
                Detail = "Accept the checkout terms and consent requirements before starting payment.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (request.CheckoutAttemptId == Guid.Empty)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Checkout attempt id is required.",
                Detail = "Refresh the checkout and try again.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var addressValidation = await ValidateCheckoutAddressesAsync(customerId, request, cancellationToken);
        if (addressValidation.Error is not null)
        {
            return addressValidation.Error;
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

        var billingAddressId = request.BillingAddressId.GetValueOrDefault();
        var shippingAddressId = request.ShippingAddressId.GetValueOrDefault();
        var deliverySnapshotUpdated = await orderClient.UpdateDeliverySnapshotAsync(
            new OrderDeliverySnapshotRequest(
                request.OrderNumber,
                billingAddressId,
                shippingAddressId,
                addressValidation.ShippingAddress.ShippingAddressLine1,
                addressValidation.ShippingAddress.ShippingAddressLine2,
                addressValidation.ShippingAddress.ShippingCity,
                addressValidation.ShippingAddress.ShippingProvince,
                addressValidation.ShippingAddress.ShippingPostalCode,
                addressValidation.ShippingAddress.ShippingCountry,
                request.BillingCompanyName,
                request.BillingVatNumber,
                addressValidation.ShippingAddress.DeliveryContactName,
                addressValidation.ShippingAddress.DeliveryContactPhone,
                addressValidation.ShippingAddress.DeliveryContactEmail),
            cancellationToken);

        if (!deliverySnapshotUpdated)
        {
            logger.LogWarning(
                "Could not persist delivery snapshot for order {OrderNumber}; payment initiation blocked.",
                request.OrderNumber);
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = "Order delivery details could not be saved.",
                Detail = "Checkout cannot start until the selected delivery address is saved on the order.",
                Status = StatusCodes.Status502BadGateway
            });
        }

        // Build return/cancel URLs from the current request so the redirect lands back in the SPA.
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var returnUrl = $"{baseUrl}/payment/success?orderNumber={Uri.EscapeDataString(request.OrderNumber)}";
        var cancelUrl = $"{baseUrl}/payment/cancel?orderNumber={Uri.EscapeDataString(request.OrderNumber)}";
        var idempotencyKey = BuildPaymentIdempotencyKey(customerId, request.OrderId, request.CheckoutAttemptId);

        // Advance order to Accepted (customer accepted the quoted price) so that when
        // PaymentService fires PaymentCompletedEvent, OrderService can apply Accepted → Paid.
        var accepted = await orderClient.AddStatusAsync(request.OrderNumber, "Accepted", cancellationToken);
        if (!accepted)
        {
            logger.LogWarning(
                "Could not advance order {OrderNumber} to Accepted before payment initiation; checkout blocked.",
                request.OrderNumber);
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = "Order could not be accepted for checkout.",
                Detail = "Checkout cannot start until the order acceptance state is saved.",
                Status = StatusCodes.Status502BadGateway
            });
        }

        var result = await paymentClient.InitiateAsync(
            customerId.ToString("D"),
            request.OrderId.ToString("D"),
            request.OrderNumber,
            paymentAmount,
            paymentCurrency,
            returnUrl,
            cancelUrl,
            idempotencyKey,
            request.BillingAddressId,
            request.ShippingAddressId,
            request.BillingCompanyName,
            request.BillingVatNumber,
            addressValidation.ShippingAddress.DeliveryContactName,
            addressValidation.ShippingAddress.DeliveryContactPhone,
            addressValidation.ShippingAddress.DeliveryContactEmail,
            request.AcceptedTerms,
            cancellationToken);

        if (result is null)
        {
            logger.LogError("PaymentService returned null for orderId {OrderId}", request.OrderId);
            return StatusCode(502, new ProblemDetails { Title = "Payment service unavailable. Please try again." });
        }

        return Ok(new InitiatePaymentResponse(result.TransactionId, result.PaymentUrl, result.Status));
    }

    private async Task<CheckoutAddressValidationResult> ValidateCheckoutAddressesAsync(
        Guid customerId,
        InitiatePaymentRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.BillingAddressId.HasValue || !request.ShippingAddressId.HasValue)
        {
            return new CheckoutAddressValidationResult(BadRequest(new ProblemDetails
            {
                Title = "Billing and shipping addresses are required before checkout.",
                Detail = "Select a billing address and a shipping address before starting payment.",
                Status = StatusCodes.Status400BadRequest
            }), null!);
        }

        using var response = await customerClient.GetCustomerAddressesAsync(customerId, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new CheckoutAddressValidationResult(StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
            {
                Title = "Customer addresses are temporarily unavailable.",
                Detail = "Checkout cannot start until billing and shipping addresses can be verified.",
                Status = StatusCodes.Status503ServiceUnavailable
            }), null!);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var addresses = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().Select(ReadCheckoutAddress).ToList()
            : [];

        var billingAddress = addresses.FirstOrDefault(address => address.Id == request.BillingAddressId.Value);
        var shippingAddress = addresses.FirstOrDefault(address => address.Id == request.ShippingAddressId.Value);
        if (billingAddress is null || shippingAddress is null)
        {
            return new CheckoutAddressValidationResult(BadRequest(new ProblemDetails
            {
                Title = "Selected checkout address was not found.",
                Detail = "Billing and shipping addresses must belong to the signed-in customer.",
                Status = StatusCodes.Status400BadRequest
            }), null!);
        }

        if (!string.Equals(billingAddress.Type, "Billing", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(shippingAddress.Type, "Shipping", StringComparison.OrdinalIgnoreCase))
        {
            return new CheckoutAddressValidationResult(BadRequest(new ProblemDetails
            {
                Title = "Selected checkout address roles are invalid.",
                Detail = "Use a billing address for billing and a shipping address for delivery before payment.",
                Status = StatusCodes.Status400BadRequest
            }), null!);
        }

        if (string.IsNullOrWhiteSpace(shippingAddress.RecipientPhone))
        {
            return new CheckoutAddressValidationResult(BadRequest(new ProblemDetails
            {
                Title = "Shipping phone is required before checkout.",
                Detail = "Select or update a shipping address with a recipient phone number before payment.",
                Status = StatusCodes.Status400BadRequest
            }), null!);
        }

        return new CheckoutAddressValidationResult(null, shippingAddress);
    }

    private static CheckoutAddressSnapshot ReadCheckoutAddress(JsonElement root)
    {
        return new CheckoutAddressSnapshot(
            Id: GetGuid(root, "id", "Id") ?? Guid.Empty,
            Type: GetString(root, "type", "Type") ?? string.Empty,
            RecipientPhone: GetString(root, "recipientPhone", "RecipientPhone"),
            ShippingAddressLine1: GetString(root, "addressLine1", "AddressLine1"),
            ShippingAddressLine2: BuildAddressLine2(root),
            ShippingCity: GetString(root, "city", "City"),
            ShippingProvince: GetString(root, "stateProvince", "StateProvince"),
            ShippingPostalCode: GetString(root, "postalCode", "PostalCode"),
            ShippingCountry: GetString(root, "countryCode", "CountryCode", "country", "Country") ??
                GetGuid(root, "countryId", "CountryId")?.ToString("D"),
            DeliveryContactName: GetString(root, "recipientName", "RecipientName"),
            DeliveryContactPhone: GetString(root, "recipientPhone", "RecipientPhone"),
            DeliveryContactEmail: GetString(root, "recipientEmail", "RecipientEmail"));
    }

    private static string? BuildAddressLine2(JsonElement root)
    {
        var parts = new[]
        {
            GetString(root, "addressLine2", "AddressLine2"),
            GetString(root, "addressLine3", "AddressLine3"),
            GetString(root, "district", "District")
        }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToArray();

        return parts.Length == 0 ? null : string.Join(", ", parts);
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

    private sealed record CheckoutAddressValidationResult(ActionResult? Error, CheckoutAddressSnapshot ShippingAddress);

    private sealed record CheckoutAddressSnapshot(
        Guid Id,
        string Type,
        string? RecipientPhone,
        string? ShippingAddressLine1,
        string? ShippingAddressLine2,
        string? ShippingCity,
        string? ShippingProvince,
        string? ShippingPostalCode,
        string? ShippingCountry,
        string? DeliveryContactName,
        string? DeliveryContactPhone,
        string? DeliveryContactEmail);

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

        return Ok(new GenerateFormalQuoteResponse(quotation.Id, quotation.QuotationNumber, quotation.PdfArtifactUrl ?? string.Empty, "Approved")
        {
            QuoteVersionId = quotation.QuoteVersionId,
            QuoteVersionNumber = quotation.QuoteVersionNumber,
            PdfArtifactStoragePath = quotation.PdfArtifactStoragePath
        });
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

        var acceptabilityError = ValidateQuotationVersionAcceptability(
            quotation,
            request.QuoteVersionId,
            request.QuoteVersionNumber);
        if (acceptabilityError is not null)
        {
            return acceptabilityError;
        }

        var dfmReviewError = ValidateDfmReviewAcknowledgement(request.Parts);
        if (dfmReviewError is not null)
        {
            return dfmReviewError;
        }

        var productionItems = new List<OrderProductionItemRequest>(request.Parts.Count);
        foreach (var part in request.Parts)
        {
            var materialGuid = await materialCatalog.ResolveMaterialIdAsync(
                part.ProcessId,
                part.MaterialId,
                cancellationToken);
            productionItems.Add(BuildProductionItem(request.ProjectServiceProjectId ?? request.QuoteId, part, materialGuid));
        }

        var orderRequest = new OrderCreateRequest
        {
            CustomerId = customerId.ToString("D"),
            OrderedQuantity = request.Parts.Count == 0 ? 1 : request.Parts.Sum(part => Math.Max(1, part.Quantity)),
            CustomerPoNumber = string.IsNullOrWhiteSpace(request.CustomerPoNumber) ? null : request.CustomerPoNumber,
            Requirements = BuildOrderRequirements(request),
            QuotedAmount = CalculateOrderQuotedTotal(request.Parts),
            QuoteCurrency = "THB",
            QuoteId = quotation.Id,
            QuoteNumber = quotation.QuotationNumber,
            QuoteVersionId = quotation.QuoteVersionId,
            QuoteVersionNumber = quotation.QuoteVersionNumber,
            ProductionItems = productionItems
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

    private static ActionResult? ValidateQuotationVersionAcceptability(
        QuotationCreatedResult quotation,
        Guid? requestedVersionId,
        int? requestedVersionNumber)
    {
        if (quotation.Status.Equals("Expired", StringComparison.OrdinalIgnoreCase) ||
            quotation.Status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase))
        {
            return new BadRequestObjectResult(new ProblemDetails
            {
                Title = "Quote version cannot be accepted.",
                Detail = $"Quotation {quotation.QuotationNumber} is {quotation.Status}. Request a revised quote before creating an order.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (quotation.ValidityPeriodEnd is { } validityEnd &&
            validityEnd.Date < DateTime.UtcNow.Date)
        {
            return new BadRequestObjectResult(new ProblemDetails
            {
                Title = "Quote version cannot be accepted.",
                Detail = $"Quotation {quotation.QuotationNumber} expired on {validityEnd:yyyy-MM-dd}. Request a revised quote before creating an order.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var requestedSpecificVersion = requestedVersionId.HasValue || requestedVersionNumber.HasValue;
        if (!requestedSpecificVersion)
        {
            return null;
        }

        if (!quotation.QuoteVersionId.HasValue || !quotation.QuoteVersionNumber.HasValue)
        {
            return new BadRequestObjectResult(new ProblemDetails
            {
                Title = "Quote version cannot be verified.",
                Detail = $"Quotation {quotation.QuotationNumber} did not return current version metadata. Refresh the quote before creating an order.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var versionIdMismatch = requestedVersionId.HasValue && requestedVersionId.Value != quotation.QuoteVersionId.Value;
        var versionNumberMismatch = requestedVersionNumber.HasValue && requestedVersionNumber.Value != quotation.QuoteVersionNumber.Value;
        var currentNumberMismatch = requestedVersionNumber.HasValue &&
            quotation.CurrentVersionNumber.HasValue &&
            requestedVersionNumber.Value != quotation.CurrentVersionNumber.Value;
        if (versionIdMismatch || versionNumberMismatch || currentNumberMismatch)
        {
            return new BadRequestObjectResult(new ProblemDetails
            {
                Title = "Quote version has been superseded.",
                Detail = $"Quotation {quotation.QuotationNumber} has a newer version. Refresh the quote and accept the current version before creating an order.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        return null;
    }

    private static ActionResult? ValidateDfmReviewAcknowledgement(IReadOnlyList<QuotePartDraftDto> parts)
    {
        var blockedPart = parts.FirstOrDefault(RequiresDfmAcknowledgement);
        if (blockedPart is null)
        {
            return null;
        }

        var fileName = string.IsNullOrWhiteSpace(blockedPart.FileName) ? "the selected part" : blockedPart.FileName.Trim();
        return new BadRequestObjectResult(new ProblemDetails
        {
            Title = "DFM review is required before checkout.",
            Detail = $"Review and acknowledge DFM issues for {fileName} before creating an order.",
            Status = StatusCodes.Status400BadRequest
        });
    }

    private static bool RequiresDfmAcknowledgement(QuotePartDraftDto part) =>
        !part.DfmAcknowledged && HasDfmIssues(part);

    private static bool HasDfmIssues(QuotePartDraftDto part) =>
        part.Findings.Count > 0
        || part.FdmReport?.Issues.Count > 0
        || part.SlaReport?.Issues.Count > 0
        || part.CncReport?.Issues.Count > 0
        || (!part.IsManifold && !string.IsNullOrWhiteSpace(part.NonManifoldReason));

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

    private static decimal CalculateOrderQuotedTotal(IReadOnlyList<QuotePartDraftDto> parts)
    {
        if (parts.Count == 0)
        {
            return 0m;
        }

        var subtotal = parts.Sum(part => EstimateUnitPrice(part) * Math.Max(1, part.Quantity));
        var discount = subtotal >= 25_000m ? Math.Round(subtotal * 0.05m, 2) : 0m;
        return Math.Round(subtotal - discount, 2);
    }

    private static OrderProductionItemRequest BuildProductionItem(Guid sourceProjectId, QuotePartDraftDto part, Guid materialGuid)
    {
        return new OrderProductionItemRequest
        {
            SourceProjectId = sourceProjectId,
            SourceProjectPartId = part.PartId,
            MaterialId = materialGuid,
            MaterialSnapshotJson = JsonSerializer.Serialize(new
            {
                sourceMaterialId = part.MaterialId,
                resolvedMaterialId = materialGuid,
                finishId = part.FinishId,
                finishCode = part.FinishCode,
                color = part.Color
            }, SnapshotJsonOptions),
            ConfigurationSnapshotJson = JsonSerializer.Serialize(new
            {
                part.PartId,
                part.FileId,
                part.UploadId,
                part.FileName,
                part.ProcessId,
                part.MaterialId,
                part.FinishId,
                part.FinishCode,
                part.Color,
                part.Quantity,
                part.VolumeCc,
                part.SurfaceAreaCm2,
                part.ToleranceId,
                part.ToleranceCode,
                part.InspectionLevel,
                part.RoughnessCode,
                part.ProcessOptionValues,
                part.HasThreadedHoles,
                part.ThreadSpecification,
                part.ThreadedHoleCount,
                part.InsertType,
                part.InsertCount,
                part.BodyCount,
                part.SelectedBodyIndex,
                part.DfmAcknowledged,
                part.PartNotes,
                part.DrawingFiles,
                part.StoragePath,
                part.ViewerStoragePath,
                part.ViewerFileExtension
            }, SnapshotJsonOptions),
            Technology = part.ProcessId.ToUpperInvariant(),
            VolumeCm3 = part.VolumeCc,
            Quantity = Math.Max(1, part.Quantity),
            EstimatedPrintTimeMinutes = 0
        };
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

    private static string BuildPaymentIdempotencyKey(Guid customerId, Guid orderId, Guid checkoutAttemptId)
    {
        var input = $"{customerId:D}:{orderId:D}:{checkoutAttemptId:D}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
        return $"qe:{hash}";
    }

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
