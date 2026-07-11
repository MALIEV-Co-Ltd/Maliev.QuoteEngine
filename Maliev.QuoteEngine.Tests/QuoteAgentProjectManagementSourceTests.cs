namespace Maliev.QuoteEngine.Tests;

public sealed class QuoteAgentProjectManagementSourceTests
{
    [Fact]
    public void Project_rail_uses_distinct_sections_and_compact_busy_actions()
    {
        var shell = ReadRepoFile(
            "Maliev.QuoteEngine.Client",
            "Components",
            "QuoteAgent",
            "QuoteAgentLaunchShell.razor");
        var styles = ReadRepoFile(
            "Maliev.QuoteEngine.Client",
            "wwwroot",
            "css",
            "app.css");
        var sharedDtos = ReadRepoFile(
            "Maliev.QuoteEngine.Shared",
            "Quotes",
            "QuoteEngineDtos.cs");

        Assert.Contains("private bool _chatsCollapsed = true;", shell, StringComparison.Ordinal);
        Assert.Contains("Icons.Material.Outlined.ChatBubbleOutline", shell, StringComparison.Ordinal);
        Assert.Contains("Icons.Material.Outlined.Folder", shell, StringComparison.Ordinal);
        Assert.Contains("private bool _railProjectsCollapsed;", shell, StringComparison.Ordinal);
        Assert.Contains("private bool _managementProjectsCollapsed;", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("ProjectManagementSectionClass(_projectsCollapsed)", shell, StringComparison.Ordinal);

        Assert.Contains("class=\"qe-agent-project-meta-row\"", shell, StringComparison.Ordinal);
        Assert.Contains("project.CreatedAt ?? project.UpdatedAt", shell, StringComparison.Ordinal);
        Assert.Contains("\"quoted\" => Text(\"Quoted\"", shell, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-agent-project-actions\"", shell, StringComparison.Ordinal);
        Assert.Contains("ProjectAction.Pin", shell, StringComparison.Ordinal);
        Assert.Contains("ProjectAction.Duplicate", shell, StringComparison.Ordinal);
        Assert.Contains("ProjectAction.Archive", shell, StringComparison.Ordinal);
        Assert.Contains("private bool IsProjectActionBusy(ProjectNavItem project, ProjectAction action)", shell, StringComparison.Ordinal);
        Assert.Contains("class=\"qe-agent-project-action-spinner\"", shell, StringComparison.Ordinal);

        Assert.Contains(".qe-agent-project-row:hover .qe-agent-project-actions", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-project-row:focus-within .qe-agent-project-actions", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-project-actions:focus-within", styles, StringComparison.Ordinal);
        Assert.Contains(".qe-agent-project-badge--quoted", styles, StringComparison.Ordinal);

        var navDtoStart = sharedDtos.IndexOf("public sealed record CustomerProjectNavItemDto", StringComparison.Ordinal);
        var searchDtoStart = sharedDtos.IndexOf("public sealed record CustomerProjectSearchResponse", StringComparison.Ordinal);
        Assert.True(navDtoStart >= 0 && searchDtoStart > navDtoStart);
        Assert.Contains(
            "public DateTimeOffset? CreatedAt { get; init; }",
            sharedDtos[navDtoStart..searchDtoStart],
            StringComparison.Ordinal);
    }

    [Fact]
    public void Projects_page_debounces_search_and_discards_stale_results()
    {
        var shell = ReadRepoFile(
            "Maliev.QuoteEngine.Client",
            "Components",
            "QuoteAgent",
            "QuoteAgentLaunchShell.razor");

        Assert.Contains("id=\"qe-agent-project-search\"", shell, StringComparison.Ordinal);
        Assert.Contains("OnProjectManagementSearchInputAsync", shell, StringComparison.Ordinal);
        Assert.Contains("Api.SearchProjectsAsync(normalizedQuery, 50, cancellationToken)", shell, StringComparison.Ordinal);
        Assert.Contains("Task.Delay(ProjectSearchDebounceDelay, cancellationToken)", shell, StringComparison.Ordinal);
        Assert.Contains("requestVersion != _projectSearchVersion", shell, StringComparison.Ordinal);
        Assert.Contains("_projectManagementSearchCts?.Cancel();", shell, StringComparison.Ordinal);
        Assert.Contains("_projectSearchResults.Clear();", shell, StringComparison.Ordinal);
        Assert.Contains("aria-live=\"polite\"", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void Projects_page_lazy_loads_real_project_and_file_detail()
    {
        var shell = ReadRepoFile(
            "Maliev.QuoteEngine.Client",
            "Components",
            "QuoteAgent",
            "QuoteAgentLaunchShell.razor");
        var row = ReadRepoFile(
            "Maliev.QuoteEngine.Client",
            "Components",
            "QuoteAgent",
            "QuoteProjectManagementRow.razor");
        var sharedDtos = ReadRepoFile(
            "Maliev.QuoteEngine.Shared",
            "Quotes",
            "QuoteEngineDtos.cs");
        var projectClient = ReadRepoFile(
            "Maliev.QuoteEngine.Bff",
            "Clients",
            "ProjectServiceClient.cs");

        Assert.Contains("<QuoteProjectManagementRow", shell, StringComparison.Ordinal);
        Assert.Contains("ToggleProjectDetailAsync", shell, StringComparison.Ordinal);
        Assert.Contains("if (state.IsExpanded && !state.IsLoading)", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("state.IsExpanded && state.Detail is null", shell, StringComparison.Ordinal);
        Assert.Contains("Api.GetProjectDetailAsync(project.ProjectId", shell, StringComparison.Ordinal);
        Assert.Contains("ProjectManagementDetailState", shell, StringComparison.Ordinal);

        Assert.Contains("Detail.Parts", row, StringComparison.Ordinal);
        Assert.Contains("part.DrawingFiles", row, StringComparison.Ordinal);
        Assert.Contains("Detail.CreatedAt", row, StringComparison.Ordinal);
        Assert.Contains("ProjectNumber", row, StringComparison.Ordinal);
        Assert.Contains("Quantity", row, StringComparison.Ordinal);
        Assert.Contains("ProcessId", row, StringComparison.Ordinal);
        Assert.Contains("MaterialId", row, StringComparison.Ordinal);
        Assert.DoesNotContain("Fake", row, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("placeholder", row, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("public DateTimeOffset? CreatedAt { get; init; }", sharedDtos, StringComparison.Ordinal);
        Assert.Contains("CreatedAt = project.CreatedAt == default", projectClient, StringComparison.Ordinal);
        Assert.Contains("new DateTimeOffset(project.CreatedAt, TimeSpan.Zero)", projectClient, StringComparison.Ordinal);
    }

    [Fact]
    public void Expanded_project_files_use_customer_safe_analysis_and_adaptive_volume_labels()
    {
        var row = ReadRepoFile(
            "Maliev.QuoteEngine.Client",
            "Components",
            "QuoteAgent",
            "QuoteProjectManagementRow.razor");

        Assert.Contains("QuoteAgentUiHelpers.FormatCustomerStatus(part.Status)", row, StringComparison.Ordinal);
        Assert.Contains("QuoteAgentUiHelpers.FormatVolume(part.VolumeCc)", row, StringComparison.Ordinal);
        Assert.Contains("Text(\"Not analyzed\", \"ยังไม่ได้วิเคราะห์\")", row, StringComparison.Ordinal);
        Assert.DoesNotContain("HumanizeCode(part.Status)", row, StringComparison.Ordinal);
        Assert.DoesNotContain("@FormatNumber(part.VolumeCc) cm³", row, StringComparison.Ordinal);
    }

    [Fact]
    public void Expanded_project_row_renders_only_service_backed_commercial_records()
    {
        var row = ReadRepoFile(
            "Maliev.QuoteEngine.Client",
            "Components",
            "QuoteAgent",
            "QuoteProjectManagementRow.razor");
        var sharedDtos = ReadRepoFile(
            "Maliev.QuoteEngine.Shared",
            "Quotes",
            "QuoteEngineDtos.cs");

        Assert.Contains("Detail.Quote", row, StringComparison.Ordinal);
        Assert.Contains("Detail.Order", row, StringComparison.Ordinal);
        Assert.Contains("Detail.Invoice", row, StringComparison.Ordinal);
        Assert.Contains("Detail.Receipts", row, StringComparison.Ordinal);
        Assert.Contains("Detail.Documents", row, StringComparison.Ordinal);
        Assert.Contains("Detail.DataWarnings", row, StringComparison.Ordinal);
        Assert.Contains("Live project snapshot", row, StringComparison.Ordinal);
        Assert.Contains("@onclick=\"OnRetry\"", row, StringComparison.Ordinal);
        Assert.Contains("invoice.DocumentUrl", row, StringComparison.Ordinal);
        Assert.Contains("receipt.DocumentUrl", row, StringComparison.Ordinal);
        Assert.Contains("orderWithFiles.OrderFiles", row, StringComparison.Ordinal);
        Assert.DoesNotContain("placeholder", row, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("public CustomerQuoteSummaryDto? Quote", sharedDtos, StringComparison.Ordinal);
        Assert.Contains("public CustomerOrderDetailDto? Order", sharedDtos, StringComparison.Ordinal);
        Assert.Contains("public CustomerProjectInvoiceDto? Invoice", sharedDtos, StringComparison.Ordinal);
        Assert.Contains("public IReadOnlyList<CustomerProjectReceiptDto> Receipts", sharedDtos, StringComparison.Ordinal);
        Assert.Contains("public IReadOnlyList<CustomerDocumentDto> Documents", sharedDtos, StringComparison.Ordinal);
        Assert.Contains("public IReadOnlyList<CustomerProjectDataWarningDto> DataWarnings", sharedDtos, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] relativePathParts)
    {
        var repoRoot = Directory.GetParent(GetSourceDirectory())!.FullName;
        return File.ReadAllText(Path.Combine([repoRoot, .. relativePathParts])).ReplaceLineEndings("\n");
    }

    private static string GetSourceDirectory([System.Runtime.CompilerServices.CallerFilePath] string filePath = "") =>
        Path.GetDirectoryName(filePath)!;
}
