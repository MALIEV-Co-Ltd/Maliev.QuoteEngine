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
