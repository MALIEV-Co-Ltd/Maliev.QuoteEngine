using System.Runtime.CompilerServices;

namespace Maliev.QuoteEngine.Tests;

public sealed class QuoteAgentArtifactSourceTests
{
    [Fact]
    public void Inline_preview_runtime_registers_bridge_before_blazor_uses_global_interop()
    {
        var loader = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-engine-loader.js");
        var inlineViewer = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-inline-viewer-three.js");
        var component = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteAgent", "QeInlinePartViewer.razor");
        var index = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "index.html");

        Assert.Contains("function createInlineViewerBridge()", loader, StringComparison.Ordinal);
        Assert.Contains("window.quoteInlineViewer = window.quoteInlineViewer || createInlineViewerBridge();", loader, StringComparison.Ordinal);
        Assert.Contains("registerRuntime: function (nextRuntime)", loader, StringComparison.Ordinal);
        Assert.Contains("reject(new Error(\"3D preview runtime is still loading.\"));", loader, StringComparison.Ordinal);

        Assert.Contains("const runtime = {", inlineViewer, StringComparison.Ordinal);
        Assert.Contains("window.quoteInlineViewer.registerRuntime(runtime);", inlineViewer, StringComparison.Ordinal);
        Assert.Contains("export { createPreview, disposePreview, resetPreviewCamera, resizePreviews };", inlineViewer, StringComparison.Ordinal);

