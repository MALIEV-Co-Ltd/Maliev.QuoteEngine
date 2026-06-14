using System.Diagnostics.Metrics;
using MassTransit;
using Maliev.MessagingContracts.Contracts.Geometry;
using Maliev.MessagingContracts.Contracts.Payments;
using Maliev.MessagingContracts.Contracts.Shared;
using Maliev.QuoteEngine.Bff;
using Maliev.QuoteEngine.Client.Components;
using Maliev.QuoteEngine.Client.Components.QuoteEngine;
using Maliev.QuoteEngine.Client.Models;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Consumers;
using Maliev.QuoteEngine.Bff.Hubs;
using Maliev.QuoteEngine.Bff.Services;
using Maliev.QuoteEngine.Shared.Localization;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Maliev.QuoteEngine.Tests;

public sealed class QuoteEngineSourceTests
{
    [Fact]
    public void SupportedCultures_normalizes_english_and_thai()
    {
        Assert.Equal(SupportedCultures.DefaultCulture, SupportedCultures.Normalize(null));
        Assert.Equal(SupportedCultures.DefaultCulture, SupportedCultures.Normalize("en"));
        Assert.Equal(SupportedCultures.ThaiCulture, SupportedCultures.Normalize("th"));
        Assert.Equal(SupportedCultures.ThaiCulture, SupportedCultures.Normalize("th-TH"));
    }

    [Fact]
    public void QuoteEngine_customer_shell_has_multilingual_preference_contract()
    {
        var layout = ReadRepoFile("Maliev.QuoteEngine.Client", "Layout", "MainLayout.razor");
        var preferences = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "Preferences.razor");
        var service = ReadRepoFile("Maliev.QuoteEngine.Client", "Services", "PreferenceService.cs");
        var loader = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-engine-loader.js");

        Assert.Contains("SupportedCultures.DefaultCulture", layout, StringComparison.Ordinal);
        Assert.Contains("SupportedCultures.ThaiCulture", layout, StringComparison.Ordinal);
        Assert.Contains("class=\"quote-language-toggle\"", layout, StringComparison.Ordinal);
        Assert.Contains("Text(\"Orders\", \"คำสั่งซื้อ\")", layout, StringComparison.Ordinal);
        Assert.Contains("Platform language", preferences, StringComparison.Ordinal);

