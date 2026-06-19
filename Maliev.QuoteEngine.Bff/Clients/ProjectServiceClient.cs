// Maliev.QuoteEngine.Bff/Clients/ProjectServiceClient.cs
using System.Net.Http.Json;
using System.Text.Json;
using Maliev.QuoteEngine.Shared.Quotes;

namespace Maliev.QuoteEngine.Bff.Clients;

/// <summary>
/// Calls ProjectService to persist customer Make Studio projects.
/// </summary>
public interface IProjectServiceClient
{
    /// <summary>Creates a customer project and persists its current configured parts.</summary>
    Task<ProjectServiceDraftProjectResult?> CreateDraftProjectAsync(
        Guid customerId,
        string customerName,
        CreateDraftProjectRequest request,
        Func<QuotePartDraftDto, CancellationToken, Task<Guid?>> resolveMaterialIdAsync,
        CancellationToken ct = default);
}

internal sealed class ProjectServiceClient(HttpClient http, ILogger<ProjectServiceClient> logger) : IProjectServiceClient
{
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ProjectServiceDraftProjectResult?> CreateDraftProjectAsync(
        Guid customerId,
        string customerName,
        CreateDraftProjectRequest request,
        Func<QuotePartDraftDto, CancellationToken, Task<Guid?>> resolveMaterialIdAsync,
        CancellationToken ct = default)
    {
        try
        {
            var createResponse = await http.PostAsJsonAsync("/project/v1/projects", new
            {
                customerId,
                customerName,
                title = string.IsNullOrWhiteSpace(request.Title) ? "Untitled quote" : request.Title.Trim(),
                description = request.Notes,
                currency = "THB"
            }, ct);

            if (!createResponse.IsSuccessStatusCode)
            {
                var body = await createResponse.Content.ReadAsStringAsync(ct);
                logger.LogWarning("ProjectService returned {Status} on create: {Body}", createResponse.StatusCode, body);
                return null;
            }

            var project = await createResponse.Content.ReadFromJsonAsync<ProjectServiceProjectResponse>(cancellationToken: ct);
            if (project is null || project.Id == Guid.Empty)
            {
                logger.LogWarning("ProjectService returned an empty create response for customer {CustomerId}.", customerId);
                return null;
            }

            foreach (var part in request.Parts)
            {
                var materialId = await resolveMaterialIdAsync(part, ct);
                using var partResponse = await http.PostAsJsonAsync(
                    $"/project/v1/projects/{project.Id:D}/parts",
                    BuildPartRequest(part, materialId),
                    ct);
                if (!partResponse.IsSuccessStatusCode)
                {
                    var body = await partResponse.Content.ReadAsStringAsync(ct);
                    logger.LogWarning(
                        "ProjectService returned {Status} on add part for project {ProjectId}: {Body}",
                        partResponse.StatusCode,
                        project.Id,
                        body);
                    return null;
                }
            }

            return new ProjectServiceDraftProjectResult(project.Id, project.ProjectNumber, project.Status);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ProjectService draft project create failed for customer {CustomerId}.", customerId);
            return null;
        }
    }

