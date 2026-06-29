// Maliev.QuoteEngine.Bff/Clients/ProjectServiceClient.cs
using System.Net.Http.Json;
using System.Text.Json;
using Maliev.QuoteEngine.Shared.Agent;
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

    /// <summary>Duplicates a customer-scoped ProjectService project into a new draft project.</summary>
    Task<DuplicateDraftProjectResponse?> DuplicateDraftProjectAsync(
        Guid customerId,
        string customerName,
        Guid projectId,
        DuplicateDraftProjectRequest request,
        Func<QuotePartDraftDto, CancellationToken, Task<Guid?>> resolveMaterialIdAsync,
        CancellationToken ct = default);

    /// <summary>Returns customer-scoped project navigation items from ProjectService.</summary>
    Task<IReadOnlyList<CustomerProjectNavItemDto>> GetProjectNavigationAsync(Guid customerId, CancellationToken ct = default);

    /// <summary>Returns a customer-scoped project detail from ProjectService.</summary>
    Task<CustomerProjectDetailResponse?> GetProjectDetailAsync(Guid customerId, Guid projectId, CancellationToken ct = default);

    /// <summary>Sets whether a customer-scoped ProjectService project is pinned.</summary>
    Task<ProjectManagementResponse?> SetProjectPinnedAsync(Guid customerId, Guid projectId, bool isPinned, CancellationToken ct = default);

    /// <summary>Archives a customer-scoped ProjectService project.</summary>
    Task<ProjectManagementResponse?> ArchiveProjectAsync(Guid customerId, Guid projectId, CancellationToken ct = default);

    /// <summary>Routes a customer-scoped ProjectService project to employee review.</summary>
    Task<ProjectManagementResponse?> RequestProjectReviewAsync(Guid customerId, Guid projectId, string note, CancellationToken ct = default);

    /// <summary>Searches customer-scoped projects from ProjectService for QuoteAgent recall.</summary>
    Task<IReadOnlyList<QuoteAgentSearchResultDto>> SearchProjectResultsAsync(
        Guid customerId,
        string? query,
        int limit,
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

                var createdPart = await partResponse.Content.ReadFromJsonAsync<ProjectServicePartResponse>(cancellationToken: ct);
                if (createdPart is not null && createdPart.Id != Guid.Empty)
                {
                    part.PartId = createdPart.Id;
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

    public async Task<DuplicateDraftProjectResponse?> DuplicateDraftProjectAsync(
        Guid customerId,
        string customerName,
        Guid projectId,
        DuplicateDraftProjectRequest request,
        Func<QuotePartDraftDto, CancellationToken, Task<Guid?>> resolveMaterialIdAsync,
        CancellationToken ct = default)
    {
        var source = await GetProjectDetailAsync(customerId, projectId, ct);
        if (source is null)
        {
            return null;
        }

        try
        {
            var title = string.IsNullOrWhiteSpace(request.Title)
                ? $"Copy of {source.Title}".Trim()
                : request.Title.Trim();
            var createResponse = await http.PostAsJsonAsync("/project/v1/projects", new
            {
                customerId,
                customerName,
                title,
                description = $"Duplicated from {source.ProjectNumber}.",
                currency = "THB",
                sourceProjectId = source.ProjectId,
                sourceProjectNumber = source.ProjectNumber
            }, ct);

            if (!createResponse.IsSuccessStatusCode)
            {
                var body = await createResponse.Content.ReadAsStringAsync(ct);
                logger.LogWarning("ProjectService returned {Status} on duplicate create: {Body}", createResponse.StatusCode, body);
                return null;
            }

            var duplicated = await createResponse.Content.ReadFromJsonAsync<ProjectServiceProjectResponse>(cancellationToken: ct);
            if (duplicated is null || duplicated.Id == Guid.Empty)
            {
                logger.LogWarning("ProjectService returned an empty duplicate response for project {ProjectId}.", projectId);
                return null;
            }

            foreach (var part in source.Parts)
            {
                var materialId = await resolveMaterialIdAsync(part, ct);
                using var partResponse = await http.PostAsJsonAsync(
                    $"/project/v1/projects/{duplicated.Id:D}/parts",
                    BuildPartRequest(part, materialId),
                    ct);
                if (!partResponse.IsSuccessStatusCode)
                {
                    var body = await partResponse.Content.ReadAsStringAsync(ct);
                    logger.LogWarning(
                        "ProjectService returned {Status} on duplicate add part for project {ProjectId}: {Body}",
                        partResponse.StatusCode,
                        duplicated.Id,
                        body);
                    return null;
                }
            }

            var detail = await GetProjectDetailAsync(customerId, duplicated.Id, ct);
            return new DuplicateDraftProjectResponse(
                duplicated.Id,
                duplicated.ProjectNumber,
                duplicated.Status,
                duplicated.Title,
                detail?.Parts ?? source.Parts);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ProjectService duplicate failed for project {ProjectId}.", projectId);
            return null;
        }
    }

    public async Task<IReadOnlyList<CustomerProjectNavItemDto>> GetProjectNavigationAsync(
        Guid customerId,
        CancellationToken ct = default)
    {
        try
        {
            var paged = await http.GetFromJsonAsync<ProjectServicePagedProjectsResponse>(
                $"/project/v1/projects?customerId={customerId:D}&pageSize=100",
                ct);
            return paged?.Data?
                .Where(project => !IsArchivedStatus(project))
                .Select(ToNavigationItem)
                .OrderByDescending(project => project.UpdatedAt)
                .ToArray() ?? [];
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ProjectService navigation lookup failed for customer {CustomerId}.", customerId);
            return [];
        }
    }

    public async Task<CustomerProjectDetailResponse?> GetProjectDetailAsync(
        Guid customerId,
        Guid projectId,
        CancellationToken ct = default)
    {
        try
        {
            var project = await http.GetFromJsonAsync<ProjectServiceProjectResponse>(
                $"/project/v1/projects/{projectId:D}",
                ct);
            if (project is null || project.CustomerId != customerId)
            {
                return null;
            }

            return ToCustomerProjectDetail(project);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ProjectService detail lookup failed for project {ProjectId}.", projectId);
            return null;
        }
    }

    public async Task<ProjectManagementResponse?> SetProjectPinnedAsync(
        Guid customerId,
        Guid projectId,
        bool isPinned,
        CancellationToken ct = default)
    {
        try
        {
            using var response = isPinned
                ? await http.PostAsync($"/project/v1/projects/{projectId:D}/pin", null, ct)
                : await http.DeleteAsync($"/project/v1/projects/{projectId:D}/pin", ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var project = await response.Content.ReadFromJsonAsync<ProjectServiceProjectResponse>(cancellationToken: ct);
            return project is null || project.CustomerId != customerId ? null : ToManagementResponse(project);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ProjectService pin update failed for project {ProjectId}.", projectId);
            return null;
        }
    }

    public async Task<ProjectManagementResponse?> ArchiveProjectAsync(
        Guid customerId,
        Guid projectId,
        CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsync($"/project/v1/projects/{projectId:D}/archive", null, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var project = await response.Content.ReadFromJsonAsync<ProjectServiceProjectResponse>(cancellationToken: ct);
            return project is null || project.CustomerId != customerId ? null : ToManagementResponse(project);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ProjectService archive failed for project {ProjectId}.", projectId);
            return null;
        }
    }

    public async Task<ProjectManagementResponse?> RequestProjectReviewAsync(
        Guid customerId,
        Guid projectId,
        string note,
        CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(
                $"/project/v1/projects/{projectId:D}/request-review",
                new { note },
                ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var project = await response.Content.ReadFromJsonAsync<ProjectServiceProjectResponse>(cancellationToken: ct);
            return project is null || project.CustomerId != customerId ? null : ToManagementResponse(project);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ProjectService review request failed for project {ProjectId}.", projectId);
            return null;
        }
    }

    public async Task<IReadOnlyList<QuoteAgentSearchResultDto>> SearchProjectResultsAsync(
        Guid customerId,
        string? query,
        int limit,
        CancellationToken ct = default)
    {
        try
        {
            var normalizedLimit = Math.Clamp(limit, 1, 50);
            var path = $"/project/v1/projects?customerId={customerId:D}&pageSize={normalizedLimit}";
            if (!string.IsNullOrWhiteSpace(query))
            {
                path += $"&query={Uri.EscapeDataString(query.Trim())}";
            }

            var paged = await http.GetFromJsonAsync<ProjectServicePagedProjectsResponse>(path, ct);
            return paged?.Data?
                .Select(ToSearchResult)
                .ToArray() ?? [];
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ProjectService search lookup failed for customer {CustomerId}.", customerId);
            return [];
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
            boundingBoxX = part.BoundingBoxMm?.X,
            boundingBoxY = part.BoundingBoxMm?.Y,
            boundingBoxZ = part.BoundingBoxMm?.Z,
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

    private static CustomerProjectNavItemDto ToNavigationItem(ProjectServiceProjectResponse project)
    {
        var updatedAt = project.UpdatedAt == default ? project.CreatedAt : project.UpdatedAt;
        return new CustomerProjectNavItemDto(
            project.Id,
            project.ProjectNumber,
            project.Title,
            project.Status,
            project.IsPinned,
            IsArchivedStatus(project),
            new DateTimeOffset(updatedAt, TimeSpan.Zero));
    }

    private static CustomerProjectDetailResponse ToCustomerProjectDetail(ProjectServiceProjectResponse project)
    {
        var updatedAt = project.UpdatedAt == default ? project.CreatedAt : project.UpdatedAt;
        return new CustomerProjectDetailResponse(
            project.Id,
            project.ProjectNumber,
            project.Status,
            project.Title,
            project.IsPinned,
            IsArchivedStatus(project),
            new DateTimeOffset(updatedAt, TimeSpan.Zero),
            project.Parts.Select(ToQuotePartDraft).ToArray());
    }

    private static ProjectManagementResponse ToManagementResponse(ProjectServiceProjectResponse project) =>
        new(
            project.Id,
            project.ProjectNumber,
            project.Status,
            project.Title,
            project.IsPinned,
            IsArchivedStatus(project));

    private static QuoteAgentSearchResultDto ToSearchResult(ProjectServiceProjectResponse project)
    {
        return new QuoteAgentSearchResultDto
        {
            ResourceType = "project",
            ResourceId = project.Id.ToString("D"),
            Title = project.Title,
            Detail = $"{project.ProjectNumber} · {project.Status} · {project.Parts.Count} part(s)",
            ActionHint = "resume_project",
            Url = $"/quotes?projectId={project.Id:D}",
            Metadata = new(StringComparer.OrdinalIgnoreCase)
            {
                ["projectNumber"] = project.ProjectNumber,
                ["status"] = project.Status,
                ["isPinned"] = project.IsPinned.ToString().ToLowerInvariant(),
                ["isArchived"] = IsArchivedStatus(project).ToString().ToLowerInvariant(),
                ["source"] = "project_service"
            }
        };
    }

    private static QuotePartDraftDto ToQuotePartDraft(ProjectServicePartResponse part)
    {
        return new QuotePartDraftDto
        {
            PartId = part.Id == Guid.Empty ? Guid.NewGuid() : part.Id,
            FileId = part.FileId ?? Guid.Empty,
            UploadId = part.FileReference ?? part.FileName,
            FileName = part.FileName,
            ProcessId = part.ProcessType,
            MaterialId = part.MaterialCode ?? part.MaterialName ?? part.MaterialId?.ToString("D") ?? string.Empty,
            FinishCode = part.FinishType,
            ToleranceCode = part.Tolerance,
            InspectionLevel = part.InspectionLevel,
            RoughnessCode = part.RoughnessCode,
            Color = part.Color,
            ProcessOptionValues = new Dictionary<string, string>(part.ProcessConfig, StringComparer.OrdinalIgnoreCase),
            HasThreadedHoles = part.HasThreadedHoles,
            ThreadSpecification = part.ThreadedHoleSpec,
            ThreadedHoleCount = part.ThreadedHoleCount,
            InsertType = part.InsertType,
            InsertCount = part.InsertCount,
            Quantity = Math.Max(1, part.Quantity),
            VolumeCc = Math.Max(0.01m, part.VolumeCm3 ?? 0.01m),
            SurfaceAreaCm2 = Math.Max(0m, part.SurfaceAreaCm2 ?? 0m),
            BoundingBoxMm = TryCreateBoundingBox(part.BoundingBoxX, part.BoundingBoxY, part.BoundingBoxZ),
            StoragePath = part.FileReference,
            Status = part.Status,
            ViewerStoragePath = part.GlbStoragePath,
            ThumbnailUrl = part.ThumbnailUrl,
            IsManifold = part.IsManifold ?? true,
            DfmAcknowledged = part.DfmAcknowledged,
            PartNotes = part.CustomNotes,
            BodyCount = part.BodyCount,
            SelectedBodyIndex = part.SelectedBodyIndex,
            DrawingFiles = part.DrawingFiles.Select(ToQuoteAttachment).ToList()
        };
    }

    private static QuotePartAttachmentDto ToQuoteAttachment(ProjectServiceAttachmentResponse attachment) =>
        new(
            attachment.FileName,
            attachment.StoragePath ?? string.Empty,
            attachment.ContentType ?? "application/octet-stream",
            attachment.SizeBytes ?? 0,
            "Drawing");

    private static QuotePartBoundingBoxDto? TryCreateBoundingBox(decimal? x, decimal? y, decimal? z)
    {
        return x is > 0 && y is > 0 && z is > 0
            ? new QuotePartBoundingBoxDto(x.Value, y.Value, z.Value)
            : null;
    }

    private static bool IsArchivedStatus(ProjectServiceProjectResponse project) =>
        project.IsArchived ||
        project.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase) ||
        project.Status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase);

    private sealed class ProjectServiceProjectResponse
    {
        public Guid Id { get; set; }
        public string ProjectNumber { get; set; } = string.Empty;
        public Guid CustomerId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public bool IsPinned { get; set; }
        public bool IsArchived { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public List<ProjectServicePartResponse> Parts { get; set; } = [];
    }

    private sealed class ProjectServicePagedProjectsResponse
    {
        public List<ProjectServiceProjectResponse> Data { get; set; } = [];
    }

    private sealed class ProjectServicePartResponse
    {
        public Guid Id { get; set; }
        public string FileName { get; set; } = string.Empty;
        public Guid? FileId { get; set; }
        public string? FileReference { get; set; }
        public string? ThumbnailUrl { get; set; }
        public string? GlbStoragePath { get; set; }
        public string ProcessType { get; set; } = string.Empty;
        public Guid? MaterialId { get; set; }
        public string? MaterialName { get; set; }
        public string? MaterialCode { get; set; }
        public int Quantity { get; set; } = 1;
        public string? FinishType { get; set; }
        public string? Color { get; set; }
        public string? Tolerance { get; set; }
        public string? RoughnessCode { get; set; }
        public bool DfmAcknowledged { get; set; }
        public bool HasThreadedHoles { get; set; }
        public string? ThreadedHoleSpec { get; set; }
        public int ThreadedHoleCount { get; set; }
        public string? InsertType { get; set; }
        public int InsertCount { get; set; }
        public string? InspectionLevel { get; set; }
        public Dictionary<string, string> ProcessConfig { get; set; } = [];
        public int? BodyCount { get; set; }
        public int? SelectedBodyIndex { get; set; }
        public string? CustomNotes { get; set; }
        public decimal? VolumeCm3 { get; set; }
        public decimal? SurfaceAreaCm2 { get; set; }
        public decimal? BoundingBoxX { get; set; }
        public decimal? BoundingBoxY { get; set; }
        public decimal? BoundingBoxZ { get; set; }
        public bool? IsManifold { get; set; }
        public string Status { get; set; } = string.Empty;
        public List<ProjectServiceAttachmentResponse> DrawingFiles { get; set; } = [];
    }

    private sealed class ProjectServiceAttachmentResponse
    {
        public string FileName { get; set; } = string.Empty;
        public string? StoragePath { get; set; }
        public long? SizeBytes { get; set; }
        public string? ContentType { get; set; }
    }
}

/// <summary>Durable ProjectService identifiers for a created Make Studio draft project.</summary>
public sealed record ProjectServiceDraftProjectResult(Guid ProjectId, string ProjectNumber, string Status);
