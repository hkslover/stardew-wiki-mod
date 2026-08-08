using System.Text.Encodings.Web;
using System.Text.Json;
using StardewModdingAPI;
using StardewValley;
using StardewWikiAgent.Api;

namespace StardewWikiAgent.Game;

/// <summary>Shared JSON options so Chinese display names are emitted as-is, not \uXXXX escapes.</summary>
internal static class GameToolJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string NotReady() =>
        JsonSerializer.Serialize(new { error = "no save is loaded; game state is unavailable" }, Options);
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
        return Task.FromResult(JsonSerializer.Serialize(new
        {
            usedSlots = items.Length,
            capacity = Game1.player.MaxItems,
            items
        }, GameToolJson.Options));
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
        return Task.FromResult(JsonSerializer.Serialize(new
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
        }, GameToolJson.Options));
    }
}

/// <summary>Reads villager friendship levels, birthdays and whether they were gifted today.</summary>
internal sealed class RelationshipsTool : IAgentTool
{
    public string Name => "get_relationships";
    public string Description => "Read each villager's friendship level (hearts 0-14), birthday, and how many gifts were given today/this week. Use it for gifting and social advice.";
    public string ParametersSchemaJson => "{\"type\":\"object\",\"properties\":{}}";
    public ToolExecutionAffinity ExecutionAffinity => ToolExecutionAffinity.MainThreadReadOnly;

    public Task<string> ExecuteAsync(string argumentsJson, GameContextSnapshot context, CancellationToken cancellationToken)
    {
        if (!Context.IsWorldReady || Game1.player is null)
            return Task.FromResult(GameToolJson.NotReady());

        var people = new List<object>();
        foreach (var pair in Game1.player.friendshipData.Pairs)
        {
            Friendship friendship = pair.Value;
            NPC? npc = Game1.getCharacterFromName(pair.Key);
            string? birthday = npc is not null && !string.IsNullOrEmpty(npc.Birthday_Season)
                ? $"{npc.Birthday_Season} {npc.Birthday_Day}"
                : null;
            people.Add(new
            {
                name = npc?.displayName ?? pair.Key,
                hearts = friendship.Points / 250,
                birthday,
                giftsToday = friendship.GiftsToday,
                giftsThisWeek = friendship.GiftsThisWeek
            });
        }
        return Task.FromResult(JsonSerializer.Serialize(new { relationships = people }, GameToolJson.Options));
    }
}
