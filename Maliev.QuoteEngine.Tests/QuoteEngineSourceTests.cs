using MassTransit;
using Maliev.MessagingContracts.Contracts.Geometry;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Consumers;
using Maliev.QuoteEngine.Bff.Hubs;
using Maliev.QuoteEngine.Bff.Services;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using System.Text.Json;

namespace Maliev.QuoteEngine.Tests;

public sealed class QuoteEngineSourceTests
{
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
            ErrorCode: null);

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
    public void Upload_script_matches_browser_files_by_name_and_size()
    {
        var source = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-upload.js");

        Assert.Contains("file.name === mapping.fileName", source, StringComparison.Ordinal);
        Assert.Contains("file.size === mapping.fileSizeBytes", source, StringComparison.Ordinal);
        Assert.Contains("Content-Range", source, StringComparison.Ordinal);
        Assert.Contains("event.dataTransfer.files", source, StringComparison.Ordinal);
        Assert.Contains("registerDropzone", source, StringComparison.Ordinal);
        Assert.Contains("HandleDroppedFilesAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void New_quote_workspace_supports_demo_sample_and_anonymous_upload_state()
    {
        var source = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "QuoteWorkspace.razor");
        var script = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-upload.js");

        Assert.Contains("Try the Quote Engine with a MALIEV sample file", source, StringComparison.Ordinal);
        Assert.Contains("Use sample file", source, StringComparison.Ordinal);
        Assert.Contains("LoadSampleFileAsync", source, StringComparison.Ordinal);
        Assert.Contains("await LoadDemoProjectAsync();", source, StringComparison.Ordinal);
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
        Assert.Contains(".qe-pdc-container {\n    position: relative;", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-center-tabs {\n    position: absolute;", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-dfm-tab {\n    position: absolute;", styles, StringComparison.Ordinal);
        Assert.Contains("flex: 0 0 332px;", styles, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: repeat(3, minmax(0, 1fr));", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pn-root.is-launch-screen", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pn-root.is-workspace-ready .qe-plp-root", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-pn-root.is-workspace-ready .qe-qsb-root", styles, StringComparison.Ordinal);
        Assert.Contains("@keyframes qe-panel-enter-left", styles, StringComparison.Ordinal);
        Assert.Contains("@keyframes qe-panel-enter-bottom", styles, StringComparison.Ordinal);
        Assert.DoesNotContain(".qe-dropzone .mud-icon-root {", styles, StringComparison.Ordinal);
        Assert.True(File.Exists(RepoPath("Maliev.QuoteEngine.Client", "wwwroot", "samples", "maliev-sample-bracket.step")));
        Assert.False(File.Exists(RepoPath("Maliev.QuoteEngine.Client", "wwwroot", "sample-file.step")));
        Assert.Contains("\"Sign in to quote\"", source, StringComparison.Ordinal);
        Assert.Contains("Saved temporarily", source, StringComparison.Ordinal);
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
    public void Customer_layout_uses_top_navigation_without_left_rail()
    {
        var layout = ReadRepoFile("Maliev.QuoteEngine.Client", "Layout", "MainLayout.razor");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");

        Assert.Contains("class=\"quote-topbar\"", layout, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Customer quote navigation\"", layout, StringComparison.Ordinal);
        Assert.Contains("href=\"/projects/new\"", layout, StringComparison.Ordinal);
        Assert.Contains("class=\"quote-brand-logo\"", layout, StringComparison.Ordinal);
        Assert.Contains("src=\"/images/logo.svg\"", layout, StringComparison.Ordinal);
        Assert.Contains("alt=\"MALIEV\"", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("quote-brand-mark", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("Quote Engine", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"web-link\"", layout, StringComparison.Ordinal);
        Assert.DoesNotContain(".web-link", styles, StringComparison.Ordinal);
        Assert.DoesNotContain(".quote-brand-mark", styles, StringComparison.Ordinal);
        Assert.Contains("justify-self: center;", styles, StringComparison.Ordinal);
        Assert.Contains("border-radius: var(--maliev-radius-tab);", styles, StringComparison.Ordinal);
        Assert.Contains(".quote-topnav a.active", styles, StringComparison.Ordinal);
        Assert.Contains("background: var(--primary);", styles, StringComparison.Ordinal);
        Assert.Contains(".quote-topnav a:focus-visible", styles, StringComparison.Ordinal);
        Assert.Contains("IsQuoteWorkspacePath", layout, StringComparison.Ordinal);
        Assert.Contains("\"/projects/new\"", layout, StringComparison.Ordinal);
        Assert.Contains("\"/quotes/new\"", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"app-rail\"", layout, StringComparison.Ordinal);
        Assert.DoesNotContain(".app-rail", styles, StringComparison.Ordinal);
        Assert.Contains(".workspace--quote", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Quote_workspace_is_single_viewport_application_shell()
    {
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");

        Assert.Contains("height: 100dvh;", styles, StringComparison.Ordinal);
        Assert.Contains("overflow-x: clip;", styles, StringComparison.Ordinal);
        Assert.Contains(".workspace--quote {\n    flex: 1 1 0;\n    height: 100%;", styles, StringComparison.Ordinal);
        Assert.Contains(".quote-workspace-host {\n    flex: 1 1 0;\n    height: 100%;", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("height: 100vh;", styles, StringComparison.Ordinal);
        Assert.Contains(".quote-chat-drawer.mud-drawer--closed", styles, StringComparison.Ordinal);
        Assert.Contains("display: none !important;", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-qsb-root", styles, StringComparison.Ordinal);
        Assert.Contains("max-height: 112px;", styles, StringComparison.Ordinal);
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
        Assert.Contains("Icon=\"@Icons.Material.Filled.SupportAgent\"", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("Icon=\"@Icons.Material.Outlined.Chat\"", layout, StringComparison.Ordinal);
        Assert.Contains("MudDrawer", layout, StringComparison.Ordinal);
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

        await svc.SetGlbReadyAsync(path, "https://glb.example.com/part.glb", null, 1, true);
        var ready = await svc.GetStatusAsync(path);
        Assert.Equal("GlbReady", ready!.Status);
        Assert.Equal("https://glb.example.com/part.glb", ready.GlbUrl);
        Assert.Equal(1, ready.BodyCount);
        Assert.True(ready.IsManifold);
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
        Assert.Null(glbPayload.ThumbnailUrl);
        Assert.False(glbPayload.Failed);
        Assert.Null(glbPayload.ErrorCode);
        Assert.Equal(1, glbPayload.BodyCount);
        Assert.True(glbPayload.IsManifold);
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
        Assert.Contains("\"GlbReady\"", src);
        Assert.Contains("\"DfmAnalysisReady\"", src);
        Assert.Contains("FindPartByStoragePath", src);
        Assert.Contains("QePartViewer", src);
        Assert.Contains("QeDfmTab", src);
        Assert.Contains("QeBulkTable", src);
    }

    [Fact]
    public void QeBulkTableRazor_exists_and_has_bulk_edit_columns()
    {
        var src = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteEngine", "QeBulkTable.razor");
        Assert.Contains("BulkProcess", src);
        Assert.Contains("BulkMaterial", src);
        Assert.Contains("LineTotal", src);
        Assert.Contains("DfmReport", src);  // DFM indicator column
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
    }

    private static string ExtractWindowHandleBlock(string js, string handleName)
    {
        var start = js.IndexOf($"window.{handleName}", StringComparison.Ordinal);
        if (start < 0) return "";
        var end = js.IndexOf("};", start, StringComparison.Ordinal);
        return end > start ? js[start..end] : js[start..];
    }

    private static FileAnalyzedEvent BuildFileAnalyzedEvent(
        string storagePath, string glbStoragePath, string? thumbnailStoragePath,
        int bodyCount, bool isManifold, double volumeCm3)
    {
        return new FileAnalyzedEvent
        {
            Payload = new FileAnalyzedEventPayload
            {
                StoragePath = storagePath,
                GlbStoragePath = glbStoragePath,
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
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Maliev.QuoteEngine.slnx"))
                && Directory.Exists(Path.Combine(directory.FullName, "Maliev.QuoteEngine.Client")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Maliev.QuoteEngine repository root.");
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
