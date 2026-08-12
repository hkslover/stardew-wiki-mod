using System.Reflection;
using System.Text.Json.Serialization;
using StardewValley;

namespace StardewWikiAgent.Game;

/// <summary>Small immutable copy of the item selected when a question was submitted.</summary>
public sealed class SelectedItemSnapshot
{
    [JsonPropertyName("slot")]
    public int Slot { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("qualifiedItemId")]
    public string QualifiedItemId { get; init; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "other";

    [JsonPropertyName("quantity")]
    public int Quantity { get; init; }

    [JsonPropertyName("quality")]
    public int Quality { get; init; }

    [JsonPropertyName("qualityName")]
    public string QualityName { get; init; } = "普通";

    [JsonPropertyName("toolType")]
    public string? ToolType { get; init; }

    [JsonPropertyName("upgradeLevel")]
    public int? UpgradeLevel { get; init; }

    /// <summary>
    /// Copy the mutable game item into plain values. The returned object must not retain the
    /// original Item reference or any Netcode value.
    /// </summary>
    internal static SelectedItemSnapshot? Capture(Item? item, int slot)
    {
        if (item is null)
            return null;

        string name = SafeString(() => item.DisplayName, item.GetType().Name);
        string qualifiedItemId = SafeString(() => item.QualifiedItemId, "");
        int quality = SafeInt(() => item.Quality, 0);
        ItemClassification classification = ItemSnapshotClassifier.Classify(item);
        string? toolType = classification.PrimaryKind == "tool"
            ? ItemSnapshotClassifier.GetToolType(item)
            : null;

        return new SelectedItemSnapshot
        {
            Slot = Math.Max(1, slot),
            Name = name,
            QualifiedItemId = qualifiedItemId,
            Kind = classification.PrimaryKind,
            Quantity = Math.Max(0, SafeInt(() => item.Stack, 1)),
            Quality = quality,
            QualityName = GetQualityName(quality),
            ToolType = toolType,
            UpgradeLevel = toolType is null ? null : ItemSnapshotClassifier.GetUpgradeLevel(item),
        };
    }

    internal static string GetQualityName(int quality) => quality switch
    {
        0 => "普通",
        1 => "银星",
        2 => "金星",
        4 => "铱星",
        _ => $"质量{quality}",
    };

    private static string SafeString(Func<string> read, string fallback)
    {
        try
        {
            return read() ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static int SafeInt(Func<int> read, int fallback)
    {
        try
        {
            return read();
        }
        catch
        {
            return fallback;
        }
    }
}

/// <summary>Classifies items locally without sending descriptions, tags, or modData to the model.</summary>
internal sealed record ItemClassification(string PrimaryKind, HashSet<string> SearchKinds);

internal static class ItemSnapshotClassifier
{
    private static readonly HashSet<string> WeaponTypeNames = new(StringComparer.Ordinal)
    {
        "MeleeWeapon",
        "Slingshot",
        "Weapon",
    };

    private static readonly Dictionary<string, string> ToolTypeNames = new(StringComparer.Ordinal)
    {
        ["Axe"] = "axe",
        ["FishingRod"] = "fishing_rod",
        ["Hoe"] = "hoe",
        ["Pickaxe"] = "pickaxe",
        ["WateringCan"] = "watering_can",
        ["Pan"] = "pan",
        ["MilkPail"] = "milk_pail",
        ["Shears"] = "shears",
        ["Slingshot"] = "slingshot",
        ["Wand"] = "wand",
    };

    public static ItemClassification Classify(Item item)
    {
        var searchKinds = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            IReadOnlyList<string> typeNames = GetTypeNames(item.GetType());
            if (typeNames.Any(WeaponTypeNames.Contains))
                searchKinds.Add("weapon");
            if (typeNames.Any(name => name == "Tool") || GetToolType(item) is not null)
                searchKinds.Add("tool");

            IReadOnlyList<string> tags = GetContextTags(item);
            if (HasTag(tags, "seed"))
                searchKinds.Add("seed");
            if (HasTag(tags, "fish"))
                searchKinds.Add("fish");
            if (HasTag(tags, "mineral", "ore", "geode"))
                searchKinds.Add("mineral");
            if (HasTag(tags, "crop", "fruit", "vegetable", "flower"))
                searchKinds.Add("crop");
            if (HasTag(tags, "artisan"))
                searchKinds.Add("artisan_good");
            if (HasTag(tags, "resource", "forage", "wood", "stone", "ingot"))
                searchKinds.Add("resource");
            if (TryGetIntProperty(item, "Edibility") is >= 0)
                searchKinds.Add("food");
        }
        catch
        {
            // A third-party item must not make the whole inventory tool fail.
        }

        if (searchKinds.Count == 0)
            searchKinds.Add("other");

        string primaryKind = searchKinds.Contains("weapon")
            ? "weapon"
            : searchKinds.Contains("tool")
                ? "tool"
                : searchKinds.Contains("seed")
                    ? "seed"
                    : searchKinds.Contains("fish")
                        ? "fish"
                        : searchKinds.Contains("mineral")
                            ? "mineral"
                            : searchKinds.Contains("crop")
                                ? "crop"
                                : searchKinds.Contains("artisan_good")
                                    ? "artisan_good"
                                    : searchKinds.Contains("resource")
                                        ? "resource"
                                        : searchKinds.Contains("food")
                                            ? "food"
                                            : "other";
        return new ItemClassification(primaryKind, searchKinds);
    }

    public static string? GetToolType(Item item)
    {
        try
        {
            IReadOnlyList<string> typeNames = GetTypeNames(item.GetType());
            foreach (string typeName in typeNames)
            {
                if (ToolTypeNames.TryGetValue(typeName, out string? toolType))
                    return toolType;
            }

            return typeNames.Any(name => name == "Tool")
                ? ToSnakeCase(item.GetType().Name)
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static int? GetUpgradeLevel(Item item) => TryGetIntProperty(item, "UpgradeLevel");

    public static IReadOnlyList<string> GetContextTags(Item item)
    {
        try
        {
            return item.GetContextTags().ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static bool HasTag(IReadOnlyList<string> tags, params string[] fragments)
    {
        return tags.Any(tag =>
        {
            string normalized = Normalize(tag);
            return fragments.Any(fragment => normalized.Contains(Normalize(fragment), StringComparison.Ordinal));
        });
    }

    private static IReadOnlyList<string> GetTypeNames(Type type)
    {
        var names = new List<string>();
        for (Type? current = type; current is not null; current = current.BaseType)
            names.Add(current.Name);
        return names;
    }

    private static int? TryGetIntProperty(Item item, string propertyName)
    {
        try
        {
            PropertyInfo? property = item.GetType().GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public
            );
            if (property?.PropertyType != typeof(int))
                return null;
            return property.GetValue(item) is int value ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string ToSnakeCase(string value)
    {
        if (value.Length == 0)
            return "tool";

        var chars = new List<char>(value.Length + 8);
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (char.IsUpper(character) && index > 0)
                chars.Add('_');
            chars.Add(char.ToLowerInvariant(character));
        }
        return new string(chars.ToArray());
    }
}
