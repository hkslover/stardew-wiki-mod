using StardewValley;
using StardewValley.Menus;

namespace StardewWikiAgent.Chat;

internal static class ChatAnswerPresenter
{
    private const int MaxLineLength = 180;

    public static void Show(ChatBox chat, string answer)
    {
        foreach (ChatLine line in MarkdownChatFormatter.Format(answer, MaxLineLength))
            chat.addMessage(line.Text, line.Color);

        // The chat box auto-fades when closed, so the answer can slip past unnoticed.
        // A corner HUD message persists longer and nudges the player to press T to re-read.
        Game1.addHUDMessage(HUDMessage.ForCornerTextbox("AI 回答已就绪，按 T 查看聊天框"));
    }

    public static void ShowError(ChatBox chat, string message)
    {
        chat.addMessage(message, MarkdownChatFormatter.Error);
    }
}