        Assert.Contains("await JS.InvokeVoidAsync(\"quoteInlineViewer.disposePreview\", _containerId);", component, StringComparison.Ordinal);
        Assert.Contains("await JS.InvokeVoidAsync(\"quoteInlineViewer.createPreview\", _containerId, CommandsJson);", component, StringComparison.Ordinal);
        Assert.Contains("js/quote-engine-loader.js?v=inline-preview-bridge", index, StringComparison.Ordinal);
        Assert.Contains("js/quote-inline-viewer-three.js?v=inline-preview-runtime", index, StringComparison.Ordinal);
    }

    [Fact]
    public void Supplemental_uploads_finish_ready_with_editable_artifact_analysis_notes()
    {
        var workspace = ReadRepoFile("Maliev.QuoteEngine.Client", "Pages", "QuoteWorkspace.razor");
        var shell = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteAgent", "QuoteAgentLaunchShell.razor");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");

        Assert.Contains("OnUploadedPartChanged=\"HandleUploadedPartChangedAsync\"", workspace, StringComparison.Ordinal);
        Assert.Contains("if (IsSupplementalUpload(candidate.FileName, candidate.ContentType))", workspace, StringComparison.Ordinal);
        Assert.Contains("ApplySupplementalUploadReview(part, candidate.FileName, candidate.ContentType);", workspace, StringComparison.Ordinal);
        Assert.Contains("private static bool IsSupplementalUpload(string fileName, string contentType)", workspace, StringComparison.Ordinal);
        Assert.Contains("private static void ApplySupplementalUploadReview(QuotePartViewModel part, string fileName, string contentType)", workspace, StringComparison.Ordinal);
        Assert.Contains("part.Status = \"Ready\";", workspace, StringComparison.Ordinal);
        Assert.Contains("part.PartNotes = BuildSupplementalArtifactAnalysisNote(fileName, contentType);", workspace, StringComparison.Ordinal);
        Assert.Contains("private async Task RegisterUploadedPartNoteWithAgentAsync(QuotePartViewModel part)", workspace, StringComparison.Ordinal);
        Assert.Contains("Customer edited artifact analysis for", workspace, StringComparison.Ordinal);
        Assert.Contains("Url = BuildAgentAttachmentUrl(part),", workspace, StringComparison.Ordinal);
        Assert.Contains("private static string? BuildAgentAttachmentUrl(QuotePartViewModel part)", workspace, StringComparison.Ordinal);

        var viewModel = ReadRepoFile("Maliev.QuoteEngine.Client", "Models", "QuotePartViewModel.cs");
        Assert.Contains("PartNotes = PartNotes,", viewModel, StringComparison.Ordinal);

        Assert.Contains("public EventCallback<QuotePartViewModel> OnUploadedPartChanged { get; set; }", shell, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-agent-artifact-analysis-note\"", shell, StringComparison.Ordinal);
        Assert.Contains("OpenArtifactNoteDialog(SelectedPartIndex)", shell, StringComparison.Ordinal);
        Assert.Contains("private async Task SaveArtifactNoteAsync()", shell, StringComparison.Ordinal);
        Assert.Contains("await OnUploadedPartChanged.InvokeAsync(part);", shell, StringComparison.Ordinal);
        Assert.Contains("ArtifactAnalysisNoteText(SelectedUploadedPart)", shell, StringComparison.Ordinal);

        Assert.Contains(".qe-agent-artifact-analysis-note", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-artifact-note-dialog", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-artifact-note-input", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Inline_preview_reports_build_outcome_and_offers_agent_rebuild_recovery()
    {
        var component = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteAgent", "QeInlinePartViewer.razor");
        var shell = ReadRepoFile("Maliev.QuoteEngine.Client", "Components", "QuoteAgent", "QuoteAgentLaunchShell.razor");
        var styles = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css");

        // Viewer raises a build outcome the host can act on.
        Assert.Contains("public EventCallback<PreviewBuildOutcome> OnBuildOutcome { get; set; }", component, StringComparison.Ordinal);
        Assert.Contains("await NotifyBuildOutcomeAsync(new PreviewBuildOutcome(true, \"none\", null));", component, StringComparison.Ordinal);
        Assert.Contains("await NotifyBuildOutcomeAsync(new PreviewBuildOutcome(false, ClassifyBuildError(ex.Message), ex.Message));", component, StringComparison.Ordinal);
        Assert.Contains("public sealed record PreviewBuildOutcome(bool Success, string ErrorClass, string? Message);", component, StringComparison.Ordinal);

        // Shell reports the outcome for telemetry and offers a non-dead-end recovery.
        Assert.Contains("OnBuildOutcome=\"@(outcome => HandleInlineViewerBuildOutcomeAsync(message.InlineViewer, outcome))\"", shell, StringComparison.Ordinal);
        Assert.Contains("private async Task HandleInlineViewerBuildOutcomeAsync(InlineViewerInfo? viewer, QeInlinePartViewer.PreviewBuildOutcome outcome)", shell, StringComparison.Ordinal);
        Assert.Contains("/quote/v1/agent/sessions/{SessionId:D}/artifacts/{viewer.ArtifactId:D}/preview-build", shell, StringComparison.Ordinal);
        Assert.Contains("private async Task RequestPreviewRebuildAsync(InlineViewerInfo? viewer)", shell, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-inline-viewer-rebuild-button\"", shell, StringComparison.Ordinal);

        Assert.Contains(".qe-inline-viewer-rebuild-button", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_worker_passes_point_arrays_to_replicad_lineTo_not_scalars()
    {
        // replicad's Sketcher.lineTo(point: Point2D) destructures a single [x, y] array.
        // Passing two scalars makes replicad try to iterate the first scalar and throw
        // "number <n> is not iterable", dead-ending every line-based silhouette and every cone.
        var worker = ReadRepoFile("Maliev.QuoteEngine.Client", "js-src", "replicad-worker.js");

        Assert.Contains("sketch.lineTo([p[2], p[3]]);", worker, StringComparison.Ordinal);
        Assert.Contains("sketch.lineTo([p[0], p[1]]);", worker, StringComparison.Ordinal);
        Assert.Contains(".lineTo([radiusBottom, 0])", worker, StringComparison.Ordinal);

        // Regression guard: the scalar forms must never come back.
        Assert.DoesNotContain("sketch.lineTo(p[0], p[1])", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("sketch.lineTo(p[2], p[3])", worker, StringComparison.Ordinal);
        Assert.DoesNotContain(".lineTo(radiusBottom, 0)", worker, StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_worker_clones_shapes_before_consuming_transforms_to_survive_reuse()
    {
        // replicad's translate/rotate/fillet/chamfer/loft DELETE their input shape and return a new
        // one. The worker keeps shapes in a reusable map keyed by id, so an agent that references a
        // shape id more than once (e.g. a comb whose teeth reuse one cutter template) would hand
        // replicad an already-deleted object -> "This object has been deleted". Clone before every
        // consuming op so the stored shape survives reuse.
        var worker = ReadRepoFile("Maliev.QuoteEngine.Client", "js-src", "replicad-worker.js");

        Assert.Contains("function cloneShape(shape)", worker, StringComparison.Ordinal);
        Assert.Contains("const moved = cloneShape(target);", worker, StringComparison.Ordinal);
        Assert.Contains("cloneShape(target).rotate(axis, angle)", worker, StringComparison.Ordinal);
        Assert.Contains("cloneShape(a).loftWith(cloneShape(b))", worker, StringComparison.Ordinal);
        Assert.Contains("tryApplyEdgeOperation(cloneShape(target), 'fillet'", worker, StringComparison.Ordinal);
        Assert.Contains("tryApplyEdgeOperation(cloneShape(target), 'chamfer'", worker, StringComparison.Ordinal);

        // Regression: consuming ops must not run directly on the stored shape.
        Assert.DoesNotContain("shape = target.rotate(axis, angle);", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("shape = a.loftWith(b);", worker, StringComparison.Ordinal);
    }

    [Fact]
    public void Uploaded_stl_viewer_keeps_source_orientation_while_converted_gltf_gets_axis_rotation()
    {
        var viewer = ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-part-viewer-three.js");

        Assert.Contains("Native mesh uploads such as STL/OBJ/3MF keep their source orientation.", viewer, StringComparison.Ordinal);
        Assert.Contains("function shouldApplyGltfUpAxisRotation(loadableExt)", viewer, StringComparison.Ordinal);
        Assert.Contains("return loadableExt === '.glb' || loadableExt === '.gltf';", viewer, StringComparison.Ordinal);
        Assert.Contains("if (shouldApplyGltfUpAxisRotation(loadableExt))", viewer, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] relativePathParts)
    {
        var repoRoot = Directory.GetParent(GetSourceDirectory())!.FullName;
        return File.ReadAllText(Path.Combine([repoRoot, .. relativePathParts]));
    }

    private static string GetSourceDirectory([CallerFilePath] string sourceFile = "")
    {
        return Path.GetDirectoryName(sourceFile) ?? Directory.GetCurrentDirectory();
    }
}
