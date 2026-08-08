using System.Text.Json;
using Microsoft.Xna.Framework;

namespace StardewWikiAgent.Game;

/// <summary>A resolved point on one of the game's world-map regions.</summary>
internal sealed class NavigationTarget
{
    public string DisplayName { get; init; } = "";
    public string RegionId { get; init; } = "";
    public string AreaId { get; init; } = "";
    public string TooltipId { get; init; } = "";
    public Vector2 MapPixel { get; init; }
    public float ArrivalRadius { get; init; }

    public static bool TryFromToolResult(string json, out NavigationTarget? target)
    {
        target = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("navigation", out JsonElement navigation)
                || navigation.ValueKind != JsonValueKind.Object)
                return false;

            string displayName = GetString(navigation, "displayName");
            string regionId = GetString(navigation, "regionId");
            if (displayName.Length == 0 || regionId.Length == 0
                || !TryGetSingle(navigation, "mapX", out float mapX)
                || !TryGetSingle(navigation, "mapY", out float mapY))
                return false;

            TryGetSingle(navigation, "arrivalRadius", out float arrivalRadius);
            target = new NavigationTarget
            {
                DisplayName = displayName,
                RegionId = regionId,
                AreaId = GetString(navigation, "areaId"),
                TooltipId = GetString(navigation, "tooltipId"),
                MapPixel = new Vector2(mapX, mapY),
                ArrivalRadius = Math.Max(16f, arrivalRadius)
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static bool TryGetSingle(JsonElement element, string property, out float value)
    {
        value = 0;
        return element.TryGetProperty(property, out JsonElement raw)
            && raw.ValueKind == JsonValueKind.Number
            && raw.TryGetSingle(out value);
    }
}
