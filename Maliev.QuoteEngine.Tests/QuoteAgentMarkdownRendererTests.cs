using Maliev.QuoteEngine.Client.Components.QuoteAgent;

namespace Maliev.QuoteEngine.Tests;

public sealed class QuoteAgentMarkdownRendererTests
{
    [Fact]
    public void Render_supports_quote_agent_markdown_blocks()
    {
        var markdown = """
            # Quote summary

            - **Material:** 6061-T6
            - *Finish:* clear anodize

            | Qty | Unit |
            | --- | --- |
            | 50 | `$9.40` |

            ```json
            {"process":"sheet-metal"}
            ```
            """;

        var html = QuoteAgentMarkdownRenderer.Render(markdown).Value;

        Assert.Contains("<h1>Quote summary</h1>", html, StringComparison.Ordinal);
        Assert.Contains("<ul>", html, StringComparison.Ordinal);
        Assert.Contains("<strong>Material:</strong>", html, StringComparison.Ordinal);
        Assert.Contains("<em>Finish:</em>", html, StringComparison.Ordinal);
        Assert.Contains("<table>", html, StringComparison.Ordinal);
        Assert.Contains("<td><code>$9.40</code></td>", html, StringComparison.Ordinal);
        Assert.Contains("<pre><code>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_escapes_html_before_applying_supported_markdown()
    {
        var html = QuoteAgentMarkdownRenderer.Render("<script>alert(1)</script> **safe**").Value;

        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("<strong>safe</strong>", html, StringComparison.Ordinal);
    }
}
