using System.Text.Json;
using StardewModdingAPI;
using StardewValley;
using StardewWikiAgent.Agent;
using StardewWikiAgent.Api;

namespace StardewWikiAgent.Game;

/// <summary>Shared JSON options so Chinese display names are emitted as-is, not \uXXXX escapes.</summary>
internal static class GameToolJson
{
    public static string NotReady() =>
        ToolResultEnvelope.Failure(
            "game_not_ready",
            "No save is loaded, so live game state is unavailable.",
            "Ask the player to load a save, then retry this tool."
        );
}

/// <summary>Reads the player's carried inventory. Runs on the main thread and reads live game state.</summary>
internal sealed class InventoryTool : IAgentTool
{
    public string Name => "get_inventory";
    public string Description => "Read the names and quantities of items in the player's carried inventory. Use it to check what materials, gifts, crops, etc. the player currently has.";
    public string ParametersSchemaJson => "{\"type\":\"object\",\"properties\":{}}";
    public ToolExecutionAffinity ExecutionAffinity => ToolExecutionAffinity.MainThreadReadOnly;

    public Task<string> ExecuteAsync(string argumentsJson, GameContextSnapshot context, CancellationToken cancellationToken)
    {
        if (!Context.IsWorldReady || Game1.player is null)
            return Task.FromResult(GameToolJson.NotReady());

        var items = Game1.player.Items
            .Where(item => item is not null)
            .Select(item => new { name = item!.DisplayName, quantity = item.Stack })
            .ToArray();
        return Task.FromResult(ToolResultEnvelope.Success(new
        {
            usedSlots = items.Length,
            capacity = Game1.player.MaxItems,
            items
        }));
    }
}

/// <summary>Reads money, energy, health, skill levels and today's luck.</summary>
internal sealed class PlayerStatusTool : IAgentTool
{
    public string Name => "get_player_status";
    public string Description => "Read the player's money, energy, health, skill levels (farming/mining/foraging/fishing/combat/luck), and today's luck.";
    public string ParametersSchemaJson => "{\"type\":\"object\",\"properties\":{}}";
    public ToolExecutionAffinity ExecutionAffinity => ToolExecutionAffinity.MainThreadReadOnly;

    public Task<string> ExecuteAsync(string argumentsJson, GameContextSnapshot context, CancellationToken cancellationToken)
    {
        if (!Context.IsWorldReady || Game1.player is null)
            return Task.FromResult(GameToolJson.NotReady());

        Farmer p = Game1.player;
        return Task.FromResult(ToolResultEnvelope.Success(new
        {
            money = p.Money,
            energy = (int)p.Stamina,
            maxEnergy = p.MaxStamina,
            health = p.health,
            maxHealth = p.maxHealth,
            skills = new
            {
                farming = p.FarmingLevel,
                mining = p.MiningLevel,
                foraging = p.ForagingLevel,
                fishing = p.FishingLevel,
                combat = p.CombatLevel,
                luck = p.LuckLevel
            },
            dailyLuck = p.DailyLuck
        }));
    }
}

/// <summary>Reads villager friendship levels, birthdays and whether they were gifted today.</summary>
internal sealed class RelationshipsTool : IAgentTool
{
    public string Name => "get_relationships";
    public string Description => "Read villagers' friendship level (hearts 0-14), birthday, and how many gifts were given today/this week. Pass 'name' with a villager's Chinese display name (or internal name) to get just that villager; leave it empty for everyone. Use it for gifting and social advice.";
    public string ParametersSchemaJson => "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"description\":\"Optional villager name (Chinese display name works best, e.g. 艾米丽) to return only that villager; leave empty for all.\"}}}";
    public ToolExecutionAffinity ExecutionAffinity => ToolExecutionAffinity.MainThreadReadOnly;

    public Task<string> ExecuteAsync(string argumentsJson, GameContextSnapshot context, CancellationToken cancellationToken)
    {
        if (!Context.IsWorldReady || Game1.player is null)
            return Task.FromResult(GameToolJson.NotReady());

        string name;
        try
        {
            using JsonDocument args = JsonDocument.Parse(argumentsJson);
            name = args.RootElement.TryGetProperty("name", out JsonElement nameElement)
                && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()?.Trim() ?? ""
                    : "";
        }
        catch (JsonException)
        {
            return Task.FromResult(ToolResultEnvelope.Failure(
                "invalid_arguments",
                "The tool arguments are not valid JSON.",
                "Call the tool again with a JSON object containing an optional string name."
            ));
        }

        var people = new List<Villager>();
        foreach (var pair in Game1.player.friendshipData.Pairs)
        {
            Friendship friendship = pair.Value;
            NPC? npc = Game1.getCharacterFromName(pair.Key);
            string displayName = npc?.displayName ?? pair.Key;
            string? birthday = npc is not null && !string.IsNullOrEmpty(npc.Birthday_Season)
                ? $"{npc.Birthday_Season} {npc.Birthday_Day}"
                : null;
            people.Add(new Villager(displayName, pair.Key, new
            {
                name = displayName,
                hearts = friendship.Points / 250,
                birthday,
                giftsToday = friendship.GiftsToday,
                giftsThisWeek = friendship.GiftsThisWeek
            }));
        }

        string normalizedQuery = Normalize(name);
        if (normalizedQuery.Length == 0)
            return Task.FromResult(ToolResultEnvelope.Success(new
            {
                relationships = people.Select(person => person.JsonValue).ToArray()
            }));

        Villager[] matches = people
            .Where(person => Normalize(person.DisplayName).Contains(normalizedQuery, StringComparison.Ordinal)
                || Normalize(person.InternalName).Contains(normalizedQuery, StringComparison.Ordinal))
            .ToArray();

        if (matches.Length > 0)
            return Task.FromResult(ToolResultEnvelope.Success(new
            {
                query = name,
                matched = matches.Length,
                relationships = matches.Select(person => person.JsonValue).ToArray()
            }));

        return Task.FromResult(ToolResultEnvelope.Failure(
            "not_found",
            "No villager matched that name.",
            "Pick a name from availableNames, or call this tool again with an empty name to list everyone.",
            new
            {
                query = name,
                availableNames = people.Select(person => person.DisplayName).ToArray()
            }
        ));
    }

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private sealed record Villager(string DisplayName, string InternalName, object JsonValue);
}
