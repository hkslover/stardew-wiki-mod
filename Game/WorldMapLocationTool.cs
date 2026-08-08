using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.WorldMaps;
using StardewWikiAgent.Api;

namespace StardewWikiAgent.Game;

/// <summary>Resolves a localized place name against the active Data/WorldMap data.</summary>
internal sealed class WorldMapLocationTool : IAgentTool
{
    public const string ToolName = "find_game_location";

    public string Name => ToolName;
    public string Description =>
        "在当前游戏实际加载的世界地图数据中查找地点。玩家询问某个地点在哪里、怎么走或要求指路时调用；" +
        "query 应使用 Wiki 确认后的中文地点名称。唯一匹配时会返回可用于显示方向箭头的 navigation 目标。";
    public string ParametersSchemaJson =>
        "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"description\":\"Wiki 确认后的地点名称，例如矿井\"}},\"required\":[\"query\"]}";
    public ToolExecutionAffinity ExecutionAffinity => ToolExecutionAffinity.MainThreadReadOnly;

    public Task<string> ExecuteAsync(
        string argumentsJson,
        GameContextSnapshot context,
        CancellationToken cancellationToken)
    {
        if (!Context.IsWorldReady || Game1.player is null)
            return Task.FromResult(GameToolJson.NotReady());

        using JsonDocument arguments = JsonDocument.Parse(argumentsJson);
        string query = arguments.RootElement.TryGetProperty("query", out JsonElement queryElement)
            && queryElement.ValueKind == JsonValueKind.String
                ? queryElement.GetString()?.Trim() ?? ""
                : "";
        if (query.Length == 0)
            return Task.FromResult(JsonSerializer.Serialize(new { error = "query 不能为空" }, GameToolJson.Options));

        string normalizedQuery = Normalize(query);
        var candidates = new List<LocationCandidate>();

        foreach (MapRegion region in WorldMapManager.GetMapRegions())
        {
            foreach (MapArea area in region.GetAreas())
            {
                foreach (MapAreaTooltip tooltip in area.GetTooltips())
                {
                    Rectangle pixelArea = tooltip.GetPixelArea();
                    if (pixelArea.Width <= 0 || pixelArea.Height <= 0)
                        continue;

                    int score = Score(
                        normalizedQuery,
                        Normalize(tooltip.Text),
                        Normalize(tooltip.NamespacedId),
                        Normalize(area.Id)
                    );
                    if (score <= 0)
                        continue;

                    candidates.Add(new LocationCandidate(
                        tooltip.Text,
                        region.Id,
                        area.Id,
                        tooltip.NamespacedId,
                        pixelArea.Center.X,
                        pixelArea.Center.Y,
                        Math.Clamp(Math.Max(pixelArea.Width, pixelArea.Height) * 0.45f, 24f, 64f),
                        score
                    ));
                }
            }
        }

        LocationCandidate[] ranked = candidates
            .GroupBy(candidate => candidate.TooltipId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(candidate => candidate.Score).First())
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.DisplayName, StringComparer.Ordinal)
            .Take(8)
            .ToArray();

        LocationCandidate? resolved = ranked.Length > 0
            && (ranked.Length == 1 || ranked[0].Score > ranked[1].Score)
            ? ranked[0]
            : null;

        object[] results = ranked.Select(candidate => (object)new
        {
            displayName = candidate.DisplayName,
            regionId = candidate.RegionId,
            areaId = candidate.AreaId,
            tooltipId = candidate.TooltipId,
            mapX = candidate.MapX,
            mapY = candidate.MapY,
            matchScore = candidate.Score
        }).ToArray();

        if (resolved is null)
        {
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                query,
                results,
                navigation = (object?)null,
                message = ranked.Length == 0
                    ? "当前游戏世界地图中没有找到对应地点，不能启动箭头导航。"
                    : "找到多个同等匹配的地点，请先向玩家确认具体目标。"
            }, GameToolJson.Options));
        }

        return Task.FromResult(JsonSerializer.Serialize(new
        {
            query,
            results,
            navigation = new
            {
                displayName = resolved.DisplayName,
                regionId = resolved.RegionId,
                areaId = resolved.AreaId,
                tooltipId = resolved.TooltipId,
                mapX = resolved.MapX,
                mapY = resolved.MapY,
                arrivalRadius = resolved.ArrivalRadius
            }
        }, GameToolJson.Options));
    }

    private static int Score(string query, params string[] values)
    {
        int score = 0;
        foreach (string value in values)
        {
            if (value.Length == 0)
                continue;
            if (value == query)
                score = Math.Max(score, 100);
            else if (value.Contains(query, StringComparison.Ordinal))
                score = Math.Max(score, 80);
            else if (query.Contains(value, StringComparison.Ordinal) && value.Length >= 2)
                score = Math.Max(score, 70);
        }
        return score;
    }

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private sealed record LocationCandidate(
        string DisplayName,
        string RegionId,
        string AreaId,
        string TooltipId,
        int MapX,
        int MapY,
        float ArrivalRadius,
        int Score
    );
}