        Assert.Contains("resolveCulture", service, StringComparison.Ordinal);
        Assert.Contains("quoteEnginePreferences.setCulture", service, StringComparison.Ordinal);
        Assert.Contains("document.documentElement", loader, StringComparison.Ordinal);
        Assert.Contains("root.lang", loader, StringComparison.Ordinal);
        Assert.Contains("maliev.quote.culture", loader, StringComparison.Ordinal);
        Assert.Contains("maliev.culture", loader, StringComparison.Ordinal);
        Assert.Contains("setCookie(\"maliev.culture\"", loader, StringComparison.Ordinal);
    }

    [Fact]
    public void QeDfmDtos_round_trip_through_json()
    {
        var report = new QeFdmDfmReport(
            ThinWallCount: 2,
            OverhangFaceCount: 5,
            OverhangAreaCm2: 3.14m,
            SupportRequired: true,
            SmallDetailCount: 0,
            Issues: [new QeDfmIssueItem("warning", "THIN_WALL", "Wall is thinner than 1.2mm")]);

        var json = JsonSerializer.Serialize(report);
        var deserialized = JsonSerializer.Deserialize<QeFdmDfmReport>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized.ThinWallCount);
        Assert.Single(deserialized.Issues);
        Assert.Equal("THIN_WALL", deserialized.Issues[0].Code);
        Assert.Equal(5, deserialized.OverhangFaceCount);
        Assert.Equal(3.14m, deserialized.OverhangAreaCm2);
        Assert.True(deserialized.SupportRequired);
        Assert.Equal(0, deserialized.SmallDetailCount);
        Assert.Equal("warning", deserialized.Issues[0].Severity);
        Assert.Equal("Wall is thinner than 1.2mm", deserialized.Issues[0].Message);
    }

    [Fact]
    public void QuotePartViewModel_process_selection_clears_stale_ProjectNew_configuration()
    {
        var part = new QuotePartViewModel
        {
            ProcessId = "fdm",
            MaterialId = "pla-black",
            FinishId = "fdm-matte",
            FinishCode = "MATTE",
            ToleranceId = "fdm-standard",
            ToleranceCode = "FDM_STANDARD",
            RoughnessCode = "RA_1_6",
            Color = "black",
            LocalDfmRuntimeUnavailable = true,
            LocalDfmRuntimeUnavailableReason = "process_code_missing",
            LocalDfmRuntimeRunningProcessId = "fdm",
            LocalDfmRuntimeStartedAtUtc = DateTimeOffset.UtcNow,
            UnitPriceTHB = 120m
        };
        part.ProcessOptionValues["paint_color"] = "RAL 9005";

        var reference = BuildConfigurationReferenceData();

        part.ApplyProjectNewProcessSelection("cnc", reference);

        Assert.Equal("cnc", part.ProcessId);
        Assert.Equal("al6061", part.MaterialId);
        Assert.Equal("cnc-as-machined", part.FinishId);
        Assert.Equal("AS_MACHINED", part.FinishCode);
        Assert.Equal("iso-2768-m", part.ToleranceId);
        Assert.Equal("ISO2768_M", part.ToleranceCode);
        Assert.Equal("RA_3_2", part.RoughnessCode);
        Assert.Equal("natural", part.Color);
        Assert.Empty(part.ProcessOptionValues);
        Assert.False(part.LocalDfmRuntimeUnavailable);
        Assert.Null(part.LocalDfmRuntimeUnavailableReason);
        Assert.Null(part.LocalDfmRuntimeRunningProcessId);
        Assert.Null(part.LocalDfmRuntimeStartedAtUtc);
        Assert.Null(part.UnitPriceTHB);
    }

    [Fact]
    public void QeGlbReadyPayload_round_trip_through_json()
    {
        var payload = new QeGlbReadyPayload(
            StoragePath: "quotes/temp/sess/uid/part.step",
            GlbUrl: "https://cdn.example.com/part.glb?sig=abc",
            ThumbnailUrl: null,
            BodyCount: 1,
            IsManifold: true,
            Failed: false,
            ErrorCode: null,
            ViewerStoragePath: "processed/u/part.glb",
            ViewerFileExtension: ".glb");

        var json = JsonSerializer.Serialize(payload);
        var deserialized = JsonSerializer.Deserialize<QeGlbReadyPayload>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("quotes/temp/sess/uid/part.step", deserialized.StoragePath);
        Assert.False(deserialized.Failed);
        Assert.Equal("https://cdn.example.com/part.glb?sig=abc", deserialized.GlbUrl);
        Assert.Null(deserialized.ThumbnailUrl);
        Assert.Equal(1, deserialized.BodyCount);
        Assert.True(deserialized.IsManifold);
        Assert.Null(deserialized.ErrorCode);
        Assert.Equal("processed/u/part.glb", deserialized.ViewerStoragePath);
        Assert.Equal(".glb", deserialized.ViewerFileExtension);
    }

    [Fact]
    public void QeDfmAnalysisReadyPayload_round_trip_covers_partial_and_full_paths()
    {
        // Partial: only FDM report, SLA and CNC null, empty overlay list
        var partial = new QeDfmAnalysisReadyPayload(
            StoragePath: "quotes/temp/s/u/part.step",
            IsManifold: true,
            NonManifoldReason: null,
            FdmReport: new QeFdmDfmReport(1, 2, 1.5m, true, 0, [new QeDfmIssueItem("warning", "THIN_WALL", "Too thin")]),
            SlaReport: null,
            CncReport: null,
            OverlayGlbUrls: [],
            AnalysisErrorCode: null);

        var json = JsonSerializer.Serialize(partial);
        var d = JsonSerializer.Deserialize<QeDfmAnalysisReadyPayload>(json);

        Assert.NotNull(d);
        Assert.Equal("quotes/temp/s/u/part.step", d.StoragePath);
        Assert.True(d.IsManifold);
        Assert.NotNull(d.FdmReport);
        Assert.Equal(1, d.FdmReport.ThinWallCount);
        Assert.Equal(1.5m, d.FdmReport.OverhangAreaCm2);
        Assert.True(d.FdmReport.SupportRequired);
        Assert.Single(d.FdmReport.Issues);
        Assert.Equal("THIN_WALL", d.FdmReport.Issues[0].Code);
        Assert.Null(d.SlaReport);
        Assert.Null(d.CncReport);
        Assert.Empty(d.OverlayGlbUrls);

        // Full: all three reports, overlay URLs, non-null error context
        var full = new QeDfmAnalysisReadyPayload(
            StoragePath: "quotes/temp/s/u/housing.step",
            IsManifold: false,
            NonManifoldReason: "Open boundary edges detected",
            FdmReport: new QeFdmDfmReport(0, 0, 0m, false, 0, []),
            SlaReport: new QeSlaDfmReport(1, true, false, false, []),
            CncReport: new QeCncDfmReport(3, true, false, 0, false, false, false, []),
            OverlayGlbUrls: ["https://cdn.example.com/overlay1.glb", "https://cdn.example.com/overlay2.glb"],
            AnalysisErrorCode: null);

        var json2 = JsonSerializer.Serialize(full);
        var d2 = JsonSerializer.Deserialize<QeDfmAnalysisReadyPayload>(json2);

        Assert.NotNull(d2);
        Assert.False(d2.IsManifold);
        Assert.Equal("Open boundary edges detected", d2.NonManifoldReason);
        Assert.NotNull(d2.FdmReport);
        Assert.NotNull(d2.SlaReport);
        Assert.True(d2.SlaReport.ResinTrappingRisk);
        Assert.NotNull(d2.CncReport);
        Assert.Equal(3, d2.CncReport.SharpCornerCount);
        Assert.True(d2.CncReport.HasUndercuts);
        Assert.Equal(2, d2.OverlayGlbUrls.Count);
        Assert.Equal("https://cdn.example.com/overlay1.glb", d2.OverlayGlbUrls[0]);
    }

    [Fact]
    public void QeLocalDfmMapper_accepts_matching_browser_result_and_populates_current_report()
    {
        var part = new QuotePartViewModel
        {
            ProcessId = "fdm",
            Status = "GlbReady"
        };
        var result = new LocalGeometryRuntimeResult
        {
            ProcessCode = "fdm",
            Authority = "local_primary",
            ExecutionMode = "primary_interactive",
            IsAuthoritative = false,
            Metrics = new LocalGeometryRuntimeMetrics
            {
                VolumeMm3 = 12500,
                SurfaceAreaMm2 = 4500,
                IsManifold = false,
                NonManifoldEdgeCount = 8,
            },
            Issues =
            [
                new LocalGeometryRuntimeIssue
                {
                    Category = "thin_wall",
                    Severity = "warning",
                    Description = "Thin feature risk."
                },
                new LocalGeometryRuntimeIssue
                {
                    Category = "overhang",
                    Severity = "warning",
                    Description = "Support risk.",
                    Value = 1.25,
                    FaceIndices = [1, 2, 3]
                }
            ]
        };

        Assert.True(QeLocalDfmMapper.TryApply(part, result));

        Assert.Equal("DfmAnalysisReady", part.Status);
        Assert.NotNull(part.FdmDfmReport);
        Assert.Equal(1, part.FdmDfmReport.ThinWallCount);
        Assert.Equal(3, part.FdmDfmReport.OverhangFaceCount);
        Assert.Equal(1.25m, part.FdmDfmReport.OverhangAreaCm2);
        Assert.True(part.FdmDfmReport.SupportRequired);
        Assert.Equal(12.5m, part.VolumeCc);
        Assert.Equal(45m, part.SurfaceAreaCm2);
        Assert.False(part.IsManifold);
        Assert.Equal("Browser local DFM found 8 non-manifold edge(s).", part.NonManifoldReason);
        Assert.True(QeLocalDfmMapper.HasCurrentProcessReport(part));
    }

    [Fact]
    public void QeLocalDfmMapper_rejects_stale_process_results()
    {
        var part = new QuotePartViewModel
        {
            ProcessId = "cnc",
            Status = "GlbReady"
        };
        var result = new LocalGeometryRuntimeResult
        {
            ProcessCode = "fdm",
            Authority = "local_primary",
            ExecutionMode = "primary_interactive",
            IsAuthoritative = false
        };

        Assert.False(QeLocalDfmMapper.TryApply(part, result));
        Assert.Null(part.CncDfmReport);
        Assert.False(QeLocalDfmMapper.HasCurrentProcessReport(part));
    }

    [Fact]
    public void QeLocalDfmMapper_ApplyAnalysisStatus_preserves_browser_local_dfm_when_server_status_has_no_report()
    {
        var part = new QuotePartViewModel
        {
            ProcessId = "cnc",
            Status = "DfmAnalysisReady",
            VolumeCc = 27.122m,
            SurfaceAreaCm2 = 84.5m,
            GlbUrl = "blob:https://quote.local/viewer",
            ViewerStoragePath = "quotes/temp/session/part.stl",
            ViewerFileExtension = ".stl",
            IsManifold = false,
            BodyCount = 2,
            NonManifoldReason = "Browser local DFM found 8 non-manifold edge(s).",
            CncDfmReport = new QeCncDfmReport(
                SharpCornerCount: 1,
                HasUndercuts: false,
                HasDrillHoles: true,
                DrillHoleCount: 2,
                RequiresEdm: false,
                RequiresGrinding: false,
                IsTurnable: true,
                Issues:
                [
                    new QeDfmIssueItem("warning", "LOCAL_PRIMARY", "Browser local CNC warning.")
                ])
        };
        var status = new QuoteAnalysisStatusResponse
        {
            Status = "GlbReady",
            ViewerGlbUrl = "https://cdn.example.com/generated.glb",
            ViewerStoragePath = "processed/generated.glb",
            ViewerFileExtension = ".glb",
            IsManifold = true,
            BodyCount = 1
        };

        QeLocalDfmMapper.ApplyAnalysisStatus(part, status);

        Assert.Equal("DfmAnalysisReady", part.Status);
        Assert.NotNull(part.CncDfmReport);
        Assert.Equal(1, part.CncDfmReport.SharpCornerCount);
        Assert.Equal("LOCAL_PRIMARY", part.CncDfmReport.Issues[0].Code);
        Assert.Equal("https://cdn.example.com/generated.glb", part.GlbUrl);
        Assert.Equal("processed/generated.glb", part.ViewerStoragePath);
        Assert.Equal(".glb", part.ViewerFileExtension);
        Assert.Equal(27.122m, part.VolumeCc);
        Assert.Equal(84.5m, part.SurfaceAreaCm2);
        Assert.False(part.IsManifold);
        Assert.Equal(2, part.BodyCount);
        Assert.Equal("Browser local DFM found 8 non-manifold edge(s).", part.NonManifoldReason);
        Assert.True(QeLocalDfmMapper.HasCurrentProcessReport(part));
    }

    [Theory]
    [InlineData("cnc", "CNC_MILL", "cnc")]
    [InlineData("CNC_MILL", "cnc", "cnc")]
    [InlineData("sla", "SLA_DLP", "sla")]
    [InlineData("SLA_DLP", "sla", "sla")]
    [InlineData("fdm", "FDM", "fdm")]
    public void QeLocalDfmMapper_accepts_browser_result_process_aliases(
        string selectedProcessId,
        string browserProcessCode,
        string expectedReport)
    {
        var part = new QuotePartViewModel
        {
            ProcessId = selectedProcessId,
            Status = "GlbReady"
        };
        var result = new LocalGeometryRuntimeResult
        {
            ProcessCode = browserProcessCode,
            Authority = "local_primary",
            ExecutionMode = "primary_interactive",
            IsAuthoritative = false,
            Issues =
            [
                new LocalGeometryRuntimeIssue
                {
                    Category = "thin_wall",
                    Severity = "warning",
                    Description = "Local warning."
                }
            ]
        };

        Assert.True(QeLocalDfmMapper.TryApply(part, result));

        Assert.Equal("DfmAnalysisReady", part.Status);
        Assert.Equal(expectedReport == "fdm", part.FdmDfmReport is not null);
        Assert.Equal(expectedReport == "sla", part.SlaDfmReport is not null);
        Assert.Equal(expectedReport == "cnc", part.CncDfmReport is not null);
        Assert.True(QeLocalDfmMapper.HasCurrentProcessReport(part));
    }

    [Fact]
    public void Quote_part_draft_customer_configuration_round_trips_through_json()
    {
        var draft = new QuotePartDraftDto
        {
            PartId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            FileId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            UploadId = "upload-projectnew-parity",
            FileName = "customer-fixture.step",
            ProcessId = "cnc",
            MaterialId = "al6061",
            FinishId = "cnc-bead-blast-clear",
            FinishCode = "BEAD_BLAST_CLEAR",
            ToleranceId = "iso-2768-m",
            ToleranceCode = "ISO2768_M",
            InspectionLevel = "Standard",
            RoughnessCode = "RA_1_6",
            Color = "Natural",
            ProcessOptionValues =
            {
                ["paint_color_hex"] = "#111111",
                ["paint_color_reference"] = "RAL 9005"
            },
            HasThreadedHoles = true,
            ThreadSpecification = "M3x0.5",
            ThreadedHoleCount = 4,
            InsertType = "HeatSet",
            InsertCount = 2,
            BodyCount = 2,
            SelectedBodyIndex = 1,
            DrawingFiles =
            [
                new QuotePartAttachmentDto(
                    FileName: "customer-fixture-drawing.pdf",
                    StoragePath: "customers/c/quotes/q/drawing.pdf",
                    ContentType: "application/pdf",
                    FileSizeBytes: 123_456,
                    Kind: "Drawing")
            ],
            ViewerSettings = new QuotePartViewerSettingsDto(
                CameraPreset: "iso",
                EdgesEnabled: true,
                GridEnabled: false,
                DfmOverlayEnabled: true)
        };

        var json = JsonSerializer.Serialize(draft, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var deserialized = JsonSerializer.Deserialize<QuotePartDraftDto>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(deserialized);
        Assert.Equal("cnc-bead-blast-clear", deserialized.FinishId);
        Assert.Equal("BEAD_BLAST_CLEAR", deserialized.FinishCode);
        Assert.Equal("iso-2768-m", deserialized.ToleranceId);
        Assert.Equal("ISO2768_M", deserialized.ToleranceCode);
        Assert.Equal("Standard", deserialized.InspectionLevel);
        Assert.Equal("RA_1_6", deserialized.RoughnessCode);
        Assert.Equal("Natural", deserialized.Color);
        Assert.Equal("#111111", deserialized.ProcessOptionValues["paint_color_hex"]);
        Assert.Equal("RAL 9005", deserialized.ProcessOptionValues["paint_color_reference"]);
        Assert.True(deserialized.HasThreadedHoles);
        Assert.Equal("M3x0.5", deserialized.ThreadSpecification);
        Assert.Equal(4, deserialized.ThreadedHoleCount);
        Assert.Equal("HeatSet", deserialized.InsertType);
        Assert.Equal(2, deserialized.InsertCount);
        Assert.Equal(2, deserialized.BodyCount);
        Assert.Equal(1, deserialized.SelectedBodyIndex);
        Assert.Single(deserialized.DrawingFiles);
        Assert.Equal("Drawing", deserialized.DrawingFiles[0].Kind);
        Assert.Equal("iso", deserialized.ViewerSettings.CameraPreset);
        Assert.Equal("realistic", deserialized.ViewerSettings.RenderMode);
        Assert.Equal("orthographic", deserialized.ViewerSettings.CameraProjection);
        Assert.True(deserialized.ViewerSettings.EdgesEnabled);
        Assert.False(deserialized.ViewerSettings.GridEnabled);
        Assert.True(deserialized.ViewerSettings.DfmOverlayEnabled);
        Assert.Contains("\"finishId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"processOptionValues\"", json, StringComparison.Ordinal);
        Assert.Contains("\"selectedBodyIndex\"", json, StringComparison.Ordinal);
        Assert.Contains("\"drawingFiles\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotePartViewModel_ToDraft_preserves_browser_local_dfm_analysis_payload()
    {
        var part = new QuotePartViewModel
        {
            PartId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            FileId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            UploadId = "upload-local-dfm",
            FileName = "local-bracket.stl",
            ProcessId = "cnc",
            MaterialId = "al6061",
            ProcessOptionValues =
            {
                ["paint_color_hex"] = "#f5f5f5",
                ["paint_color_reference"] = "RAL 9016"
            },
            Quantity = 1,
            VolumeCc = 27.122m,
            SurfaceAreaCm2 = 84.5m,
            StoragePath = "projects/session/local-bracket.stl",
            Status = "DfmAnalysisReady",
            GlbUrl = "blob:https://quote.local/viewer",
            ViewerStoragePath = "projects/session/local-bracket.stl",
            ViewerFileExtension = ".stl",
            ThumbnailUrl = "blob:https://quote.local/thumb",
            Findings =
            [
                new DfmFindingDto("warning", "LOCAL_PRIMARY", "Browser local DFM warning.")
            ],
            IsManifold = false,
            NonManifoldReason = "Open edge detected locally.",
            FdmDfmReport = new QeFdmDfmReport(0, 0, 0m, false, 0, []),
            CncDfmReport = new QeCncDfmReport(
                SharpCornerCount: 1,
                HasUndercuts: false,
                HasDrillHoles: true,
                DrillHoleCount: 2,
                RequiresEdm: false,
                RequiresGrinding: false,
                IsTurnable: true,
                Issues:
                [
                    new QeDfmIssueItem("warning", "LOCAL_SHARP_CORNER", "Local CNC warning.")
                ]),
            OverlayGlbUrls = ["blob:https://quote.local/overlay"]
        };

        var json = JsonSerializer.Serialize(part.ToDraft(), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("projects/session/local-bracket.stl", root.GetProperty("storagePath").GetString());
        Assert.Equal("DfmAnalysisReady", root.GetProperty("status").GetString());
        Assert.Equal("blob:https://quote.local/viewer", root.GetProperty("viewerGlbUrl").GetString());
        Assert.Equal("projects/session/local-bracket.stl", root.GetProperty("viewerStoragePath").GetString());
        Assert.Equal(".stl", root.GetProperty("viewerFileExtension").GetString());
        Assert.Equal("blob:https://quote.local/thumb", root.GetProperty("thumbnailUrl").GetString());
        Assert.False(root.GetProperty("isManifold").GetBoolean());
        Assert.Equal("Open edge detected locally.", root.GetProperty("nonManifoldReason").GetString());
        Assert.Equal("LOCAL_PRIMARY", root.GetProperty("findings")[0].GetProperty("code").GetString());
        Assert.Equal(0, root.GetProperty("fdmReport").GetProperty("thinWallCount").GetInt32());
        Assert.Equal(1, root.GetProperty("cncReport").GetProperty("sharpCornerCount").GetInt32());
        Assert.Equal("LOCAL_SHARP_CORNER", root.GetProperty("cncReport").GetProperty("issues")[0].GetProperty("code").GetString());
        Assert.Equal("blob:https://quote.local/overlay", root.GetProperty("overlayGlbUrls")[0].GetString());
        Assert.Equal("RAL 9016", root.GetProperty("processOptionValues").GetProperty("paint_color_reference").GetString());
    }


    [Fact]
    public void Upload_script_matches_browser_files_by_name_and_size()
    {
        var source = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-upload.js");

        Assert.Contains("getCapturedInputFiles", source, StringComparison.Ordinal);
        Assert.Contains("input._blazorFilesById", source, StringComparison.Ordinal);
        Assert.Contains("file.name === mapping.fileName", source, StringComparison.Ordinal);
        Assert.Contains("file.size === Number(mapping.fileSizeBytes)", source, StringComparison.Ordinal);
        Assert.Contains("const pendingUploadIds = new Set();", source, StringComparison.Ordinal);
        Assert.Contains("const activeUploadIds = new Set();", source, StringComparison.Ordinal);
        Assert.Contains("pendingUploadIds.add(mapping.clientFileId)", source, StringComparison.Ordinal);
        Assert.Contains("pendingUploadIds.delete(clientFileId);", source, StringComparison.Ordinal);
        Assert.Contains("activeUploadIds.add(clientFileId);", source, StringComparison.Ordinal);
        Assert.Contains("const isUploadRetained = pendingUploadIds.has(clientFileId) || activeUploadIds.has(clientFileId);", source, StringComparison.Ordinal);
        Assert.Contains("skipped: true", source, StringComparison.Ordinal);
        Assert.Contains("xhr.send(uploadBody)", source, StringComparison.Ordinal);
        Assert.Contains("Content-Range", source, StringComparison.Ordinal);
        Assert.Contains("event.dataTransfer.files", source, StringComparison.Ordinal);
        Assert.Contains("registerDropzone", source, StringComparison.Ordinal);
        Assert.Contains("HandleDroppedFilesAsync", source, StringComparison.Ordinal);
        Assert.Contains("async function getFileBytes(clientFileId)", source, StringComparison.Ordinal);
        Assert.Contains("function getObjectUrl(clientFileId)", source, StringComparison.Ordinal);
        Assert.Contains("scheduleClearFile", source, StringComparison.Ordinal);
    }

    [Fact]
    public void QuoteWorkspace_RetainsBrowserViewerFilesUntilPartLifecycleEnds()
    {
        var workspace = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "QuoteWorkspace.razor");
        var uploadScript = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-upload.js");

        Assert.DoesNotContain("scheduleClearFile(clientFileId);", uploadScript, StringComparison.Ordinal);
        Assert.Contains("ShouldRetainBrowserUploadFile(part)", workspace, StringComparison.Ordinal);
        Assert.Contains("!ShouldRetainBrowserUploadFile(part)", workspace, StringComparison.Ordinal);
        Assert.Contains("ClearBrowserUploadFileAsync(part, reason: \"remove-part\")", workspace, StringComparison.Ordinal);
        Assert.Contains("ClearRetainedBrowserUploadFilesAsync(\"workspace-reset\")", workspace, StringComparison.Ordinal);
        Assert.Contains("ClearRetainedBrowserUploadFilesAsync(\"dispose\")", workspace, StringComparison.Ordinal);
        Assert.Contains("quoteEngineUploads.clearFile", workspace, StringComparison.Ordinal);
    }

    [Fact]
    public void QuoteWorkspace_RendersAcceptedUploadBeforeBackendUploadCompletes()
    {
        var workspace = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "QuoteWorkspace.razor")
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains(
            "_parts.Add(part);\n            _selectedPartIndex = _parts.Count - 1;\n            if (_agentShell is not null)\n            {\n                await _agentShell.NotifyUploadStartedAsync(part);\n            }\n\n            await InvokeAsync(StateHasChanged);\n            try",
            workspace,
            StringComparison.Ordinal);
        var uploadFailureBlock = ExtractSourceBlock(
            workspace,
            "part.Status = \"Upload failed\";",
            "private bool ValidateUploadCandidate");
        Assert.Contains(
            "BuildUploadFailureMessage(candidate.FileName)",
            uploadFailureBlock,
            StringComparison.Ordinal);
        Assert.DoesNotContain("_error = ex.Message", uploadFailureBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void New_quote_workspace_uses_chat_first_agent_shell_for_anonymous_start()
    {
        var source = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "QuoteWorkspace.razor");
        var legacyQuotes = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "Quotes.razor");
        var script = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-upload.js");
        var apiClient = ReadRepoFile("Maliev.QuoteEngine.Client", "Services", "QuoteEngineApiClient.cs");

        Assert.Contains("@page \"/quotes\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("@page \"/quotes\"", legacyQuotes, StringComparison.Ordinal);
        Assert.DoesNotContain("Try the Quote Engine with a MALIEV sample file", source, StringComparison.Ordinal);
        Assert.Contains("<QuoteAgentLaunchShell", source, StringComparison.Ordinal);
        Assert.Contains("SessionId=\"@AgentSessionId\"", source, StringComparison.Ordinal);
        Assert.Contains("UploadedParts=\"@_parts\"", source, StringComparison.Ordinal);
        Assert.Contains("SelectedPartIndex=\"@_selectedPartIndex\"", source, StringComparison.Ordinal);
        Assert.Contains("OnUploadedPartSelected=\"SelectPart\"", source, StringComparison.Ordinal);
        Assert.Contains("OnUploadRequested=\"OpenFilePickerAsync\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OnSampleRequested=\"LoadSampleFileAsync\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowDemoSampleCard", source, StringComparison.Ordinal);
        Assert.Contains("LoadSampleFileAsync", source, StringComparison.Ordinal);
        Assert.Contains("await LoadDemoProjectAsync();", source, StringComparison.Ordinal);
        Assert.Contains("private bool ShowLaunchAccountCard => !IsSignedIn && !IsDemoMode;", source, StringComparison.Ordinal);
        Assert.Contains("private Guid AgentSessionId", source, StringComparison.Ordinal);
        Assert.Contains("[SupplyParameterFromQuery(Name = \"projectId\")]", source, StringComparison.Ordinal);
        Assert.Contains("public Guid? ProjectId { get; set; }", source, StringComparison.Ordinal);
        Assert.Contains("ResumeProjectAsync(ProjectId.Value)", source, StringComparison.Ordinal);
        Assert.Contains("GetProjectDetailAsync(projectId)", source, StringComparison.Ordinal);
        Assert.Contains("ApplyDefaultRouting(part, candidate.FileName)", source, StringComparison.Ordinal);
        Assert.Contains("await TryApplyLocalSupplementalPreviewUrlAsync(part, candidate);", source, StringComparison.Ordinal);
        Assert.Contains("private bool IsWorkspaceReady => _parts.Count > 0;", source, StringComparison.Ordinal);
        Assert.Contains("RegisterCompletedUploadWithAgentAsync(part, candidate)", source, StringComparison.Ordinal);
        Assert.Contains("await _agentShell.NotifyUploadStartedAsync(part);", source, StringComparison.Ordinal);
        Assert.Contains("await _agentShell.NotifyUploadCompletedAsync(part);", source, StringComparison.Ordinal);
        Assert.Contains("RegisterAgentAttachmentsAsync", source, StringComparison.Ordinal);
        Assert.Contains("QuoteAgentAttachmentRegisterRequest", source, StringComparison.Ordinal);
        Assert.Contains("InferAgentAttachmentKind", source, StringComparison.Ordinal);
        Assert.Contains("QuoteUploadConstraints.IsSupportedCadFileName(candidate.FileName)", source, StringComparison.Ordinal);
        Assert.Contains("Task<QuoteAgentStateResponse> RegisterAgentAttachmentsAsync", apiClient, StringComparison.Ordinal);
        Assert.Contains("quote/v1/agent/sessions/{sessionId:D}/attachments", apiClient, StringComparison.Ordinal);
        Assert.Contains("Task<CustomerProjectDetailResponse> GetProjectDetailAsync", apiClient, StringComparison.Ordinal);
        Assert.Contains("quote/v1/projects/{projectId:D}", apiClient, StringComparison.Ordinal);
        Assert.Contains("id=\"@PageDropzoneId\"", source, StringComparison.Ordinal);
        Assert.Contains("private const string PageDropzoneId = \"quote-page-dropzone\";", source, StringComparison.Ordinal);
        Assert.Contains("Drop files anywhere to start", source, StringComparison.Ordinal);
        Assert.Contains("qe-pn-root is-launch-screen", source, StringComparison.Ordinal);
        Assert.Contains("is-agent-studio has-uploads", source, StringComparison.Ordinal);
        Assert.DoesNotContain("qe-pn-root is-workspace-ready", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<QePartsListPanel", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<QeQuoteSummaryBar", source, StringComparison.Ordinal);
        Assert.DoesNotContain("qe-pn-mobile-toolbar", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Drop quote files here", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Untitled quote</", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CompactDropzoneId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MobileDropzoneId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HeroDropzoneId", source, StringComparison.Ordinal);
        Assert.Contains("RegisterActiveDropzoneAsync", source, StringComparison.Ordinal);
        Assert.Contains("string[] activeDropzoneIds = [PageDropzoneId];", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new[] { PageDropzoneId, CompactDropzoneId, MobileDropzoneId }", source, StringComparison.Ordinal);
        Assert.DoesNotContain("[PageDropzoneId, HeroDropzoneId]", source, StringComparison.Ordinal);
        Assert.Contains("new { clickToOpen = false }", source, StringComparison.Ordinal);
        Assert.Contains("quoteEngineUploads.unregisterDropzone", source, StringComparison.Ordinal);
        Assert.Contains("private async Task TryApplyLocalSupplementalPreviewUrlAsync(QuotePartViewModel part, UploadCandidate candidate)", source, StringComparison.Ordinal);
        Assert.Contains("part.ThumbnailUrl = objectUrl;", source, StringComparison.Ordinal);
        Assert.Contains("candidate.ContentType.StartsWith(\"image/\", StringComparison.OrdinalIgnoreCase)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("loadSampleFile", script, StringComparison.Ordinal);
        Assert.DoesNotContain("fetch(sampleUrl", script, StringComparison.Ordinal);
        Assert.Contains("registrationOptions.clickToOpen !== false", script, StringComparison.Ordinal);
        Assert.Contains("event.stopPropagation();", script, StringComparison.Ordinal);
        Assert.Contains("HandleDroppedFilesAsync", script, StringComparison.Ordinal);
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");
        Assert.Contains(".qe-agent-shell", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-composer", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-artifact-drawer", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-upload-strip", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-upload-card", styles, StringComparison.Ordinal);
        Assert.Contains(".app-shell.is-quote-workspace .quote-topbar", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pdc-container {\n    position: relative;", styles, StringComparison.Ordinal);
        Assert.Contains("3D Model", ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartDetailCard.razor"), StringComparison.Ordinal);
        Assert.Contains(".qe-dfm-tab {\n    flex: 1 1 0;", styles, StringComparison.Ordinal);
        Assert.Contains("flex: 0 0 380px;", styles, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: repeat(3, minmax(0, 1fr));", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pn-root.is-launch-screen", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-page-drop-overlay {\n    position: fixed;", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pn-root.is-dragover .qe-page-drop-overlay", styles, StringComparison.Ordinal);
        Assert.Contains("pointer-events: none;", styles, StringComparison.Ordinal);
        Assert.Contains("@keyframes qe-panel-enter-left", styles, StringComparison.Ordinal);
        Assert.Contains("@keyframes qe-panel-enter-bottom", styles, StringComparison.Ordinal);
        Assert.DoesNotContain(".qe-dropzone .mud-icon-root {", styles, StringComparison.Ordinal);
        Assert.True(File.Exists(RepoPath("Maliev.QuoteEngine.Client", "wwwroot", "samples", "sample.step")));
        Assert.True(File.Exists(RepoPath("Maliev.QuoteEngine.Client", "wwwroot", "models", "sample.glb")));
        Assert.True(File.Exists(RepoPath("Maliev.QuoteEngine.Client", "wwwroot", "images", "generated", "metal-components-cutout.png")));
        Assert.False(File.Exists(RepoPath("Maliev.QuoteEngine.Client", "wwwroot", "sample-file.step")));
        Assert.DoesNotContain("\"Sign in to quote\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Saved temporarily", source, StringComparison.Ordinal);
        Assert.DoesNotContain("qe-qsb-customer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CustomerBoundaryTitle", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CustomerBoundaryHint", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=\"qe-zone-label\">Bill To</span>", source, StringComparison.Ordinal);
        Assert.Contains("QuoteUploadConstraints.SupportedAttachmentAccept", source, StringComparison.Ordinal);
        Assert.Contains("QuoteUploadConstraints.SupportedAttachmentExtensionLabel", source, StringComparison.Ordinal);
        Assert.Contains("QuoteUploadConstraints.MaxFileSizeMegabytes", source, StringComparison.Ordinal);
        Assert.Contains("QuoteUploadHandoffRequest", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Temporary upload storage until you sign in", source, StringComparison.Ordinal);
        Assert.DoesNotContain("max 10 GB per file", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Sign in before uploading customer-owned manufacturing files.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void QuoteAgentLaunchShell_has_monochrome_chatgpt_like_contract()
    {
        var component = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteAgent", "QuoteAgentLaunchShell.razor");
        var workspace = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "QuoteWorkspace.razor");
        var apiClient = ReadRepoFile("Maliev.QuoteEngine.Client", "Services", "QuoteEngineApiClient.cs");
        var composerScript = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-agent-composer.js");
        var uploadScript = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-upload.js");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");
        var agentStyles = ExtractSourceBlock(styles, "/* Quote agent chat-first shell */", "/* End quote agent chat-first shell */");
        var primaryNav = ExtractSourceBlock(component, "<nav class=\"qe-agent-primary-nav\"", "</nav>");

        Assert.Contains("<section class=\"@ShellClass\"", component, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-agent-rail\"", component, StringComparison.Ordinal);
        Assert.Contains("class=\"@($\"qe-agent-composer {(_sending ? \"is-sending\" : string.Empty)}\")\"", component, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-agent-artifact-toggle\"", component, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-agent-artifact-drawer\"", component, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-agent-summary-toggle\"", component, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-agent-summary-drawer\"", component, StringComparison.Ordinal);
        Assert.Contains("ProjectSummaryMetrics", component, StringComparison.Ordinal);
        Assert.Contains("ProjectSummaryDetails", component, StringComparison.Ordinal);
        Assert.Contains("LatestWorkbench", component, StringComparison.Ordinal);
        Assert.Contains("ToggleSummaryPanel", component, StringComparison.Ordinal);
        Assert.Contains("CloseSidePanels", component, StringComparison.Ordinal);
        Assert.Contains("RightPanelOpen", component, StringComparison.Ordinal);
        Assert.Contains("Icons.Material.Outlined.Summarize", component, StringComparison.Ordinal);
        Assert.Contains("Text(\"Summary\"", component, StringComparison.Ordinal);
        Assert.Contains("ShellClass", component, StringComparison.Ordinal);
        Assert.Contains("qe-agent-shell--rail-collapsed", component, StringComparison.Ordinal);
        Assert.Contains("qe-agent-shell--artifacts-open", component, StringComparison.Ordinal);
        Assert.Contains("ToggleRail", component, StringComparison.Ordinal);
        Assert.Contains("AriaExpanded", component, StringComparison.Ordinal);
        Assert.Contains("TogglePinnedProjects", component, StringComparison.Ordinal);
        Assert.Contains("ToggleProjects", component, StringComparison.Ordinal);
        Assert.Contains("ProjectManagementSectionClass", component, StringComparison.Ordinal);
        Assert.Contains("_pinnedProjectsCollapsed", component, StringComparison.Ordinal);
        Assert.Contains("_projectsCollapsed", component, StringComparison.Ordinal);
        Assert.Contains("aria-expanded=\"@AriaExpanded(!_pinnedProjectsCollapsed)\"", component, StringComparison.Ordinal);
        Assert.Contains("aria-expanded=\"@AriaExpanded(!_projectsCollapsed)\"", component, StringComparison.Ordinal);
        Assert.Contains("@if (!_pinnedProjectsCollapsed)", component, StringComparison.Ordinal);
        Assert.Contains("@if (!_projectsCollapsed)", component, StringComparison.Ordinal);
        Assert.Contains("MainClass => _messages.Count == 0 ? \"qe-agent-main qe-agent-main--empty\" : \"qe-agent-main qe-agent-main--chat\"", component, StringComparison.Ordinal);
        Assert.DoesNotContain("qe-agent-avatar", component, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-agent-project-name\"", component, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-agent-signin-btn\"", component, StringComparison.Ordinal);
        Assert.Contains("@Text(\"Sign in\", \"เข้าสู่ระบบ\")", component, StringComparison.Ordinal);
        Assert.DoesNotContain("Sign in / Sign up", component, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"qe-agent-signup-btn\"", component, StringComparison.Ordinal);
        Assert.Contains("href=\"/auth/sign-in?returnUrl=/quotes\"", component, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/auth/sign-up?returnUrl=/quotes\"", component, StringComparison.Ordinal);
        Assert.Contains("@bind=\"_projectName\"", component, StringComparison.Ordinal);
        Assert.Contains("private string _projectName = string.Empty;", component, StringComparison.Ordinal);
        Assert.Contains("OpenSketchAsync", component, StringComparison.Ordinal);
        Assert.Contains("quote-agent-sketch.js", component, StringComparison.Ordinal);
        Assert.Contains("initSketchCanvas", component, StringComparison.Ordinal);
        Assert.Contains("setSketchBrushColor", component, StringComparison.Ordinal);
        Assert.Contains("exportSketchCanvas", component, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-agent-sketch-canvas\"", component, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-agent-sketch-tools\"", component, StringComparison.Ordinal);
        Assert.Contains("InsertSketchImageAsync", component, StringComparison.Ordinal);
        Assert.Contains("PasteSketchImageAsync", component, StringComparison.Ordinal);
        Assert.Contains("UseSketchEraserAsync", component, StringComparison.Ordinal);
        Assert.Contains("SetSketchImageNameAsync", component, StringComparison.Ordinal);
        Assert.Contains("ApplySketchImageTitle(Text(\"Clipboard image\", \"รูปจากคลิปบอร์ด\"))", component, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplySketchImageTitle(Text(\"Clipboard image annotation\"", component, StringComparison.Ordinal);
        Assert.Contains("InputFile id=\"@SketchImageInputId\"", component, StringComparison.Ordinal);
        Assert.Contains("SketchColorOption", component, StringComparison.Ordinal);
        Assert.Contains("SelectSketchColorAsync", component, StringComparison.Ordinal);
        Assert.Contains("new(\"Red\", \"แดง\", \"#dc2626\")", component, StringComparison.Ordinal);
        Assert.Contains("new(\"Blue\", \"น้ำเงิน\", \"#2563eb\")", component, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"qe-agent-workbench\"", component, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"qe-agent-gates\"", component, StringComparison.Ordinal);
        Assert.DoesNotContain("<span>@Text(\"Gates\"", component, StringComparison.Ordinal);
        Assert.DoesNotContain("qe-agent-gate", component, StringComparison.Ordinal);
        Assert.DoesNotContain("Try sample part", component, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowDemoSampleCard", component, StringComparison.Ordinal);
        Assert.DoesNotContain("OnSampleRequested", component, StringComparison.Ordinal);
        Assert.DoesNotContain("Icons.Material.Outlined.Description", component, StringComparison.Ordinal);
        Assert.Contains("WorkspacePage", component, StringComparison.Ordinal);
        Assert.Contains("WorkspacePage.Chat", component, StringComparison.Ordinal);
        Assert.Contains("WorkspacePage.Plugins", component, StringComparison.Ordinal);
        Assert.Contains("WorkspacePage.Projects", component, StringComparison.Ordinal);
        Assert.Contains("WorkspacePage.Settings", component, StringComparison.Ordinal);
        Assert.Contains("PageNavClass(WorkspacePage.Chat)", component, StringComparison.Ordinal);
        Assert.Contains("PageNavClass(WorkspacePage.Plugins)", component, StringComparison.Ordinal);
        Assert.Contains("PageNavClass(WorkspacePage.Projects)", component, StringComparison.Ordinal);
        Assert.Contains("OpenChatWorkspaceAsync", component, StringComparison.Ordinal);
        Assert.Contains("OpenPluginsPageAsync", component, StringComparison.Ordinal);
        Assert.Contains("OpenProjectsPage", component, StringComparison.Ordinal);
        Assert.Contains("OpenSettingsPage", component, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-agent-management-page\"", component, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-agent-management-row\"", component, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-agent-settings-grid\"", component, StringComparison.Ordinal);
        Assert.Contains("@if (PinnedProjects.Any())", component, StringComparison.Ordinal);
        Assert.Contains("private readonly List<ProjectNavItem> _projects = [];", component, StringComparison.Ordinal);
        Assert.Contains("GetProjectNavigationAsync", component, StringComparison.Ordinal);
        Assert.Contains("ProjectNavItem.FromDto", component, StringComparison.Ordinal);
        Assert.Contains("private IEnumerable<ProjectNavItem> RegularProjects => _projects.Where(project => !project.IsPinned && !project.IsArchived);", component, StringComparison.Ordinal);
        Assert.Contains("@foreach (var project in RegularProjects)", component, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"qe-agent-project-row\"", component, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-agent-management-row\"", component, StringComparison.Ordinal);
        Assert.Contains("href=\"@ProjectHref(project)\"", component, StringComparison.Ordinal);
        Assert.Contains("private static string ProjectHref(ProjectNavItem project)", component, StringComparison.Ordinal);
        Assert.Contains("/quotes?projectId={project.ProjectId:D}", component, StringComparison.Ordinal);
        Assert.Contains("DuplicateProjectAsync", component, StringComparison.Ordinal);
        Assert.Contains("Api.DuplicateProjectAsync(project.ProjectId", component, StringComparison.Ordinal);
        Assert.Contains("DuplicateDraftProjectRequest", component, StringComparison.Ordinal);
        Assert.Contains("ApplyDuplicatedProject", component, StringComparison.Ordinal);
        Assert.Contains("ProjectNavItem.FromDuplicateResponse", component, StringComparison.Ordinal);
        Assert.Contains("Icons.Material.Outlined.ContentCopy", component, StringComparison.Ordinal);
        Assert.Contains("Duplicate project", component, StringComparison.Ordinal);
        Assert.Contains("ToggleProjectPinAsync", component, StringComparison.Ordinal);
        Assert.Contains("PinProjectAsync", component, StringComparison.Ordinal);
        Assert.Contains("UnpinProjectAsync", component, StringComparison.Ordinal);
        Assert.Contains("ArchiveProjectAsync", component, StringComparison.Ordinal);
        Assert.Contains("Api.ArchiveProjectAsync(project.ProjectId)", component, StringComparison.Ordinal);
        Assert.Contains("Icons.Material.Outlined.Archive", component, StringComparison.Ordinal);
        Assert.Contains("Archive project", component, StringComparison.Ordinal);
        Assert.Contains("Task<ProjectManagementResponse> ArchiveProjectAsync", apiClient, StringComparison.Ordinal);
        Assert.Contains("quote/v1/projects/{projectId:D}/archive", apiClient, StringComparison.Ordinal);
        Assert.Contains("AchieveProjectAsync", component, StringComparison.Ordinal);
        Assert.Contains("Api.AchieveProjectAsync(project.ProjectId)", component, StringComparison.Ordinal);
        Assert.Contains("Icons.Material.Outlined.TaskAlt", component, StringComparison.Ordinal);
        Assert.Contains("Mark achieved", component, StringComparison.Ordinal);
        Assert.Contains("Task<ProjectManagementResponse> AchieveProjectAsync", apiClient, StringComparison.Ordinal);
        Assert.Contains("quote/v1/projects/{projectId:D}/achieve", apiClient, StringComparison.Ordinal);
        Assert.Contains("Icons.Material.Outlined.PushPin", component, StringComparison.Ordinal);
        Assert.Contains("Icons.Material.Filled.PushPin", component, StringComparison.Ordinal);
        Assert.Contains("Icons.Material.Outlined.Search", primaryNav, StringComparison.Ordinal);
        Assert.Contains("@onclick=\"ToggleSearch\"", primaryNav, StringComparison.Ordinal);
        Assert.DoesNotContain("qe-agent-global-search", primaryNav, StringComparison.Ordinal);
        Assert.Contains("qe-agent-global-search", component, StringComparison.Ordinal);
        Assert.Contains("@onfocus=\"OpenSearchAsync\"", component, StringComparison.Ordinal);
        Assert.Contains("placeholder=\"@Text(\"Projects, orders, files, quotes...\"", component, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-agent-search-panel\"", component, StringComparison.Ordinal);
        Assert.Contains("Search everything", component, StringComparison.Ordinal);
        Assert.Contains("SearchResults", component, StringComparison.Ordinal);
        Assert.Contains("BuildSearchResults", component, StringComparison.Ordinal);
        Assert.Contains("OnSearchInputAsync", component, StringComparison.Ordinal);
        Assert.Contains("RefreshSearchResultsAsync", component, StringComparison.Ordinal);
        Assert.Contains("SearchCustomerDataAsync", component, StringComparison.Ordinal);
        Assert.Contains("private readonly List<QuoteAgentSearchResultDto> _customerSearchResults = [];", component, StringComparison.Ordinal);
        Assert.Contains("SearchNavItem.FromDto", component, StringComparison.Ordinal);
        Assert.Contains("ApplySearchResult", component, StringComparison.Ordinal);
        Assert.Contains("Navigation.NavigateTo(item.Url)", component, StringComparison.Ordinal);
        Assert.Contains("item.ActionHint.Equals(\"open_artifact\"", component, StringComparison.Ordinal);
        Assert.Contains("_artifactPanelOpen = true;", component, StringComparison.Ordinal);
        Assert.Contains("SearchNavItem", component, StringComparison.Ordinal);
        Assert.Contains("Search all projects, orders, files, and artifacts", component, StringComparison.Ordinal);
        Assert.Contains("projects, orders, files, or artifacts", component, StringComparison.Ordinal);
        Assert.Contains("Pinned projects", component, StringComparison.Ordinal);
        Assert.Contains("Plugins", component, StringComparison.Ordinal);
        Assert.DoesNotContain("TogglePluginsAsync", component, StringComparison.Ordinal);
        Assert.DoesNotContain("_pluginsOpen", component, StringComparison.Ordinal);
        Assert.Contains("_composerPluginsOpen", component, StringComparison.Ordinal);
        Assert.Contains("ToggleComposerPluginsMenu", component, StringComparison.Ordinal);
        Assert.Contains("qe-agent-google-drive-icon", component, StringComparison.Ordinal);
        Assert.Contains("GetConnectorRegistryAsync", component, StringComparison.Ordinal);
        Assert.Contains("GetConnectorHandoffAsync", component, StringComparison.Ordinal);
        Assert.Contains("QuoteAgentConnectorHandoffResponse", component, StringComparison.Ordinal);
        Assert.Contains("connector.ConnectorId", component, StringComparison.Ordinal);
        Assert.Contains("handoff.HandoffUrl", component, StringComparison.Ordinal);
        Assert.Contains("Navigation.NavigateTo(handoff.HandoffUrl)", component, StringComparison.Ordinal);
        Assert.Contains("handoff.Message", component, StringComparison.Ordinal);
        Assert.Contains("QuoteAgentConnectorDto", component, StringComparison.Ordinal);
        Assert.Contains("ConnectorStatus", component, StringComparison.Ordinal);
        Assert.Contains("ApplyConnector", component, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"qe-agent-plugin-panel\"", component, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"qe-agent-plugin-list\"", component, StringComparison.Ordinal);
        Assert.Contains("Projects", component, StringComparison.Ordinal);
        Assert.DoesNotContain("Attach files", primaryNav, StringComparison.Ordinal);
        Assert.DoesNotContain("RequestUploadAsync", primaryNav, StringComparison.Ordinal);
        Assert.Contains("Icons.Material.Outlined.Add", component, StringComparison.Ordinal);
        Assert.Contains("Icons.Material.Outlined.Mic", component, StringComparison.Ordinal);
        Assert.Contains("Icons.Material.Filled.ArrowUpward", component, StringComparison.Ordinal);
        Assert.Contains("SendAgentMessageStreamAsync", component, StringComparison.Ordinal);
        Assert.Contains("assistantMessage.Content += streamEvent.Delta", component, StringComparison.Ordinal);
        Assert.Contains("QuoteAgentMarkdownRenderer.Render(message.Content)", component, StringComparison.Ordinal);
        Assert.Contains("BuildAgentConnectionFallbackMessage(message)", component, StringComparison.Ordinal);
        Assert.DoesNotContain("_error = ex.Message;", component, StringComparison.Ordinal);
        Assert.Contains("I can still help collect requirements", component, StringComparison.Ordinal);
        Assert.Contains("part.ThumbnailUrl", component, StringComparison.Ordinal);
        Assert.Contains("qe-agent-artifact-thumb", component, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-agent-markdown\"", component, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-agent-thinking\"", component, StringComparison.Ordinal);
        Assert.Contains("assistantMessage.ThinkingSteps = streamEvent.Response.ThinkingSteps", component, StringComparison.Ordinal);
        Assert.Contains("<details class=\"qe-agent-thinking\">", component, StringComparison.Ordinal);
        Assert.Contains("streamEvent.Type.Equals(\"delta\"", component, StringComparison.Ordinal);
        Assert.Contains("streamEvent.Type.Equals(\"final\"", component, StringComparison.Ordinal);
        Assert.Contains("ConfirmAgentActionAsync", component, StringComparison.Ordinal);
        Assert.Contains("QuoteAgentGateDto", component, StringComparison.Ordinal);
        Assert.Contains("QuoteAgentProposedActionDto", component, StringComparison.Ordinal);
        Assert.Contains("@ref=\"_composerTextarea\"", component, StringComparison.Ordinal);
        Assert.Contains("@bind:event=\"oninput\"", component, StringComparison.Ordinal);
        Assert.Contains("autofocus", component, StringComparison.Ordinal);
        Assert.Contains("maxlength=\"@QuoteAgentTextLimits.MaxMessageCharacters\"", component, StringComparison.Ordinal);
        Assert.Contains("quote-agent-composer.js", component, StringComparison.Ordinal);
        Assert.Contains("SubmitComposerFromKeyboardAsync", component, StringComparison.Ordinal);
        Assert.Contains("FocusComposerAsync", component, StringComparison.Ordinal);
        Assert.Contains("StartDictationAsync", component, StringComparison.Ordinal);
        Assert.Contains("Icons.Material.Outlined.MicNone", component, StringComparison.Ordinal);
        Assert.Contains("Click to dictate or hold", component, StringComparison.Ordinal);
        Assert.Contains("_dictating", component, StringComparison.Ordinal);
        Assert.Contains("_composerMenuOpen", component, StringComparison.Ordinal);
        Assert.Contains("CloseComposerMenuAsync", component, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-agent-add-backdrop\"", component, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-agent-composer-attachments\"", component, StringComparison.Ordinal);
        Assert.Contains("qe-agent-composer-attachment-card", component, StringComparison.Ordinal);
        Assert.Contains("@part.FileName", component, StringComparison.Ordinal);
        Assert.Contains("AttachmentStatusLabel(part)", component, StringComparison.Ordinal);
        Assert.Contains("IsUploadBusy(part)", component, StringComparison.Ordinal);
        Assert.Contains("_pendingComposerAttachments", component, StringComparison.Ordinal);
        Assert.Contains("BuildPendingMessageAttachments()", component, StringComparison.Ordinal);
        Assert.Contains("pendingAttachments.Count == 0", component, StringComparison.Ordinal);
        Assert.Contains("new AgentMessageRow(\"user\", message, attachments: pendingAttachments)", component, StringComparison.Ordinal);
        Assert.Contains("Attachments = pendingAttachments.Select(attachment => attachment.ToAgentAttachment()).ToList()", component, StringComparison.Ordinal);
        Assert.Contains("message.Attachments.Count > 0", component, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-agent-message-attachments\"", component, StringComparison.Ordinal);
        Assert.Contains("NotifyUploadStartedAsync", component, StringComparison.Ordinal);
        Assert.Contains("NotifyUploadCompletedAsync", component, StringComparison.Ordinal);
        Assert.DoesNotContain("Attached file:", component, StringComparison.Ordinal);
        Assert.DoesNotContain("Upload ready:", component, StringComparison.Ordinal);
        Assert.Contains("qe-agent-send-spinner", component, StringComparison.Ordinal);
        Assert.Contains("Text(\"Processing\", \"กำลังประมวลผล\")", component, StringComparison.Ordinal);
        Assert.Contains("class=\"sr-only\"", component, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-agent-add-menu\"", component, StringComparison.Ordinal);
        Assert.Contains("Add photos and files", component, StringComparison.Ordinal);
        Assert.Contains("Add hand sketch", component, StringComparison.Ordinal);
        Assert.Contains("Google Drive connector", component, StringComparison.Ordinal);
        Assert.Contains("Google Drive is available first; CAD tools follow after MVP.", component, StringComparison.Ordinal);
        Assert.Contains("connector.Status.Equals(\"available\"", component, StringComparison.Ordinal);
        Assert.Contains("Text(\"Connect\", \"เชื่อมต่อ\")", component, StringComparison.Ordinal);
        Assert.Contains("Paste an image from the clipboard", component, StringComparison.Ordinal);
        Assert.DoesNotContain(">Paste image<", component, StringComparison.Ordinal);
        Assert.Contains("Icons.Material.Outlined.DeleteOutline", component, StringComparison.Ordinal);
        Assert.Contains("ApplyGoogleDriveConnectorAsync", component, StringComparison.Ordinal);
        Assert.Contains("RequestUploadFromComposerMenuAsync", component, StringComparison.Ordinal);
        Assert.Contains("OpenSketchFromComposerMenuAsync", component, StringComparison.Ordinal);
        Assert.Contains("EnsureLocalProjectFromMessage(message);", component, StringComparison.Ordinal);
        Assert.Contains("BuildLocalProjectTitle", component, StringComparison.Ordinal);
        Assert.Contains("ThinkingStepTitle(step)", component, StringComparison.Ordinal);
        Assert.Contains("HumanizeAgentActivity", component, StringComparison.Ordinal);
        Assert.Contains("await FocusComposerAsync();", component, StringComparison.Ordinal);
        Assert.Contains("focusComposer", component, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-agent-use-cases\"", component, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-search-panel", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-global-search", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-global-search input", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-search-results", agentStyles, StringComparison.Ordinal);
        Assert.Contains("SearchCustomerDataAsync", apiClient, StringComparison.Ordinal);
        Assert.Contains("GetConnectorHandoffAsync", apiClient, StringComparison.Ordinal);
        Assert.Contains("quote/v1/agent/tools/quote_get_connector_handoff", apiClient, StringComparison.Ordinal);
        Assert.Contains("connector_id", apiClient, StringComparison.Ordinal);
        Assert.Contains("quote/v1/agent/sessions/{sessionId:D}/search", apiClient, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-management-page", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-management-row", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-management-section-toggle", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-settings-grid", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-search-panel,", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-search-panel *", agentStyles, StringComparison.Ordinal);
        Assert.Contains("max-height: min(34dvh, 280px);", agentStyles, StringComparison.Ordinal);
        Assert.Contains("overflow-y: auto;", agentStyles, StringComparison.Ordinal);
        Assert.Contains("overflow-x: hidden;", agentStyles, StringComparison.Ordinal);
        Assert.Contains("padding: 4px 8px 4px 10px;", agentStyles, StringComparison.Ordinal);
        Assert.Contains("contain: inline-size;", agentStyles, StringComparison.Ordinal);
        Assert.Contains("scrollbar-gutter: stable;", agentStyles, StringComparison.Ordinal);
        Assert.Contains("inline-size: 100%;", agentStyles, StringComparison.Ordinal);
        Assert.Contains("max-inline-size: 100%;", agentStyles, StringComparison.Ordinal);
        Assert.Contains("overflow: clip;", agentStyles, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: minmax(0, 1fr);", agentStyles, StringComparison.Ordinal);
        Assert.Contains("padding-inline: 4px;", agentStyles, StringComparison.Ordinal);
        Assert.Contains("overflow-wrap: anywhere;", agentStyles, StringComparison.Ordinal);
        Assert.Contains("display: block;", agentStyles, StringComparison.Ordinal);
        Assert.Contains("border: 1px solid var(--qe-agent-line);", agentStyles, StringComparison.Ordinal);
        Assert.Contains("display: -webkit-box;", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-shell--rail-collapsed", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-shell--artifacts-open", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-message--user .qe-agent-bubble p", agentStyles, StringComparison.Ordinal);
        Assert.Contains("border-radius: 999px;", agentStyles, StringComparison.Ordinal);
        Assert.Contains("private string ThemeIcon => IsDarkMode ? Icons.Material.Outlined.DarkMode : Icons.Material.Outlined.LightMode;", component, StringComparison.Ordinal);
        Assert.Contains("Quote ideas", component, StringComparison.Ordinal);
        Assert.DoesNotContain(">Sample use cases<", component, StringComparison.Ordinal);
        Assert.Contains("SampleUseCases", component, StringComparison.Ordinal);
        Assert.Contains("VisibleUseCases", component, StringComparison.Ordinal);
        Assert.Contains("Random.Shared.Next()", component, StringComparison.Ordinal);
        Assert.Contains("ApplyUseCaseAsync", component, StringComparison.Ordinal);
        Assert.Contains("typeComposerText", component, StringComparison.Ordinal);
        Assert.Contains("UseCaseOption", component, StringComparison.Ordinal);
        Assert.Contains("Quote a 3D printed enclosure", component, StringComparison.Ordinal);
        Assert.Contains("Turn a sketch into requirements", component, StringComparison.Ordinal);
        Assert.Contains("Compare material and finish options", component, StringComparison.Ordinal);
        Assert.Contains("Quote production-ready revision", component, StringComparison.Ordinal);
        Assert.Contains("AgentStatusClass", component, StringComparison.Ordinal);
        Assert.Contains("AgentStatusTooltip", component, StringComparison.Ordinal);
        Assert.Contains("is-connected", component, StringComparison.Ordinal);
        Assert.Contains("is-warning", component, StringComparison.Ordinal);
        Assert.Contains("is-failed", component, StringComparison.Ordinal);
        Assert.Contains("Task HandleSubmitAsync()", component, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-agent-workflow\"", component, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-agent-flow-card qe-agent-analysis-card\"", component, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-agent-flow-card qe-agent-requirements-card\"", component, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-agent-flow-card qe-agent-dfm-card\"", component, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-agent-flow-card qe-agent-model-card\"", component, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-agent-flow-card qe-agent-options-card\"", component, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-agent-flow-card qe-agent-price-card\"", component, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-agent-flow-card qe-agent-order-card\"", component, StringComparison.Ordinal);
        Assert.Contains("TryBuildLocalDemoTurn", component, StringComparison.Ordinal);
        Assert.Contains("BuildSketchDemoTurn", component, StringComparison.Ordinal);
        Assert.Contains("Hole too close to bend", component, StringComparison.Ordinal);
        Assert.Contains("6061-T6 aluminum", component, StringComparison.Ordinal);
        Assert.Contains("$470.00", component, StringComparison.Ordinal);

        Assert.Contains("Task<QuoteAgentTurnResponse> SendAgentMessageAsync", apiClient, StringComparison.Ordinal);
        Assert.Contains("IAsyncEnumerable<QuoteAgentStreamEvent> SendAgentMessageStreamAsync", apiClient, StringComparison.Ordinal);
        Assert.Contains("HttpCompletionOption.ResponseHeadersRead", apiClient, StringComparison.Ordinal);
        Assert.Contains("quote/v1/agent/messages/stream", apiClient, StringComparison.Ordinal);
        Assert.Contains("Task<QuoteAgentActionResultResponse> ConfirmAgentActionAsync", apiClient, StringComparison.Ordinal);
        Assert.Contains("event.key !== \"Enter\"", composerScript, StringComparison.Ordinal);
        Assert.Contains("event.shiftKey", composerScript, StringComparison.Ordinal);
        Assert.Contains("event.preventDefault();", composerScript, StringComparison.Ordinal);
        Assert.Contains("textarea.dispatchEvent(new Event(\"input\", { bubbles: true }))", composerScript, StringComparison.Ordinal);
        Assert.Contains("window.requestAnimationFrame", composerScript, StringComparison.Ordinal);
        Assert.Contains("dotNetRef.invokeMethodAsync(\"SubmitComposerFromKeyboardAsync\")", composerScript, StringComparison.Ordinal);
        Assert.DoesNotContain("textarea.form.requestSubmit();", composerScript, StringComparison.Ordinal);
        Assert.Contains("export function focusComposer", composerScript, StringComparison.Ordinal);
        Assert.Contains("moveCaretToEnd(textarea)", composerScript, StringComparison.Ordinal);
        Assert.Contains("textarea.setSelectionRange(end, end)", composerScript, StringComparison.Ordinal);
        Assert.Contains("clearTextSelection", composerScript, StringComparison.Ordinal);
        Assert.Contains("selection.rangeCount > 0", composerScript, StringComparison.Ordinal);
        Assert.DoesNotContain("window.addEventListener(\"focus\", windowFocus)", composerScript, StringComparison.Ordinal);
        Assert.DoesNotContain("window.removeEventListener(\"focus\", handlers.windowFocus)", composerScript, StringComparison.Ordinal);
        Assert.DoesNotContain("document.addEventListener(\"selectionchange\", selectionchange)", composerScript, StringComparison.Ordinal);
        Assert.DoesNotContain("document.removeEventListener(\"selectionchange\", handlers.selectionchange)", composerScript, StringComparison.Ordinal);
        Assert.Contains("textarea.addEventListener(\"input\", input)", composerScript, StringComparison.Ordinal);
        Assert.Contains("textarea.removeEventListener(\"input\", handlers.input)", composerScript, StringComparison.Ordinal);
        Assert.Contains("function normalizeCaretAfterInput", composerScript, StringComparison.Ordinal);
        Assert.Contains("textarea.selectionStart === 0 && textarea.selectionEnd === 0", composerScript, StringComparison.Ordinal);
        Assert.Contains("function updateComposerShape", composerScript, StringComparison.Ordinal);
        Assert.Contains("qe-agent-composer--multiline", composerScript, StringComparison.Ordinal);
        Assert.Contains("textarea.value.includes(\"\\n\")", composerScript, StringComparison.Ordinal);
        Assert.Contains("textarea.scrollHeight > lineHeight * 2.25", composerScript, StringComparison.Ordinal);
        Assert.Contains("SubmitComposerFromKeyboardAsync", composerScript, StringComparison.Ordinal);
        Assert.Contains("export async function typeComposerText", composerScript, StringComparison.Ordinal);
        Assert.Contains("export async function dictateComposerText", composerScript, StringComparison.Ordinal);
        Assert.Contains("SpeechRecognition", composerScript, StringComparison.Ordinal);
        Assert.Contains("summarizeDictation", composerScript, StringComparison.Ordinal);
        Assert.Contains("typingAnimations", composerScript, StringComparison.Ordinal);
        Assert.Contains("registerPasteTarget", workspace, StringComparison.Ordinal);
        Assert.Contains("unregisterPasteTarget", workspace, StringComparison.Ordinal);
        Assert.Contains("LoadAuthenticatedAccountAsync", workspace, StringComparison.Ordinal);
        Assert.Contains("HttpStatusCode.Unauthorized", workspace, StringComparison.Ordinal);
        Assert.Contains("_authStatus = new QuoteAuthStatusResponse(false, null, null);", workspace, StringComparison.Ordinal);
        Assert.Contains("function registerPasteTarget", uploadScript, StringComparison.Ordinal);
        Assert.Contains("filesFromClipboard", uploadScript, StringComparison.Ordinal);
        Assert.Contains("HandleDroppedFilesAsync", uploadScript, StringComparison.Ordinal);
        Assert.Contains("storeBrowserFile", uploadScript, StringComparison.Ordinal);
        Assert.Contains("event.clipboardData", uploadScript, StringComparison.Ordinal);
        Assert.Contains("event.preventDefault();", uploadScript, StringComparison.Ordinal);

        Assert.Contains(":root[data-maliev-theme=\"dark\"] .qe-agent-shell", agentStyles, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: 260px minmax(0, 1fr) 0;", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-artifact-drawer", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-summary-drawer", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-summary-toggle", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-summary-card", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-summary-grid", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-summary-section", agentStyles, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: 260px minmax(0, 1fr) minmax(320px, 380px);", agentStyles, StringComparison.Ordinal);
        Assert.Contains("transition: grid-template-columns 260ms cubic-bezier(.2, .8, .2, 1);", agentStyles, StringComparison.Ordinal);
        Assert.Contains("@keyframes qe-agent-artifact-drawer-enter", agentStyles, StringComparison.Ordinal);
        Assert.Contains("animation: qe-agent-artifact-drawer-enter 260ms cubic-bezier(.2, .8, .2, 1) both;", agentStyles, StringComparison.Ordinal);
        Assert.Contains("height: 100dvh;", agentStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("max-height: min(36dvh, 320px);", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-quick-actions", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-use-cases", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-workflow", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-markdown", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-markdown-table", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-thinking", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-thinking summary", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-flow-card", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-analysis-card", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-requirements-card table", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-dfm-issue.is-blocker", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-model-stage", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-options-card", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-price-total", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-order-card footer button:first-child", agentStyles, StringComparison.Ordinal);
        Assert.Contains("-webkit-line-clamp: 2;", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-main--empty .qe-agent-composer-wrap", agentStyles, StringComparison.Ordinal);
        Assert.Contains("user-select: none;", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-composer .qe-agent-round-btn", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-composer.qe-agent-composer--multiline", agentStyles, StringComparison.Ordinal);
        Assert.Contains("border-radius: 28px;", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-send-spinner", agentStyles, StringComparison.Ordinal);
        Assert.Contains("@keyframes qe-agent-spin", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".sr-only", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-dictation-btn.active", agentStyles, StringComparison.Ordinal);
        Assert.Contains("@keyframes qe-agent-dictation-pulse", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-add-menu", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-add-backdrop", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-composer-attachments", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-composer-attachment-card", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-upload-progress", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-add-btn.active", agentStyles, StringComparison.Ordinal);
        Assert.Contains("scrollbar-gutter: stable;", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-use-cases button:focus-visible", agentStyles, StringComparison.Ordinal);
        Assert.Contains("box-shadow: 0 0 0 2px color-mix(in srgb, var(--qe-agent-primary) 28%, transparent);", agentStyles, StringComparison.Ordinal);
        Assert.Contains("top: 50%;", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-main--chat .qe-agent-thread", agentStyles, StringComparison.Ordinal);
        Assert.Contains("justify-content: flex-end;", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-project-row", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-pin-btn", agentStyles, StringComparison.Ordinal);
        Assert.Contains("opacity: 0;", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-empty-projects", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-signin-btn", agentStyles, StringComparison.Ordinal);
        Assert.DoesNotContain(".qe-agent-signup-btn", agentStyles, StringComparison.Ordinal);
        Assert.DoesNotContain(".qe-agent-auth-actions", agentStyles, StringComparison.Ordinal);
        Assert.Contains("white-space: nowrap;", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-status.is-warning span", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-status > span:first-child", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-status.is-failed span", agentStyles, StringComparison.Ordinal);
        Assert.Contains("field-sizing: content;", agentStyles, StringComparison.Ordinal);
        Assert.Contains("overflow-y: auto;", agentStyles, StringComparison.Ordinal);
        Assert.Contains("align-items: start;", agentStyles, StringComparison.Ordinal);
        Assert.Contains("font-size: 16px !important;", agentStyles, StringComparison.Ordinal);
        Assert.Contains("line-height: 1.5 !important;", agentStyles, StringComparison.Ordinal);
        Assert.Contains("--qe-agent-primary: #0a72ef;", agentStyles, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("background: var(--qe-agent-primary);", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-sketch-dialog", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-sketch-tools", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-sketch-actions", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-hidden-file", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-sketch-color", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-sketch-paper", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-sketch-canvas.is-eraser", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-sketch-trash-btn", agentStyles, StringComparison.Ordinal);
        Assert.Contains("border: 0 !important;", agentStyles, StringComparison.Ordinal);
        Assert.Contains("background: var(--qe-agent-bg);", agentStyles, StringComparison.Ordinal);
        Assert.Contains("border-right: 0;", agentStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("#0072f5", agentStyles, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("var(--accent", agentStyles, StringComparison.OrdinalIgnoreCase);

        var sketchScript = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-agent-sketch.js");
        Assert.Contains("function pressureFor(event)", sketchScript, StringComparison.Ordinal);
        Assert.Contains("event.pressure", sketchScript, StringComparison.Ordinal);
        Assert.Contains("quadraticCurveTo", sketchScript, StringComparison.Ordinal);
        Assert.Contains("drawSoftSegment", sketchScript, StringComparison.Ordinal);
        Assert.Contains("state.images", sketchScript, StringComparison.Ordinal);
        Assert.Contains("hitTestImage", sketchScript, StringComparison.Ordinal);
        Assert.Contains("imageResizeHandle", sketchScript, StringComparison.Ordinal);
        Assert.Contains("renderEraserIndicator", sketchScript, StringComparison.Ordinal);
        Assert.Contains("export async function insertSketchImage", sketchScript, StringComparison.Ordinal);
        Assert.Contains("export async function pasteSketchImage", sketchScript, StringComparison.Ordinal);
        Assert.Contains("export function setSketchEraser", sketchScript, StringComparison.Ordinal);
        Assert.Contains("window.addEventListener(\"paste\", paste)", sketchScript, StringComparison.Ordinal);
        Assert.Contains("window.removeEventListener(\"paste\", entry.paste)", sketchScript, StringComparison.Ordinal);
        Assert.Contains("export function setSketchBrushColor", sketchScript, StringComparison.Ordinal);
        Assert.Contains("SketchPreviewDataUrl", component, StringComparison.Ordinal);
        Assert.Contains("_lastSketchDataUrl", component, StringComparison.Ordinal);
        Assert.Contains("message.SketchPreviewDataUrl", component, StringComparison.Ordinal);
        Assert.Contains("QueueSketchComposerAttachment", component, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-agent-message-sketch-preview\"", component, StringComparison.Ordinal);
        Assert.DoesNotContain("Attached hand sketch:", component, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-message-attachments", agentStyles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-message-sketch-preview", agentStyles, StringComparison.Ordinal);
    }

    [Fact]
    public void QuoteUploadJs_does_not_steal_sketchboard_clipboard_images()
    {
        var uploadScript = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-upload.js");
        var sketchScript = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-agent-sketch.js");

        Assert.Contains("if (shouldIgnorePasteEvent(event))", uploadScript, StringComparison.Ordinal);
        Assert.Contains("function shouldIgnorePasteEvent(event)", uploadScript, StringComparison.Ordinal);
        Assert.Contains("event.defaultPrevented", uploadScript, StringComparison.Ordinal);
        Assert.Contains("event.target?.closest?.(\".qe-agent-sketch-dialog\")", uploadScript, StringComparison.Ordinal);
        Assert.Contains("window.addEventListener(\"paste\", paste)", sketchScript, StringComparison.Ordinal);
        Assert.Contains("await insertClipboardFile(entry, image);", sketchScript, StringComparison.Ordinal);
    }

    [Fact]
    public void QuoteAgentLaunchShell_disposes_sketch_canvas_when_component_is_disposed()
    {
        var component = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteAgent", "QuoteAgentLaunchShell.razor");
        var disposeBlock = ExtractSourceBlock(component, "public async ValueTask DisposeAsync()", "\n    }\n}");

        Assert.Contains("if (_sketchOpen)", disposeBlock, StringComparison.Ordinal);
        Assert.Contains("disposeSketchCanvas", disposeBlock, StringComparison.Ordinal);
        Assert.Contains("await _sketchModule.DisposeAsync();", disposeBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void QuoteAgentLaunchShell_surfaces_confirmation_cards_in_chat_when_artifacts_are_collapsed()
    {
        var component = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteAgent", "QuoteAgentLaunchShell.razor");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");
        var threadBlock = ExtractSourceBlock(component, "<div class=\"qe-agent-thread\"", "<section class=\"qe-agent-composer-wrap\"");
        var artifactDrawerBlock = ExtractSourceBlock(component, "<aside class=\"qe-agent-artifact-drawer\"", "</aside>");
        var confirmBlock = ExtractSourceBlock(component, "private async Task ConfirmActionAsync", "public Task ApplyAgentStateAsync");

        Assert.Contains("class=\"qe-agent-chat-actions\"", threadBlock, StringComparison.Ordinal);
        Assert.Contains("@if (_proposedActions.Count > 0)", threadBlock, StringComparison.Ordinal);
        Assert.Contains("@foreach (var action in _proposedActions)", threadBlock, StringComparison.Ordinal);
        Assert.Contains("ConfirmActionAsync(action)", threadBlock, StringComparison.Ordinal);
        Assert.Contains("action.RequiresAuthentication && !IsSignedIn", threadBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"qe-agent-chat-actions\"", artifactDrawerBlock, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-chat-actions", styles, StringComparison.Ordinal);
        Assert.Contains("var previousArtifactCount = _artifacts.Count;", confirmBlock, StringComparison.Ordinal);
        Assert.Contains("if (result.State.Artifacts.Count > previousArtifactCount)", confirmBlock, StringComparison.Ordinal);
        Assert.Contains("_artifactPanelOpen = true;", confirmBlock, StringComparison.Ordinal);
        Assert.Contains("_summaryPanelOpen = false;", confirmBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void QuoteAgentLaunchShell_previews_then_auto_sends_completed_attachments()
    {
        var component = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteAgent", "QuoteAgentLaunchShell.razor");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");
        var submitBlock = ExtractSourceBlock(component, "private async Task HandleSubmitAsync()", "private async Task FocusComposerAsync()");
        var uploadStartedBlock = ExtractSourceBlock(component, "public async Task NotifyUploadStartedAsync", "public async Task NotifyUploadCompletedAsync");
        var uploadCompletedBlock = ExtractSourceBlock(component, "public async Task NotifyUploadCompletedAsync", "private string UploadedPartStatus");
        var sketchBlock = ExtractSourceBlock(component, "private async Task AttachSketchAsync()", "private string RoleLabel");

        Assert.Contains("var pendingAttachments = BuildPendingMessageAttachments();", submitBlock, StringComparison.Ordinal);
        Assert.Contains("string.IsNullOrWhiteSpace(message) && pendingAttachments.Count == 0", submitBlock, StringComparison.Ordinal);
        Assert.Contains("new AgentMessageRow(\"user\", message, attachments: pendingAttachments)", submitBlock, StringComparison.Ordinal);
        Assert.Contains("Attachments = pendingAttachments.Select(attachment => attachment.ToAgentAttachment()).ToList()", submitBlock, StringComparison.Ordinal);
        Assert.Contains("_pendingComposerAttachments.Clear();", submitBlock, StringComparison.Ordinal);

        Assert.Contains("QueueComposerAttachment(part);", uploadStartedBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("_messages.Add", uploadStartedBlock, StringComparison.Ordinal);
        Assert.Contains("QueueComposerAttachment(part);", uploadCompletedBlock, StringComparison.Ordinal);
        Assert.Contains("await PreviewThenAutoSubmitPendingAttachmentsAsync();", uploadCompletedBlock, StringComparison.Ordinal);

        Assert.Contains("QueueSketchComposerAttachment", sketchBlock, StringComparison.Ordinal);
        Assert.Contains("await PreviewThenAutoSubmitPendingAttachmentsAsync();", sketchBlock, StringComparison.Ordinal);
        Assert.Contains("private async Task PreviewThenAutoSubmitPendingAttachmentsAsync()", component, StringComparison.Ordinal);
        Assert.Contains("await InvokeAsync(StateHasChanged);", component, StringComparison.Ordinal);
        Assert.Contains("await Task.Yield();", component, StringComparison.Ordinal);
        Assert.Contains("_pendingComposerAttachments.Count == 0", component, StringComparison.Ordinal);
        Assert.Contains("await HandleSubmitAsync();", component, StringComparison.Ordinal);

        Assert.Contains("message.Attachments.Count > 0", component, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-agent-message-attachments\"", component, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-message-attachments", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void QuoteWorkspace_PushesRegisteredUploadAgentStateIntoMakeStudioShell()
    {
        var workspace = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "QuoteWorkspace.razor");
        var shell = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteAgent", "QuoteAgentLaunchShell.razor");

        Assert.Contains("@ref=\"_agentShell\"", workspace, StringComparison.Ordinal);
        Assert.Contains("private QuoteAgentLaunchShell? _agentShell;", workspace, StringComparison.Ordinal);
        Assert.Contains("await _agentShell.ApplyAgentStateAsync(state);", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("agent registered the uploaded file", workspace, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("public Task ApplyAgentStateAsync(QuoteAgentStateResponse state)", shell, StringComparison.Ordinal);
        Assert.Contains("ApplyState(state);", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void QuoteAgentLaunchShell_RendersUploadedPartViewerInsideArtifactDrawer()
    {
        var shell = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteAgent", "QuoteAgentLaunchShell.razor");

        Assert.Contains("qe-agent-artifact-viewer", shell, StringComparison.Ordinal);
        Assert.Contains("<QePartViewer", shell, StringComparison.Ordinal);
        Assert.Contains("GlbUrl=\"@SelectedUploadedPart.GlbUrl\"", shell, StringComparison.Ordinal);
        Assert.Contains("BrowserFileClientId=\"@SelectedUploadedPart.ClientFileId\"", shell, StringComparison.Ordinal);
        Assert.Contains("ViewerSettings=\"@SelectedUploadedPart.ViewerSettings\"", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("<QePartDetailCard", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("<QePartsListPanel", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("<QePartConfigSidebar", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void QuoteAgentLaunchShell_RendersSupplementalUploadsAsContextArtifacts()
    {
        var shell = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteAgent", "QuoteAgentLaunchShell.razor");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");
        var drawer = ExtractSourceBlock(shell, "<aside class=\"qe-agent-artifact-drawer\"", "</aside>");

        Assert.Contains("@if (CanRenderUploadedPartViewer(SelectedUploadedPart))", drawer, StringComparison.Ordinal);
        Assert.Contains("qe-agent-artifact-context", drawer, StringComparison.Ordinal);
        Assert.Contains("SupplementalArtifactLabel(SelectedUploadedPart)", drawer, StringComparison.Ordinal);
        Assert.Contains("SupplementalArtifactHint(SelectedUploadedPart)", drawer, StringComparison.Ordinal);
        Assert.Contains("CanRenderUploadedPartViewer", shell, StringComparison.Ordinal);
        Assert.Contains("QuoteUploadConstraints.IsSupportedCadFileName(part.FileName)", shell, StringComparison.Ordinal);
        Assert.Contains("SelectedUploadedPart.ThumbnailUrl", drawer, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-artifact-context", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void QuoteAgentLaunchShell_RendersArtifactUrlActionsInArtifactDrawer()
    {
        var shell = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteAgent", "QuoteAgentLaunchShell.razor");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");
        var drawer = ExtractSourceBlock(shell, "<aside class=\"qe-agent-artifact-drawer\"", "</aside>");

        Assert.Contains("!string.IsNullOrWhiteSpace(artifact.Url)", drawer, StringComparison.Ordinal);
        Assert.Contains("href=\"@artifact.Url\"", drawer, StringComparison.Ordinal);
        Assert.Contains("target=\"_blank\"", drawer, StringComparison.Ordinal);
        Assert.Contains("rel=\"noopener noreferrer\"", drawer, StringComparison.Ordinal);
        Assert.Contains("ArtifactActionLabel(artifact)", drawer, StringComparison.Ordinal);
        Assert.Contains("private string ArtifactActionLabel(QuoteAgentArtifactDto artifact)", shell, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-artifact-link", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void QuoteAgentLaunchShell_RendersTrustedAuthHandoffMethodsFromAgentTurns()
    {
        var component = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteAgent", "QuoteAgentLaunchShell.razor");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");

        Assert.Contains("_authHandoff", component, StringComparison.Ordinal);
        Assert.Contains("streamEvent.Response.AuthHandoff", component, StringComparison.Ordinal);
        Assert.Contains("ApplyAuthHandoff", component, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-agent-auth-handoff\"", component, StringComparison.Ordinal);
        Assert.Contains("@foreach (var method in _authHandoff!.Methods)", component, StringComparison.Ordinal);
        Assert.Contains("method.DisplayName", component, StringComparison.Ordinal);
        Assert.Contains("AuthMethodIcon(method)", component, StringComparison.Ordinal);
        Assert.Contains("AuthMethodDescription(method)", component, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-auth-handoff", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-auth-methods", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Web_and_quote_engine_boundaries_are_documented()
    {
        var readme = ReadRepoFile("README.md");

        Assert.Contains("Maliev.Web", readme, StringComparison.Ordinal);
        Assert.Contains("Maliev.QuoteEngine", readme, StringComparison.Ordinal);
        Assert.Contains("browser never supplies a trusted customer id", readme, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Root_route_is_chat_workspace_and_auth_routes_redirect_to_web()
    {
        var program = ReadRepoFile("Maliev.QuoteEngine.Bff", "Program.cs");
        var authPath = RepoPath("Maliev.QuoteEngine.Bff", "Pages", "AuthPageRenderer.cs");
        var workspace = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "QuoteWorkspace.razor");
        var loader = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-engine-loader.js");
        var index = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "index.html");

        Assert.Contains("app.MapGet(\"/\", RenderClientAppAsync)", program, StringComparison.Ordinal);
        Assert.DoesNotContain("LandingPageRenderer.RenderAsync", program, StringComparison.Ordinal);
        Assert.Contains("@page \"/\"", workspace, StringComparison.Ordinal);
        Assert.Contains("@page \"/quote/new\"", workspace, StringComparison.Ordinal);
        Assert.Contains("<QuoteAgentLaunchShell", workspace, StringComparison.Ordinal);
        // Auth routes redirect to Maliev.Web — QuoteEngine has no own sign-in surface.
        Assert.Contains("app.MapGet(\"/auth/sign-in\"", program, StringComparison.Ordinal);
        Assert.Contains("app.MapGet(\"/auth/sign-up\"", program, StringComparison.Ordinal);
        Assert.Contains("RedirectToWebAuth", program, StringComparison.Ordinal);
        Assert.True(File.Exists(authPath), "The BFF should own server-rendered auth pages before the WASM fallback.");

        var auth = File.ReadAllText(authPath);
        Assert.Contains("class=\"auth-shell\"", auth, StringComparison.Ordinal);
        Assert.Contains("data-auth-appbar", auth, StringComparison.Ordinal);
        Assert.Contains("width: min(100%, 640px);", auth, StringComparison.Ordinal);
        Assert.Contains("padding: clamp(34px, 5vw, 56px);", auth, StringComparison.Ordinal);
        Assert.Contains("var formMode = isSignUp ? \"sign-up\" : \"sign-in\";", auth, StringComparison.Ordinal);
        Assert.Contains("class=\"auth-email-entry-form", auth, StringComparison.Ordinal);
        Assert.Contains("class=\"auth-credential-form", auth, StringComparison.Ordinal);
        Assert.Contains("data-auth-step", auth, StringComparison.Ordinal);
        Assert.Contains("data-auth-back", auth, StringComparison.Ordinal);
        Assert.Contains("Verify email address", auth, StringComparison.Ordinal);
        Assert.Contains("Password has at least 6 characters.", auth, StringComparison.Ordinal);
        Assert.Contains("const handoffKey = \"{{WorkspaceHandoffKey}}\";", auth, StringComparison.Ordinal);
        Assert.Contains("sessionStorage.setItem(handoffKey, \"true\")", auth, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"firstName\"", auth, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"lastName\"", auth, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"phone\"", auth, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"companyName\"", auth, StringComparison.Ordinal);
        Assert.DoesNotContain("data-wasm-entry", auth, StringComparison.Ordinal);
        Assert.DoesNotContain("_framework/blazor.webassembly.js", auth, StringComparison.Ordinal);
        Assert.DoesNotContain("MudBlazor", auth, StringComparison.Ordinal);

        Assert.Contains("sessionStorage.getItem(\"maliev.quote.workspace.handoff\")", loader, StringComparison.Ordinal);
        Assert.Contains("Starting your studio", loader, StringComparison.Ordinal);
        Assert.Contains("_framework/blazor.webassembly.js", index, StringComparison.Ordinal);
    }

    [Fact]
    public void QuoteWorkspace_imports_handoff_viewer_fields_for_canvas_rendering()
    {
        var workspace = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "QuoteWorkspace.razor");
        var importStart = workspace.IndexOf("private async Task ImportHandoffAsync", StringComparison.Ordinal);
        var selectStart = workspace.IndexOf("private void SelectPart", StringComparison.Ordinal);
        Assert.True(importStart >= 0, "QuoteWorkspace must retain an explicit handoff import method.");
        Assert.True(selectStart > importStart, "QuoteWorkspace handoff import method must precede part selection.");
        var importMethod = workspace[importStart..selectStart];

        Assert.Contains("GlbUrl = part.ViewerGlbUrl", importMethod, StringComparison.Ordinal);
        Assert.Contains("ViewerStoragePath = part.ViewerStoragePath", importMethod, StringComparison.Ordinal);
        Assert.Contains("ViewerFileExtension = NormalizeViewerFileExtension(part.ViewerFileExtension, part.ViewerStoragePath ?? part.StoragePath)", importMethod, StringComparison.Ordinal);
        Assert.Contains("ThumbnailUrl = part.ThumbnailUrl", importMethod, StringComparison.Ordinal);
        Assert.Contains("FileSizeBytes = part.FileSizeBytes", importMethod, StringComparison.Ordinal);
        Assert.Contains("ContentType = string.IsNullOrWhiteSpace(part.ContentType)", importMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void QuoteWorkspace_registers_web_handoff_uploads_with_agent_shell()
    {
        var workspace = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "QuoteWorkspace.razor");
        var importStart = workspace.IndexOf("private async Task ImportHandoffAsync", StringComparison.Ordinal);
        var selectStart = workspace.IndexOf("private void SelectPart", StringComparison.Ordinal);
        Assert.True(importStart >= 0, "QuoteWorkspace must retain an explicit handoff import method.");
        Assert.True(selectStart > importStart, "QuoteWorkspace handoff import method must precede part selection.");
        var importMethod = workspace[importStart..selectStart];

        Assert.Contains("await RegisterImportedHandoffPartWithAgentAsync(importedPart);", importMethod, StringComparison.Ordinal);
        Assert.Contains("await _agentShell.NotifyUploadCompletedAsync(importedPart);", importMethod, StringComparison.Ordinal);
        Assert.Contains("private async Task RegisterImportedHandoffPartWithAgentAsync(QuotePartViewModel part)", workspace, StringComparison.Ordinal);
        Assert.Contains("Message = $\"Customer imported {part.FileName} from Maliev.Web.\"", workspace, StringComparison.Ordinal);
        Assert.Contains("Kind = InferAgentAttachmentKind(part.FileName, contentType)", workspace, StringComparison.Ordinal);
        Assert.Contains("SatisfiesGeometryGate = QuoteUploadConstraints.IsSupportedCadFileName(part.FileName)", workspace, StringComparison.Ordinal);
        Assert.Contains("Url = part.GlbUrl ?? part.ThumbnailUrl", workspace, StringComparison.Ordinal);
        Assert.Contains("ContentType = contentType", workspace, StringComparison.Ordinal);
        Assert.Contains("FileSizeBytes = Math.Max(1, part.FileSizeBytes)", workspace, StringComparison.Ordinal);
        Assert.Contains("await _agentShell.ApplyAgentStateAsync(state);", workspace, StringComparison.Ordinal);
        Assert.Contains("private static string InferImportedContentType(string fileName)", workspace, StringComparison.Ordinal);
        Assert.Contains("application/step", workspace, StringComparison.Ordinal);
        Assert.Contains("application/pdf", workspace, StringComparison.Ordinal);
        Assert.Contains("image/png", workspace, StringComparison.Ordinal);
    }

    [Fact]
    public void Customer_auth_pages_follow_maliev_web_sign_in_pattern()
    {
        var signIn = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "SignIn.razor");
        var signUp = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "SignUp.razor");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");

        Assert.Contains("class=\"auth-shell\"", signIn, StringComparison.Ordinal);
        Assert.Contains("class=\"auth-title\"", signIn, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"auth-title-logo\"", signIn, StringComparison.Ordinal);
        Assert.DoesNotContain("<img class=\"auth-title-logo\"", signIn, StringComparison.Ordinal);
        Assert.Contains("class=\"auth-google\"", signIn, StringComparison.Ordinal);
        Assert.Contains("href=\"@GoogleHref\"", signIn, StringComparison.Ordinal);
        Assert.Contains("private string GoogleHref =>", signIn, StringComparison.Ordinal);
        Assert.DoesNotContain("ExchangeGoogleAsync", signIn, StringComparison.Ordinal);
        Assert.Contains("class=\"auth-email-entry-form", signIn, StringComparison.Ordinal);
        Assert.Contains("class=\"auth-credential-form", signIn, StringComparison.Ordinal);
        Assert.Contains("@onsubmit=\"ContinueWithEmail\"", signIn, StringComparison.Ordinal);
        Assert.Contains("@onclick=\"BackToEmailStep\"", signIn, StringComparison.Ordinal);
        Assert.Contains("PasswordRequirementClass", signIn, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"auth-email-panel\"", signIn, StringComparison.Ordinal);
        Assert.DoesNotContain("Use email instead", signIn, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=\"eyebrow\">Customer account</span>", signIn, StringComparison.Ordinal);
        Assert.DoesNotContain("<div class=\"auth-divider\">or use email</div>", signIn, StringComparison.Ordinal);

        Assert.Contains("class=\"auth-shell\"", signUp, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"auth-title-logo\"", signUp, StringComparison.Ordinal);
        Assert.DoesNotContain("<img class=\"auth-title-logo\"", signUp, StringComparison.Ordinal);
        Assert.Contains("class=\"auth-google\"", signUp, StringComparison.Ordinal);
        Assert.Contains("href=\"@GoogleHref\"", signUp, StringComparison.Ordinal);
        Assert.DoesNotContain("ExchangeGoogleAsync", signUp, StringComparison.Ordinal);
        Assert.Contains("class=\"auth-email-entry-form", signUp, StringComparison.Ordinal);
        Assert.Contains("class=\"auth-credential-form", signUp, StringComparison.Ordinal);
        Assert.Contains("@Text(\"Verify email address\", \"ยืนยันอีเมล\")", signUp, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"auth-email-panel\"", signUp, StringComparison.Ordinal);
        Assert.DoesNotContain("First name", signUp, StringComparison.Ordinal);
        Assert.DoesNotContain("Company", signUp, StringComparison.Ordinal);

        Assert.Contains(".auth-shell", styles, StringComparison.Ordinal);
        Assert.Contains(".auth-title-logo", styles, StringComparison.Ordinal);
        Assert.Contains(".auth-google", styles, StringComparison.Ordinal);
        Assert.Contains(".auth-email-entry-form", styles, StringComparison.Ordinal);
        Assert.Contains(".auth-credential-form", styles, StringComparison.Ordinal);
        Assert.Contains(".auth-requirement-list", styles, StringComparison.Ordinal);
        Assert.Contains(".auth-field-help", styles, StringComparison.Ordinal);
        Assert.Contains("width: min(100%, 640px);", styles, StringComparison.Ordinal);
        Assert.Contains("max-width: 640px;", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Customer_layout_uses_top_navigation_without_left_rail()
    {
        var layout = ReadRepoFile("Maliev.QuoteEngine.Client", "Layout", "MainLayout.razor");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");

        Assert.Contains("class=\"quote-topbar\"", layout, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"@Text(\"Customer quote navigation\"", layout, StringComparison.Ordinal);
        // Brand logo links to the landing page ("/"), not the workspace.
        Assert.Contains("class=\"quote-brand\" href=\"/\"", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"quote-brand\" href=\"/quote/new\"", layout, StringComparison.Ordinal);
        Assert.Contains("class=\"quote-brand-logo\"", layout, StringComparison.Ordinal);
        Assert.Contains("src=\"/images/logo.svg\"", layout, StringComparison.Ordinal);
        Assert.Contains("alt=\"MALIEV\"", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("https://www.maliev.com", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("quote-brand-mark", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("Quote Engine", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"web-link\"", layout, StringComparison.Ordinal);
        Assert.DoesNotContain(".web-link", styles, StringComparison.Ordinal);
        Assert.DoesNotContain(".quote-brand-mark", styles, StringComparison.Ordinal);
        // Topbar uses a max-width inner container; top-actions pushed right via margin-left: auto.
        Assert.Contains(".quote-topbar-inner", styles, StringComparison.Ordinal);
        Assert.Contains("margin-left: auto;", styles, StringComparison.Ordinal);
        Assert.Contains("class=\"quote-topbar-inner\"", layout, StringComparison.Ordinal);
        Assert.Contains("border-radius: var(--maliev-radius-tab);", styles, StringComparison.Ordinal);
        Assert.Contains(".quote-topnav a.active", styles, StringComparison.Ordinal);
        Assert.Contains("background: var(--primary);", styles, StringComparison.Ordinal);
        Assert.Contains(".quote-topnav a:focus-visible", styles, StringComparison.Ordinal);
        Assert.Contains("IsQuoteWorkspacePath", layout, StringComparison.Ordinal);
        Assert.Contains("\"/projects/new\"", layout, StringComparison.Ordinal);
        Assert.Contains("\"/quotes\"", layout, StringComparison.Ordinal);
        Assert.Contains("\"/quotes/new\"", layout, StringComparison.Ordinal);
        Assert.Contains("@inject QuoteEngineApiClient Api", layout, StringComparison.Ordinal);
        Assert.Contains("@if (_authStatus.IsSignedIn)", layout, StringComparison.Ordinal);
        Assert.Contains("<NavLink href=\"/quotes/new\"", layout, StringComparison.Ordinal);
        Assert.Contains("Text(\"New quote\", \"ใบเสนอราคาใหม่\")", layout, StringComparison.Ordinal);
        Assert.Contains("<NavLink href=\"/orders\"", layout, StringComparison.Ordinal);
        Assert.Contains("Text(\"Orders\", \"คำสั่งซื้อ\")", layout, StringComparison.Ordinal);
        Assert.Contains("<NavLink href=\"/documents\"", layout, StringComparison.Ordinal);
        Assert.Contains("Text(\"Documents\", \"เอกสาร\")", layout, StringComparison.Ordinal);
        Assert.Contains("<NavLink href=\"/profile\"", layout, StringComparison.Ordinal);
        Assert.Contains("Text(\"Account\", \"บัญชี\")", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("<NavLink href=\"/ndas\"", layout, StringComparison.Ordinal);
        Assert.Contains("<a class=\"quote-signin-btn\" href=\"/auth/sign-in\">@Text(\"Sign in\"", layout, StringComparison.Ordinal);
        Assert.Contains("class=\"billing-account-menu\"", layout, StringComparison.Ordinal);
        Assert.Contains("class=\"billing-account-trigger\" aria-label=\"@Text(\"Customer account and billing\"", layout, StringComparison.Ordinal);
        Assert.Contains("CustomerAvatarMarkup", layout, StringComparison.Ordinal);
        Assert.Contains("ProfileImageUrl", layout, StringComparison.Ordinal);
        Assert.Contains("AvatarInitials", layout, StringComparison.Ordinal);
        Assert.Contains("class=\"customer-data-section\"", layout, StringComparison.Ordinal);
        Assert.Contains("href=\"/profile\"", layout, StringComparison.Ordinal);
        Assert.Contains("href=\"/ndas\"", layout, StringComparison.Ordinal);
        Assert.Contains("href=\"/documents\"", layout, StringComparison.Ordinal);
        Assert.Contains("href=\"/preferences\"", layout, StringComparison.Ordinal);
        Assert.Contains("ToggleThemeAsync", layout, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"@Text(\"Toggle light or dark mode\"", layout, StringComparison.Ordinal);
        Assert.Contains("<CascadingValue Value=\"OpenAssistantCallback\" Name=\"OpenAssistant\">", layout, StringComparison.Ordinal);
        Assert.Contains("private Func<Task> OpenAssistantCallback => OpenAssistantDrawerAsync;", layout, StringComparison.Ordinal);
        Assert.Contains("private Task OpenAssistantDrawerAsync()", layout, StringComparison.Ordinal);
        Assert.Contains("Class=\"quote-currency-autocomplete\"", layout, StringComparison.Ordinal);
        Assert.Contains("PersistCurrencyAsync", layout, StringComparison.Ordinal);
        Assert.Contains("<CascadingValue Value=\"_isDarkMode\" Name=\"IsDarkMode\">", layout, StringComparison.Ordinal);
        Assert.Contains("class=\"billing-account-options\" role=\"listbox\" aria-label=\"@Text(\"Billing account\"", layout, StringComparison.Ordinal);
        Assert.Contains("Personal account", layout, StringComparison.Ordinal);
        Assert.Contains("Company account", layout, StringComparison.Ordinal);
        Assert.Contains("Manage account", layout, StringComparison.Ordinal);
        Assert.Contains("_profile = await Api.GetProfileAsync();", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("Start quote", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"primary-button\" href=\"/projects/new\"", layout, StringComparison.Ordinal);
        Assert.Contains("_authStatus = await Api.GetAuthStatusAsync();", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"app-rail\"", layout, StringComparison.Ordinal);
        Assert.DoesNotContain(".app-rail", styles, StringComparison.Ordinal);
        Assert.Contains(".customer-avatar", styles, StringComparison.Ordinal);
        Assert.Contains(".customer-data-section", styles, StringComparison.Ordinal);
        Assert.Contains(".quote-theme-toggle", styles, StringComparison.Ordinal);
        Assert.Contains(".quote-currency-autocomplete", styles, StringComparison.Ordinal);
        Assert.Contains(":root[data-maliev-theme=\"dark\"]", styles, StringComparison.Ordinal);
        Assert.Contains("--primary-contrast: #111111;", styles, StringComparison.Ordinal);
        Assert.Contains(":root[data-maliev-theme=\"dark\"] .quote-brand-logo", styles, StringComparison.Ordinal);
        Assert.Contains("filter: invert(1) brightness(1.08) contrast(0.96);", styles, StringComparison.Ordinal);
        Assert.Contains("color: var(--primary-contrast);", styles, StringComparison.Ordinal);
        Assert.Contains("--topbar-icon-ring: none;", styles, StringComparison.Ordinal);
        Assert.Contains("box-shadow: var(--topbar-icon-ring);", styles, StringComparison.Ordinal);
        Assert.Contains("flex: 0 0 40px;", styles, StringComparison.Ordinal);
        Assert.Contains("min-width: 40px;", styles, StringComparison.Ordinal);
        Assert.Contains("height: 40px;", styles, StringComparison.Ordinal);
        Assert.Contains("flex: 0 0 104px;", styles, StringComparison.Ordinal);
        Assert.Contains("max-width: 104px;", styles, StringComparison.Ordinal);
        Assert.Contains(".workspace--quote", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Currency_selectors_load_options_from_currency_service()
    {
        var layout = ReadRepoFile("Maliev.QuoteEngine.Client", "Layout", "MainLayout.razor");
        var preferences = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "Preferences.razor");
        var apiClient = ReadRepoFile("Maliev.QuoteEngine.Client", "Services", "QuoteEngineApiClient.cs");
        var program = ReadRepoFile("Maliev.QuoteEngine.Bff", "Program.cs");
        var currencyClient = ReadRepoFile("Maliev.QuoteEngine.Bff", "Clients", "CurrencyServiceClient.cs");
        var referenceController = ReadRepoFile("Maliev.QuoteEngine.Bff", "Controllers", "ReferenceDataController.cs");
        var referenceDtos = ReadRepoFile("Maliev.QuoteEngine.Shared", "ReferenceData", "ReferenceDataDtos.cs");

        Assert.DoesNotContain("SupportedCurrencies", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("SupportedCurrencies", preferences, StringComparison.Ordinal);
        Assert.Contains("SearchCurrenciesAsync", layout, StringComparison.Ordinal);
        Assert.Contains("@foreach (var currency in _currencyOptions)", preferences, StringComparison.Ordinal);
        Assert.Contains("await LoadCurrencyOptionsAsync()", layout, StringComparison.Ordinal);
        Assert.Contains("await LoadCurrencyOptionsAsync()", preferences, StringComparison.Ordinal);
        Assert.Contains("GetCurrenciesAsync", apiClient, StringComparison.Ordinal);
        Assert.Contains("quote/v1/reference-data/currencies", apiClient, StringComparison.Ordinal);
        Assert.Contains("AddAuthenticatedServiceClient<ICurrencyServiceClient, CurrencyServiceClient>(\"CurrencyService\")", program, StringComparison.Ordinal);
        Assert.Contains("/currency/v1/currencies?pageSize=1000&isActive=true", currencyClient, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"currencies\")]", referenceController, StringComparison.Ordinal);
        Assert.Contains("CurrencyOptionDto", referenceDtos, StringComparison.Ordinal);
    }

    [Fact]
    public void Preferences_page_persists_customer_quote_defaults_for_new_workspace_parts()
    {
        var preferences = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "Preferences.razor");
        var service = ReadRepoFile("Maliev.QuoteEngine.Client", "Services", "PreferenceService.cs");
        var workspace = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "QuoteWorkspace.razor");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");

        Assert.Contains("data-preferences-section=\"quote-defaults\"", preferences, StringComparison.Ordinal);
        Assert.Contains("Default manufacturing process", preferences, StringComparison.Ordinal);
        Assert.Contains("Default tolerance", preferences, StringComparison.Ordinal);
        Assert.Contains("Default inspection", preferences, StringComparison.Ordinal);
        Assert.Contains("Default delivery speed", preferences, StringComparison.Ordinal);
        Assert.Contains("QuoteDefaultProcessKey", preferences, StringComparison.Ordinal);
        Assert.Contains("QuoteDefaultToleranceKey", preferences, StringComparison.Ordinal);
        Assert.Contains("QuoteDefaultInspectionKey", preferences, StringComparison.Ordinal);
        Assert.Contains("QuoteDefaultLeadTimeKey", preferences, StringComparison.Ordinal);
        Assert.Contains("SetPreferenceAsync(QuoteDefaultProcessKey", preferences, StringComparison.Ordinal);
        Assert.Contains("GetPreferenceAsync(QuoteDefaultProcessKey", preferences, StringComparison.Ordinal);

        Assert.Contains("Task<string?> GetPreferenceAsync(string key)", service, StringComparison.Ordinal);
        Assert.Contains("Task SetPreferenceAsync(string key, string? value)", service, StringComparison.Ordinal);
        Assert.Contains("quoteEnginePreferences.getPreference", service, StringComparison.Ordinal);
        Assert.Contains("quoteEnginePreferences.setPreference", service, StringComparison.Ordinal);

        Assert.Contains("@inject PreferenceService PreferenceState", workspace, StringComparison.Ordinal);
        Assert.Contains("await PreferenceState.InitializeAsync();", workspace, StringComparison.Ordinal);
        Assert.Contains("LoadQuoteDefaultsAsync", workspace, StringComparison.Ordinal);
        Assert.Contains("ApplyCustomerQuoteDefaults(part);", workspace, StringComparison.Ordinal);
        Assert.Contains("NormalizeQuoteDefaultProcess", workspace, StringComparison.Ordinal);
        Assert.Contains("QuoteDefaultLeadTimeKey", workspace, StringComparison.Ordinal);
        Assert.Contains("part.LocalDfmRuntimeUnavailable = false;", workspace, StringComparison.Ordinal);

        Assert.Contains(".preferences-default-grid", styles, StringComparison.Ordinal);
        Assert.Contains(".preferences-select", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Profile_page_requires_signed_in_customer_session()
    {
        var profile = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "Profile.razor");

        Assert.Contains("_authStatus = await Api.GetAuthStatusAsync();", profile, StringComparison.Ordinal);
        Assert.Contains("if (!_authStatus.IsSignedIn)", profile, StringComparison.Ordinal);
        Assert.Contains("Navigation.NavigateTo(\"/auth/sign-in?returnUrl=/profile\"", profile, StringComparison.Ordinal);
        Assert.Contains("_profile = await Api.GetProfileAsync();", profile, StringComparison.Ordinal);
        Assert.Contains("_ndas = [.. await Api.GetNdasAsync()];", profile, StringComparison.Ordinal);
        Assert.Contains("_quotes = [.. await Api.GetQuotesAsync()];", profile, StringComparison.Ordinal);
        Assert.Contains("class=\"account-overview-card\"", profile, StringComparison.Ordinal);
        Assert.Contains("class=\"account-profile-card\"", profile, StringComparison.Ordinal);
        Assert.Contains("class=\"account-action-grid\"", profile, StringComparison.Ordinal);
        Assert.Contains("class=\"profile-quote-history\"", profile, StringComparison.Ordinal);
        Assert.Contains("CustomerQuoteSummaryDto", profile, StringComparison.Ordinal);
        Assert.Contains("href=\"/quotes/@quote.QuoteId\"", profile, StringComparison.Ordinal);
        Assert.Contains("quote.QuoteNumber", profile, StringComparison.Ordinal);
        Assert.Contains("FormatMoney(quote.Total, quote.Currency)", profile, StringComparison.Ordinal);
        Assert.Contains("ProfileImageUrl", profile, StringComparison.Ordinal);
        Assert.Contains("ProfileInitials", profile, StringComparison.Ordinal);
        Assert.Contains("NdaStatus", profile, StringComparison.Ordinal);
        Assert.Contains("NdaExpiryNotice", profile, StringComparison.Ordinal);
        Assert.Contains("href=\"/ndas\"", profile, StringComparison.Ordinal);
        Assert.Contains("href=\"/documents\"", profile, StringComparison.Ordinal);
        Assert.Contains("href=\"/orders\"", profile, StringComparison.Ordinal);
        Assert.Contains("href=\"/preferences\"", profile, StringComparison.Ordinal);
    }

    [Fact]
    public void Ndas_page_exposes_customer_confidentiality_status_and_actions()
    {
        var ndas = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "Ndas.razor");
        var apiClient = ReadRepoFile("Maliev.QuoteEngine.Client", "Services", "QuoteEngineApiClient.cs");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");

        Assert.Contains("@page \"/ndas\"", ndas, StringComparison.Ordinal);
        Assert.Contains("GetNdasAsync", ndas, StringComparison.Ordinal);
        Assert.Contains("CustomerNdaDto", ndas, StringComparison.Ordinal);
        Assert.Contains("ActiveNdas", ndas, StringComparison.Ordinal);
        Assert.Contains("ExpiringNdas", ndas, StringComparison.Ordinal);
        Assert.Contains("NdaStatusClass", ndas, StringComparison.Ordinal);
        Assert.Contains("data-nda-section=\"summary\"", ndas, StringComparison.Ordinal);
        Assert.Contains("data-nda-section=\"documents\"", ndas, StringComparison.Ordinal);
        Assert.Contains("href=\"/documents?kind=Requirement\"", ndas, StringComparison.Ordinal);
        Assert.Contains("href=\"/quotes/new\"", ndas, StringComparison.Ordinal);
        Assert.Contains("NdaDateLine(nda)", ndas, StringComparison.Ordinal);

        Assert.Contains("GetNdasAsync", apiClient, StringComparison.Ordinal);
        Assert.Contains(".nda-dashboard", styles, StringComparison.Ordinal);
        Assert.Contains(".nda-summary-grid", styles, StringComparison.Ordinal);
        Assert.Contains(".nda-status-pill", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Order_detail_page_is_customer_manufacturing_progress_surface()
    {
        var orders = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "Orders.razor");
        var detail = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "OrderDetail.razor");
        var apiClient = ReadRepoFile("Maliev.QuoteEngine.Client", "Services", "QuoteEngineApiClient.cs");
        var accountController = ReadRepoFile("Maliev.QuoteEngine.Bff", "Controllers", "AccountController.cs");
        var accountDtos = ReadRepoFile("Maliev.QuoteEngine.Shared", "Account", "AccountDtos.cs");
        var orderServiceClient = ReadRepoFile("Maliev.QuoteEngine.Bff", "Clients", "OrderServiceClient.cs");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");

        Assert.Contains("href=\"/orders/@Uri.EscapeDataString(order.OrderNumber)\"", orders, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/orders/@order.OrderId\"", orders, StringComparison.Ordinal);

        Assert.Contains("@page \"/orders/{OrderNumber}\"", detail, StringComparison.Ordinal);
        Assert.Contains("@inject QuoteEngineApiClient Api", detail, StringComparison.Ordinal);
        Assert.Contains("@inject NavigationManager Navigation", detail, StringComparison.Ordinal);
        Assert.Contains("GetOrderDetailAsync(OrderNumber)", detail, StringComparison.Ordinal);
        Assert.Contains("GetDocumentsAsync", detail, StringComparison.Ordinal);
        Assert.Contains("OrderDocuments", detail, StringComparison.Ordinal);
        Assert.Contains("data-order-section=\"documents\"", detail, StringComparison.Ordinal);
        Assert.Contains("CustomerDocumentDto", detail, StringComparison.Ordinal);
        Assert.Contains("document.OrderNumber", detail, StringComparison.Ordinal);
        Assert.Contains("document.Kind", detail, StringComparison.Ordinal);
        Assert.Contains("document.FileName", detail, StringComparison.Ordinal);
        Assert.Contains("OpenDocumentAsync", detail, StringComparison.Ordinal);
        Assert.Contains("GetDocumentDownloadAsync(document.DocumentId)", detail, StringComparison.Ordinal);
        Assert.Contains("Navigation.NavigateTo(download.DownloadUrl, forceLoad: true)", detail, StringComparison.Ordinal);
        Assert.Contains("IAsyncDisposable", detail, StringComparison.Ordinal);
        Assert.Contains("HubConnectionBuilder", detail, StringComparison.Ordinal);
        Assert.Contains(".WithUrl(Navigation.ToAbsoluteUri(\"/hubs/quote-notifications\"))", detail, StringComparison.Ordinal);
        Assert.Contains("JoinOrderGroup", detail, StringComparison.Ordinal);
        Assert.Contains("LeaveOrderGroup", detail, StringComparison.Ordinal);
        Assert.Contains("OrderStatusChanged", detail, StringComparison.Ordinal);
        Assert.Contains("PaymentCompleted", detail, StringComparison.Ordinal);
        Assert.Contains("PaymentPending", detail, StringComparison.Ordinal);
        Assert.Contains("PaymentFailed", detail, StringComparison.Ordinal);
        Assert.Contains("PaymentCancelled", detail, StringComparison.Ordinal);
        Assert.Contains("PaymentExpired", detail, StringComparison.Ordinal);
        Assert.Contains("QeOrderStatusChangedPayload", detail, StringComparison.Ordinal);
        Assert.Contains("QePaymentCompletedPayload", detail, StringComparison.Ordinal);
        Assert.Contains("QePaymentPendingPayload", detail, StringComparison.Ordinal);
        Assert.Contains("QePaymentFailedPayload", detail, StringComparison.Ordinal);
        Assert.Contains("QePaymentCancelledPayload", detail, StringComparison.Ordinal);
        Assert.Contains("QePaymentExpiredPayload", detail, StringComparison.Ordinal);
        Assert.Contains("ApplyOrderStatusChanged", detail, StringComparison.Ordinal);
        Assert.Contains("ApplyPaymentCompleted", detail, StringComparison.Ordinal);
        Assert.Contains("ApplyPaymentPending", detail, StringComparison.Ordinal);
        Assert.Contains("ApplyPaymentFailed", detail, StringComparison.Ordinal);
        Assert.Contains("ApplyPaymentCancelled", detail, StringComparison.Ordinal);
        Assert.Contains("ApplyPaymentExpired", detail, StringComparison.Ordinal);
        Assert.Contains("InitiatePaymentAsync", detail, StringComparison.Ordinal);
        Assert.Contains("new InitiatePaymentRequest", detail, StringComparison.Ordinal);
        Assert.Contains("OrderId = _order.OrderId", detail, StringComparison.Ordinal);
        Assert.Contains("OrderNumber = _order.OrderNumber", detail, StringComparison.Ordinal);
        Assert.Contains("Amount = _order.QuotedAmount.Value", detail, StringComparison.Ordinal);
        Assert.Contains("Currency = _order.QuoteCurrency ?? \"THB\"", detail, StringComparison.Ordinal);
        Assert.Contains("BillingCompanyName = BillingCompanyName", detail, StringComparison.Ordinal);
        Assert.Contains("BillingVatNumber = BillingVatNumber", detail, StringComparison.Ordinal);
        Assert.Contains("Navigation.NavigateTo(payment.PaymentUrl, forceLoad: true)", detail, StringComparison.Ordinal);
        Assert.Contains("catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)", detail, StringComparison.Ordinal);
        Assert.Contains("Navigation.NavigateTo($\"/auth/sign-in?returnUrl={Uri.EscapeDataString($\"/orders/{_order.OrderNumber}\")}\"", detail, StringComparison.Ordinal);
        Assert.Contains("CanPay", detail, StringComparison.Ordinal);
        Assert.Contains("class=\"order-detail-shell\"", detail, StringComparison.Ordinal);
        Assert.Contains("data-order-section=\"payment\"", detail, StringComparison.Ordinal);
        Assert.Contains("data-order-section=\"purchase-order\"", detail, StringComparison.Ordinal);
        Assert.Contains("data-order-section=\"manufacturing-progress\"", detail, StringComparison.Ordinal);
        Assert.Contains("data-order-section=\"delivery\"", detail, StringComparison.Ordinal);
        Assert.Contains("data-order-section=\"timeline\"", detail, StringComparison.Ordinal);
        Assert.Contains("CustomerPoNumber", detail, StringComparison.Ordinal);
        Assert.Contains("StatusHistory", detail, StringComparison.Ordinal);
        Assert.Contains("ManufacturingMilestones", detail, StringComparison.Ordinal);
        Assert.Contains("CustomerManufacturingMilestoneDto", accountDtos, StringComparison.Ordinal);
        Assert.Contains("BuildCustomerManufacturingMilestones", orderServiceClient, StringComparison.Ordinal);
        Assert.Contains("PromisedDeliveryDate", detail, StringComparison.Ordinal);
        Assert.Contains("ActualDeliveryDate", detail, StringComparison.Ordinal);
        Assert.Contains("DeliveryStatusText", detail, StringComparison.Ordinal);
        Assert.Contains("DeliveryTrackingText", detail, StringComparison.Ordinal);
        Assert.Contains("ManufacturingProgressPercent", detail, StringComparison.Ordinal);
        Assert.Contains("milestone.State", detail, StringComparison.Ordinal);
        Assert.Contains("MilestoneTimestamp", detail, StringComparison.Ordinal);
        Assert.Contains("DocumentHref(\"PurchaseOrder\")", detail, StringComparison.Ordinal);
        Assert.Contains("DocumentHref(\"Receipt\")", detail, StringComparison.Ordinal);
        Assert.Contains("DocumentHref(\"Invoice\")", detail, StringComparison.Ordinal);
        Assert.Contains("kind={Uri.EscapeDataString(kind)}&orderNumber={Uri.EscapeDataString(_order.OrderNumber)}", detail, StringComparison.Ordinal);

        Assert.Contains("GetOrderDetailAsync", apiClient, StringComparison.Ordinal);
        Assert.Contains("GetDocumentsAsync", apiClient, StringComparison.Ordinal);
        Assert.Contains("quote/v1/account/orders/{Uri.EscapeDataString(orderNumber)}", apiClient, StringComparison.Ordinal);
        Assert.Contains("TryResolveCustomerId(out var customerId)", accountController, StringComparison.Ordinal);
        Assert.Contains("GetByCustomerAsync(customerId.ToString(\"D\")", accountController, StringComparison.Ordinal);
        Assert.Contains("string.Equals(order.OrderNumber, orderNumber, StringComparison.OrdinalIgnoreCase)", accountController, StringComparison.Ordinal);
        Assert.Contains("return NotFound();", accountController, StringComparison.Ordinal);

        Assert.Contains(".order-detail-shell", styles, StringComparison.Ordinal);
        Assert.Contains(".order-progress-track", styles, StringComparison.Ordinal);
        Assert.Contains(".order-milestone-list", styles, StringComparison.Ordinal);
        Assert.Contains(".order-milestone-current", styles, StringComparison.Ordinal);
        Assert.Contains(".order-documents-list", styles, StringComparison.Ordinal);
        Assert.Contains(".order-delivery-list", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Orders_page_groups_active_and_completed_customer_orders()
    {
        var orders = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "Orders.razor");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");

        Assert.Contains("@page \"/orders\"", orders, StringComparison.Ordinal);
        Assert.Contains("GetOrdersAsync", orders, StringComparison.Ordinal);
        Assert.Contains("ActiveOrders", orders, StringComparison.Ordinal);
        Assert.Contains("CompletedOrders", orders, StringComparison.Ordinal);
        Assert.Contains("IsCompletedOrder", orders, StringComparison.Ordinal);
        Assert.Contains("class=\"orders-dashboard\"", orders, StringComparison.Ordinal);
        Assert.Contains("data-orders-section=\"active\"", orders, StringComparison.Ordinal);
        Assert.Contains("data-orders-section=\"completed\"", orders, StringComparison.Ordinal);
        Assert.Contains("FormatDate(order.UpdatedAt)", orders, StringComparison.Ordinal);
        Assert.Contains("href=\"/orders/@Uri.EscapeDataString(order.OrderNumber)\"", orders, StringComparison.Ordinal);

        Assert.Contains(".orders-dashboard", styles, StringComparison.Ordinal);
        Assert.Contains(".orders-summary-grid", styles, StringComparison.Ordinal);
        Assert.Contains(".orders-section", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Documents_page_supports_customer_purchase_order_invoice_receipt_uploads()
    {
        var documents = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "Documents.razor");
        var apiClient = ReadRepoFile("Maliev.QuoteEngine.Client", "Services", "QuoteEngineApiClient.cs");
        var accountController = ReadRepoFile("Maliev.QuoteEngine.Bff", "Controllers", "AccountController.cs");
        var accountDtos = ReadRepoFile("Maliev.QuoteEngine.Shared", "Account", "AccountDtos.cs");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");

        Assert.Contains("InputFile", documents, StringComparison.Ordinal);
        Assert.Contains("@inject NavigationManager Navigation", documents, StringComparison.Ordinal);
        Assert.Contains("UploadDocumentAsync", documents, StringComparison.Ordinal);
        Assert.Contains("UploadDocumentFileAsync(file, _selectedKind, NormalizeOrderNumber(_orderNumber))", documents, StringComparison.Ordinal);
        Assert.Contains("ApplyDocumentRouteContext", documents, StringComparison.Ordinal);
        Assert.Contains("VisibleDocuments", documents, StringComparison.Ordinal);
        Assert.Contains("HasOrderContext", documents, StringComparison.Ordinal);
        Assert.Contains("ClearOrderContext", documents, StringComparison.Ordinal);
        Assert.Contains("data-documents-section=\"order-context\"", documents, StringComparison.Ordinal);
        Assert.Contains("data-documents-section=\"library-filters\"", documents, StringComparison.Ordinal);
        Assert.Contains("LibraryKindFilters", documents, StringComparison.Ordinal);
        Assert.Contains("_libraryKindFilter", documents, StringComparison.Ordinal);
        Assert.Contains("SetLibraryKindFilter", documents, StringComparison.Ordinal);
        Assert.Contains("VisibleDocuments => _documents", documents, StringComparison.Ordinal);
        Assert.Contains(".Where(MatchesDocumentContext)", documents, StringComparison.Ordinal);
        Assert.Contains(".Where(MatchesDocumentKindFilter)", documents, StringComparison.Ordinal);
        Assert.Contains("private bool MatchesDocumentContext(CustomerDocumentDto document)", documents, StringComparison.Ordinal);
        Assert.Contains("private bool MatchesDocumentKindFilter(CustomerDocumentDto document)", documents, StringComparison.Ordinal);
        Assert.Contains("document.OrderNumber", documents, StringComparison.Ordinal);
        Assert.Contains("OpenDocumentAsync", documents, StringComparison.Ordinal);
        Assert.Contains("GetDocumentDownloadAsync(document.DocumentId)", documents, StringComparison.Ordinal);
        Assert.Contains("Navigation.NavigateTo(download.DownloadUrl, forceLoad: true)", documents, StringComparison.Ordinal);
        Assert.Contains("PurchaseOrder", documents, StringComparison.Ordinal);
        Assert.Contains("Invoice", documents, StringComparison.Ordinal);
        Assert.Contains("Receipt", documents, StringComparison.Ordinal);
        Assert.Contains("class=\"documents-upload-panel\"", documents, StringComparison.Ordinal);
        Assert.Contains("class=\"documents-kind-grid\"", documents, StringComparison.Ordinal);
        Assert.Contains("class=\"documents-filter-chip", documents, StringComparison.Ordinal);
        Assert.Contains("class=\"documents-order-field\"", documents, StringComparison.Ordinal);

        Assert.Contains("UploadDocumentAsync", apiClient, StringComparison.Ordinal);
        Assert.Contains("UploadDocumentFileAsync", apiClient, StringComparison.Ordinal);
        Assert.Contains("MultipartFormDataContent", apiClient, StringComparison.Ordinal);
        Assert.Contains("quote/v1/account/documents/upload", apiClient, StringComparison.Ordinal);
        Assert.Contains("GetDocumentDownloadAsync", apiClient, StringComparison.Ordinal);
        Assert.Contains("quote/v1/account/documents/{documentId:D}/download", apiClient, StringComparison.Ordinal);
        Assert.Contains("quote/v1/account/documents", apiClient, StringComparison.Ordinal);
        Assert.Contains("[HttpPost(\"documents\")]", accountController, StringComparison.Ordinal);
        Assert.Contains("[HttpPost(\"documents/upload\")]", accountController, StringComparison.Ordinal);
        Assert.Contains("InitiateResumableUploadAsync", accountController, StringComparison.Ordinal);
        Assert.Contains("StreamUploadAsync", accountController, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"documents/{documentId:guid}/download\")]", accountController, StringComparison.Ordinal);
        Assert.Contains("GetDownloadUrlByPathAsync", accountController, StringComparison.Ordinal);
        Assert.Contains("UploadDocument(customerId", accountController, StringComparison.Ordinal);
        Assert.Contains("public sealed record CustomerDocumentDownloadResponse", accountDtos, StringComparison.Ordinal);
        Assert.Contains("public sealed class CustomerDocumentUploadRequest", accountDtos, StringComparison.Ordinal);
        Assert.Contains("public sealed record CustomerDocumentDto", accountDtos, StringComparison.Ordinal);
        Assert.Contains("string? OrderNumber", accountDtos, StringComparison.Ordinal);

        Assert.Contains(".documents-upload-panel", styles, StringComparison.Ordinal);
        Assert.Contains(".documents-kind-grid", styles, StringComparison.Ordinal);
        Assert.Contains(".documents-filter-bar", styles, StringComparison.Ordinal);
        Assert.Contains(".documents-filter-chip", styles, StringComparison.Ordinal);
        Assert.Contains(".documents-order-field", styles, StringComparison.Ordinal);
        Assert.Contains(".documents-order-context", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Payment_return_pages_route_customers_back_to_order_detail()
    {
        var success = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "PaymentSuccess.razor");
        var cancel = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "PaymentCancel.razor");
        var quoteController = ReadRepoFile("Maliev.QuoteEngine.Bff", "Controllers", "QuoteController.cs");

        Assert.Contains("@page \"/payment/success\"", success, StringComparison.Ordinal);
        Assert.Contains("@inject NavigationManager Navigation", success, StringComparison.Ordinal);
        Assert.Contains("GetOrderNumberFromQuery", success, StringComparison.Ordinal);
        Assert.Contains("href=\"@OrderDetailHref\"", success, StringComparison.Ordinal);
        Assert.Contains("Payment received", success, StringComparison.Ordinal);

        Assert.Contains("@page \"/payment/cancel\"", cancel, StringComparison.Ordinal);
        Assert.Contains("@inject NavigationManager Navigation", cancel, StringComparison.Ordinal);
        Assert.Contains("GetOrderNumberFromQuery", cancel, StringComparison.Ordinal);
        Assert.Contains("href=\"@OrderDetailHref\"", cancel, StringComparison.Ordinal);
        Assert.Contains("Payment was not completed", cancel, StringComparison.Ordinal);

        Assert.Contains("/payment/success?orderId={Uri.EscapeDataString(request.OrderNumber)}", quoteController, StringComparison.Ordinal);
        Assert.Contains("/payment/cancel?orderId={Uri.EscapeDataString(request.OrderNumber)}", quoteController, StringComparison.Ordinal);
    }

    [Fact]
    public void Quote_detail_page_loads_customer_quote_summary_and_pdf_actions()
    {
        var quoteDetail = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "Quotes.razor");
        var apiClient = ReadRepoFile("Maliev.QuoteEngine.Client", "Services", "QuoteEngineApiClient.cs");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");

        Assert.Contains("@page \"/quotes/{QuoteId:guid}\"", quoteDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("@page \"/quotes\"", quoteDetail, StringComparison.Ordinal);
        Assert.Contains("@inject QuoteEngineApiClient Api", quoteDetail, StringComparison.Ordinal);
        Assert.Contains("GetQuotesAsync", quoteDetail, StringComparison.Ordinal);
        Assert.Contains("CustomerQuoteSummaryDto", quoteDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("_quotes = [.. quotes]", quoteDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("IsHistoryRoute", quoteDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("data-quote-section=\"history\"", quoteDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("quote-history-list", quoteDetail, StringComparison.Ordinal);
        Assert.Contains("_quote.QuoteNumber", quoteDetail, StringComparison.Ordinal);
        Assert.Contains("_quote = quotes.FirstOrDefault", quoteDetail, StringComparison.Ordinal);
        Assert.Contains("data-quote-section=\"summary\"", quoteDetail, StringComparison.Ordinal);
        Assert.Contains("data-quote-section=\"actions\"", quoteDetail, StringComparison.Ordinal);
        Assert.Contains("@_quote.QuoteNumber", quoteDetail, StringComparison.Ordinal);
        Assert.Contains("@_quote.Status", quoteDetail, StringComparison.Ordinal);
        Assert.Contains("FormatMoney(_quote.Total, _quote.Currency)", quoteDetail, StringComparison.Ordinal);
        Assert.Contains("href=\"@_quote.PdfUrl\"", quoteDetail, StringComparison.Ordinal);
        Assert.Contains("ApproveQuoteAsync", quoteDetail, StringComparison.Ordinal);
        Assert.Contains("CreateOrderAsync", quoteDetail, StringComparison.Ordinal);
        Assert.Contains("CanApproveQuote", quoteDetail, StringComparison.Ordinal);
        Assert.Contains("CanCreateOrder", quoteDetail, StringComparison.Ordinal);
        Assert.Contains("new CreateManufacturingOrderRequest(_quote.QuoteId", quoteDetail, StringComparison.Ordinal);
        Assert.Contains("Navigation.NavigateTo($\"/orders/{Uri.EscapeDataString(order.OrderNumber)}\")", quoteDetail, StringComparison.Ordinal);
        Assert.Contains("data-quote-section=\"conversion\"", quoteDetail, StringComparison.Ordinal);
        Assert.Contains("Start another quote", quoteDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("ready to connect to QuotationService", quoteDetail, StringComparison.Ordinal);

        Assert.Contains("GetQuotesAsync", apiClient, StringComparison.Ordinal);
        Assert.Contains("ApproveQuoteAsync", apiClient, StringComparison.Ordinal);
        Assert.Contains("CreateOrderAsync", apiClient, StringComparison.Ordinal);
        Assert.Contains(".quote-detail-shell", styles, StringComparison.Ordinal);
        Assert.Contains(".quote-detail-actions", styles, StringComparison.Ordinal);
        Assert.Contains(".quote-conversion-card", styles, StringComparison.Ordinal);
        Assert.Contains(".quote-history-list", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Quote_workspace_is_single_viewport_application_shell()
    {
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");

        Assert.Contains("height: 100dvh;", styles, StringComparison.Ordinal);
        Assert.Contains("overflow-x: clip;", styles, StringComparison.Ordinal);
        Assert.Contains(".workspace--quote {\n    flex: 1 1 0;\n    display: flex;\n    flex-direction: column;", styles, StringComparison.Ordinal);
        Assert.Contains(".quote-workspace-host {\n    flex: 1 1 0;\n    min-height: 0;", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("height: 100vh;", styles, StringComparison.Ordinal);
        Assert.Contains(".quote-chat-backdrop", styles, StringComparison.Ordinal);
        Assert.Contains(".quote-chat-drawer", styles, StringComparison.Ordinal);
        Assert.Contains("position: fixed;", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-qsb-root", styles, StringComparison.Ordinal);
        Assert.Contains("max-height: 99px;", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-qsb-customer", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-qsb-lead {\n    flex: 1 1 auto;", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-qsb-divider {\n    flex: 0 0 1px;\n    width: 1px;\n    background: var(--maliev-border);\n}", styles, StringComparison.Ordinal);
        Assert.DoesNotContain(".qe-qsb-divider {\n    width: 1px;\n    box-shadow: var(--maliev-shadow-ring);\n}", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Quote_workspace_uses_projectnew_parity_component_shell()
    {
        var workspace = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "QuoteWorkspace.razor");
        var agentShell = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteAgent", "QuoteAgentLaunchShell.razor");
        var detailCard = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartDetailCard.razor");
        var quoteController = ReadRepoFile("Maliev.QuoteEngine.Bff", "Controllers", "QuoteController.cs");
        var quoteDtos = ReadRepoFile("Maliev.QuoteEngine.Shared", "Quotes", "QuoteEngineDtos.cs");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");

        Assert.DoesNotContain("<QePartsListPanel", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("<QePartDetailCard", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("<QePartConfigSidebar", workspace, StringComparison.Ordinal);
        Assert.Contains("<QuoteAgentLaunchShell", workspace, StringComparison.Ordinal);
        Assert.Contains("UploadedParts=\"@_parts\"", workspace, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-agent-upload-strip\"", agentShell, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-agent-artifact-section\"", agentShell, StringComparison.Ordinal);
        Assert.Contains("UploadedPartStatus", agentShell, StringComparison.Ordinal);
        Assert.Contains("UploadedPartIcon", agentShell, StringComparison.Ordinal);
        Assert.DoesNotContain("<QeQuoteSummaryBar", workspace, StringComparison.Ordinal);
        Assert.Contains("QeDrawingAttachmentsTab", detailCard, StringComparison.Ordinal);
        Assert.Contains("3D Model", detailCard, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"DFM Analysis\"", detailCard, StringComparison.Ordinal);
        Assert.DoesNotContain("qe-pn-mobile-toolbar", workspace, StringComparison.Ordinal);
        Assert.Contains("_isPartsDrawerOpen", workspace, StringComparison.Ordinal);
        Assert.Contains("_centerMode", workspace, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"@Text(\"Quantity\"", ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartConfigSidebar.razor"), StringComparison.Ordinal);
        Assert.Contains("Parts = _parts.Select(x => x.ToDraft()).ToArray()", workspace, StringComparison.Ordinal);
        Assert.Contains("BuildOrderRequirements", quoteController, StringComparison.Ordinal);
        Assert.Contains("BuildConfiguredPartSummary", quoteController, StringComparison.Ordinal);
        Assert.Contains("AllDfmIssuesAcknowledged", workspace, StringComparison.Ordinal);
        Assert.Contains("CanRequestFormalQuote => _estimate is not null && IsSignedIn && !IsDemoMode && AllDfmIssuesAcknowledged", workspace, StringComparison.Ordinal);
        Assert.Contains("CanCreateOrder => _formalQuote is not null && IsSignedIn && !IsDemoMode && AllDfmIssuesAcknowledged", workspace, StringComparison.Ordinal);
        Assert.Contains("IReadOnlyList<QuotePartDraftDto> Parts", quoteDtos, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=\"qe-zone-label\">Bill To</span>", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("CustomerPicker", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("internal pricing override", workspace, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("flex: 0 0 270px;", styles, StringComparison.Ordinal);
        Assert.Contains("flex: 0 0 380px;", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pn-mobile-toolbar", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pn-parts-drawer", styles, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 1200px)", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Customer_assistant_drawer_is_integrated_into_quote_layout()
    {
        var layout = ReadRepoFile("Maliev.QuoteEngine.Client", "Layout", "MainLayout.razor");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");
        var index = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "index.html");
        var authComplete = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "AuthChatbotComplete.razor");

        Assert.Contains("CustomerAssistantDrawer", layout, StringComparison.Ordinal);
        Assert.Contains("topbar-chat-toggle", layout, StringComparison.Ordinal);
        Assert.Contains("class=\"topbar-chat-icon\"", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("Icon=\"@Icons.Material.Outlined.Chat\"", layout, StringComparison.Ordinal);
        Assert.Contains("quote-chat-backdrop", layout, StringComparison.Ordinal);
        Assert.Contains("role=\"dialog\"", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("MudDrawer", layout, StringComparison.Ordinal);
        Assert.Contains("chatbot-open", styles, StringComparison.Ordinal);
        Assert.Contains("js/maliev-chatbot.js", index, StringComparison.Ordinal);
        Assert.Contains("@page \"/auth/chatbot-complete\"", authComplete, StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_loader_tells_make_studio_story_with_progress_contract()
    {
        var index = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "index.html");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");
        var loaderScript = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-engine-loader.js");

        // The startup screen is the Make Studio narrative — no MALIEV logo in it.
        Assert.Contains("id=\"quote-startup\"", index, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"ms-tag\"", index, StringComparison.Ordinal);
        Assert.Contains("class=\"ms-beat\"", index, StringComparison.Ordinal);
        Assert.Contains("data-ms-beat=\"4\"", index, StringComparison.Ordinal);
        Assert.Contains("class=\"ms-wordmark\"", index, StringComparison.Ordinal);
        Assert.Contains("Make Studio", index, StringComparison.Ordinal);
        Assert.Contains("Describe what you want to make. <strong>Make&nbsp;Studio</strong> quotes&nbsp;it, reviews&nbsp;it, and orders&nbsp;it in minutes.", index, StringComparison.Ordinal);
        Assert.DoesNotContain("Describe what you want to make&nbsp;— Make&nbsp;Studio", index, StringComparison.Ordinal);
        Assert.Contains("role=\"progressbar\"", index, StringComparison.Ordinal);
        Assert.Contains("id=\"startup-status\"", index, StringComparison.Ordinal);
        Assert.Contains("id=\"startup-percent\"", index, StringComparison.Ordinal);
        Assert.Contains("id=\"startup-skip\"", index, StringComparison.Ordinal);
        Assert.Contains("disabled hidden aria-disabled=\"true\"", index, StringComparison.Ordinal);
        Assert.Contains("class=\"ms-progress\"", index, StringComparison.Ordinal);
        Assert.DoesNotContain("maliev-logo-loader", index, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-label=\"MALIEV\"", index, StringComparison.Ordinal);

        // Styling: scoped startup screen, dismissal contract, theming, reduced motion.
        Assert.Contains(".startup-screen {", styles, StringComparison.Ordinal);
        Assert.Contains("body.quote-ready .startup-screen", styles, StringComparison.Ordinal);
        Assert.Contains("body.quote-skip-ready:not(.quote-ready) .ms-skip", styles, StringComparison.Ordinal);
        Assert.Contains(":root[data-maliev-theme=\"dark\"] .startup-screen", styles, StringComparison.Ordinal);
        Assert.Contains(".ms-beat {", styles, StringComparison.Ordinal);
        Assert.Contains(".ms-beat--dim {\n    color: currentColor;\n    font-weight: 550;", styles, StringComparison.Ordinal);
        Assert.Contains(".ms-tagline {\n    font-family: var(--maliev-font-mono);\n    font-size: 11px;\n    letter-spacing: 0.42em;\n    text-transform: uppercase;\n    opacity: 1;\n    color: currentColor;", styles, StringComparison.Ordinal);
        Assert.Contains(".ms-progress-fill {", styles, StringComparison.Ordinal);
        Assert.Contains("width: var(--ms-progress, 0%)", styles, StringComparison.Ordinal);
        Assert.Contains("prefers-reduced-motion", styles, StringComparison.Ordinal);
        Assert.Contains(":root[data-culture=\"th-TH\"] .ms-beat", styles, StringComparison.Ordinal);
        Assert.DoesNotContain(".maliev-logo-loader", styles, StringComparison.Ordinal);

        // Loader: real WASM progress contract + story timeline + dismissal gate.
        Assert.Contains("startBlazor", loaderScript, StringComparison.Ordinal);
        Assert.Contains("loadBootResource", loaderScript, StringComparison.Ordinal);
        Assert.Contains("if (type === \"dotnetjs\")", loaderScript, StringComparison.Ordinal);
        Assert.Contains("resolveStaticAsset(defaultUri)", loaderScript, StringComparison.Ordinal);
        Assert.Contains("withBootCacheBust(resolveStaticAsset(defaultUri))", loaderScript, StringComparison.Ordinal);
        Assert.Contains("cache: \"no-store\"", loaderScript, StringComparison.Ordinal);
        Assert.Contains("Failed to fetch dynamically imported module", loaderScript, StringComparison.Ordinal);
        Assert.Contains("staleBootRetryKey", loaderScript, StringComparison.Ordinal);
        Assert.Contains("window.location.replace(url.toString())", loaderScript, StringComparison.Ordinal);
        Assert.Contains("let displayedProgress = 0", loaderScript, StringComparison.Ordinal);
        Assert.Contains("const STORY_BEAT_MS = [3200, 4200, 2600, 5200];", loaderScript, StringComparison.Ordinal);
        Assert.Contains("const FINALE_HOLD_MS = 3000;", loaderScript, StringComparison.Ordinal);
        Assert.Contains("Math.max(displayedProgress, progress)", loaderScript, StringComparison.Ordinal);
        Assert.Contains("markRuntimeReady", loaderScript, StringComparison.Ordinal);
        Assert.Contains("enableSkipStory();", loaderScript, StringComparison.Ordinal);
        Assert.Contains("function skipStory()", loaderScript, StringComparison.Ordinal);
        Assert.Contains("if (!canSkipStory)", loaderScript, StringComparison.Ordinal);
        Assert.Contains("const isFirstWasmLoad = shouldPlayFirstWasmStory();", loaderScript, StringComparison.Ordinal);
        Assert.Contains("firstWasmStoryPending = isFirstWasmLoad;", loaderScript, StringComparison.Ordinal);
        Assert.Contains("beginBoot(isWorkspaceHandoff || isFirstWasmLoad);", loaderScript.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("function beginBoot(wantsStory)", loaderScript, StringComparison.Ordinal);
        Assert.Contains(
            "if (!wantsStory) {\n      finishStartupStory();\n      return;\n    }",
            loaderScript.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.Contains("params.has(\"handoff\")", loaderScript, StringComparison.Ordinal);
        Assert.Contains("params.get(\"source\") !== \"web\"", loaderScript, StringComparison.Ordinal);
        Assert.Contains("function consumeQueryWorkspaceHandoff(params)", loaderScript, StringComparison.Ordinal);
        Assert.Contains("url.searchParams.delete(\"handoff\")", loaderScript, StringComparison.Ordinal);
        Assert.Contains("url.searchParams.delete(\"source\")", loaderScript, StringComparison.Ordinal);
        Assert.Contains("window.history.replaceState(window.history.state, document.title, url.toString())", loaderScript, StringComparison.Ordinal);
        Assert.Contains("const firstWasmLoadKey = \"maliev.makestudio.first-wasm-loaded\";", loaderScript, StringComparison.Ordinal);
        Assert.Contains("function shouldPlayFirstWasmStory()", loaderScript, StringComparison.Ordinal);
        Assert.Contains("function rememberFirstWasmLoaded()", loaderScript, StringComparison.Ordinal);
        Assert.Contains("window.localStorage.getItem(firstWasmLoadKey) !== \"1\"", loaderScript, StringComparison.Ordinal);
        var firstLoadCheckBlock = ExtractSourceBlock(loaderScript, "function shouldPlayFirstWasmStory()", "function rememberFirstWasmLoaded()");
        var rememberBlock = ExtractSourceBlock(loaderScript, "function rememberFirstWasmLoaded()", "function getQueryCulture()");
        Assert.DoesNotContain("window.localStorage.setItem(firstWasmLoadKey, \"1\")", firstLoadCheckBlock, StringComparison.Ordinal);
        Assert.Contains("window.localStorage.setItem(firstWasmLoadKey, \"1\")", rememberBlock, StringComparison.Ordinal);
        Assert.Contains("if (firstWasmStoryPending)", loaderScript, StringComparison.Ordinal);
        Assert.Contains("rememberFirstWasmLoaded();", loaderScript, StringComparison.Ordinal);
        Assert.DoesNotContain("const wantsStory = isWorkspaceHandoff;", loaderScript, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "minVisibleMs = 0;\n    document.body.classList.add(\"quote-ready\");",
            loaderScript.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "function markRuntimeReady() {\n    enableSkipStory();",
            loaderScript.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.Contains("ms-beat--on", loaderScript, StringComparison.Ordinal);
        Assert.DoesNotContain("ms-strike", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("ms-beat--on .ms-kill", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("maliev.makestudio.story.seen", loaderScript, StringComparison.Ordinal);
        Assert.Contains("Preparing Make Studio", loaderScript, StringComparison.Ordinal);

        // Localization: Thai display language carried from Maliev.Web survives the
        // subdomain hop via the query string, and the story renders in Thai.
        Assert.Contains("new URLSearchParams(window.location.search).get(\"culture\")", loaderScript, StringComparison.Ordinal);
        Assert.Contains("\"th-TH\"", loaderScript, StringComparison.Ordinal);
        Assert.Contains("applyStoryStrings", loaderScript, StringComparison.Ordinal);
        Assert.Contains("กำลังโหลด Make Studio", loaderScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusService_merge_not_overwrite_when_second_dfm_event_arrives()
    {
        var svc = new QuoteFileAnalysisStatusService();
        const string path = "quotes/temp/s/u/part.step";

        // First event: FDM report only
        var fdm = new QeFdmDfmReport(1, 0, 0m, false, 0, []);
        await svc.SetDfmReportsAsync(path, fdm, null, null, [], null, null);

        // Second event: CNC report only — FDM must survive
        var cnc = new QeCncDfmReport(2, false, true, 3, false, false, false, []);
        await svc.SetDfmReportsAsync(path, null, null, cnc, ["https://overlay1.glb"], null, null);

        var status = await svc.GetStatusAsync(path);
        Assert.NotNull(status);
        Assert.NotNull(status.FdmReport);           // preserved from first event
        Assert.Equal(1, status.FdmReport.ThinWallCount);
        Assert.NotNull(status.CncReport);           // added by second event
        Assert.Equal(3, status.CncReport.DrillHoleCount);
        Assert.Single(status.OverlayGlbUrls);       // accumulated
    }

    [Fact]
    public async Task StatusService_SetGlbReady_transitions_status()
    {
        var svc = new QuoteFileAnalysisStatusService();
        const string path = "quotes/temp/s/u/part.step";

        await svc.SetProcessingAsync(path);
        var processing = await svc.GetStatusAsync(path);
        Assert.Equal("Processing", processing!.Status);

        await svc.SetGlbReadyAsync(
            path,
            "https://glb.example.com/part.glb",
            null,
            1,
            true,
            viewerStoragePath: "processed/p.glb",
            viewerFileExtension: ".glb");
        var ready = await svc.GetStatusAsync(path);
        Assert.Equal("GlbReady", ready!.Status);
        Assert.Equal("https://glb.example.com/part.glb", ready.GlbUrl);
        Assert.Equal("processed/p.glb", ready.ViewerStoragePath);
        Assert.Equal(".glb", ready.ViewerFileExtension);
        Assert.Equal(1, ready.BodyCount);
        Assert.True(ready.IsManifold);
    }

    [Fact]
    public async Task StatusService_SetLocalGeometryMetrics_merges_with_existing_viewer_status()
    {
        var svc = new QuoteFileAnalysisStatusService();
        const string path = "quotes/temp/s/u/browser-local.stl";

        await svc.SetGlbReadyAsync(
            path,
            "https://glb.example.com/browser-local.glb",
            "https://glb.example.com/browser-local.webp",
            1,
            true,
            viewerStoragePath: "processed/browser-local.glb",
            viewerFileExtension: ".glb");

        await svc.SetLocalGeometryMetricsAsync(
            path,
            12.5m,
            62m,
            false,
            "Browser local DFM found 4 non-manifold edge(s).");
        await svc.SetProcessingAsync(path);

        var status = await svc.GetStatusAsync(path);
        Assert.NotNull(status);
        Assert.Equal("Processing", status.Status);
        Assert.Equal("https://glb.example.com/browser-local.glb", status.GlbUrl);
        Assert.Equal("processed/browser-local.glb", status.ViewerStoragePath);
        Assert.Equal(12.5m, status.VolumeCc);
        Assert.Equal(62m, status.SurfaceAreaCm2);
        Assert.False(status.IsManifold);
        Assert.Equal("Browser local DFM found 4 non-manifold edge(s).", status.NonManifoldReason);
    }

    [Fact]
    public async Task StatusService_SetFailed_sets_error_code()
    {
        var svc = new QuoteFileAnalysisStatusService();
        const string path = "quotes/temp/s/u/part.step";

        await svc.SetProcessingAsync(path);
        await svc.SetFailedAsync(path, "GlbSigningFailed");

        var status = await svc.GetStatusAsync(path);
        Assert.Equal("Failed", status!.Status);
        Assert.Equal("GlbSigningFailed", status.AnalysisErrorCode);
    }

    [Fact]
    public async Task StatusService_SetDfmReports_cold_insert_path_works_when_no_prior_entry()
    {
        // Scenario: DfmAnalysisReady event arrives before GlbReady (out-of-order delivery)
        var svc = new QuoteFileAnalysisStatusService();
        const string path = "quotes/temp/s/u/part.step";

        // No SetProcessingAsync or SetGlbReadyAsync called first
        var fdm = new QeFdmDfmReport(0, 0, 0m, false, 0, []);
        var cnc = new QeCncDfmReport(1, false, false, 0, false, false, true, []);
        await svc.SetDfmReportsAsync(path, fdm, null, cnc,
            ["https://overlay1.glb"], null, null);

        var status = await svc.GetStatusAsync(path);
        Assert.NotNull(status);
        Assert.Equal("DfmAnalysisReady", status.Status);
        Assert.NotNull(status.FdmReport);
        Assert.Equal(0, status.FdmReport.ThinWallCount);
        Assert.NotNull(status.CncReport);
        Assert.True(status.CncReport.IsTurnable);
        Assert.Null(status.SlaReport);
        Assert.Single(status.OverlayGlbUrls);
        // GlbUrl not set (no GlbReady event) — must be null, not throw
        Assert.Null(status.GlbUrl);
    }

    [Fact]
    public async Task FileAnalyzedConsumer_success_path_updates_status_and_pushes_GlbReady()
    {
        // Arrange
        var statusSvc = new QuoteFileAnalysisStatusService();
        await statusSvc.SetProcessingAsync("quotes/temp/s/u/bracket.step");

        var uploadClient = new FakeQuoteUploadServiceClient(
            glbUrl: "https://cdn.example.com/part.glb");

        var hubClients = Substitute.For<IHubClients>();
        var hubGroup = Substitute.For<IClientProxy>();
        hubClients.Group(Arg.Any<string>()).Returns(hubGroup);
        var hubCtx = Substitute.For<IHubContext<QuoteNotificationsHub>>();
        hubCtx.Clients.Returns(hubClients);

        var consumer = new QuoteFileAnalyzedConsumer(
            statusSvc, uploadClient, hubCtx,
            NullLogger<QuoteFileAnalyzedConsumer>.Instance);

        var @event = BuildFileAnalyzedEvent(
            storagePath: "quotes/temp/s/u/bracket.step",
            glbStoragePath: "processed/u/bracket.glb",
            thumbnailStoragePath: null,
            bodyCount: 1,
            isManifold: true,
            volumeCm3: 12.4);

        var consumeCtx = Substitute.For<ConsumeContext<FileAnalyzedEvent>>();
        consumeCtx.Message.Returns(@event);
        consumeCtx.CancellationToken.Returns(CancellationToken.None);

        // Act
        await consumer.Consume(consumeCtx);

        // Assert — status updated
        var stored = await statusSvc.GetStatusAsync("quotes/temp/s/u/bracket.step");
        Assert.NotNull(stored);
        Assert.Equal("GlbReady", stored.Status);
        Assert.Equal("https://cdn.example.com/part.glb", stored.GlbUrl);
        Assert.Equal("processed/u/bracket.glb", stored.ViewerStoragePath);
        Assert.Equal(".glb", stored.ViewerFileExtension);
        Assert.Null(stored.ThumbnailUrl);
        Assert.Equal(1, stored.BodyCount);
        Assert.True(stored.IsManifold);

        // Assert — SignalR hub called with correct GlbReady payload
        var calls = hubGroup.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "SendCoreAsync")
            .ToList();
        Assert.Single(calls);
        var args = calls[0].GetArguments();
        Assert.Equal("GlbReady", args[0]);
        var payloadArgs = (object?[])args[1]!;
        var glbPayload = Assert.IsType<QeGlbReadyPayload>(payloadArgs[0]);
        Assert.Equal("quotes/temp/s/u/bracket.step", glbPayload.StoragePath);
        Assert.Equal("https://cdn.example.com/part.glb", glbPayload.GlbUrl);
        Assert.Equal("processed/u/bracket.glb", glbPayload.ViewerStoragePath);
        Assert.Equal(".glb", glbPayload.ViewerFileExtension);
        Assert.Null(glbPayload.ThumbnailUrl);
        Assert.False(glbPayload.Failed);
        Assert.Null(glbPayload.ErrorCode);
        Assert.Equal(1, glbPayload.BodyCount);
        Assert.True(glbPayload.IsManifold);
    }

    [Fact]
    public async Task PaymentFailedConsumer_pushes_provider_neutral_failure_to_order_group()
    {
        var hubClients = Substitute.For<IHubClients>();
        var hubGroup = Substitute.For<IClientProxy>();
        hubClients.Group(Arg.Any<string>()).Returns(hubGroup);
        var hubCtx = Substitute.For<IHubContext<QuoteNotificationsHub>>();
        hubCtx.Clients.Returns(hubClients);
        var consumer = new QuotePaymentFailedConsumer(
            hubCtx,
            NullLogger<QuotePaymentFailedConsumer>.Instance);
        var transactionId = Guid.Parse("e84059a4-53a6-4f4d-a76e-c118f646c027");
        var failedAt = DateTimeOffset.Parse("2026-06-13T07:15:00Z");
        var @event = new PaymentFailedEvent(
            MessageId: Guid.NewGuid(),
            MessageName: nameof(PaymentFailedEvent),
            MessageType: MessageType.Event,
            MessageVersion: "1.0.0",
            PublishedBy: "PaymentService",
            ConsumedBy: ["QuoteEngine"],
            CorrelationId: Guid.NewGuid(),
            CausationId: null,
            OccurredAtUtc: failedAt,
            IsPublic: false,
            Payload: new PaymentFailedEventPayload(
                TransactionId: transactionId,
                IdempotencyKey: "customer:order:attempt",
                Amount: 1500.00,
                Currency: "THB",
                CustomerId: "customer-1",
                OrderId: "ORD-2026-0001",
                ProviderName: "omise",
                ErrorMessage: "card_declined",
                ProviderErrorCode: "failed_fraud_check",
                FailedAt: failedAt));
        var consumeCtx = Substitute.For<ConsumeContext<PaymentFailedEvent>>();
        consumeCtx.Message.Returns(@event);
        consumeCtx.CancellationToken.Returns(CancellationToken.None);

        await consumer.Consume(consumeCtx);

        hubClients.Received(1).Group(QuoteNotificationsHub.OrderGroup("ORD-2026-0001"));
        var call = Assert.Single(
            hubGroup.ReceivedCalls(),
            c => c.GetMethodInfo().Name == "SendCoreAsync");
        var args = call.GetArguments();
        Assert.Equal("PaymentFailed", args[0]);
        var payloadArgs = (object?[])args[1]!;
        var payload = Assert.IsType<QePaymentFailedPayload>(payloadArgs[0]);
        Assert.Equal("ORD-2026-0001", payload.OrderNumber);
        Assert.Equal(transactionId, payload.PaymentId);
        Assert.Equal(1500.00m, payload.Amount);
        Assert.Equal("THB", payload.Currency);
        Assert.Equal("card_declined", payload.ErrorMessage);
        Assert.Equal("failed_fraud_check", payload.ProviderErrorCode);
        Assert.Equal(failedAt, payload.FailedAt);
    }

    [Fact]
    public async Task PaymentPendingConsumer_pushes_provider_neutral_pending_to_order_group()
    {
        var hubClients = Substitute.For<IHubClients>();
        var hubGroup = Substitute.For<IClientProxy>();
        hubClients.Group(Arg.Any<string>()).Returns(hubGroup);
        var hubCtx = Substitute.For<IHubContext<QuoteNotificationsHub>>();
        hubCtx.Clients.Returns(hubClients);
        var consumer = new QuotePaymentPendingConsumer(
            hubCtx,
            NullLogger<QuotePaymentPendingConsumer>.Instance);
        var transactionId = Guid.Parse("0fd63cdb-3de4-4f8e-8792-98b32aa329a7");
        var pendingAt = DateTimeOffset.Parse("2026-06-13T10:05:00Z");
        var @event = new PaymentPendingEvent(
            MessageId: Guid.NewGuid(),
            MessageName: nameof(PaymentPendingEvent),
            MessageType: MessageType.Event,
            MessageVersion: "1.0.0",
            PublishedBy: "PaymentService",
            ConsumedBy: ["QuoteEngine"],
            CorrelationId: Guid.NewGuid(),
            CausationId: null,
            OccurredAtUtc: pendingAt,
            IsPublic: true,
            Payload: new PaymentPendingEventPayload(
                TransactionId: transactionId,
                IdempotencyKey: "customer:order:attempt",
                Amount: 1500.00,
                Currency: "THB",
                CustomerId: "customer-1",
                OrderId: "ORD-2026-0004",
                ProviderName: "opn",
                ProviderEventCode: "payment.processing",
                PendingAt: pendingAt));
        var consumeCtx = Substitute.For<ConsumeContext<PaymentPendingEvent>>();
        consumeCtx.Message.Returns(@event);
        consumeCtx.CancellationToken.Returns(CancellationToken.None);

        await consumer.Consume(consumeCtx);

        hubClients.Received(1).Group(QuoteNotificationsHub.OrderGroup("ORD-2026-0004"));
        var call = Assert.Single(
            hubGroup.ReceivedCalls(),
            c => c.GetMethodInfo().Name == "SendCoreAsync");
        var args = call.GetArguments();
        Assert.Equal("PaymentPending", args[0]);
        var payloadArgs = (object?[])args[1]!;
        var payload = Assert.IsType<QePaymentPendingPayload>(payloadArgs[0]);
        Assert.Equal("ORD-2026-0004", payload.OrderNumber);
        Assert.Equal(transactionId, payload.PaymentId);
        Assert.Equal(1500.00m, payload.Amount);
        Assert.Equal("THB", payload.Currency);
        Assert.Equal("payment.processing", payload.ProviderEventCode);
        Assert.Equal(pendingAt, payload.PendingAt);
    }

    [Fact]
    public async Task PaymentCancelledConsumer_pushes_provider_neutral_cancellation_to_order_group()
    {
        var hubClients = Substitute.For<IHubClients>();
        var hubGroup = Substitute.For<IClientProxy>();
        hubClients.Group(Arg.Any<string>()).Returns(hubGroup);
        var hubCtx = Substitute.For<IHubContext<QuoteNotificationsHub>>();
        hubCtx.Clients.Returns(hubClients);
        var consumer = new QuotePaymentCancelledConsumer(
            hubCtx,
            NullLogger<QuotePaymentCancelledConsumer>.Instance);
        var transactionId = Guid.Parse("ac72691e-d6a0-4f2b-91ac-4a3108082ba7");
        var cancelledAt = DateTimeOffset.Parse("2026-06-13T08:20:00Z");
        var @event = new PaymentCancelledEvent(
            MessageId: Guid.NewGuid(),
            MessageName: nameof(PaymentCancelledEvent),
            MessageType: MessageType.Event,
            MessageVersion: "1.0.0",
            PublishedBy: "PaymentService",
            ConsumedBy: ["QuoteEngine"],
            CorrelationId: Guid.NewGuid(),
            CausationId: null,
            OccurredAtUtc: cancelledAt,
            IsPublic: false,
            Payload: new PaymentCancelledEventPayload(
                TransactionId: transactionId,
                IdempotencyKey: "customer:order:attempt",
                Amount: 1500.00,
                Currency: "THB",
                CustomerId: "customer-1",
                OrderId: "ORD-2026-0002",
                ProviderName: "stripe",
                Reason: "Customer returned from cancel URL",
                ProviderEventCode: "checkout.session.expired",
                CancelledAt: cancelledAt));
        var consumeCtx = Substitute.For<ConsumeContext<PaymentCancelledEvent>>();
        consumeCtx.Message.Returns(@event);
        consumeCtx.CancellationToken.Returns(CancellationToken.None);

        await consumer.Consume(consumeCtx);

        hubClients.Received(1).Group(QuoteNotificationsHub.OrderGroup("ORD-2026-0002"));
        var call = Assert.Single(
            hubGroup.ReceivedCalls(),
            c => c.GetMethodInfo().Name == "SendCoreAsync");
        var args = call.GetArguments();
        Assert.Equal("PaymentCancelled", args[0]);
        var payloadArgs = (object?[])args[1]!;
        var payload = Assert.IsType<QePaymentCancelledPayload>(payloadArgs[0]);
        Assert.Equal("ORD-2026-0002", payload.OrderNumber);
        Assert.Equal(transactionId, payload.PaymentId);
        Assert.Equal(1500.00m, payload.Amount);
        Assert.Equal("THB", payload.Currency);
        Assert.Equal("Customer returned from cancel URL", payload.Reason);
        Assert.Equal("checkout.session.expired", payload.ProviderEventCode);
        Assert.Equal(cancelledAt, payload.CancelledAt);
    }

    [Fact]
    public async Task PaymentExpiredConsumer_pushes_provider_neutral_expiry_to_order_group()
    {
        var hubClients = Substitute.For<IHubClients>();
        var hubGroup = Substitute.For<IClientProxy>();
        hubClients.Group(Arg.Any<string>()).Returns(hubGroup);
        var hubCtx = Substitute.For<IHubContext<QuoteNotificationsHub>>();
        hubCtx.Clients.Returns(hubClients);
        var consumer = new QuotePaymentExpiredConsumer(
            hubCtx,
            NullLogger<QuotePaymentExpiredConsumer>.Instance);
        var transactionId = Guid.Parse("a81a7c84-1b35-4626-bc0b-a74d55e224a4");
        var expiredAt = DateTimeOffset.Parse("2026-06-13T09:10:00Z");
        var @event = new PaymentExpiredEvent(
            MessageId: Guid.NewGuid(),
            MessageName: nameof(PaymentExpiredEvent),
            MessageType: MessageType.Event,
            MessageVersion: "1.0.0",
            PublishedBy: "PaymentService",
            ConsumedBy: ["QuoteEngine"],
            CorrelationId: Guid.NewGuid(),
            CausationId: null,
            OccurredAtUtc: expiredAt,
            IsPublic: false,
            Payload: new PaymentExpiredEventPayload(
                TransactionId: transactionId,
                IdempotencyKey: "customer:order:attempt",
                Amount: 1500.00,
                Currency: "THB",
                CustomerId: "customer-1",
                OrderId: "ORD-2026-0003",
                ProviderName: "stripe",
                Reason: "Hosted checkout expired",
                ProviderEventCode: "checkout.session.expired",
                ExpiredAt: expiredAt));
        var consumeCtx = Substitute.For<ConsumeContext<PaymentExpiredEvent>>();
        consumeCtx.Message.Returns(@event);
        consumeCtx.CancellationToken.Returns(CancellationToken.None);

        await consumer.Consume(consumeCtx);

        hubClients.Received(1).Group(QuoteNotificationsHub.OrderGroup("ORD-2026-0003"));
        var call = Assert.Single(
            hubGroup.ReceivedCalls(),
            c => c.GetMethodInfo().Name == "SendCoreAsync");
        var args = call.GetArguments();
        Assert.Equal("PaymentExpired", args[0]);
        var payloadArgs = (object?[])args[1]!;
        var payload = Assert.IsType<QePaymentExpiredPayload>(payloadArgs[0]);
        Assert.Equal("ORD-2026-0003", payload.OrderNumber);
        Assert.Equal(transactionId, payload.PaymentId);
        Assert.Equal(1500.00m, payload.Amount);
        Assert.Equal("THB", payload.Currency);
        Assert.Equal("Hosted checkout expired", payload.Reason);
        Assert.Equal("checkout.session.expired", payload.ProviderEventCode);
        Assert.Equal(expiredAt, payload.ExpiredAt);
    }

    [Fact]
    public async Task FileAnalyzedConsumer_WhenViewerSourceIsOriginalStl_SignsOriginalPath()
    {
        var statusSvc = new QuoteFileAnalysisStatusService();
        await statusSvc.SetProcessingAsync("quotes/temp/s/u/bracket.stl");

        var uploadClient = new FakeQuoteUploadServiceClient(
            glbUrl: "https://cdn.example.com/bracket.stl");

        var hubClients = Substitute.For<IHubClients>();
        var hubGroup = Substitute.For<IClientProxy>();
        hubClients.Group(Arg.Any<string>()).Returns(hubGroup);
        var hubCtx = Substitute.For<IHubContext<QuoteNotificationsHub>>();
        hubCtx.Clients.Returns(hubClients);

        var consumer = new QuoteFileAnalyzedConsumer(
            statusSvc, uploadClient, hubCtx,
            NullLogger<QuoteFileAnalyzedConsumer>.Instance);

        var @event = BuildFileAnalyzedEvent(
            storagePath: "quotes/temp/s/u/bracket.stl",
            glbStoragePath: null,
            thumbnailStoragePath: null,
            bodyCount: 1,
            isManifold: true,
            volumeCm3: 12.4,
            viewerStoragePath: "quotes/temp/s/u/bracket.stl",
            viewerFileExtension: ".stl");

        var consumeCtx = Substitute.For<ConsumeContext<FileAnalyzedEvent>>();
        consumeCtx.Message.Returns(@event);
        consumeCtx.CancellationToken.Returns(CancellationToken.None);

        await consumer.Consume(consumeCtx);

        var stored = await statusSvc.GetStatusAsync("quotes/temp/s/u/bracket.stl");
        Assert.NotNull(stored);
        Assert.Equal("https://cdn.example.com/bracket.stl", stored.GlbUrl);
        Assert.Equal("quotes/temp/s/u/bracket.stl", stored.ViewerStoragePath);
        Assert.Equal(".stl", stored.ViewerFileExtension);

        var call = Assert.Single(
            hubGroup.ReceivedCalls(),
            c => c.GetMethodInfo().Name == "SendCoreAsync");
        var payloadArgs = (object?[])call.GetArguments()[1]!;
        var glbPayload = Assert.IsType<QeGlbReadyPayload>(payloadArgs[0]);
        Assert.Equal("https://cdn.example.com/bracket.stl", glbPayload.GlbUrl);
        Assert.Equal("quotes/temp/s/u/bracket.stl", glbPayload.ViewerStoragePath);
        Assert.Equal(".stl", glbPayload.ViewerFileExtension);
    }

    [Fact]
    public async Task FileAnalyzedConsumer_url_signing_failure_marks_failed_and_still_pushes_GlbReady()
    {
        var statusSvc = new QuoteFileAnalysisStatusService();
        var uploadClient = new ThrowingQuoteUploadServiceClient();

        var hubClients = Substitute.For<IHubClients>();
        var hubGroup = Substitute.For<IClientProxy>();
        hubClients.Group(Arg.Any<string>()).Returns(hubGroup);
        var hubCtx = Substitute.For<IHubContext<QuoteNotificationsHub>>();
        hubCtx.Clients.Returns(hubClients);

        var consumer = new QuoteFileAnalyzedConsumer(
            statusSvc, uploadClient, hubCtx,
            NullLogger<QuoteFileAnalyzedConsumer>.Instance);

        var @event = BuildFileAnalyzedEvent("quotes/s/u/p.step", "processed/p.glb", null, 1, true, 5.0);

        var consumeCtx = Substitute.For<ConsumeContext<FileAnalyzedEvent>>();
        consumeCtx.Message.Returns(@event);
        consumeCtx.CancellationToken.Returns(CancellationToken.None);

        await consumer.Consume(consumeCtx);

        var stored = await statusSvc.GetStatusAsync("quotes/s/u/p.step");
        Assert.Equal("Failed", stored!.Status);
        Assert.Equal("GlbSigningFailed", stored.AnalysisErrorCode);

        // Assert — hub still called with Failed=true payload
        var calls = hubGroup.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "SendCoreAsync")
            .ToList();
        Assert.Single(calls);
        var args = calls[0].GetArguments();
        Assert.Equal("GlbReady", args[0]);
        var payloadArgs = (object?[])args[1]!;
        var glbPayload = Assert.IsType<QeGlbReadyPayload>(payloadArgs[0]);
        Assert.True(glbPayload.Failed);
        Assert.Equal("GlbSigningFailed", glbPayload.ErrorCode);
        Assert.Equal("", glbPayload.GlbUrl);  // empty string per contract when Failed=true
    }

    [Fact]
    public async Task DfmAnalysisReadyConsumer_merges_fdm_then_cnc_and_pushes_DfmAnalysisReady()
    {
        // Arrange: status service with an existing GlbReady entry
        var statusSvc = new QuoteFileAnalysisStatusService();
        const string storagePath = "quotes/temp/s/u/bracket.step";
        await statusSvc.SetGlbReadyAsync(storagePath, "https://glb.example.com/part.glb", null, 1, true);

        var hubClients = Substitute.For<IHubClients>();
        var hubGroup = Substitute.For<IClientProxy>();
        hubClients.Group(Arg.Any<string>()).Returns(hubGroup);
        var hubCtx = Substitute.For<IHubContext<QuoteNotificationsHub>>();
        hubCtx.Clients.Returns(hubClients);

        var metricMeasurements = new List<Dictionary<string, object?>>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Name == "quote_dfm_execution_decisions")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var snapshot = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                snapshot[tag.Key] = tag.Value;
            }

            metricMeasurements.Add(snapshot);
        });
        listener.Start();
        using var metricProvider = new ServiceCollection()
            .AddMetrics()
            .BuildServiceProvider();
        var bffMetrics = new BffMetrics(metricProvider.GetRequiredService<IMeterFactory>());

        var uploadClient = new FakeQuoteUploadServiceClient("https://cdn.example.com/overlay.glb");
        var consumer = new QuoteDfmAnalysisReadyConsumer(
            statusSvc, uploadClient, hubCtx, bffMetrics,
            NullLogger<QuoteDfmAnalysisReadyConsumer>.Instance);

        // First event: FDM report only
        var fdmPayload = new FdmDfmReportPayload
        {
            ThinWallCount = 2,
            SupportRequired = true,
            Issues = Array.Empty<FdmDfmReportPayloadIssuesItem>()
        };
        var firstEvent = new DfmAnalysisReadyEvent
        {
            Payload = new DfmAnalysisReadyEventPayload
            {
                StoragePath = storagePath,
                FdmReport = fdmPayload,
                OverlayPaths = new[] { "processed/u/overlay-fdm.glb" }
            }
        };
        var firstCtx = Substitute.For<ConsumeContext<DfmAnalysisReadyEvent>>();
        firstCtx.Message.Returns(firstEvent);
        firstCtx.CancellationToken.Returns(CancellationToken.None);
        await consumer.Consume(firstCtx);

        // Second event: CNC report only — FDM report must survive (merge-not-overwrite)
        var cncPayload = new CncDfmReportPayload
        {
            SharpCornerCount = 3,
            HasUndercuts = false,
            Issues = Array.Empty<CncDfmReportPayloadIssuesItem>()
        };
        var secondEvent = new DfmAnalysisReadyEvent
        {
            Payload = new DfmAnalysisReadyEventPayload
            {
                StoragePath = storagePath,
                CncReport = cncPayload,
                OverlayPaths = Array.Empty<string>()
            }
        };
        var secondCtx = Substitute.For<ConsumeContext<DfmAnalysisReadyEvent>>();
        secondCtx.Message.Returns(secondEvent);
        secondCtx.CancellationToken.Returns(CancellationToken.None);
        await consumer.Consume(secondCtx);

        // Assert — merged status: FDM survived, CNC added
        var stored = await statusSvc.GetStatusAsync(storagePath);
        Assert.NotNull(stored!.FdmReport);                             // from first event
        Assert.Equal(2, stored.FdmReport.ThinWallCount);
        Assert.True(stored.FdmReport.SupportRequired);
        Assert.NotNull(stored.CncReport);                              // from second event
        Assert.Equal(3, stored.CncReport.SharpCornerCount);
        Assert.Null(stored.SlaReport);
        Assert.Single(stored.OverlayGlbUrls);                         // signed URL accumulated

        // Assert — hub called once per Consume call = twice total
        var calls = hubGroup.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "SendCoreAsync")
            .ToList();
        Assert.Equal(2, calls.Count);

        // Second call should carry the merged payload (FDM + CNC both present)
        var secondCallArgs = calls[1].GetArguments();
        Assert.Equal("DfmAnalysisReady", secondCallArgs[0]);
        var payloadArgs = (object?[])secondCallArgs[1]!;
        var dfmPayload = Assert.IsType<QeDfmAnalysisReadyPayload>(payloadArgs[0]);
        Assert.Equal(storagePath, dfmPayload.StoragePath);
        Assert.NotNull(dfmPayload.FdmReport);
        Assert.Equal(2, dfmPayload.FdmReport.ThinWallCount);
        Assert.NotNull(dfmPayload.CncReport);
        Assert.Equal(3, dfmPayload.CncReport.SharpCornerCount);

        Assert.Equal(2, metricMeasurements.Count);
        var fdmMetric = Assert.Single(metricMeasurements, tags => string.Equals(tags["process_family"], "fdm"));
        Assert.Equal("server_fallback", fdmMetric["execution_path"]);
        Assert.Equal("server_completed", fdmMetric["decision"]);
        Assert.Equal("consumed", fdmMetric["server_cpu"]);
        var cncMetric = Assert.Single(metricMeasurements, tags => string.Equals(tags["process_family"], "cnc"));
        Assert.Equal("server_fallback", cncMetric["execution_path"]);
        Assert.Equal("server_completed", cncMetric["decision"]);
        Assert.Equal("consumed", cncMetric["server_cpu"]);
    }

    [Fact]
    public void QuoteWorkspaceRazor_registers_GlbReady_and_DfmAnalysisReady_handlers()
    {
        var src = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "QuoteWorkspace.razor");
        var detail = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartDetailCard.razor");
        Assert.Contains("\"GlbReady\"", src);
        Assert.Contains("\"DfmAnalysisReady\"", src);
        Assert.Contains("FindPartByStoragePath", src);
        Assert.Contains("QePartViewer", detail);
        Assert.Contains("QeDfmTab", detail);
        Assert.Contains("QeBulkTable", detail);
    }

    [Fact]
    public void QuoteWorkspaceRazor_owns_legacy_projects_new_route()
    {
        var src = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "QuoteWorkspace.razor");

        Assert.Contains("@page \"/projects/new\"", src, StringComparison.Ordinal);
        Assert.Contains("@page \"/quotes\"", src, StringComparison.Ordinal);
    }

    [Fact]
    public void QuoteUploadJs_clones_selected_browser_file_before_fetch_upload()
    {
        var src = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-upload.js");

        Assert.Contains("const uploadBody = typeof file.blob.slice === \"function\"", src, StringComparison.Ordinal);
        Assert.Contains("new XMLHttpRequest()", src, StringComparison.Ordinal);
        Assert.Contains("xhr.send(uploadBody)", src, StringComparison.Ordinal);
    }

    [Fact]
    public void QuoteWorkspaceRazor_browser_primary_upload_skips_analysis_status_after_local_viewer()
    {
        var src = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "QuoteWorkspace.razor")
            .ReplaceLineEndings("\n");
        var uploadBlock = ExtractBlock(src, "private async Task ProcessUploadCandidatesAsync");

        Assert.Contains("TryCompleteBrowserPrimaryViewerLocallyAsync(part)", uploadBlock, StringComparison.Ordinal);
        Assert.Contains("if (!completedLocally)", uploadBlock, StringComparison.Ordinal);
        Assert.Contains("Api.GetAnalysisStatusAsync(upload.UploadId)", uploadBlock, StringComparison.Ordinal);
        Assert.True(
            uploadBlock.IndexOf("TryCompleteBrowserPrimaryViewerLocallyAsync(part)", StringComparison.Ordinal)
            < uploadBlock.IndexOf("Api.GetAnalysisStatusAsync(upload.UploadId)", StringComparison.Ordinal),
            "QuoteEngine should try the retained browser file viewer before asking the BFF for analysis status.");

        var localViewerBlock = ExtractBlock(src, "private async Task<bool> TryCompleteBrowserPrimaryViewerLocallyAsync");
        Assert.Contains("part.Status = \"Ready\";", localViewerBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void QuoteWorkspaceRazor_disables_upload_input_until_route_state_is_initialized()
    {
        var src = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "QuoteWorkspace.razor");

        Assert.Contains("disabled=\"@(!_routeStateInitialized || IsDemoMode)\"", src, StringComparison.Ordinal);
    }

    [Fact]
    public void QuoteWorkspaceRazor_resets_demo_workspace_when_leaving_demo_route()
    {
        var src = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "QuoteWorkspace.razor");

        Assert.Contains("SynchronizeRouteStateAsync", src, StringComparison.Ordinal);
        Assert.Contains("HasDemoWorkspace", src, StringComparison.Ordinal);
        Assert.Contains("ResetWorkspaceState", src, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowDemoSampleCard", src, StringComparison.Ordinal);
        Assert.Contains("ShowLaunchAccountCard => !IsSignedIn && !IsDemoMode", src, StringComparison.Ordinal);
        Assert.Contains("Navigation.NavigateTo(\"/demo\")", src, StringComparison.Ordinal);
    }

    [Fact]
    public void QeBulkTableRazor_exists_and_has_bulk_edit_columns()
    {
        var src = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QeBulkTable.razor");
        Assert.Contains("Select process", src);
        Assert.Contains("Select material", src);
        Assert.DoesNotContain("<option value=\"\">BulkProcess", src);
        Assert.DoesNotContain("<option value=\"\">BulkMaterial", src);
        Assert.Contains("LineTotal", src);
        Assert.Contains("DfmReport", src);  // DFM indicator column
    }

    [Fact]
    public void Quote_workspace_scopes_ProjectNew_density_tokens()
    {
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");

        Assert.Contains(".qe-pn-root {\n    --maliev-font-nano: 10px;", styles, StringComparison.Ordinal);
        Assert.Contains("--maliev-font-micro: 11px;", styles, StringComparison.Ordinal);
        Assert.Contains("--maliev-font-small: 12px;", styles, StringComparison.Ordinal);
        Assert.Contains("--maliev-font-base: 13px;", styles, StringComparison.Ordinal);
        Assert.Contains("--maliev-font-body: 13px;", styles, StringComparison.Ordinal);
        Assert.Contains("--maliev-font-large: 14px;", styles, StringComparison.Ordinal);
        Assert.Contains("--mud-typography-body1-size: 13px;", styles, StringComparison.Ordinal);
        Assert.Contains("--maliev-track-display: 0;", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pn-root .mud-input,", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Quote_workspace_matches_ProjectNew_tablet_layout_contract()
    {
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");

        Assert.Contains("@media (max-width: 1200px)", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pn-root.is-workspace-ready {\n        height: auto;", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pdc-thumb-area {\n        position: sticky;", styles, StringComparison.Ordinal);
        Assert.Contains("height: 45dvh;", styles, StringComparison.Ordinal);
        Assert.Contains("max-height: 520px;", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pcs-sidebar {\n        flex: 0 0 auto;", styles, StringComparison.Ordinal);
        Assert.Contains("max-height: none;", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-qsb-root {\n        position: sticky;", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void QeConfigSidebar_uses_ProjectNew_card_controls_not_native_selects()
    {
        var src = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartConfigSidebar.razor");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");

        Assert.Contains("Surface Finish", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pcs-fin-card", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pcs-tol-card", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pcs-choice-card", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pcs-feature-card", src, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"@Text(\"DFM reviewed\"", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pcs-notes-input\"", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pcs-help-link\"", src, StringComparison.Ordinal);
        Assert.Contains("SetPartNotes", src, StringComparison.Ordinal);
        Assert.DoesNotContain("<select", src, StringComparison.OrdinalIgnoreCase);

        Assert.Contains(".qe-pcs-fin-card", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pcs-tol-card", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pcs-choice-card", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pcs-notes-input", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void QeConfigSidebar_uses_ProjectNew_process_photo_selector()
    {
        var src = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartConfigSidebar.razor");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");

        Assert.Contains("data-config-section=\"manufacturing-process\"", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pcs-process-row\"", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pcs-process-photo\"", src, StringComparison.Ordinal);
        Assert.Contains("background-image:url('/images/processes/@ProcessImageFile(process.Id)')", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pcs-process-description\"", src, StringComparison.Ordinal);
        Assert.Contains("aria-pressed=\"@active\"", src, StringComparison.Ordinal);
        Assert.Contains("qe-pcs-process-card--dimmed", src, StringComparison.Ordinal);
        Assert.Contains("ProcessDescription(process.Id)", src, StringComparison.Ordinal);
        Assert.Contains("ProcessCategory(process.Id)", src, StringComparison.Ordinal);
        Assert.Contains("ProcessCategory(selProc.Id)", src, StringComparison.Ordinal);
        Assert.Contains("qe-pcs-process-no-results", src, StringComparison.Ordinal);
        Assert.Contains("FilteredProcesses", src, StringComparison.Ordinal);

        Assert.Contains(".qe-pcs-process-row", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pcs-process-photo", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pcs-process-category", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pcs-process-description", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pcs-process-no-results", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pcs-process-card--dimmed", styles, StringComparison.Ordinal);
        Assert.Contains("flex: 0 0 112px;", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void QeConfigSidebar_uses_ProjectNew_dedicated_material_color_section()
    {
        var src = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartConfigSidebar.razor");

        Assert.Contains("data-config-section=\"material-color\"", src, StringComparison.Ordinal);
        Assert.Contains("@Text(\"Material Color\"", src, StringComparison.Ordinal);
        Assert.Contains("MaterialColorOptions", src, StringComparison.Ordinal);
        Assert.Contains("SetMaterialColor", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pcs-color-choice", src, StringComparison.Ordinal);

        var materialIndex = src.IndexOf("data-config-section=\"material\"", StringComparison.Ordinal);
        var colorIndex = src.IndexOf("data-config-section=\"material-color\"", StringComparison.Ordinal);
        var finishIndex = src.IndexOf("data-config-section=\"surface-finish\"", StringComparison.Ordinal);

        Assert.True(materialIndex >= 0, "Material section must declare the Project New material data marker.");
        Assert.True(colorIndex > materialIndex, "Material Color must appear after Material.");
        Assert.True(finishIndex > colorIndex, "Surface Finish must appear after Material Color.");
    }

    [Fact]
    public void QeConfigSidebar_places_quantity_after_part_configuration_sections()
    {
        var src = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartConfigSidebar.razor");

        Assert.Contains("data-config-section=\"surface-finish\"", src, StringComparison.Ordinal);
        Assert.Contains("data-config-section=\"tolerance\"", src, StringComparison.Ordinal);
        Assert.Contains("data-config-section=\"part-features\"", src, StringComparison.Ordinal);
        Assert.Contains("data-config-section=\"quantity\"", src, StringComparison.Ordinal);

        var finishIndex = src.IndexOf("data-config-section=\"surface-finish\"", StringComparison.Ordinal);
        var toleranceIndex = src.IndexOf("data-config-section=\"tolerance\"", StringComparison.Ordinal);
        var featuresIndex = src.IndexOf("data-config-section=\"part-features\"", StringComparison.Ordinal);
        var quantityIndex = src.IndexOf("data-config-section=\"quantity\"", StringComparison.Ordinal);

        Assert.True(toleranceIndex > finishIndex, "Tolerance must appear after Surface Finish.");
        Assert.True(featuresIndex > toleranceIndex, "Part Features must appear after Tolerance.");
        Assert.True(quantityIndex > featuresIndex, "Quantity must appear after Part Features like Project New.");
    }

    [Fact]
    public void QeConfigSidebar_shows_inspection_before_quantity_like_ProjectNew()
    {
        var src = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartConfigSidebar.razor");

        Assert.Contains("data-config-section=\"inspection\"", src, StringComparison.Ordinal);
        Assert.Contains("@Text(\"Inspection\"", src, StringComparison.Ordinal);
        Assert.Contains("ReferenceData?.InspectionLevels", src, StringComparison.Ordinal);
        Assert.Contains("SetInspection", src, StringComparison.Ordinal);
        Assert.DoesNotContain("qe-pcs-advanced-toggle", src, StringComparison.Ordinal);

        var featuresIndex = src.IndexOf("data-config-section=\"part-features\"", StringComparison.Ordinal);
        var inspectionIndex = src.IndexOf("data-config-section=\"inspection\"", StringComparison.Ordinal);
        var quantityIndex = src.IndexOf("data-config-section=\"quantity\"", StringComparison.Ordinal);

        Assert.True(inspectionIndex > featuresIndex, "Inspection must appear after Part Features.");
        Assert.True(quantityIndex > inspectionIndex, "Quantity must appear after Inspection.");
    }

    [Fact]
    public void QeConfigSidebar_uses_ProjectNew_grouped_tolerance_layout()
    {
        var src = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartConfigSidebar.razor");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");

        Assert.Contains("class=\"qe-pcs-tolerance-groups\"", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pcs-tolerance-group", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pcs-tolerance-group-header\"", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pcs-tolerance-group-title\"", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pcs-tolerance-group-standard\"", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pcs-tolerance-group-count\"", src, StringComparison.Ordinal);
        Assert.Contains("ToleranceGroupTitle", src, StringComparison.Ordinal);
        Assert.Contains("ToleranceGroupStandard", src, StringComparison.Ordinal);

        Assert.Contains(".qe-pcs-tolerance-groups", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pcs-tolerance-group", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pcs-tolerance-group-header", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pcs-tolerance-group-title", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void QeConfigSidebar_uses_ProjectNew_roughness_section_contract()
    {
        var src = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartConfigSidebar.razor");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");

        Assert.Contains("data-config-section=\"surface-roughness\"", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pcs-choice-card qe-pcs-roughness-card", src, StringComparison.Ordinal);
        Assert.Contains("RoughnessFor(Part.ProcessId)", src, StringComparison.Ordinal);
        Assert.Contains("SetRoughness(roughness.Code)", src, StringComparison.Ordinal);
        Assert.Contains("GetRoughnessImageUrl(roughness.Code)", src, StringComparison.Ordinal);
        Assert.Contains("FormatOptionCount(roughnessOptions.Length)", src, StringComparison.Ordinal);

        Assert.Contains(".qe-pcs-roughness-card", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pcs-roughness-card .qe-pcs-choice-swatch", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void QeConfigSidebar_uses_ProjectNew_finish_options_contract()
    {
        var src = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartConfigSidebar.razor");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");
        var viewModel = ReadRepoFile("Maliev.QuoteEngine.Client", "Models", "QuotePartViewModel.cs");
        var dto = ReadRepoFile("Maliev.QuoteEngine.Shared", "Quotes", "QuoteEngineDtos.cs");
        var store = ReadRepoFile("Maliev.QuoteEngine.Bff", "Services", "QuoteEnginePrototypeStore.cs");
        var controller = ReadRepoFile("Maliev.QuoteEngine.Bff", "Controllers", "QuoteController.cs");
        var workspace = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "QuoteWorkspace.razor");

        Assert.Contains("data-config-section=\"finish-options\"", src, StringComparison.Ordinal);
        Assert.Contains("FinishColorOptions", src, StringComparison.Ordinal);
        Assert.Contains("StandardPaintColors", src, StringComparison.Ordinal);
        Assert.Contains("SetStandardPaintColor", src, StringComparison.Ordinal);
        Assert.Contains("SetCustomPaintColorReference", src, StringComparison.Ordinal);
        Assert.Contains("SetProcessOptionValue", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pcs-options-stack\"", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pcs-native-color\"", src, StringComparison.Ordinal);

        Assert.Contains("Dictionary<string, string> ProcessOptionValues", dto, StringComparison.Ordinal);
        Assert.Contains("ProcessOptionValues = new(ProcessOptionValues", viewModel, StringComparison.Ordinal);
        Assert.Contains("ProcessOptionValues = new(part.ProcessOptionValues", workspace, StringComparison.Ordinal);
        Assert.Contains("ProcessOptionValues = new(source.ProcessOptionValues", store, StringComparison.Ordinal);
        Assert.Contains("process options", controller, StringComparison.Ordinal);

        Assert.Contains(".qe-pcs-options-stack", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pcs-native-color", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void QeConfigSidebar_uses_ProjectNew_process_options_contract()
    {
        var src = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartConfigSidebar.razor");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");
        var dto = ReadRepoFile("Maliev.QuoteEngine.Shared", "Quotes", "QuoteEngineDtos.cs");
        var store = ReadRepoFile("Maliev.QuoteEngine.Bff", "Services", "QuoteEnginePrototypeStore.cs");

        Assert.Contains("public sealed record ProcessConfigOptionDto", dto, StringComparison.Ordinal);
        Assert.Contains("IReadOnlyList<ProcessConfigOptionDto> ProcessOptions", dto, StringComparison.Ordinal);
        Assert.Contains("\"deburr_edges\"", store, StringComparison.Ordinal);
        Assert.Contains("\"print_orientation\"", store, StringComparison.Ordinal);

        Assert.Contains("data-config-section=\"process-options\"", src, StringComparison.Ordinal);
        Assert.Contains("VisibleProcessOptions", src, StringComparison.Ordinal);
        Assert.Contains("IsBooleanOption", src, StringComparison.Ordinal);
        Assert.Contains("IsCardChoiceOption", src, StringComparison.Ordinal);
        Assert.Contains("SetProcessOptionBool", src, StringComparison.Ordinal);
        Assert.Contains("SetProcessOptionText", src, StringComparison.Ordinal);
        Assert.Contains("GetProcessOptionChoices", src, StringComparison.Ordinal);
        Assert.Contains("qe-pcs-process-option-card", src, StringComparison.Ordinal);

        Assert.Contains(".qe-pcs-process-option-card", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void QeDetailCard_retains_viewer_and_exposes_body_tree_and_dfm_overlay()
    {
        var src = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartDetailCard.razor");
        var viewer = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartViewer.razor");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");

        Assert.Contains("QePartViewer @ref", src, StringComparison.Ordinal);
        Assert.Contains("style=\"@(CenterMode == \"model\" ? \"\" : \"display:none\")\"", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pdc-body-tree", src, StringComparison.Ordinal);
        Assert.Contains("QeDfmOverlayPanel", src, StringComparison.Ordinal);
        Assert.Contains("OnRequestFreshViewerUrl", src, StringComparison.Ordinal);
        Assert.Contains("SelectBodyAsync", src, StringComparison.Ordinal);
        Assert.Contains("ToggleDfmOverlayAsync", src, StringComparison.Ordinal);
        Assert.Contains("NotifyVisibleAsync", viewer, StringComparison.Ordinal);
        Assert.Contains("SelectBodyAsync", viewer, StringComparison.Ordinal);
        Assert.Contains("ToggleDfmOverlayAsync", viewer, StringComparison.Ordinal);
        Assert.Contains(".qe-pdc-body-tree", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-dfm-overlay", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void QeBulkTable_uses_ProjectNew_dense_table_shell()
    {
        var src = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QeBulkTable.razor");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");

        Assert.Contains("class=\"qe-pbt-root\"", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pbt-header\"", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pbt-bulk-panel\"", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pbt-table-surface\"", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pbt-input", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pbt-configurator-button\"", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pbt-card-stack\"", src, StringComparison.Ordinal);
        Assert.Contains("Threaded holes", src, StringComparison.Ordinal);
        Assert.Contains("Inserts", src, StringComparison.Ordinal);
        Assert.Contains("Notes", src, StringComparison.Ordinal);

        Assert.Contains(".qe-pbt-bulk-panel", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pbt-input", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pbt-card-stack", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void QeDrawingAttachmentsTab_uses_ProjectNew_two_column_surface()
    {
        var src = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QeDrawingAttachmentsTab.razor");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");

        Assert.Contains("class=\"qe-dat-root\"", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-dat-list-col\"", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-dat-preview-col\"", src, StringComparison.Ordinal);
        Assert.Contains("InputFile", src, StringComparison.Ordinal);
        Assert.Contains("SelectDrawing", src, StringComparison.Ordinal);
        Assert.Contains("RemoveDrawing", src, StringComparison.Ordinal);
        Assert.DoesNotContain("AddPlaceholderDrawing", src, StringComparison.Ordinal);
        Assert.DoesNotContain("draft://quote-engine", src, StringComparison.Ordinal);

        Assert.Contains(".qe-dat-root", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-dat-preview-iframe", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Quote_summary_bar_exposes_customer_quote_approval_order_and_payment_actions()
    {
        var src = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QeQuoteSummaryBar.razor");
        var workspace = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "QuoteWorkspace.razor");
        var apiClient = ReadRepoFile("Maliev.QuoteEngine.Client", "Services", "QuoteEngineApiClient.cs");

        Assert.Contains("class=\"qe-qsb-details-popover\"", src, StringComparison.Ordinal);
        Assert.Contains("Approve quote", src, StringComparison.Ordinal);
        Assert.Contains("Create order", src, StringComparison.Ordinal);
        Assert.Contains("Pay now", src, StringComparison.Ordinal);
        Assert.Contains("Customer documents", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-qsb-zone qe-qsb-customer\"", src, StringComparison.Ordinal);
        Assert.Contains("BillingName", src, StringComparison.Ordinal);
        Assert.Contains("BillingHint", src, StringComparison.Ordinal);
        Assert.Contains("OnApproveQuoteRequested", src, StringComparison.Ordinal);
        Assert.Contains("OnPaymentRequested", src, StringComparison.Ordinal);

        Assert.DoesNotContain("<QeQuoteSummaryBar", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("BillingName=\"@BillingSummaryName\"", workspace, StringComparison.Ordinal);
        Assert.Contains("BillingCompanyName = BillingCompanyName", workspace, StringComparison.Ordinal);
        Assert.Contains("BillingVatNumber = BillingVatNumber", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("BillingHint=\"@BillingSummaryHint\"", workspace, StringComparison.Ordinal);
        Assert.Contains("ApproveQuoteAsync", workspace, StringComparison.Ordinal);
        Assert.Contains("InitiatePaymentAsync", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("PaymentBusy=\"@_paymentBusy\"", workspace, StringComparison.Ordinal);
        Assert.Contains("_paymentBusy = true", workspace, StringComparison.Ordinal);
        Assert.Contains("_paymentBusy = false", workspace, StringComparison.Ordinal);
        Assert.Contains("disabled=\"@(!CanPay || !TermsAccepted || PaymentBusy)\"", src, StringComparison.Ordinal);
        Assert.Contains("ApproveQuoteAsync", apiClient, StringComparison.Ordinal);
        Assert.Contains("InitiatePaymentAsync", apiClient, StringComparison.Ordinal);
    }

    [Fact]
    public void QuoteWorkspace_payment_auth_redirect_returns_to_order_checkout_context()
    {
        var workspace = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "QuoteWorkspace.razor");
        var initiatePayment = ExtractSourceBlock(workspace, "private async Task InitiatePaymentAsync()", "private async Task RequestFreshViewerUrlAsync");

        Assert.Contains("CheckoutReturnUrl", workspace, StringComparison.Ordinal);
        Assert.Contains("$\"/orders/{Uri.EscapeDataString(_order.OrderNumber)}\"", workspace, StringComparison.Ordinal);
        Assert.Contains("Navigation.NavigateTo($\"/auth/sign-in?returnUrl={Uri.EscapeDataString(CheckoutReturnUrl)}\")", initiatePayment, StringComparison.Ordinal);
        Assert.DoesNotContain("Navigation.NavigateTo(\"/auth/sign-in?returnUrl=/quote/new\")", initiatePayment, StringComparison.Ordinal);
    }

    [Fact]
    public void QuoteWorkspace_payment_failure_uses_problem_details_from_api_client()
    {
        var workspace = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "QuoteWorkspace.razor");
        var initiatePayment = ExtractSourceBlock(workspace, "private async Task InitiatePaymentAsync()", "private async Task RequestFreshViewerUrlAsync");

        Assert.Contains("catch (QuoteEngineApiException ex)", initiatePayment, StringComparison.Ordinal);
        Assert.Contains("_error = ex.UserMessage", initiatePayment, StringComparison.Ordinal);
        Assert.DoesNotContain("catch (Exception)", initiatePayment, StringComparison.Ordinal);
    }

    [Fact]
    public void QeDfmTabRazor_exists_and_references_all_three_report_types()
    {
        var src = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QeDfmTab.razor");
        Assert.Contains("QeFdmDfmReport", src);
        Assert.Contains("QeSlaDfmReport", src);
        Assert.Contains("QeCncDfmReport", src);
        Assert.Contains("IsManifold", src);
    }

    [Fact]
    public void Quote_viewer_and_part_thumbnail_have_customer_safe_fallbacks()
    {
        var viewer = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartViewer.razor");
        var partsList = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartsListPanel.razor");

        Assert.Contains("DotNetObjectReference<QePartViewer>", viewer, StringComparison.Ordinal);
        Assert.Contains("OnLoadError", viewer, StringComparison.Ordinal);
        Assert.Contains("qe-viewer-fallback", viewer, StringComparison.Ordinal);
        Assert.Contains("/images/generated/sample-part.svg", viewer, StringComparison.Ordinal);
        Assert.Contains("onerror=", partsList, StringComparison.Ordinal);
        Assert.Contains("FallbackPartImage", partsList, StringComparison.Ordinal);
    }

    [Fact]
    public void Quote_viewer_exposes_ProjectNew_cad_toolbar_controls()
    {
        var viewer = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartViewer.razor");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");

        Assert.Contains("SetRenderModeAsync", viewer, StringComparison.Ordinal);
        Assert.Contains("ToggleGridAsync", viewer, StringComparison.Ordinal);
        Assert.Contains("ToggleBoundingBoxAsync", viewer, StringComparison.Ordinal);
        Assert.Contains("ToggleMeasureAsync", viewer, StringComparison.Ordinal);
        Assert.Contains("ToggleThicknessAsync", viewer, StringComparison.Ordinal);
        Assert.Contains("ToggleSectionAsync", viewer, StringComparison.Ordinal);
        Assert.Contains("SetSectionAxisAsync", viewer, StringComparison.Ordinal);
        Assert.Contains("SetProjectionAsync", viewer, StringComparison.Ordinal);
        Assert.Contains("RotateModelAsync", viewer, StringComparison.Ordinal);
        Assert.Contains("NotifyMeasureResult", viewer, StringComparison.Ordinal);
        Assert.Contains("new(\"left\", \"Left view\")", viewer, StringComparison.Ordinal);
        Assert.Contains("new(\"back\", \"Back view\")", viewer, StringComparison.Ordinal);
        Assert.Contains("new(\"bottom\", \"Bottom view\")", viewer, StringComparison.Ordinal);
        Assert.Contains("\"setRenderMode\"", viewer, StringComparison.Ordinal);
        Assert.Contains("\"setCameraProjection\"", viewer, StringComparison.Ordinal);
        Assert.Contains("\"rotateModel\"", viewer, StringComparison.Ordinal);
        Assert.Contains("\"showGrid\"", viewer, StringComparison.Ordinal);
        Assert.Contains("\"hideGrid\"", viewer, StringComparison.Ordinal);
        Assert.Contains("\"toggleBoundingBox\"", viewer, StringComparison.Ordinal);
        Assert.Contains("\"enableMeasureTool\"", viewer, StringComparison.Ordinal);
        Assert.Contains("\"enableThicknessAnalysis\"", viewer, StringComparison.Ordinal);
        Assert.Contains("\"setSectionPlane\"", viewer, StringComparison.Ordinal);
        Assert.Contains("qe-viewer-loading-orbit", viewer, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Solid shaded view\"", viewer, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Transparent view\"", viewer, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Orthographic camera\"", viewer, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Rotate model 90 degrees\"", viewer, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Toggle section cut\"", viewer, StringComparison.Ordinal);
        Assert.Contains("Icons.Material.Filled.FlipCameraIos", viewer, StringComparison.Ordinal);
        Assert.Contains("private string _renderMode = \"realistic\";", viewer, StringComparison.Ordinal);
        Assert.Contains("private bool _ortho = true;", viewer, StringComparison.Ordinal);
        Assert.Contains("[Parameter] public QuotePartViewerSettingsDto?", viewer, StringComparison.Ordinal);
        Assert.Contains("ViewerSettings=\"@Part.ViewerSettings\"", ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartDetailCard.razor"), StringComparison.Ordinal);
        Assert.Contains("public string RenderMode", ReadRepoFile("Maliev.QuoteEngine.Shared", "Quotes", "QuoteEngineDtos.cs"), StringComparison.Ordinal);
        Assert.Contains("public string CameraProjection", ReadRepoFile("Maliev.QuoteEngine.Shared", "Quotes", "QuoteEngineDtos.cs"), StringComparison.Ordinal);
        Assert.Contains("renderMode = _renderMode", viewer, StringComparison.Ordinal);
        Assert.Contains("cameraProjection = _ortho ? \"orthographic\" : \"perspective\"", viewer, StringComparison.Ordinal);
        Assert.Contains("normalizeMode(settings.renderMode)", ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-part-viewer.js"), StringComparison.Ordinal);
        Assert.Contains("normalizeMode(settings.initialRenderMode)", ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-part-viewer.js"), StringComparison.Ordinal);
        Assert.Contains("normalizeMode(settings.targetRenderMode)", ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-part-viewer.js"), StringComparison.Ordinal);
        Assert.Contains("if (isMaterialAppliedMode) mesh.material = mat;", ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-part-viewer.js"), StringComparison.Ordinal);
        Assert.Contains(".qe-viewer-loading-orbit", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-vt-subtools", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Quote_viewer_replica_uses_ProjectNew_clear_material_transparency()
    {
        var detailCard = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartDetailCard.razor");
        var viewer = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartViewer.razor");
        var js = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-part-viewer.js");

        Assert.Contains("[Parameter] public string? MaterialId", viewer, StringComparison.Ordinal);
        Assert.Contains("MaterialId=\"@Part.MaterialId\"", detailCard, StringComparison.Ordinal);
        Assert.Contains("materialId = MaterialId", viewer, StringComparison.Ordinal);
        Assert.Contains("await _module.InvokeVoidAsync(\"setPartMaterial\", _canvasId, ProcessId, FinishCode, RoughnessCode, PartColor, MaterialId);", viewer, StringComparison.Ordinal);
        Assert.Contains("MaterialId != _lastMaterialId", viewer, StringComparison.Ordinal);

        Assert.Contains("MATERIAL_REALISTIC", js, StringComparison.Ordinal);
        Assert.Contains("'steel'", js, StringComparison.Ordinal);
        Assert.Contains("'stainless-steel'", js, StringComparison.Ordinal);
        Assert.Contains("'black-pom'", js, StringComparison.Ordinal);
        Assert.Contains("'white-pom'", js, StringComparison.Ordinal);
        Assert.Contains("'blue-pom'", js, StringComparison.Ordinal);
        Assert.Contains("'brass'", js, StringComparison.Ordinal);
        Assert.Contains("'copper'", js, StringComparison.Ordinal);
        Assert.Contains("'bronze'", js, StringComparison.Ordinal);
        Assert.Contains("'titanium'", js, StringComparison.Ordinal);
        Assert.Contains("'abs'", js, StringComparison.Ordinal);
        Assert.Contains("'petg'", js, StringComparison.Ordinal);
        Assert.Contains("'nylon'", js, StringComparison.Ordinal);
        Assert.Contains("'nylon-powder'", js, StringComparison.Ordinal);
        Assert.Contains("'peek'", js, StringComparison.Ordinal);
        Assert.Contains("'carbon-fiber'", js, StringComparison.Ordinal);
        Assert.Contains("'petg-clear'", js, StringComparison.Ordinal);
        Assert.Contains("'acrylic-clear'", js, StringComparison.Ordinal);
        Assert.Contains("'resin-clear'", js, StringComparison.Ordinal);
        Assert.Contains("resolveRealisticMaterialKey", js, StringComparison.Ordinal);
        Assert.Contains("materialKey.includes('pom')", js, StringComparison.Ordinal);
        Assert.Contains("materialKey.includes('brass')", js, StringComparison.Ordinal);
        Assert.Contains("materialKey.includes('carbon')", js, StringComparison.Ordinal);
        Assert.Contains("materialKey.includes('stainless')", js, StringComparison.Ordinal);
        Assert.Contains("processKey === 'sls'", js, StringComparison.Ordinal);
        Assert.Contains("applyRealisticTransparencySettings", js, StringComparison.Ordinal);
        Assert.Contains("material.transparencyMode = BABYLON.Material?.MATERIAL_ALPHABLEND ?? 2;", js, StringComparison.Ordinal);
        Assert.Contains("material.needDepthPrePass = true;", js, StringComparison.Ordinal);
        Assert.Contains("material.separateCullingPass = true;", js, StringComparison.Ordinal);
        Assert.Contains("material.linkRefractionWithTransparency = true;", js, StringComparison.Ordinal);
        Assert.Contains("material.useRadianceOverAlpha = true;", js, StringComparison.Ordinal);
        Assert.Contains("material.useSpecularOverAlpha = true;", js, StringComparison.Ordinal);
        Assert.Contains("material.indexOfRefraction = preset.indexOfRefraction;", js, StringComparison.Ordinal);
        Assert.Contains("material.subSurface.isRefractionEnabled", js, StringComparison.Ordinal);
        Assert.Contains("material.subSurface.isTranslucencyEnabled = true;", js, StringComparison.Ordinal);
        Assert.Contains("const mat = albedo || realisticPreset", js, StringComparison.Ordinal);
        Assert.Contains("setPartMaterial(canvasId, processId, finishCode, roughnessCode, cssColor, materialId", js, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_parts_panel_and_workspace_expose_duplicate_project_flow()
    {
        var partsList = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartsListPanel.razor");
        var workspace = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "QuoteWorkspace.razor");
        var apiClient = ReadRepoFile("Maliev.QuoteEngine.Client", "Services", "QuoteEngineApiClient.cs");

        Assert.Contains("[Parameter] public EventCallback OnDuplicateProject", partsList, StringComparison.Ordinal);
        Assert.Contains("@onclick=\"OnDuplicateProject\"", partsList, StringComparison.Ordinal);
        Assert.DoesNotContain("OnDuplicateProject=\"DuplicateProjectAsync\"", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("<QePartsListPanel", workspace, StringComparison.Ordinal);
        Assert.Contains("private async Task DuplicateProjectAsync()", workspace, StringComparison.Ordinal);
        Assert.Contains("DuplicateProjectAsync(_draftProjectId.Value", workspace, StringComparison.Ordinal);
        Assert.Contains("CreateDraftProjectAsync", workspace, StringComparison.Ordinal);
        Assert.Contains("ApplyDuplicatedProject", workspace, StringComparison.Ordinal);
        Assert.Contains("DuplicateDraftProjectRequest", apiClient, StringComparison.Ordinal);
        Assert.Contains("DuplicateDraftProjectResponse", apiClient, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotePartViewerJs_has_correct_window_handle_and_no_internal_tools()
    {
        var js = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-part-viewer.js");

        // Customer-facing debug handle
        Assert.Contains("window.quotePartViewer", js);

        // Internal tools must not be in the exported debug handle
        // (they may still exist as functions — just not exported via the handle)
        var handleBlock = ExtractWindowHandleBlock(js, "quotePartViewer");
        Assert.DoesNotContain("enableMeasureTool", handleBlock);
        Assert.DoesNotContain("enableThicknessAnalysis", handleBlock);
        Assert.DoesNotContain("enableBodyPicking", handleBlock);

        // Must have the customer-facing exports
        Assert.Contains("initialize", handleBlock);
        Assert.Contains("toggleDfmOverlay", handleBlock);
        Assert.Contains("setCameraPreset", handleBlock);
        Assert.Contains("collectAdvisoryMeshBuffers", handleBlock);
        Assert.Contains("runLocalAdvisoryGeometry", handleBlock);
        Assert.DoesNotContain("tryLoadLocalViewerMeshFromRuntime", handleBlock, StringComparison.Ordinal);
        Assert.Contains("/quote/v1/geometry/runtime/manifest", js, StringComparison.Ordinal);
        Assert.Contains("fetchLocalAdvisoryManifest", js, StringComparison.Ordinal);
        Assert.Contains("LOCAL_ADVISORY_MANIFEST_RETRY_ATTEMPTS = 6", js, StringComparison.Ordinal);
        Assert.Contains("/quote/v1/geometry/runtime/telemetry", js, StringComparison.Ordinal);
        Assert.Contains("Local preliminary DFM", js, StringComparison.Ordinal);
        Assert.Contains("maliev:geometry-local-runtime-complete", js, StringComparison.Ordinal);
        Assert.Contains("dispatchLocalAdvisoryTelemetry", js, StringComparison.Ordinal);
        Assert.Contains("postLocalAdvisoryTelemetry", js, StringComparison.Ordinal);
        Assert.Contains("navigator.sendBeacon", js, StringComparison.Ordinal);
        Assert.Contains("NotifyLocalGeometryRuntimeComplete", js, StringComparison.Ordinal);
        Assert.Contains("dispatchLocalAdvisoryStartedTelemetry", js, StringComparison.Ordinal);
        Assert.Contains("status: 'started'", js, StringComparison.Ordinal);
        Assert.Contains("inputByteCount: payload?.inputByteCount ?? null", js, StringComparison.Ordinal);
        Assert.Contains("inputTriangleCount: payload?.inputTriangleCount ?? null", js, StringComparison.Ordinal);
        Assert.Contains("dispatchLocalAdvisoryUnavailableTelemetry", js, StringComparison.Ordinal);
        Assert.Contains("status: 'unavailable'", js, StringComparison.Ordinal);
        Assert.Contains("reason: payload?.reason ?? 'local_runtime_unavailable'", js, StringComparison.Ordinal);
        Assert.Contains("notifyLocalAdvisoryDotNet", js, StringComparison.Ordinal);
        Assert.Contains("normalizeAdvisoryFileBytes(options.fileBytes)", js, StringComparison.Ordinal);
        Assert.Contains("resolveAdvisoryFileBytes(options)", js, StringComparison.Ordinal);
        Assert.Contains("fileBytesProvider", js, StringComparison.Ordinal);
        Assert.Contains("{ fileBytes: runtimeFileBytes, fileName: runtimeFileName }", js, StringComparison.Ordinal);
        Assert.Contains("createLocalViewerMeshFromBuffers", js, StringComparison.Ordinal);
        Assert.Contains("tryLoadLocalViewerMeshFromRuntime", js, StringComparison.Ordinal);
        Assert.Contains("operation: 'extract_mesh'", js, StringComparison.Ordinal);
        Assert.Contains("const wasmUrl = resolveRuntimeAssetUrl(", js, StringComparison.Ordinal);
        Assert.Contains("manifest.assets?.wasm", js, StringComparison.Ordinal);
        Assert.Contains("worker.postMessage({ id: messageId, input, processCode, wasmUrl });", js, StringComparison.Ordinal);
        Assert.Contains("storagePath: result?.storagePath ?? null", js, StringComparison.Ordinal);
        Assert.Contains("result.storagePath = typeof options.storagePath === 'string' && options.storagePath.trim()", js, StringComparison.Ordinal);
        Assert.Contains("metrics: result?.metrics", js, StringComparison.Ordinal);
        Assert.Contains("issues,", js, StringComparison.Ordinal);
        Assert.Contains("local_primary", js, StringComparison.Ordinal);
        Assert.Contains("primary_interactive", js, StringComparison.Ordinal);
        Assert.DoesNotContain("authority !== 'advisory'", js, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotePartViewerJs_tries_browser_runtime_mesh_extraction_before_signed_url_fallback()
    {
        var js = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-part-viewer.js")
            .ReplaceLineEndings("\n");

        var normalizeIndex = js.IndexOf("browserFileClientId:", StringComparison.Ordinal);
        Assert.True(normalizeIndex >= 0, "normalizeViewerSettings must preserve the retained browser upload id.");

        var localIndex = js.IndexOf("tryLoadLocalViewerMeshFromRuntime(canvasId, scene, forcedExt, viewerSettings", StringComparison.Ordinal);
        var fallbackIndex = js.IndexOf("_loadAttempt(0);", StringComparison.Ordinal);

        Assert.True(localIndex >= 0, "initialize should try local runtime mesh extraction.");
        Assert.True(fallbackIndex >= 0, "initialize should retain signed-url SceneLoader fallback.");
        Assert.True(localIndex < fallbackIndex, "local runtime mesh extraction must run before signed-url fallback.");
    }

    [Fact]
    public void QuoteGeometryRuntimeTelemetry_tracks_browser_local_start_attempts()
    {
        var controller = ReadRepoFile("Maliev.QuoteEngine.Bff", "Controllers", "GeometryRuntimeController.cs");
        var metrics = ReadRepoFile("Maliev.QuoteEngine.Bff", "BffMetrics.cs");
        var js = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-part-viewer.js")
            .ReplaceLineEndings("\n");

        Assert.Contains("public bool IsStarted", controller, StringComparison.Ordinal);
        Assert.Contains("InputByteCount", controller, StringComparison.Ordinal);
        Assert.Contains("InputTriangleCount", controller, StringComparison.Ordinal);
        Assert.Contains("RecordBrowserDfmRuntimeStart", controller, StringComparison.Ordinal);
        Assert.Contains("inputByteCount: result?.inputByteCount ?? null", js, StringComparison.Ordinal);
        Assert.Contains("inputTriangleCount: result?.inputTriangleCount ?? null", js, StringComparison.Ordinal);
        Assert.Contains("quote_browser_dfm_runtime_starts", metrics, StringComparison.Ordinal);
        Assert.Contains("quote_browser_dfm_runtime_input_bytes", metrics, StringComparison.Ordinal);
        Assert.Contains("quote_browser_dfm_runtime_input_triangles", metrics, StringComparison.Ordinal);
        Assert.Contains("quote_dfm_server_avoided_input_bytes", metrics, StringComparison.Ordinal);
        Assert.Contains("quote_dfm_server_avoided_input_triangles", metrics, StringComparison.Ordinal);
        Assert.Contains("RecordBrowserDfmRuntimeStart", metrics, StringComparison.Ordinal);
        Assert.Contains("function dispatchLocalAdvisoryStartedTelemetry(payload)", js, StringComparison.Ordinal);
        Assert.Contains("dispatchLocalAdvisoryStartedTelemetry(payload);", js, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotePartViewModel_tracks_browser_local_dfm_running_state()
    {
        var started = new LocalGeometryRuntimeStarted { ProcessCode = "fdm" };
        var part = new QuotePartViewModel
        {
            ProcessId = "fdm",
            LocalDfmRuntimeRunningProcessId = started.ProcessCode,
            LocalDfmRuntimeStartedAtUtc = DateTimeOffset.UtcNow
        };

        Assert.Equal("fdm", part.LocalDfmRuntimeRunningProcessId);
        Assert.True(part.LocalDfmRuntimeStartedAtUtc.HasValue);
    }

    [Fact]
    public void QuotePartViewerJs_clears_local_dfm_panel_only_when_blazor_accepts_result()
    {
        var js = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-part-viewer.js");

        Assert.Contains("const accepted = await dotNetRef.invokeMethodAsync('NotifyLocalGeometryRuntimeComplete', result);", js, StringComparison.Ordinal);
        Assert.Contains("return accepted === true;", js, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotePartViewerJs_suppresses_internal_local_dfm_panel_when_blazor_handles_status()
    {
        var js = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-part-viewer.js")
            .ReplaceLineEndings("\n");

        Assert.Contains("function shouldRenderLocalAdvisoryPanel(options)", js, StringComparison.Ordinal);
        Assert.Contains("const renderLocalPanel = shouldRenderLocalAdvisoryPanel(options);", js, StringComparison.Ordinal);
        Assert.DoesNotContain("\n    renderLocalAdvisoryStatus(canvasId, 'pending');", js, StringComparison.Ordinal);
        Assert.Contains("if (renderLocalPanel) renderLocalAdvisoryStatus(canvasId, 'pending');", js, StringComparison.Ordinal);
        Assert.Contains("if (renderLocalPanel) renderLocalAdvisoryStatus(canvasId, 'complete', result);", js, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotePartViewerJs_notifies_blazor_when_local_dfm_starts()
    {
        var js = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-part-viewer.js")
            .ReplaceLineEndings("\n");

        Assert.Contains("function notifyLocalAdvisoryStartedDotNet(dotNetRef, payload)", js, StringComparison.Ordinal);
        Assert.Contains("NotifyLocalGeometryRuntimeStarted", js, StringComparison.Ordinal);
        Assert.Contains("const LOCAL_ADVISORY_MANIFEST_RETRY_ATTEMPTS = 6;", js, StringComparison.Ordinal);
        Assert.Contains("function waitLocalAdvisoryManifestRetry(attempt)", js, StringComparison.Ordinal);
        Assert.Contains("attempt < LOCAL_ADVISORY_MANIFEST_RETRY_ATTEMPTS - 1", js, StringComparison.Ordinal);
        Assert.Contains("await waitLocalAdvisoryManifestRetry(attempt);", js, StringComparison.Ordinal);
        Assert.DoesNotContain("for (let attempt = 0; attempt < 2; attempt += 1)", js, StringComparison.Ordinal);

        var runStart = js.IndexOf("export async function runLocalAdvisoryGeometry", StringComparison.Ordinal);
        Assert.True(runStart >= 0, "runLocalAdvisoryGeometry must exist.");
        var runEnd = js.IndexOf("\nfunction getPointerRenderCoordinates", runStart, StringComparison.Ordinal);
        Assert.True(runEnd > runStart, "runLocalAdvisoryGeometry block must end before pointer helpers.");
        var runBlock = js[runStart..runEnd];

        var inputIndex = runBlock.IndexOf("const runtimeInput =", StringComparison.Ordinal);
        var startedIndex = runBlock.IndexOf("await notifyLocalAdvisoryStartedDotNet(\n        options.dotNetRef", StringComparison.Ordinal);
        var fetchIndex = runBlock.IndexOf("const manifestResponse = await fetch", StringComparison.Ordinal);

        Assert.True(inputIndex >= 0, "runtime input creation must exist");
        Assert.True(startedIndex > inputIndex, "local DFM start must be reported after local input is available");
        Assert.True(fetchIndex > startedIndex, "local DFM start must be reported before manifest/worker work can stall");
    }

    [Fact]
    public void QuotePartViewerJs_uses_geometry_manifest_device_profile_timeout()
    {
        var js = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-part-viewer.js")
            .ReplaceLineEndings("\n");

        Assert.Contains("function resolveLocalAdvisoryDeviceProfileName()", js, StringComparison.Ordinal);
        Assert.Contains("function resolveLocalAdvisoryTimeoutMs(manifest, options = {})", js, StringComparison.Ordinal);
        Assert.Contains("manifest?.deviceProfiles", js, StringComparison.Ordinal);
        Assert.Contains("resolveLocalAdvisoryTimeoutMs(manifest, options));", js, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Number(options.timeoutMs) > 0 ? Number(options.timeoutMs) : 15000",
            js,
            StringComparison.Ordinal);
    }

    [Fact]
    public void QuotePartViewerJs_serializes_local_geometry_workers()
    {
        var js = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-part-viewer.js")
            .ReplaceLineEndings("\n");

        Assert.Contains("let localAdvisoryWorkerQueue = Promise.resolve();", js, StringComparison.Ordinal);
        Assert.Contains("function enqueueLocalAdvisoryWorker(work)", js, StringComparison.Ordinal);
        Assert.Contains("localAdvisoryWorkerQueue = run.catch(() => {});", js, StringComparison.Ordinal);
        Assert.Contains("const result = await enqueueLocalAdvisoryWorker(() => {", js, StringComparison.Ordinal);
        Assert.Contains("if (localAdvisoryRuns[canvasId] !== runId) return null;", js, StringComparison.Ordinal);
        Assert.Contains("return analyzeWithLocalAdvisoryWorker(", js, StringComparison.Ordinal);
        Assert.Contains("if (!result) {", js, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotePartViewerJs_honors_geometry_manifest_device_input_limits()
    {
        var js = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-part-viewer.js")
            .ReplaceLineEndings("\n");

        Assert.Contains("function isLocalAdvisoryInputWithinDeviceProfile(manifest, input)", js, StringComparison.Ordinal);
        Assert.Contains("profile?.maxInputBytes", js, StringComparison.Ordinal);
        Assert.Contains("profile?.maxTriangles", js, StringComparison.Ordinal);
        Assert.Contains("function getArrayLikeByteLength(values, bytesPerElement)", js, StringComparison.Ordinal);
        Assert.Contains("countLocalAdvisoryInputTriangles(input)", js, StringComparison.Ordinal);
        Assert.Contains("if (!isLocalAdvisoryInputWithinDeviceProfile(manifest, runtimeInput))", js, StringComparison.Ordinal);
        Assert.Contains("clearLocalAdvisoryPanel(canvasId);", js, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotePartViewerJs_notifies_blazor_when_local_dfm_cannot_run()
    {
        var js = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-part-viewer.js")
            .ReplaceLineEndings("\n");

        Assert.Contains("function notifyLocalAdvisoryUnavailableDotNet(dotNetRef, payload)", js, StringComparison.Ordinal);
        Assert.Contains("NotifyLocalGeometryRuntimeUnavailable", js, StringComparison.Ordinal);
        Assert.Contains("options.dotNetRef", js, StringComparison.Ordinal);
        Assert.Contains("dispatchLocalAdvisoryUnavailableTelemetry(payload);", js, StringComparison.Ordinal);
        Assert.Contains("'input_too_large'", js, StringComparison.Ordinal);
        Assert.Contains("'worker_failed'", js, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotePartViewerJs_refuses_missing_process_code_instead_of_defaulting_to_fdm()
    {
        var js = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-part-viewer.js")
            .ReplaceLineEndings("\n");

        Assert.Contains("const processCode = typeof options.processCode === 'string' && options.processCode.trim()", js, StringComparison.Ordinal);
        Assert.Contains("processCode,", js, StringComparison.Ordinal);
        Assert.Contains("unavailablePayload('process_code_missing')", js, StringComparison.Ordinal);
        Assert.DoesNotContain("options.processCode ?? 'FDM'", js, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotePartViewer_and_detail_card_wire_browser_local_dfm_to_part_state()
    {
        var viewer = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartViewer.razor");
        var detail = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartDetailCard.razor");
        var workspace = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "QuoteWorkspace.razor");
        var model = ReadRepoFile("Maliev.QuoteEngine.Client", "Models", "QuotePartViewModel.cs");

        Assert.Contains("Func<LocalGeometryRuntimeResult, Task<bool>>? OnLocalGeometryRuntimeCompleted", viewer, StringComparison.Ordinal);
        Assert.Contains("Func<LocalGeometryRuntimeStarted, Task>? OnLocalGeometryRuntimeStarted", viewer, StringComparison.Ordinal);
        Assert.Contains("NotifyLocalGeometryRuntimeStarted", viewer, StringComparison.Ordinal);
        Assert.Contains("public async Task NotifyLocalGeometryRuntimeStarted(LocalGeometryRuntimeStarted result)", viewer, StringComparison.Ordinal);
        Assert.Contains("NotifyLocalGeometryRuntimeComplete", viewer, StringComparison.Ordinal);
        Assert.Contains("public async Task<bool> NotifyLocalGeometryRuntimeComplete(LocalGeometryRuntimeResult result)", viewer, StringComparison.Ordinal);
        Assert.Contains("RunLocalGeometryRuntimeAsync(localDfmProcessCode)", viewer, StringComparison.Ordinal);
        Assert.Contains("[Parameter] public string FileExtension", viewer, StringComparison.Ordinal);
        Assert.Contains("[Parameter] public string? BrowserFileClientId", viewer, StringComparison.Ordinal);
        Assert.Contains("[Parameter] public string? BrowserFileName", viewer, StringComparison.Ordinal);
        Assert.Contains("[Parameter] public string? StoragePath", viewer, StringComparison.Ordinal);
        Assert.Contains("clientUploadId = BrowserFileClientId", viewer, StringComparison.Ordinal);
        Assert.Contains("fileName = BrowserFileName", viewer, StringComparison.Ordinal);
        Assert.Contains("storagePath = StoragePath", viewer, StringComparison.Ordinal);
        Assert.Contains("fileBytesProvider = \"quoteEngineUploads\"", viewer, StringComparison.Ordinal);
        Assert.Contains("_canvasId, GlbUrl, FileExtension", viewer, StringComparison.Ordinal);
        Assert.Contains("browserFileClientId = BrowserFileClientId", viewer, StringComparison.Ordinal);
        Assert.Contains("browserFileName = BrowserFileName", viewer, StringComparison.Ordinal);
        Assert.Contains("FileExtension=\"@ResolveViewerFileExtension()\"", detail, StringComparison.Ordinal);
        Assert.Contains("BrowserFileClientId=\"@Part.ClientFileId\"", detail, StringComparison.Ordinal);
        Assert.Contains("BrowserFileName=\"@Part.FileName\"", detail, StringComparison.Ordinal);
        Assert.Contains("StoragePath=\"@Part.StoragePath\"", detail, StringComparison.Ordinal);
        Assert.Contains("OnLocalGeometryRuntimeCompleted=\"@HandleLocalGeometryRuntimeCompletedAsync\"", detail, StringComparison.Ordinal);
        Assert.Contains("OnLocalGeometryRuntimeStarted=\"@HandleLocalGeometryRuntimeStartedAsync\"", detail, StringComparison.Ordinal);
        Assert.Contains("private async Task HandleLocalGeometryRuntimeStartedAsync(LocalGeometryRuntimeStarted result)", detail, StringComparison.Ordinal);
        Assert.Contains("Part.LocalDfmRuntimeRunningProcessId = result.ProcessCode", detail, StringComparison.Ordinal);
        Assert.Contains("Part.LocalDfmRuntimeStartedAtUtc = DateTimeOffset.UtcNow", detail, StringComparison.Ordinal);
        Assert.Contains("private async Task<bool> HandleLocalGeometryRuntimeCompletedAsync(LocalGeometryRuntimeResult result)", detail, StringComparison.Ordinal);
        Assert.Contains("QeLocalDfmMapper.TryApply(Part, result)", detail, StringComparison.Ordinal);
        Assert.Contains("QeLocalDfmMapper.HasCurrentProcessReport(Part)", detail, StringComparison.Ordinal);
        Assert.Contains("public string? ClientFileId { get; set; }", model, StringComparison.Ordinal);
        Assert.Contains("public string? LocalDfmRuntimeRunningProcessId { get; set; }", model, StringComparison.Ordinal);
        Assert.Contains("public DateTimeOffset? LocalDfmRuntimeStartedAtUtc { get; set; }", model, StringComparison.Ordinal);
        Assert.Contains("ClientFileId = candidate.ClientFileId", workspace, StringComparison.Ordinal);
        Assert.Contains("TryApplyLocalViewerUrlAsync(part)", workspace, StringComparison.Ordinal);
        Assert.Contains("quoteEngineUploads.getObjectUrl", workspace, StringComparison.Ordinal);
        Assert.Contains("CanUseBrowserFileViewer", workspace, StringComparison.Ordinal);
        Assert.Contains("ResolveBrowserFileViewerExtensionAsync(part)", workspace, StringComparison.Ordinal);
        Assert.Contains("Api.GetGeometryRuntimeManifestAsync", workspace, StringComparison.Ordinal);
        Assert.Contains("artifactPolicy", workspace, StringComparison.Ordinal);
        Assert.Contains("directBrowserViewerExtensions", workspace, StringComparison.Ordinal);
        Assert.Contains("DefaultBrowserViewerExtensions", workspace, StringComparison.Ordinal);
        Assert.Contains("QeLocalDfmMapper.ApplyAnalysisStatus(part, status)", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("part.FdmDfmReport = status.FdmReport", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("part.SlaDfmReport = status.SlaReport", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("part.CncDfmReport = status.CncReport", workspace, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotePartViewerJs_initial_local_dfm_uses_retained_browser_file_bytes()
    {
        var js = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-part-viewer.js")
            .ReplaceLineEndings("\n");

        var runIndex = js.IndexOf("runLocalAdvisoryGeometry(canvasId, {", StringComparison.Ordinal);
        Assert.True(runIndex >= 0, "Initial model-load local DFM invocation must exist.");

        var runBlock = js[runIndex..js.IndexOf("});", runIndex, StringComparison.Ordinal)];
        Assert.Contains("clientUploadId: viewerSettings.browserFileClientId ?? viewerSettings.clientUploadId", runBlock, StringComparison.Ordinal);
        Assert.Contains("fileName: viewerSettings.browserFileName ?? viewerSettings.fileName", runBlock, StringComparison.Ordinal);
        Assert.Contains("fileBytesProvider: viewerSettings.fileBytesProvider ?? 'quoteEngineUploads'", runBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotePartViewer_DefersBrowserLocalDfmUntilJsModuleExists()
    {
        var viewer = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartViewer.razor")
            .ReplaceLineEndings("\n");

        Assert.Contains("private string? _pendingLocalDfmProcessCode;", viewer, StringComparison.Ordinal);
        Assert.Contains("private bool _viewerMaterialPushPending;", viewer, StringComparison.Ordinal);
        Assert.Contains("protected override async Task OnAfterRenderAsync(bool firstRender)", viewer, StringComparison.Ordinal);
        Assert.Contains("await PushViewerMaterialStateAsync(ProcessId != _lastProcessId ? ProcessId : null);", viewer, StringComparison.Ordinal);
        Assert.Contains("_pendingLocalDfmProcessCode = runtimeProcessCode;", viewer, StringComparison.Ordinal);
        Assert.Contains("await RunLocalGeometryRuntimeAsync(localDfmProcessCode);", viewer, StringComparison.Ordinal);
        Assert.Contains("private async Task PushViewerMaterialStateAsync(string? localDfmProcessCode = null)", viewer, StringComparison.Ordinal);

        var pushMethodStart = viewer.IndexOf("private async Task PushViewerMaterialStateAsync(string? localDfmProcessCode = null)", StringComparison.Ordinal);
        var materialChangedStart = viewer.IndexOf("private bool MaterialChanged()", StringComparison.Ordinal);
        Assert.True(pushMethodStart >= 0, "QePartViewer must have a helper for pushing viewer material/process state.");
        Assert.True(materialChangedStart > pushMethodStart, "QePartViewer push helper must appear before MaterialChanged.");
        var pushMethod = viewer[pushMethodStart..materialChangedStart];

        var moduleGuardIndex = pushMethod.IndexOf("if (_module is null)", StringComparison.Ordinal);
        var lastProcessUpdateIndex = pushMethod.IndexOf("_lastProcessId = ProcessId;", StringComparison.Ordinal);
        Assert.True(moduleGuardIndex >= 0, "QePartViewer must check for a missing JS module before pushing material/process state.");
        Assert.True(lastProcessUpdateIndex >= 0, "QePartViewer must track the last pushed process id.");
        Assert.True(
            moduleGuardIndex < lastProcessUpdateIndex,
            "QePartViewer must not mark material/process state as pushed before the JS module exists.");
    }

    [Fact]
    public void QuotePartViewer_and_detail_card_wire_browser_local_dfm_unavailable_to_part_state()
    {
        var viewer = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartViewer.razor");
        var detail = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartDetailCard.razor");
        var model = ReadRepoFile("Maliev.QuoteEngine.Client", "Models", "QuotePartViewModel.cs");
        var mapper = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QeLocalDfmMapper.cs");

        Assert.Contains("Func<LocalGeometryRuntimeUnavailable, Task>? OnLocalGeometryRuntimeUnavailable", viewer, StringComparison.Ordinal);
        Assert.Contains("NotifyLocalGeometryRuntimeUnavailable", viewer, StringComparison.Ordinal);
        Assert.Contains("OnLocalGeometryRuntimeUnavailable=\"@HandleLocalGeometryRuntimeUnavailableAsync\"", detail, StringComparison.Ordinal);
        Assert.Contains("Part.LocalDfmRuntimeUnavailable = true", detail, StringComparison.Ordinal);
        Assert.Contains("!Part.LocalDfmRuntimeUnavailable", detail, StringComparison.Ordinal);
        Assert.Contains("Part.LocalDfmRuntimeUnavailable ||", detail, StringComparison.Ordinal);
        Assert.Contains("LocalDfmRuntimeUnavailable", model, StringComparison.Ordinal);
        Assert.Contains("part.LocalDfmRuntimeUnavailable = false", mapper, StringComparison.Ordinal);
        Assert.Contains("part.LocalDfmRuntimeRunningProcessId = null", mapper, StringComparison.Ordinal);
        Assert.Contains("part.LocalDfmRuntimeStartedAtUtc = null", mapper, StringComparison.Ordinal);
    }

    [Fact]
    public void QuoteDfmOverlayPanel_labels_browser_local_analysis()
    {
        var panel = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QeDfmOverlayPanel.razor");
        var detail = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartDetailCard.razor");

        Assert.Contains("[Parameter] public bool LocalAnalyzing { get; set; }", panel, StringComparison.Ordinal);
        Assert.Contains("Local DFM running on this device", panel, StringComparison.Ordinal);
        Assert.Contains("LocalAnalyzing=\"@IsLocalDfmRuntimeRunning\"", detail, StringComparison.Ordinal);
        Assert.Contains("private bool IsLocalDfmRuntimeRunning", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Quote_process_changes_clear_terminal_local_dfm_unavailable_state()
    {
        var model = ReadRepoFile("Maliev.QuoteEngine.Client", "Models", "QuotePartViewModel.cs");
        var sidebar = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartConfigSidebar.razor");
        var bulkTable = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QeBulkTable.razor");
        var workspace = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "QuoteWorkspace.razor");

        Assert.Contains("ClearLocalDfmRuntimeState", model, StringComparison.Ordinal);
        Assert.Contains("LocalDfmRuntimeUnavailable = false", model, StringComparison.Ordinal);
        Assert.Contains("LocalDfmRuntimeUnavailableReason = null", model, StringComparison.Ordinal);
        Assert.Contains("LocalDfmRuntimeRunningProcessId = null", model, StringComparison.Ordinal);
        Assert.Contains("LocalDfmRuntimeStartedAtUtc = null", model, StringComparison.Ordinal);
        Assert.Contains("Part.ApplyProjectNewProcessSelection(process.Id, ReferenceData)", sidebar, StringComparison.Ordinal);
        Assert.Contains("part.ApplyProjectNewProcessSelection(processId, ReferenceData())", bulkTable, StringComparison.Ordinal);
        Assert.Contains("part.LocalDfmRuntimeUnavailable = false", workspace, StringComparison.Ordinal);
        Assert.Contains("part.LocalDfmRuntimeUnavailableReason = null", workspace, StringComparison.Ordinal);
        Assert.Contains("part.LocalDfmRuntimeRunningProcessId = null", workspace, StringComparison.Ordinal);
        Assert.Contains("part.LocalDfmRuntimeStartedAtUtc = null", workspace, StringComparison.Ordinal);
    }

    private static string ExtractWindowHandleBlock(string js, string handleName)
    {
        var start = js.IndexOf($"window.{handleName}", StringComparison.Ordinal);
        if (start < 0) return "";
        var end = js.IndexOf("};", start, StringComparison.Ordinal);
        return end > start ? js[start..end] : js[start..];
    }

    private static string ExtractBlock(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0) return string.Empty;

        var firstBrace = source.IndexOf('{', start);
        if (firstBrace < 0) return source[start..];

        var depth = 0;
        for (var index = firstBrace; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[start..(index + 1)];
                }
            }
        }

        return source[start..];
    }

    private static FileAnalyzedEvent BuildFileAnalyzedEvent(
        string storagePath, string? glbStoragePath, string? thumbnailStoragePath,
        int bodyCount, bool isManifold, double volumeCm3,
        string? viewerStoragePath = null,
        string? viewerFileExtension = null)
    {
        return new FileAnalyzedEvent
        {
            Payload = new FileAnalyzedEventPayload
            {
                StoragePath = storagePath,
                GlbStoragePath = glbStoragePath,
                ViewerStoragePath = viewerStoragePath,
                ViewerFileExtension = viewerFileExtension,
                ThumbnailStoragePath = thumbnailStoragePath,
                BodyCount = bodyCount,
                Metrics = new FileAnalyzedEventPayloadMetrics
                {
                    IsManifold = isManifold,
                    VolumeCm3 = volumeCm3,
                    SurfaceAreaCm2 = 0.0
                }
            }
        };
    }

    private static QuoteReferenceDataResponse BuildConfigurationReferenceData() => new(
        Processes:
        [
            new("fdm", "FDM", "Fused filament fabrication", ["stl"]),
            new("cnc", "CNC machining", "Machined parts", ["step"])
        ],
        Materials:
        [
            new("pla-black", "fdm", "PLA Black", "PLA", 1.24m, "Matte"),
            new("al6061", "cnc", "Aluminum 6061", "6061-T6", 2.70m, "Natural")
        ],
        Finishes:
        [
            new("fdm-matte", "fdm", "MATTE", "Matte", "Printed matte finish", 1.0m),
            new("cnc-as-machined", "cnc", "AS_MACHINED", "As machined", "Standard machined finish", 1.0m)
        ],
        Tolerances:
        [
            new("iso-2768-c", "cnc", "ISO2768_C", "ISO 2768-c", "Coarse machining tolerance", 1.0m),
            new("iso-2768-m", "cnc", "ISO2768_M", "ISO 2768-m", "Medium machining tolerance", 1.1m)
        ],
        InspectionLevels: [new("STANDARD", "Standard", "Standard inspection", 1.0m)],
        RoughnessOptions:
        [
            new("RA_3_2", "cnc", "Ra 3.2", "Standard machined roughness", 1.0m),
            new("RA_1_6", "cnc", "Ra 1.6", "Fine machined roughness", 1.08m)
        ],
        Colors: [new("natural", "Natural", "#d6d2c8", ["al6061"])],
        LeadTimes: [],
        ProcessOptions: [new("paint_color", "fdm", "Paint color", "text", string.Empty, "Paint color", [])],
        SupportedExtensions: ["stl", "step"]);

    private static string ReadRepoFile(params string[] pathParts)
    {
        return File.ReadAllText(RepoPath(pathParts)).ReplaceLineEndings("\n");
    }

    private static string RepoPath(params string[] pathParts)
    {
        var root = FindRepoRoot();
        return Path.Combine([root, .. pathParts]);
    }

    private static string FindRepoRoot()
    {
        var startDirectories = new List<string>();
        var configuredRoot = Environment.GetEnvironmentVariable("MALIEV_QUOTEENGINE_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            startDirectories.Add(configuredRoot);
        }

        startDirectories.Add(GetSourceDirectory());
        startDirectories.Add(AppContext.BaseDirectory);
        startDirectories.Add(Directory.GetCurrentDirectory());

        foreach (var startDirectory in startDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var directory = new DirectoryInfo(startDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Maliev.QuoteEngine.slnx"))
                    && Directory.Exists(Path.Combine(directory.FullName, "Maliev.QuoteEngine.Client")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Maliev.QuoteEngine repository root.");
    }

    private static string GetSourceDirectory([CallerFilePath] string sourceFile = "") => Path.GetDirectoryName(sourceFile) ?? Directory.GetCurrentDirectory();

    [Fact]
    public void QuotePartViewerJs_NormalizeViewerSettings_IncludesInitialRenderModeAndTransition()
    {
        var js = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-part-viewer.js").ReplaceLineEndings("\n");

        // Verify normalizeViewerSettings parses initialRenderMode, targetRenderMode, and renderModeTransition
        Assert.Contains("const initialRenderMode = normalizeMode(settings.initialRenderMode);", js, StringComparison.Ordinal);
        Assert.Contains("const targetRenderMode = normalizeMode(settings.targetRenderMode);", js, StringComparison.Ordinal);
        Assert.Contains("const transition = settings.renderModeTransition", js, StringComparison.Ordinal);
        Assert.Contains("transitionEnabled", js, StringComparison.Ordinal);
        Assert.Contains("fallbackDelayMs", js, StringComparison.Ordinal);
        Assert.Contains("transitionMs", js, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotePartViewerJs_StagedRender_InitialRenderModeDefaultsToSolidWhenTargetIsRealistic()
    {
        var js = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-part-viewer.js").ReplaceLineEndings("\n");

        // Verify that when target is realistic and initial is not realistic, solid is used for first paint
        Assert.Contains("const effectiveInitialMode = (targetMode === 'realistic' && initialMode !== 'realistic')", js, StringComparison.Ordinal);
        Assert.Contains("setRenderMode(canvasId, effectiveInitialMode);", js, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotePartViewerJs_StagedRender_ScheduleFallbackTransition_UsesConfiguredDelayAndDuration()
    {
        var js = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-part-viewer.js").ReplaceLineEndings("\n");

        // Verify fallback scheduling with configurable delay and transition duration
        Assert.Contains("function scheduleRenderModeFallback(canvasId, delayMs = 1200, transitionMs = 250)", js, StringComparison.Ordinal);
        Assert.Contains("clearTimeout(state.fallbackTimer);", js, StringComparison.Ordinal);
        Assert.Contains("state.fallbackTimer = setTimeout(() =>", js, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotePartViewerJs_StagedRender_TransitionToRealistic_AnimatesAlphaFade()
    {
        var js = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-part-viewer.js").ReplaceLineEndings("\n");

        // Verify transition animates material alpha from 0 to 1
        Assert.Contains("function transitionToRealistic(canvasId, transitionMs = 250)", js, StringComparison.Ordinal);
        Assert.Contains("sharedRealisticMaterial.alpha = 0;", js, StringComparison.Ordinal);
        Assert.Contains("animateMaterialAlpha(sharedRealisticMaterial, 0, 1, scene, transitionMs);", js, StringComparison.Ordinal);
        Assert.Contains("activeRenderModes[canvasId] = 'realistic';", js, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotePartViewerJs_StagedRender_RuntimeComplete_CancelsFallbackAndTransitions()
    {
        var js = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-part-viewer.js").ReplaceLineEndings("\n");

        // Verify that on runtime complete, fallback timer is cleared and transition occurs
        Assert.Contains("if (transitionState.fallbackTimer) {", js, StringComparison.Ordinal);
        Assert.Contains("clearTimeout(transitionState.fallbackTimer);", js, StringComparison.Ordinal);
        Assert.Contains("transitionState.fallbackTimer = null;", js, StringComparison.Ordinal);
        Assert.Contains("transitionToRealistic(canvasId, transitionState.transitionMs);", js, StringComparison.Ordinal);
        Assert.Contains("transitionState.completed = true;", js, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotePartViewerJs_StagedRender_ManualModeChange_UpdatesTransitionState()
    {
        var js = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-part-viewer.js").ReplaceLineEndings("\n");

        // Verify that manual render mode change updates transition state
        Assert.Contains("const transitionState = renderModeTransitionState[canvasId];", js, StringComparison.Ordinal);
        Assert.Contains("if (mode === 'realistic') {", js, StringComparison.Ordinal);
        Assert.Contains("targetRenderModes[canvasId] = mode;", js, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotePartViewerJs_UsesProjectNewCanvasFadeInContract()
    {
        var js = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-part-viewer.js").ReplaceLineEndings("\n");

        Assert.Contains("canvas.style.opacity = '0';", js, StringComparison.Ordinal);
        Assert.Contains("canvas.style.transition = `opacity ${CONFIG.ANIMATION_CANVAS_FADE_IN} ease-in`;", js, StringComparison.Ordinal);
        Assert.Contains("requestAnimationFrame(() => { canvas.style.opacity = '1'; });", js, StringComparison.Ordinal);
        Assert.DoesNotContain("engine.loadingScreen = { displayLoadingUI: () => {}, hideLoadingUI: () => {} };", js, StringComparison.Ordinal);
        Assert.DoesNotContain("canvas.style.filter = 'blur(", js, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotePartViewerSettingsDto_IncludesRenderModeTransitionFields()
    {
        var dtoSource = ReadRepoFile("Maliev.QuoteEngine.Shared", "Quotes", "QuoteEngineDtos.cs");

        Assert.Contains("public string InitialRenderMode", dtoSource, StringComparison.Ordinal);
        Assert.Contains("public string TargetRenderMode", dtoSource, StringComparison.Ordinal);
        Assert.Contains("public RenderModeTransitionSettings RenderModeTransition", dtoSource, StringComparison.Ordinal);
        Assert.Contains("public bool Enabled", dtoSource, StringComparison.Ordinal);
        Assert.Contains("public string Trigger", dtoSource, StringComparison.Ordinal);
        Assert.Contains("public int FallbackDelayMs", dtoSource, StringComparison.Ordinal);
        Assert.Contains("public int TransitionMs", dtoSource, StringComparison.Ordinal);
    }

    [Fact]
    public void QePartViewer_CaptureViewerRuntimeSettings_IncludesNewFields()
    {
        var viewer = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartViewer.razor").ReplaceLineEndings("\n");

        Assert.Contains("initialRenderMode", viewer, StringComparison.Ordinal);
        Assert.Contains("targetRenderMode", viewer, StringComparison.Ordinal);
        Assert.Contains("renderModeTransition", viewer, StringComparison.Ordinal);
        Assert.Contains("enabled = transition.Enabled", viewer, StringComparison.Ordinal);
        Assert.Contains("fallbackDelayMs = transition.FallbackDelayMs", viewer, StringComparison.Ordinal);
        Assert.Contains("transitionMs = transition.TransitionMs", viewer, StringComparison.Ordinal);
    }

    private static string ExtractSourceBlock(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start marker was not found: {startMarker}");

        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"End marker was not found after start marker: {endMarker}");

        return source[start..end];
    }
}

// ── Test helpers for consumer tests ─────────────────────────────────────────

file sealed class FakeQuoteUploadServiceClient(string glbUrl)
    : QuoteUploadServiceClient(new HttpClient(), NullLogger<QuoteUploadServiceClient>.Instance)
{
    // Returns glbUrl for any storage path. If testing both GLB + thumbnail URLs,
    // use a subclass that distinguishes by path suffix.
    public override Task<string> GetDownloadUrlByPathAsync(string storagePath,
        int expirationMinutes = 60, CancellationToken ct = default)
        => Task.FromResult(glbUrl);
}

file sealed class ThrowingQuoteUploadServiceClient()
    : QuoteUploadServiceClient(new HttpClient(), NullLogger<QuoteUploadServiceClient>.Instance)
{
    public override Task<string> GetDownloadUrlByPathAsync(string storagePath,
        int expirationMinutes = 60, CancellationToken ct = default)
        => throw new HttpRequestException("Simulated signing failure");
}
