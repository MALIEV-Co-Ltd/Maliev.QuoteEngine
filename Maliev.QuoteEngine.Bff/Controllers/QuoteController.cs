using Asp.Versioning;
using Maliev.QuoteEngine.Bff.Hubs;
using Maliev.QuoteEngine.Bff.Services;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Maliev.QuoteEngine.Bff.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("quote/v{version:apiVersion}")]
public sealed class QuoteController(
    QuoteEnginePrototypeStore store,
    CustomerSessionResolver sessionResolver,
    IHubContext<QuoteNotificationsHub> hubContext) : ControllerBase
{
    [HttpGet("reference-data")]
    public ActionResult<QuoteReferenceDataResponse> GetReferenceData()
    {
        return Ok(store.ReferenceData);
    }

    [HttpGet("demo/project")]
    public ActionResult<QuoteEngineDemoProjectResponse> GetDemoProject()
    {
        return Ok(store.DemoProject);
    }

    [HttpPost("uploads/resumable")]
    public ActionResult<InitiateQuoteUploadResponse> InitiateUpload([FromBody] InitiateQuoteUploadRequest request)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        sessionResolver.TryResolveCustomerId(out var customerId);
        var upload = store.InitiateUpload(request, customerId);
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
        if (upload is null)
        {
            return NotFound();
        }

        if (!CanAccessUpload(upload))
        {
            return Forbid();
        }

        var received = 0L;
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await Request.Body.ReadAsync(buffer, cancellationToken)) > 0)
        {
            received += read;
        }

        store.MarkUploaded(uploadId, received);
        return NoContent();
    }

    [HttpPost("uploads/resumable/{uploadId}/complete")]
    public async Task<ActionResult<CompleteQuoteUploadResponse>> CompleteUpload(string uploadId, CancellationToken cancellationToken)
    {
        var existingUpload = store.GetUpload(uploadId);
        if (existingUpload is null)
        {
            return NotFound();
        }

        if (!CanAccessUpload(existingUpload))
        {
            return Forbid();
        }

        var upload = store.MarkAnalyzed(uploadId);
        await hubContext.Clients
            .Group(QuoteNotificationsHub.FileGroup(upload.StoragePath))
            .SendAsync("FileAnalysisCompleted", upload.ToAnalysisStatus(), cancellationToken);

        return Ok(new CompleteQuoteUploadResponse(upload.UploadId, upload.FileId, upload.FileName, upload.StoragePath, upload.Status));
    }

    [HttpPost("uploads/handoff")]
    public ActionResult<QuoteUploadHandoffResponse> ImportHandoff([FromBody] QuoteUploadHandoffRequest request)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        sessionResolver.TryResolveCustomerId(out var customerId);
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
        if (!sessionResolver.TryResolveCustomerId(out _))
        {
            return Unauthorized(new ProblemDetails
            {
                Title = "Sign-in required.",
                Detail = "A formal customer project can only be created for a signed-in customer."
            });
        }

        _ = request;
        return Ok(store.CreateDraftProject());
    }

    [HttpPost("quotes/formal")]
    public ActionResult<GenerateFormalQuoteResponse> GenerateFormalQuote([FromBody] GenerateFormalQuoteRequest request)
    {
        if (!sessionResolver.TryResolveCustomerId(out var customerId))
        {
            return Unauthorized();
        }

        _ = request;
        return Ok(store.GenerateQuote(customerId));
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
    public ActionResult<CreateManufacturingOrderResponse> CreateOrder([FromBody] CreateManufacturingOrderRequest request)
    {
        if (!sessionResolver.TryResolveCustomerId(out var customerId))
        {
            return Unauthorized();
        }

        try
        {
            return Ok(store.CreateOrder(customerId, request.QuoteId));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    private bool CanAccessUpload(UploadState upload)
    {
        if (upload.IsTemporary)
        {
            return true;
        }

        return sessionResolver.TryResolveCustomerId(out var customerId) && upload.CustomerId == customerId;
    }
}
