using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;

namespace Maliev.QuoteEngine.Client.Components.QuoteAgent;

/// <summary>
/// Renders customer-safe markdown used by QuoteEngine assistant responses.
/// </summary>
public static partial class QuoteAgentMarkdownRenderer
{
    /// <summary>
    /// Converts supported markdown into escaped HTML for Blazor rendering.
    /// </summary>
    /// <param name="markdown">The assistant markdown content.</param>
    /// <returns>A markup string containing only controlled HTML tags.</returns>
    public static MarkupString Render(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new MarkupString(string.Empty);
        }

        return new MarkupString(RenderBlocks(markdown.Replace("\r\n", "\n", StringComparison.Ordinal)));
    }

    private static string RenderBlocks(string markdown)
    {
        var lines = markdown.Split('\n');
        var html = new StringBuilder();
        var paragraph = new List<string>();
        var list = new List<string>();
        var orderedList = false;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.Trim();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph(html, paragraph);
                FlushList(html, list, orderedList);

                var code = new StringBuilder();
                index++;
                while (index < lines.Length && !lines[index].Trim().StartsWith("```", StringComparison.Ordinal))
                {
                    code.AppendLine(lines[index]);
                    index++;
                }

                html.Append("<pre><code>");
                html.Append(WebUtility.HtmlEncode(code.ToString().TrimEnd('\r', '\n')));
                html.Append("</code></pre>");
                continue;
            }

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                FlushParagraph(html, paragraph);
                FlushList(html, list, orderedList);
                continue;
            }

            if (TryRenderTable(lines, index, out var tableHtml, out var consumedRows))
            {
                FlushParagraph(html, paragraph);
                FlushList(html, list, orderedList);
                html.Append(tableHtml);
                index += consumedRows - 1;
                continue;
            }

            var headingLevel = GetHeadingLevel(trimmed);
            if (headingLevel > 0)
            {
                FlushParagraph(html, paragraph);
                FlushList(html, list, orderedList);
                var headingText = trimmed[(headingLevel + 1)..].Trim();
                html.Append(CultureInvariant($"<h{headingLevel}>"));
                html.Append(RenderInline(headingText));
                html.Append(CultureInvariant($"</h{headingLevel}>"));
                continue;
            }

            if (TryGetListItem(trimmed, out var itemText, out var isOrdered))
            {
                FlushParagraph(html, paragraph);
                if (list.Count > 0 && orderedList != isOrdered)
                {
                    FlushList(html, list, orderedList);
                }

                orderedList = isOrdered;
                list.Add(itemText);
                continue;
            }

            FlushList(html, list, orderedList);
            paragraph.Add(trimmed);
        }

        FlushParagraph(html, paragraph);
        FlushList(html, list, orderedList);
        return html.ToString();
    }

    private static bool TryRenderTable(string[] lines, int startIndex, out string tableHtml, out int consumedRows)
    {
        tableHtml = string.Empty;
        consumedRows = 0;

        if (startIndex + 1 >= lines.Length)
        {
            return false;
        }

        var header = lines[startIndex].Trim();
        var separator = lines[startIndex + 1].Trim();
        if (!IsTableRow(header) || !TableSeparatorRegex().IsMatch(separator))
        {
            return false;
        }

        var rows = new List<string[]>();
        var index = startIndex + 2;
        while (index < lines.Length && IsTableRow(lines[index].Trim()))
        {
            rows.Add(SplitTableRow(lines[index].Trim()));
            index++;
        }

        var headers = SplitTableRow(header);
        var html = new StringBuilder("<div class=\"qe-agent-markdown-table\"><table><thead><tr>");
        foreach (var cell in headers)
        {
            html.Append("<th>");
            html.Append(RenderInline(cell));
            html.Append("</th>");
        }

        html.Append("</tr></thead><tbody>");
        foreach (var row in rows)
        {
            html.Append("<tr>");
            foreach (var cell in row)
            {
                html.Append("<td>");
                html.Append(RenderInline(cell));
                html.Append("</td>");
            }

            html.Append("</tr>");
        }

        html.Append("</tbody></table></div>");
        tableHtml = html.ToString();
        consumedRows = index - startIndex;
        return true;
    }

    private static bool IsTableRow(string line)
    {
        return line.Contains('|', StringComparison.Ordinal) && SplitTableRow(line).Length > 1;
    }

    private static string[] SplitTableRow(string row)
    {
        return row.Trim().Trim('|').Split('|', StringSplitOptions.TrimEntries);
    }

    private static int GetHeadingLevel(string trimmed)
    {
        if (!trimmed.StartsWith('#'))
        {
            return 0;
        }

        var count = 0;
        while (count < trimmed.Length && trimmed[count] == '#')
        {
            count++;
        }
        return count is >= 1 and <= 3 && trimmed.Length > count && trimmed[count] == ' ' ? count : 0;
    }

    private static bool TryGetListItem(string trimmed, out string itemText, out bool ordered)
    {
        ordered = false;
        itemText = string.Empty;

        if (trimmed.Length > 2 && (trimmed[0] == '-' || trimmed[0] == '*' || trimmed[0] == '+') && trimmed[1] == ' ')
        {
            itemText = trimmed[2..].Trim();
            return true;
        }

        var match = OrderedListRegex().Match(trimmed);
        if (!match.Success)
        {
            return false;
        }

        ordered = true;
        itemText = match.Groups[1].Value.Trim();
        return true;
    }

    private static string RenderInline(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var parts = value.Split('`');
        var html = new StringBuilder();
        for (var index = 0; index < parts.Length; index++)
        {
            var encoded = WebUtility.HtmlEncode(parts[index]);
            if (index % 2 == 1)
            {
                html.Append("<code>");
                html.Append(encoded);
                html.Append("</code>");
                continue;
            }

            encoded = BoldRegex().Replace(encoded, "<strong>$1</strong>");
            encoded = ItalicAsteriskRegex().Replace(encoded, "<em>$1</em>");
            encoded = ItalicUnderscoreRegex().Replace(encoded, "<em>$1</em>");
            html.Append(encoded);
        }

        return html.ToString();
    }

    private static void FlushParagraph(StringBuilder html, List<string> paragraph)
    {
        if (paragraph.Count == 0)
        {
            return;
        }

        html.Append("<p>");
        html.Append(RenderInline(string.Join(' ', paragraph)));
        html.Append("</p>");
        paragraph.Clear();
    }

    private static void FlushList(StringBuilder html, List<string> list, bool ordered)
    {
        if (list.Count == 0)
        {
            return;
        }

        html.Append(ordered ? "<ol>" : "<ul>");
        foreach (var item in list)
        {
            html.Append("<li>");
            html.Append(RenderInline(item));
            html.Append("</li>");
        }

        html.Append(ordered ? "</ol>" : "</ul>");
        list.Clear();
    }

    private static string CultureInvariant(FormattableString value)
    {
        return FormattableString.Invariant(value);
    }

    [GeneratedRegex(@"^\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?$")]
    private static partial Regex TableSeparatorRegex();

    [GeneratedRegex(@"^\d+\.\s+(.+)$")]
    private static partial Regex OrderedListRegex();

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex BoldRegex();

    [GeneratedRegex(@"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)")]
    private static partial Regex ItalicAsteriskRegex();

    [GeneratedRegex(@"(?<!_)_(?!_)(.+?)(?<!_)_(?!_)")]
    private static partial Regex ItalicUnderscoreRegex();
}
