using StardewValley;
using StardewValley.Menus;

namespace StardewWikiAgent.Chat;

internal static class ChatAnswerPresenter
{
    private const int MaxLineLength = 180;

    public static void Show(ChatBox chat, string answer, IReadOnlyList<string>? sources = null)
    {
        string completeAnswer = AppendAuthoritativeSources(answer, sources ?? Array.Empty<string>());
        foreach (ChatLine line in MarkdownChatFormatter.Format(completeAnswer, MaxLineLength))
            chat.addMessage(line.Text, line.Color);

        // The chat box auto-fades when closed, so the answer can slip past unnoticed.
        // A corner HUD message persists longer and nudges the player to press T to re-read.
        Game1.addHUDMessage(HUDMessage.ForCornerTextbox("回答已就绪，按 T 查看聊天框"));
    }

    public static void ShowError(ChatBox chat, string message)
    {
        chat.addMessage(message, MarkdownChatFormatter.Error);
    }

    private static string AppendAuthoritativeSources(string answer, IReadOnlyList<string> sources)
    {
        string text = answer ?? "";
        string[] authoritativeSources = sources
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(source => text.Contains(source, StringComparison.OrdinalIgnoreCase))
            .Take(3)
            .ToArray();
        if (authoritativeSources.Length == 0)
            return text;

        // Replace model-authored source lines with the URLs actually returned by
        // tools. This keeps one complete, de-duplicated source line at the end,
        // outside AgentRunner's body character budget.
        string body = string.Join(
            "\n",
            text.Replace("\r\n", "\n")
                .Split('\n')
                .Where(line => !IsSourceLine(line))
        ).TrimEnd();
        string separator = body.Length == 0 ? "" : "\n";
        return body + separator + "来源：" + string.Join(" ", authoritativeSources);
    }

    private static bool IsSourceLine(string line)
    {
        string trimmed = line.TrimStart('*', ' ');
        return trimmed.StartsWith("来源:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("来源：", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Sources:", StringComparison.OrdinalIgnoreCase);
    }
}
