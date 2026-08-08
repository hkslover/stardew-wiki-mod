using System.Text.Json;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Quests;
using StardewWikiAgent.Api;

namespace StardewWikiAgent.Game;

/// <summary>Reads the local player's active in-game quest journal on demand.</summary>
internal sealed class QuestLogTool : IAgentTool
{
    private const int MaxTextLength = 600;

    public string Name => "get_quest_log";
    public string Description =>
        "按需读取当前玩家游戏内任务日志中的任务标题、说明、当前目标、完成状态、剩余天数和奖励。" +
        "仅当玩家提到自己的任务、日志、任务进度或下一步该做什么时调用；不要把它当作 SMAPI 调试日志。" +
        "query 可传任务名称或简短关键词以减少返回内容，不确定具体名称时留空读取全部当前任务。";
    public string ParametersSchemaJson =>
        "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"description\":\"可选的任务名称或简短关键词；留空表示当前全部任务\"}}}";
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
            return Task.FromResult(JsonSerializer.Serialize(
                new { error = "参数不是有效的 JSON。" },
                GameToolJson.Options
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

        object result = matches.Length > 0 || normalizedQuery.Length == 0
            ? new
            {
                query,
                totalActiveQuests = Game1.player.questLog.Count,
                matchedQuests = matches.Length,
                unreadableQuests,
                quests = matches.Select(quest => quest.JsonValue).ToArray()
            }
            : new
            {
                query,
                totalActiveQuests = Game1.player.questLog.Count,
                matchedQuests = 0,
                unreadableQuests,
                quests = Array.Empty<object>(),
                availableTitles = allQuests.Select(quest => quest.Title).ToArray(),
                message = "没有匹配该关键词的任务；可根据 availableTitles 选择更准确的关键词，或留空读取全部当前任务。"
            };

        return Task.FromResult(JsonSerializer.Serialize(result, GameToolJson.Options));
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
