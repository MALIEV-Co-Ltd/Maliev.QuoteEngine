namespace Maliev.QuoteEngine.Tests;

public sealed class QuoteEngineSourceTests
{
    [Fact]
    public void Upload_script_matches_browser_files_by_name_and_size()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Maliev.QuoteEngine.Client", "wwwroot", "js", "quote-upload.js"));

        Assert.Contains("file.name === mapping.fileName", source, StringComparison.Ordinal);
        Assert.Contains("file.size === mapping.fileSizeBytes", source, StringComparison.Ordinal);
        Assert.Contains("Content-Range", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Web_and_quote_engine_boundaries_are_documented()
    {
        var readme = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "README.md"));

        Assert.Contains("Maliev.Web", readme, StringComparison.Ordinal);
        Assert.Contains("Maliev.QuoteEngine", readme, StringComparison.Ordinal);
        Assert.Contains("browser never supplies a trusted customer id", readme, StringComparison.OrdinalIgnoreCase);
    }
}
