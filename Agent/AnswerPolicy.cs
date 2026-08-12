namespace StardewWikiAgent.Agent;

/// <summary>Verifies that the agent used the required evidence tools before accepting a final answer.</summary>
internal sealed class AnswerPolicy
{
    private static readonly string[] NavigationTerms =
    {
        "哪里", "在哪", "怎么去", "怎么走", "带我去", "导航", "在哪儿", "位置",
        "zaina", "nali", "zenmequ", "zenmezou", "daohang"
    };

    private readonly bool needsNavigation;
    private readonly bool needsWikiFacts;
    private readonly bool needsWikiForItemKnowledge;
    private bool navigationCorrectionSent;
    private bool wikiCorrectionSent;

    public AnswerPolicy(string question)
    {
        string normalized = question.Trim().ToLowerInvariant();
        this.needsNavigation = NavigationTerms.Any(term => normalized.Contains(term, StringComparison.Ordinal));
        this.needsWikiFacts = normalized.Length > 0
            && !normalized.Equals("stop", StringComparison.OrdinalIgnoreCase);
        this.needsWikiForItemKnowledge = ContainsAny(normalized,
            "有什么用", "用途", "能做什么", "怎么用", "如何用", "怎么获得", "如何获得", "获取", "获得",
            "送给", "喜欢", "礼物", "配方", "食谱", "制作", "售价", "价格", "卖", "值多少钱", "效果");
    }

    public string? GetCorrection(
        bool sawLocationResult,
        bool sawSuccessfulWikiRead,
        bool sawAnyToolCall,
        bool sawSuccessfulItemStateRead
    )
    {
        if (this.needsNavigation && !sawLocationResult && !this.navigationCorrectionSent)
        {
            this.navigationCorrectionSent = true;
            return "The player's question asks for a location or directions. Before answering, call find_game_location with the Wiki-confirmed Chinese place name. If you are unsure of the exact name, look it up on the Wiki first.";
        }

        // Only nudge toward the Wiki when the model answered from pure memory (no
        // tool at all). If it deliberately used any tool — e.g. get_player_status
        // for a state-only question — trust that choice instead of forcing a
        // pointless wiki_search/wiki_read round-trip.
        if (this.needsWikiFacts
            && !sawSuccessfulWikiRead
            && (!sawAnyToolCall
                || (this.needsWikiForItemKnowledge && sawSuccessfulItemStateRead))
            && !this.wikiCorrectionSent)
        {
            this.wikiCorrectionSent = true;
            return "Answer only after consulting the Wiki: call wiki_search then wiki_read for the facts you need.";
        }

        return null;
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.Ordinal));
}
