using System.Text.Json;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Quests;
using StardewWikiAgent.Agent;
using StardewWikiAgent.Api;

namespace StardewWikiAgent.Game;

/// <summary>Reads the local player's active in-game quest journal on demand.</summary>
internal sealed class QuestLogTool : IAgentTool
{
    private const int MaxTextLength = 600;

    public string Name => "get_quest_log";
    public string Description =>
        "Read the player's in-game quest journal on demand: quest titles, descriptions, current objectives, completion status, days left, and rewards. " +
        "Call it only when the player mentions their own quests, journal, quest progress, or what to do next; this is NOT the SMAPI debug log. " +
        "Pass 'query' with a quest title or short keyword to narrow the results; leave it empty to read all current quests when unsure of the exact name.";
    public string ParametersSchemaJson =>
        "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"description\":\"Optional quest title or short keyword; leave empty for all current quests.\"}}}";
    public ToolExecutionAffinity ExecutionAffinity => ToolExecutionAffinity.MainThreadReadOnly;

    public Task<string> ExecuteAsync(
        string argumentsJson,
        GameContextSnapshot context,
        CancellationToken cancellationToken)
    {
        if (!Context.IsWorldReady || Game1.player is null)
            return Task.FromResult(GameToolJson.NotReady());

        string query;
        try
        {
            using JsonDocument arguments = JsonDocument.Parse(argumentsJson);
            query = arguments.RootElement.TryGetProperty("query", out JsonElement queryElement)
                && queryElement.ValueKind == JsonValueKind.String
                    ? queryElement.GetString()?.Trim() ?? ""
                    : "";
        }
        catch (JsonException)
        {
            return Task.FromResult(ToolResultEnvelope.Failure(
                "invalid_arguments",
                "The tool arguments are not valid JSON.",
                "Call the tool again with a JSON object containing an optional string query."
            ));
        }

        var readableQuests = new List<QuestEntry>();
        int unreadableQuests = 0;
        foreach (Quest quest in Game1.player.questLog)
        {
            try
            {
                readableQuests.Add(ToEntry(quest));
            }
            catch
            {
                // A quest added by another mod may be malformed. Keep the rest of the journal usable.
                unreadableQuests++;
            }
        }

        QuestEntry[] allQuests = readableQuests.ToArray();
        string normalizedQuery = Normalize(query);
        QuestEntry[] matches = normalizedQuery.Length == 0
            ? allQuests
            : allQuests.Where(quest => quest.SearchText.Contains(normalizedQuery, StringComparison.Ordinal)).ToArray();

        if (matches.Length > 0 || normalizedQuery.Length == 0)
        {
            return Task.FromResult(ToolResultEnvelope.Success(new
            {
                query,
                totalActiveQuests = Game1.player.questLog.Count,
                matchedQuests = matches.Length,
                unreadableQuests,
                quests = matches.Select(quest => quest.JsonValue).ToArray()
            }));
        }

        return Task.FromResult(ToolResultEnvelope.Failure(
            "not_found",
            "No active quest matched that keyword.",
            "Pick a more precise keyword from availableTitles, or call this tool again with an empty query to read all active quests.",
            new
            {
                query,
                totalActiveQuests = Game1.player.questLog.Count,
                matchedQuests = 0,
                unreadableQuests,
                quests = Array.Empty<object>(),
                availableTitles = allQuests.Select(quest => quest.Title).ToArray()
            }
        ));
    }

    private static QuestEntry ToEntry(Quest quest)
    {
        string title = Clean(quest.GetName());
        string description = Clean(quest.GetDescription());
        string[] objectives = quest.GetObjectiveDescriptions()
            .Select(Clean)
            .Where(text => text.Length > 0)
            .ToArray();
        if (objectives.Length == 0 && !string.IsNullOrWhiteSpace(quest.currentObjective))
            objectives = new[] { Clean(quest.currentObjective) };

        bool isTimed = quest.IsTimedQuest();
        bool hasMoneyReward = quest.HasMoneyReward();
        string rewardDescription = Clean(quest.rewardDescription.Value);
        object jsonValue = new
        {
            title,
            description,
            objectives,
            displayedAsComplete = quest.ShouldDisplayAsComplete(),
            isDailyQuest = quest.dailyQuest.Value,
            daysLeft = isTimed ? quest.GetDaysLeft() : (int?)null,
            rewardDescription = rewardDescription.Length > 0 ? rewardDescription : null,
            moneyReward = hasMoneyReward ? quest.GetMoneyReward() : (int?)null
        };
        string searchText = Normalize(string.Join(" ", new[] { title, description }.Concat(objectives)));
        return new QuestEntry(title, searchText, jsonValue);
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        string cleaned = string.Join(" ", value
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return cleaned.Length <= MaxTextLength ? cleaned : cleaned[..MaxTextLength] + "…";
    }

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private sealed record QuestEntry(string Title, string SearchText, object JsonValue);
}
