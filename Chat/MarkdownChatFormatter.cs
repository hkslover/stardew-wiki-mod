using System.Text.RegularExpressions;
using Microsoft.Xna.Framework;

namespace StardewWikiAgent.Chat;

/// <summary>One chat-box line plus the single color it should be drawn in.</summary>
internal readonly record struct ChatLine(string Text, Color Color);

/// <summary>
/// Turns the LLM's lightly-Markdown answer into colored chat lines.
/// The vanilla chat box applies one color per line, so coloring is done at the
/// line level; inline **bold** is stripped of its markers and wrapped in 【】 so
/// it still stands out within a single-colored line.
/// </summary>
internal static class MarkdownChatFormatter
{
    public static readonly Color Answer = new(206, 240, 158);    // soft green — the answer body
    public static readonly Color Highlight = new(255, 214, 102);  // gold — headings / whole-line bold
    public static readonly Color Source = new(120, 200, 255);     // cyan — source / link lines
    public static readonly Color Error = new(255, 120, 100);      // red — errors

    private static readonly Regex HeadingRegex = new(@"^#{1,6}\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex SourceLineRegex = new(@"^\**\s*(来源|来源页面|参考|Sources?)\s*[:：]", RegexOptions.Compiled);
    private static readonly Regex UrlLineRegex = new(@"^https?://", RegexOptions.Compiled);
    private static readonly Regex BulletRegex = new(@"^(?:[-*+]|\d+[.)])\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex WholeBoldRegex = new(@"^\*\*(.+?)\*\*[:：]?$", RegexOptions.Compiled);
    private static readonly Regex BoldRegex = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);
    private static readonly Regex ItalicRegex = new(@"(?<!\*)\*(?!\*)([^*]+?)\*(?!\*)", RegexOptions.Compiled);
    private static readonly Regex CodeRegex = new(@"`([^`]+)`", RegexOptions.Compiled);
    private static readonly Regex LinkRegex = new(@"\[([^\]]+)\]\([^)]+\)", RegexOptions.Compiled);

    public static IEnumerable<ChatLine> Format(string answer, int maxLineLength)
    {
        string normalized = (answer ?? "").Replace("\r\n", "\n").Trim();
        if (normalized.Length == 0)
        {
            yield return new ChatLine("AI 没有返回文字答案。", Answer);
            yield break;
        }

        foreach (string rawLine in normalized.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            string text = FormatLine(line, out Color color);
            if (text.Length == 0)
                continue;

            foreach (string piece in Wrap(text, maxLineLength))
                yield return new ChatLine(piece, color);
        }
    }

    private static string FormatLine(string line, out Color color)
    {
        Match heading = HeadingRegex.Match(line);
        if (heading.Success)
        {
            color = Highlight;
            return StripInline(heading.Groups[1].Value);
        }

        if (SourceLineRegex.IsMatch(line) || UrlLineRegex.IsMatch(line))
        {
            color = Source;
            return StripInline(line);
        }

        Match bullet = BulletRegex.Match(line);
        if (bullet.Success)
        {
            color = Answer;
            return "・" + StripInline(bullet.Groups[1].Value);
        }

        Match wholeBold = WholeBoldRegex.Match(line);
        if (wholeBold.Success)
        {
            color = Highlight;
            return StripInline(wholeBold.Groups[1].Value);
        }

        color = Answer;
        return StripInline(line);
    }

    /// <summary>Strip inline Markdown. Bold becomes 【…】 because the chat box can't recolor mid-line.</summary>
    private static string StripInline(string text)
    {
        text = BoldRegex.Replace(text, "【$1】");        // **bold** -> 【bold】
        text = ItalicRegex.Replace(text, "$1");          // *italic* -> italic
        text = CodeRegex.Replace(text, "$1");            // `code` -> code
        text = LinkRegex.Replace(text, "$1");            // [text](url) -> text
        text = text.Replace("**", "").Replace("`", "");                          // any stragglers
        return text.Trim();
    }

    private static IEnumerable<string> Wrap(string text, int maxLength)
    {
        string remaining = text;
        while (remaining.Length > maxLength)
        {
            yield return remaining[..maxLength];
            remaining = remaining[maxLength..];
        }
        if (remaining.Length > 0)
            yield return remaining;
    }
}
