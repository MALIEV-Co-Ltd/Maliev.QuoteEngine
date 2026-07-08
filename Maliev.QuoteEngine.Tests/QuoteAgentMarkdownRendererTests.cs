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

    [Fact]
    public void Render_isolates_leaked_tool_code_as_code_block_instead_of_italics()
    {
        // Reproduces the production leak: unfenced tool_code + print(...) calls whose snake_case
        // names were previously mangled into <em> by the underscore-italic rule.
        var markdown = "Sure! Let's get that quote for you. tool_code\n" +
            "print(quote_update_configuration(part_id='case.stl', material='ABS', quantity=6))\n" +
            "print(quote_set_project_name(name='case - FDM ABS'))\n" +
            "print(quote_calculate_estimate())";

        var html = QuoteAgentMarkdownRenderer.Render(markdown).Value;

        Assert.Contains("<pre><code>", html, StringComparison.Ordinal);
        Assert.Contains("quote_update_configuration(part_id=&#39;case.stl&#39;, material=&#39;ABS&#39;, quantity=6)", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<em>update", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<em>id=", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_leaves_normal_prose_and_fenced_blocks_untouched_by_leak_isolation()
    {
        var prose = QuoteAgentMarkdownRenderer.Render("Use _italics_ and print money responsibly.").Value;
        Assert.Contains("<em>italics</em>", prose, StringComparison.Ordinal);
        Assert.DoesNotContain("<pre>", prose, StringComparison.Ordinal);

        var fenced = QuoteAgentMarkdownRenderer.Render("```python\nprint(quote_calculate_estimate())\n```").Value;
        Assert.Equal(1, CountOccurrences(fenced, "<pre><code>"));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