    private static object BuildPartRequest(QuotePartDraftDto part, Guid? materialId)
    {
        return new
        {
            fileName = part.FileName,
            fileId = part.FileId == Guid.Empty ? null : (Guid?)part.FileId,
            fileReference = FirstNonEmpty(part.StoragePath, part.ViewerStoragePath, part.UploadId),
            thumbnailSmallGcsPath = part.ThumbnailUrl,
            thumbnailLargeGcsPath = part.ThumbnailUrl,
            glbStoragePath = part.ViewerStoragePath,
            overlayPaths = BuildOverlayPaths(part),
            processType = ResolveProjectServiceProcessType(part.ProcessId),
            materialId,
            materialName = part.MaterialId,
            materialCode = part.MaterialId,
            quantity = Math.Max(1, part.Quantity),
            finishType = part.FinishCode ?? part.FinishId,
            color = part.Color,
            tolerance = part.ToleranceCode ?? part.ToleranceId,
            roughnessCode = part.RoughnessCode,
            dfmAcknowledged = part.DfmAcknowledged,
            hasDfmWarnings = HasDfmWarnings(part),
            hasThreadedHoles = part.HasThreadedHoles,
            threadedHoleSpec = part.ThreadSpecification,
            threadedHoleCount = Math.Max(0, part.ThreadedHoleCount),
            hasInserts = !string.IsNullOrWhiteSpace(part.InsertType) &&
                !part.InsertType.Equals("None", StringComparison.OrdinalIgnoreCase),
            insertType = part.InsertType,
            insertCount = Math.Max(0, part.InsertCount),
            inspectionLevel = part.InspectionLevel,
            drawingFiles = part.DrawingFiles.Select(ToProjectAttachment).ToArray(),
            supplementaryFiles = Array.Empty<object>(),
            processConfig = part.ProcessOptionValues,
            bodyCount = part.BodyCount,
            bodiesJson = part.BodyCount.HasValue ? JsonSerializer.Serialize(new
            {
                part.BodyCount,
                part.SelectedBodyIndex
            }, SnapshotJsonOptions) : null,
            selectedBodyIndex = part.SelectedBodyIndex,
            threadsInserts = BuildThreadsInserts(part),
            customNotes = part.PartNotes,
            volumeCm3 = part.VolumeCc,
            surfaceAreaCm2 = part.SurfaceAreaCm2,
            boundingBoxX = (decimal?)null,
            boundingBoxY = (decimal?)null,
            boundingBoxZ = (decimal?)null,
            isManifold = part.IsManifold
        };
    }

    private static Dictionary<string, string> BuildOverlayPaths(QuotePartDraftDto part)
    {
        var overlays = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var overlay = part.OverlayGlbUrls.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (!string.IsNullOrWhiteSpace(overlay))
        {
            overlays["dfm"] = overlay;
        }

        return overlays;
    }

    private static object ToProjectAttachment(QuotePartAttachmentDto attachment)
    {
        return new
        {
            fileName = attachment.FileName,
            storagePath = attachment.StoragePath,
            contentType = attachment.ContentType,
            sizeBytes = attachment.FileSizeBytes
        };
    }

    private static int ResolveProjectServiceProcessType(string? processCode)
    {
        if (string.IsNullOrWhiteSpace(processCode))
        {
            return 1;
        }

        if (processCode.Contains("cnc", StringComparison.OrdinalIgnoreCase))
        {
            return 10;
        }

        if (processCode.Contains("sla", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (processCode.Contains("sls", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        return 1;
    }

    private static bool HasDfmWarnings(QuotePartDraftDto part) =>
        part.Findings.Count > 0 ||
        part.FdmReport?.Issues.Count > 0 ||
        part.SlaReport?.Issues.Count > 0 ||
        part.CncReport?.Issues.Count > 0 ||
        (!part.IsManifold && !string.IsNullOrWhiteSpace(part.NonManifoldReason));

    private static string? BuildThreadsInserts(QuotePartDraftDto part)
    {
        var values = new List<string>();
        if (part.HasThreadedHoles || part.ThreadedHoleCount > 0)
        {
            values.Add($"Threads: {Math.Max(1, part.ThreadedHoleCount)} {part.ThreadSpecification}".Trim());
        }

        if (!string.IsNullOrWhiteSpace(part.InsertType) && !part.InsertType.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            values.Add($"Inserts: {Math.Max(1, part.InsertCount)} {part.InsertType}".Trim());
        }

        return values.Count == 0 ? null : string.Join("; ", values);
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private sealed class ProjectServiceProjectResponse
    {
        public Guid Id { get; set; }
        public string ProjectNumber { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }
}

/// <summary>Durable ProjectService identifiers for a created Make Studio draft project.</summary>
public sealed record ProjectServiceDraftProjectResult(Guid ProjectId, string ProjectNumber, string Status);
