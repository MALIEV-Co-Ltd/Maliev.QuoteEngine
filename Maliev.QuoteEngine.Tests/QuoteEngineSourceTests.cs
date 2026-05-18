namespace Maliev.QuoteEngine.Tests;

public sealed class QuoteEngineSourceTests
{
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
        Assert.Contains("SampleFilePath = \"/samples/maliev-sample-bracket.step\"", source, StringComparison.Ordinal);
        Assert.Contains("quoteEngineUploads.loadSampleFile", source, StringComparison.Ordinal);
        Assert.Contains("ApplyDefaultRouting(part, candidate.FileName)", source, StringComparison.Ordinal);
        Assert.Contains("async function loadSampleFile", script, StringComparison.Ordinal);
        Assert.Contains("fetch(sampleUrl", script, StringComparison.Ordinal);
        Assert.Contains("HandleDroppedFilesAsync", script, StringComparison.Ordinal);
        Assert.Contains(".qe-dropzone::before", ReadRepoFile("Maliev.QuoteEngine.Client", "wwwroot", "css", "app.css"), StringComparison.Ordinal);
        Assert.True(File.Exists(RepoPath("Maliev.QuoteEngine.Client", "wwwroot", "samples", "maliev-sample-bracket.step")));
        Assert.False(File.Exists(RepoPath("Maliev.QuoteEngine.Client", "wwwroot", "sample-file.step")));
        Assert.Contains("\"Sign in to quote\"", source, StringComparison.Ordinal);
        Assert.Contains("Temporary upload storage until you sign in", source, StringComparison.Ordinal);
        Assert.Contains("QuoteUploadHandoffRequest", source, StringComparison.Ordinal);
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
        Assert.Contains("max-height: 128px;", styles, StringComparison.Ordinal);
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
