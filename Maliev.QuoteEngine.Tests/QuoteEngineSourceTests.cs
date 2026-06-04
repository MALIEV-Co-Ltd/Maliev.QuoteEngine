using MassTransit;
using Maliev.MessagingContracts.Contracts.Geometry;
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
        Assert.True(deserialized.ViewerSettings.EdgesEnabled);
        Assert.False(deserialized.ViewerSettings.GridEnabled);
        Assert.True(deserialized.ViewerSettings.DfmOverlayEnabled);
        Assert.Contains("\"finishId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"selectedBodyIndex\"", json, StringComparison.Ordinal);
        Assert.Contains("\"drawingFiles\"", json, StringComparison.Ordinal);
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
            "_parts.Add(part);\n            _selectedPartIndex = _parts.Count - 1;\n            await InvokeAsync(StateHasChanged);\n            try",
            workspace,
            StringComparison.Ordinal);
        Assert.Contains(
            "part.Status = \"Upload failed\";\n                _error = ex.Message;\n                await ScheduleBrowserUploadFileCleanupAsync(candidate.ClientFileId);\n                await InvokeAsync(StateHasChanged);",
            workspace,
            StringComparison.Ordinal);
    }

    [Fact]
    public void New_quote_workspace_gives_anonymous_users_demo_and_help_actions()
    {
        var source = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "QuoteWorkspace.razor");
        var script = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-upload.js");

        Assert.DoesNotContain("Try the Quote Engine with a MALIEV sample file", source, StringComparison.Ordinal);
        Assert.Contains("Use sample file", source, StringComparison.Ordinal);
        Assert.Contains("LoadSampleFileAsync", source, StringComparison.Ordinal);
        Assert.Contains("await LoadDemoProjectAsync();", source, StringComparison.Ordinal);
        Assert.Contains("private bool ShowDemoSampleCard => !IsSignedIn && !IsDemoMode;", source, StringComparison.Ordinal);
        Assert.Contains("private bool ShowLaunchAccountCard => !IsSignedIn && !IsDemoMode;", source, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-launch-account-card\"", source, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-primary-btn qe-launch-signin\"", source, StringComparison.Ordinal);
        Assert.Contains("href=\"/auth/sign-in?returnUrl=/quote/new\"", source, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-secondary-btn qe-launch-assistant\"", source, StringComparison.Ordinal);
        Assert.Contains("OpenAssistantAsync", source, StringComparison.Ordinal);
        Assert.Contains("ApplyDefaultRouting(part, candidate.FileName)", source, StringComparison.Ordinal);
        Assert.Contains("private bool IsWorkspaceReady => _parts.Count > 0;", source, StringComparison.Ordinal);
        Assert.Contains("qe-pn-root is-launch-screen", source, StringComparison.Ordinal);
        Assert.Contains("qe-pn-root is-workspace-ready", source, StringComparison.Ordinal);
        Assert.Contains("@if (IsWorkspaceReady)", source, StringComparison.Ordinal);
        Assert.Contains("RegisterActiveDropzoneAsync", source, StringComparison.Ordinal);
        Assert.Contains("quoteEngineUploads.unregisterDropzone", source, StringComparison.Ordinal);
        Assert.DoesNotContain("loadSampleFile", script, StringComparison.Ordinal);
        Assert.DoesNotContain("fetch(sampleUrl", script, StringComparison.Ordinal);
        Assert.Contains("HandleDroppedFilesAsync", script, StringComparison.Ordinal);
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");
        Assert.Contains(".qe-dropzone::before", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-dropzone-icon", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-dropzone-icon .mud-icon-root", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-launch-account-card", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-launch-actions", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-launch-assistant .mud-icon-root", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pdc-container {\n    position: relative;", styles, StringComparison.Ordinal);
        Assert.Contains("3D Model", ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartDetailCard.razor"), StringComparison.Ordinal);
        Assert.Contains(".qe-dfm-tab {\n    flex: 1 1 0;", styles, StringComparison.Ordinal);
        Assert.Contains("flex: 0 0 380px;", styles, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: repeat(3, minmax(0, 1fr));", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pn-root.is-launch-screen", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pn-root.is-workspace-ready .qe-plp-root", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pn-root.is-workspace-ready .qe-qsb-root", styles, StringComparison.Ordinal);
        Assert.Contains("@keyframes qe-panel-enter-left", styles, StringComparison.Ordinal);
        Assert.Contains("@keyframes qe-panel-enter-bottom", styles, StringComparison.Ordinal);
        Assert.DoesNotContain(".qe-dropzone .mud-icon-root {", styles, StringComparison.Ordinal);
        Assert.True(File.Exists(RepoPath("Maliev.QuoteEngine.Client", "wwwroot", "samples", "sample.step")));
        Assert.True(File.Exists(RepoPath("Maliev.QuoteEngine.Client", "wwwroot", "models", "sample.glb")));
        Assert.True(File.Exists(RepoPath("Maliev.QuoteEngine.Client", "wwwroot", "images", "generated", "metal-components-cutout.png")));
        Assert.False(File.Exists(RepoPath("Maliev.QuoteEngine.Client", "wwwroot", "sample-file.step")));
        Assert.Contains("\"Sign in to quote\"", source, StringComparison.Ordinal);
        Assert.Contains("Saved temporarily", source, StringComparison.Ordinal);
        Assert.DoesNotContain("qe-qsb-customer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CustomerBoundaryTitle", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CustomerBoundaryHint", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=\"qe-zone-label\">Bill To</span>", source, StringComparison.Ordinal);
        Assert.Contains("QuoteUploadConstraints.SupportedCadAccept", source, StringComparison.Ordinal);
        Assert.Contains("QuoteUploadConstraints.SupportedCadExtensionLabel", source, StringComparison.Ordinal);
        Assert.Contains("QuoteUploadConstraints.MaxFileSizeMegabytes", source, StringComparison.Ordinal);
        Assert.Contains("QuoteUploadHandoffRequest", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Temporary upload storage until you sign in", source, StringComparison.Ordinal);
        Assert.DoesNotContain("max 10 GB per file", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Sign in before uploading customer-owned manufacturing files.", source, StringComparison.Ordinal);
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
    public void Landing_page_is_server_rendered_and_hands_workspace_routes_to_wasm()
    {
        var program = ReadRepoFile("Maliev.QuoteEngine.Bff", "Program.cs");
        var landingPath = RepoPath("Maliev.QuoteEngine.Bff", "Pages", "LandingPageRenderer.cs");
        var authPath = RepoPath("Maliev.QuoteEngine.Bff", "Pages", "AuthPageRenderer.cs");
        var loader = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-engine-loader.js");
        var index = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "index.html");

        Assert.Contains("app.MapGet(\"/\", LandingPageRenderer.RenderAsync)", program, StringComparison.Ordinal);
        // Auth routes redirect to Maliev.Web — QuoteEngine has no own sign-in surface.
        Assert.Contains("app.MapGet(\"/auth/sign-in\"", program, StringComparison.Ordinal);
        Assert.Contains("app.MapGet(\"/auth/sign-up\"", program, StringComparison.Ordinal);
        Assert.Contains("RedirectToWebAuth", program, StringComparison.Ordinal);
        Assert.True(File.Exists(landingPath), "The BFF should own a server-rendered landing page for /.");
        Assert.True(File.Exists(authPath), "The BFF should own server-rendered auth pages before the WASM fallback.");

        var landing = File.ReadAllText(landingPath);
        Assert.Contains("class=\"landing-shell\"", landing, StringComparison.Ordinal);
        Assert.Contains("data-landing-appbar", landing, StringComparison.Ordinal);
        Assert.Contains("@keyframes landing-brand-inset", landing, StringComparison.Ordinal);
        Assert.Contains("@keyframes landing-actions-inset", landing, StringComparison.Ordinal);
        Assert.Contains("\"/auth/sign-in?returnUrl=/quote/new\"", landing, StringComparison.Ordinal);
        Assert.Contains("WorkspaceLinkAttribute(primaryHref)", landing, StringComparison.Ordinal);
        Assert.Contains("WorkspaceLinkAttribute(topActionHref)", landing, StringComparison.Ordinal);
        Assert.Contains("href=\"/demo\"", landing, StringComparison.Ordinal);
        Assert.Contains("sessionStorage.setItem(\"maliev.quote.workspace.handoff\"", landing, StringComparison.Ordinal);
        Assert.DoesNotContain("_framework/blazor.webassembly.js", landing, StringComparison.Ordinal);
        Assert.DoesNotContain("MudBlazor", landing, StringComparison.Ordinal);

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
        Assert.Contains("Starting quote workspace", loader, StringComparison.Ordinal);
        Assert.Contains("_framework/blazor.webassembly.js", index, StringComparison.Ordinal);
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
        Assert.Contains("\"/quotes/new\"", layout, StringComparison.Ordinal);
        Assert.Contains("@inject QuoteEngineApiClient Api", layout, StringComparison.Ordinal);
        Assert.Contains("@if (_authStatus.IsSignedIn)", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("<NavLink href=\"/profile\"", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("<NavLink href=\"/ndas\"", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("<NavLink href=\"/documents\"", layout, StringComparison.Ordinal);
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
    public void Profile_page_requires_signed_in_customer_session()
    {
        var profile = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "Profile.razor");

        Assert.Contains("_authStatus = await Api.GetAuthStatusAsync();", profile, StringComparison.Ordinal);
        Assert.Contains("if (!_authStatus.IsSignedIn)", profile, StringComparison.Ordinal);
        Assert.Contains("Navigation.NavigateTo(\"/auth/sign-in?returnUrl=/profile\"", profile, StringComparison.Ordinal);
        Assert.Contains("_profile = await Api.GetProfileAsync();", profile, StringComparison.Ordinal);
        Assert.Contains("_ndas = [.. await Api.GetNdasAsync()];", profile, StringComparison.Ordinal);
        Assert.Contains("class=\"account-overview-card\"", profile, StringComparison.Ordinal);
        Assert.Contains("class=\"account-profile-card\"", profile, StringComparison.Ordinal);
        Assert.Contains("class=\"account-action-grid\"", profile, StringComparison.Ordinal);
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
        var detailCard = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartDetailCard.razor");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");

        Assert.Contains("<QePartsListPanel", workspace, StringComparison.Ordinal);
        Assert.Contains("<QePartDetailCard", workspace, StringComparison.Ordinal);
        Assert.Contains("<QePartConfigSidebar", workspace, StringComparison.Ordinal);
        Assert.Contains("<QeQuoteSummaryBar", workspace, StringComparison.Ordinal);
        Assert.Contains("QeDrawingAttachmentsTab", detailCard, StringComparison.Ordinal);
        Assert.Contains("3D Model", detailCard, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"DFM Analysis\"", detailCard, StringComparison.Ordinal);
        Assert.Contains("qe-pn-mobile-toolbar", workspace, StringComparison.Ordinal);
        Assert.Contains("_isPartsDrawerOpen", workspace, StringComparison.Ordinal);
        Assert.Contains("_centerMode", workspace, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"@Text(\"Quantity\"", ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartConfigSidebar.razor"), StringComparison.Ordinal);
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
    public void Startup_loader_uses_maliev_logo_progress_and_status_contract()
    {
        var index = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "index.html");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");
        var loaderScript = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-engine-loader.js");

        Assert.Contains("class=\"maliev-logo-loader\"", index, StringComparison.Ordinal);
        Assert.Contains("role=\"img\" aria-label=\"MALIEV\"", index, StringComparison.Ordinal);
        Assert.Contains("class=\"startup-progress\"", index, StringComparison.Ordinal);
        Assert.Contains("role=\"progressbar\"", index, StringComparison.Ordinal);
        Assert.Contains("id=\"startup-status\"", index, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"startup-logo\">MALIEV</div>", index, StringComparison.Ordinal);

        Assert.True(File.Exists(RepoPath("Maliev.QuoteEngine.Client", "wwwroot", "images", "logo.svg")));
        Assert.Contains("--wasm-logo-progress: 0%", styles, StringComparison.Ordinal);
        Assert.Contains("--logo-empty: var(--wasm-logo-empty, #d7dde6)", styles, StringComparison.Ordinal);
        Assert.Contains("--logo-progress: var(--wasm-logo-progress, 0%)", styles, StringComparison.Ordinal);
        Assert.Contains("mask: url('/images/logo.svg') center / contain no-repeat", styles, StringComparison.Ordinal);
        Assert.Contains(".maliev-logo-loader::before", styles, StringComparison.Ordinal);
        Assert.Contains("width: var(--logo-progress)", styles, StringComparison.Ordinal);
        Assert.Contains(".startup-progress span", styles, StringComparison.Ordinal);
        Assert.Contains("width: var(--wasm-loader-progress)", styles, StringComparison.Ordinal);

        Assert.Contains("startBlazor", loaderScript, StringComparison.Ordinal);
        Assert.Contains("loadBootResource", loaderScript, StringComparison.Ordinal);
        Assert.Contains("let displayedProgress = 0", loaderScript, StringComparison.Ordinal);
        Assert.Contains("Math.max(displayedProgress, progress)", loaderScript, StringComparison.Ordinal);
        Assert.Contains("markRuntimeReady", loaderScript, StringComparison.Ordinal);
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

        var uploadClient = new FakeQuoteUploadServiceClient("https://cdn.example.com/overlay.glb");
        var consumer = new QuoteDfmAnalysisReadyConsumer(
            statusSvc, uploadClient, hubCtx,
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
        Assert.Contains("ShowDemoSampleCard => !IsSignedIn && !IsDemoMode", src, StringComparison.Ordinal);
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
        Assert.Contains("class=\"qe-pcs-advanced-toggle\"", src, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-pcs-help-link\"", src, StringComparison.Ordinal);
        Assert.Contains("SetPartNotes", src, StringComparison.Ordinal);
        Assert.DoesNotContain("<select", src, StringComparison.OrdinalIgnoreCase);

        Assert.Contains(".qe-pcs-fin-card", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pcs-tol-card", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pcs-choice-card", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pcs-notes-input", styles, StringComparison.Ordinal);
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

        Assert.Contains("BillingName=\"@BillingSummaryName\"", workspace, StringComparison.Ordinal);
        Assert.Contains("BillingHint=\"@BillingSummaryHint\"", workspace, StringComparison.Ordinal);
        Assert.Contains("ApproveQuoteAsync", workspace, StringComparison.Ordinal);
        Assert.Contains("InitiatePaymentAsync", workspace, StringComparison.Ordinal);
        Assert.Contains("ApproveQuoteAsync", apiClient, StringComparison.Ordinal);
        Assert.Contains("InitiatePaymentAsync", apiClient, StringComparison.Ordinal);
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
        Assert.Contains(".qe-viewer-loading-orbit", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-vt-subtools", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_parts_panel_and_workspace_expose_duplicate_project_flow()
    {
        var partsList = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartsListPanel.razor");
        var workspace = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "QuoteWorkspace.razor");
        var apiClient = ReadRepoFile("Maliev.QuoteEngine.Client", "Services", "QuoteEngineApiClient.cs");

        Assert.Contains("[Parameter] public EventCallback OnDuplicateProject", partsList, StringComparison.Ordinal);
        Assert.Contains("@onclick=\"OnDuplicateProject\"", partsList, StringComparison.Ordinal);
        Assert.Contains("OnDuplicateProject=\"DuplicateProjectAsync\"", workspace, StringComparison.Ordinal);
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
        Assert.Contains("/quote/v1/geometry/runtime/manifest", js, StringComparison.Ordinal);
        Assert.Contains("fetchLocalAdvisoryManifest", js, StringComparison.Ordinal);
        Assert.Contains("for (let attempt = 0; attempt < 2; attempt += 1)", js, StringComparison.Ordinal);
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
        Assert.Contains("quote_browser_dfm_runtime_starts", metrics, StringComparison.Ordinal);
        Assert.Contains("quote_browser_dfm_runtime_input_bytes", metrics, StringComparison.Ordinal);
        Assert.Contains("quote_browser_dfm_runtime_input_triangles", metrics, StringComparison.Ordinal);
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

        var inputIndex = js.IndexOf("const runtimeInput =", StringComparison.Ordinal);
        var startedIndex = js.IndexOf("await notifyLocalAdvisoryStartedDotNet(\n        options.dotNetRef", StringComparison.Ordinal);
        var fetchIndex = js.IndexOf("const manifestResponse = await fetch", StringComparison.Ordinal);

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
        Assert.Contains("ResolveBrowserFileViewerExtension(part)", workspace, StringComparison.Ordinal);
        Assert.Contains("NormalizeViewerFileExtension(null, part.StoragePath ?? part.FileName)", workspace, StringComparison.Ordinal);
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
        var sidebar = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QePartConfigSidebar.razor");
        var bulkTable = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QeBulkTable.razor");
        var workspace = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "QuoteWorkspace.razor");

        Assert.Contains("Part.LocalDfmRuntimeUnavailable = false", sidebar, StringComparison.Ordinal);
        Assert.Contains("Part.LocalDfmRuntimeUnavailableReason = null", sidebar, StringComparison.Ordinal);
        Assert.Contains("Part.LocalDfmRuntimeRunningProcessId = null", sidebar, StringComparison.Ordinal);
        Assert.Contains("Part.LocalDfmRuntimeStartedAtUtc = null", sidebar, StringComparison.Ordinal);
        Assert.Contains("part.LocalDfmRuntimeUnavailable = false", bulkTable, StringComparison.Ordinal);
        Assert.Contains("part.LocalDfmRuntimeUnavailableReason = null", bulkTable, StringComparison.Ordinal);
        Assert.Contains("part.LocalDfmRuntimeRunningProcessId = null", bulkTable, StringComparison.Ordinal);
        Assert.Contains("part.LocalDfmRuntimeStartedAtUtc = null", bulkTable, StringComparison.Ordinal);
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

    private static string ReadRepoFile(params string[] pathParts)
    {
        return File.ReadAllText(RepoPath(pathParts));
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
