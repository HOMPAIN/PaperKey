using System.Text;
using System.Text.RegularExpressions;

namespace PaperKey.Web;

/// <summary>
/// Маленький рендерер Markdown для страниц справки — ровно под то, что используется в docs/: заголовки, абзацы,
/// маркированные и нумерованные списки, таблицы, блоки кода, картинки, ссылки, жирный текст и встроенный код.
/// Без внешних зависимостей; исходный текст — наши собственные файлы, но HTML всё равно экранируется.
/// Ссылки на соседние .md ведут на страницы справки, картинки — в docs/images.
/// </summary>
public static partial class Markdown
{
    public static string ToHtml(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var html = new StringBuilder();
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            if (line.Trim().Length == 0) { i++; continue; }

            if (line.StartsWith("```"))
            {
                var code = new StringBuilder();
                i++;
                while (i < lines.Length && !lines[i].StartsWith("```")) code.Append(Escape(lines[i++])).Append('\n');
                i++;
                html.Append("<pre><code>").Append(code).Append("</code></pre>\n");
                continue;
            }

            var heading = HeadingPattern().Match(line);
            if (heading.Success)
            {
                var level = heading.Groups[1].Length;
                html.Append($"<h{level}>").Append(Inline(heading.Groups[2].Value)).Append($"</h{level}>\n");
                i++;
                continue;
            }

            if (line.StartsWith('|') && i + 1 < lines.Length && TableSeparator().IsMatch(lines[i + 1]))
            {
                var aligns = SplitCells(lines[i + 1]).Select(c => c.EndsWith(':') ? (c.StartsWith(':') ? "center" : "right") : "").ToArray();
                html.Append("<table>\n<thead><tr>");
                foreach (var (cell, k) in SplitCells(line).Select((c, k) => (c, k)))
                    html.Append(Cell("th", cell, k < aligns.Length ? aligns[k] : ""));
                html.Append("</tr></thead>\n<tbody>\n");
                i += 2;
                while (i < lines.Length && lines[i].StartsWith('|'))
                {
                    html.Append("<tr>");
                    foreach (var (cell, k) in SplitCells(lines[i]).Select((c, k) => (c, k)))
                        html.Append(Cell("td", cell, k < aligns.Length ? aligns[k] : ""));
                    html.Append("</tr>\n");
                    i++;
                }
                html.Append("</tbody>\n</table>\n");
                continue;
            }

            var bullet = line.StartsWith("- ");
            var numbered = NumberedItem().IsMatch(line);
            if (bullet || numbered)
            {
                var tag = bullet ? "ul" : "ol";
                html.Append($"<{tag}>\n");
                while (i < lines.Length && (bullet ? lines[i].StartsWith("- ") : NumberedItem().IsMatch(lines[i])))
                {
                    var text = bullet ? lines[i][2..] : NumberedItem().Replace(lines[i], "");
                    html.Append("<li>").Append(Inline(text)).Append("</li>\n");
                    i++;
                }
                html.Append($"</{tag}>\n");
                continue;
            }

            // Абзац: подряд идущие обычные строки
            var paragraph = new StringBuilder();
            while (i < lines.Length && lines[i].Trim().Length > 0 && !IsBlockStart(lines[i]))
            {
                if (paragraph.Length > 0) paragraph.Append(' ');
                paragraph.Append(lines[i].Trim());
                i++;
            }
            html.Append("<p>").Append(Inline(paragraph.ToString())).Append("</p>\n");
        }
        return html.ToString();
    }

    private static bool IsBlockStart(string line) =>
        line.StartsWith("```") || line.StartsWith('#') || line.StartsWith('|') || line.StartsWith("- ") || NumberedItem().IsMatch(line);

    private static string Cell(string tag, string text, string align) =>
        align.Length > 0 ? $"<{tag} style=\"text-align:{align}\">{Inline(text)}</{tag}>" : $"<{tag}>{Inline(text)}</{tag}>";

    private static string[] SplitCells(string row)
    {
        var inner = row.Trim();
        if (inner.StartsWith('|')) inner = inner[1..];
        if (inner.EndsWith('|')) inner = inner[..^1];
        return inner.Replace("\\|", "\u0001").Split('|').Select(c => c.Replace("\u0001", "|").Trim()).ToArray();
    }

    /// <summary>Строчная разметка: сначала экранирование, встроенный код прячется, чтобы внутри него ничего не размечалось.</summary>
    private static string Inline(string text)
    {
        var codes = new List<string>();
        var escaped = Escape(text);
        escaped = InlineCode().Replace(escaped, m => { codes.Add(m.Groups[1].Value); return $"\u0002{codes.Count - 1}\u0003"; });
        escaped = ImagePattern().Replace(escaped, m => $"<img src=\"{Href(m.Groups[2].Value)}\" alt=\"{m.Groups[1].Value}\" />");
        escaped = LinkPattern().Replace(escaped, m => $"<a href=\"{Href(m.Groups[2].Value)}\">{m.Groups[1].Value}</a>");
        escaped = BoldPattern().Replace(escaped, "<b>$1</b>");
        escaped = CodePlaceholder().Replace(escaped, m => $"<code>{codes[int.Parse(m.Groups[1].Value)]}</code>");
        return escaped;
    }

    /// <summary>Соседний документ → страница справки, картинка из docs/images → статический файл приложения.</summary>
    private static string Href(string target)
    {
        if (target.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return "help/" + Docs.Slug(target[..^3]);
        if (target.StartsWith("images/")) return "docs/" + target;
        return target;
    }

    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")] private static partial Regex HeadingPattern();
    [GeneratedRegex(@"^\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?\s*$")] private static partial Regex TableSeparator();
    [GeneratedRegex(@"^\d+\.\s+")] private static partial Regex NumberedItem();
    [GeneratedRegex(@"`([^`]+)`")] private static partial Regex InlineCode();
    [GeneratedRegex(@"!\[([^\]]*)\]\(([^)\s]+)\)")] private static partial Regex ImagePattern();
    [GeneratedRegex(@"\[([^\]]+)\]\(([^)\s]+)\)")] private static partial Regex LinkPattern();
    [GeneratedRegex(@"\*\*(.+?)\*\*")] private static partial Regex BoldPattern();
    [GeneratedRegex("\u0002(\\d+)\u0003")] private static partial Regex CodePlaceholder();
}
