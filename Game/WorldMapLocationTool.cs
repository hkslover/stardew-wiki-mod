using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.WorldMaps;
using StardewWikiAgent.Agent;
using StardewWikiAgent.Api;

namespace StardewWikiAgent.Game;

/// <summary>Resolves a localized place name against the active Data/WorldMap data.</summary>
internal sealed class WorldMapLocationTool : IAgentTool
{
    public const string ToolName = "find_game_location";

    public string Name => ToolName;
    public string Description =>
        "Look up a place in the game's currently loaded world-map data. Call it when the player asks where a place is, how to get there, or for directions. " +
        "Start with 'query' using the Wiki-confirmed Chinese place name. If matching fails, the result includes allLocations; choose the closest place and call again with its exact tooltipId (and regionId when supplied), or ask the player to clarify. " +
        "A unique match returns a navigation target that starts an on-screen direction arrow.";
    public string ParametersSchemaJson =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"query\":{\"type\":\"string\",\"description\":\"The Wiki-confirmed Chinese place name, e.g. 矿井 (the Mines). Required unless tooltipId is provided.\"}," +
        "\"tooltipId\":{\"type\":\"string\",\"description\":\"An exact tooltipId returned by a previous failed or ambiguous call.\"}," +
        "\"regionId\":{\"type\":\"string\",\"description\":\"Optional regionId paired with tooltipId to select an exact location.\"}}}";
    public ToolExecutionAffinity ExecutionAffinity => ToolExecutionAffinity.MainThreadReadOnly;

    public Task<string> ExecuteAsync(
        string argumentsJson,
        GameContextSnapshot context,
        CancellationToken cancellationToken)
    {
        if (!Context.IsWorldReady || Game1.player is null)
            return Task.FromResult(GameToolJson.NotReady());

        string query;
        string tooltipId;
        string regionId;
        try
        {
            using JsonDocument arguments = JsonDocument.Parse(argumentsJson);
            query = GetString(arguments.RootElement, "query").Trim();
            tooltipId = GetString(arguments.RootElement, "tooltipId").Trim();
            regionId = GetString(arguments.RootElement, "regionId").Trim();
        }
        catch (JsonException)
        {
            return Task.FromResult(ToolResultEnvelope.Failure(
                "invalid_arguments",
                "The tool arguments are not valid JSON.",
                "Call this tool with a query, or with an exact tooltipId returned by an earlier call."
            ));
        }

        if (query.Length == 0 && tooltipId.Length == 0)
        {
            return Task.FromResult(ToolResultEnvelope.Failure(
                "empty_query",
                "Either query or tooltipId must be provided.",
                "Call this tool with the Wiki-confirmed Chinese place name in query."
            ));
        }

        LocationCandidate[] allCandidates = GetAllCandidates();
        object[] allLocations = allCandidates
            .Select(ToCompactResult)
            .ToArray();

        if (tooltipId.Length > 0)
        {
            LocationCandidate[] exactMatches = allCandidates
                .Where(candidate => string.Equals(candidate.TooltipId, tooltipId, StringComparison.Ordinal)
                    && (regionId.Length == 0
                        || string.Equals(candidate.RegionId, regionId, StringComparison.Ordinal)))
                .ToArray();

            if (exactMatches.Length == 1)
                return Task.FromResult(Matched(query, exactMatches[0]));
            if (exactMatches.Length > 1)
                return Task.FromResult(Ambiguous(query, exactMatches));

            return Task.FromResult(NotFound(query, allLocations));
        }

        string normalizedQuery = Normalize(query);
        LocationCandidate[] ranked = allCandidates
            .Select(candidate => candidate with
            {
                Score = Score(
                    normalizedQuery,
                    Normalize(candidate.DisplayName),
                    Normalize(candidate.TooltipId),
                    Normalize(candidate.AreaId)
                )
            })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.DisplayName, StringComparer.Ordinal)
            .Take(8)
            .ToArray();

        LocationCandidate? resolved = ranked.Length > 0
            && (ranked.Length == 1 || ranked[0].Score > ranked[1].Score)
                ? ranked[0]
                : null;

        if (resolved is null)
        {
            return Task.FromResult(ranked.Length == 0
                ? NotFound(query, allLocations)
                : Ambiguous(query, ranked));
        }

        return Task.FromResult(Matched(query, resolved));
    }

    private static LocationCandidate[] GetAllCandidates()
    {
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

                    candidates.Add(new LocationCandidate(
                        tooltip.Text,
                        region.Id,
                        area.Id,
                        tooltip.NamespacedId,
                        pixelArea.Center.X,
                        pixelArea.Center.Y,
                        Math.Clamp(Math.Max(pixelArea.Width, pixelArea.Height) * 0.45f, 24f, 64f),
                        0
                    ));
                }
            }
        }

        return candidates
            .GroupBy(candidate => (candidate.RegionId, candidate.TooltipId))
            .Select(group => group.First())
            .OrderBy(candidate => candidate.RegionId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.DisplayName, StringComparer.Ordinal)
            .ToArray();
    }

    private static string Matched(string query, LocationCandidate resolved)
    {
        // On a unique match the model only needs the navigation block; omit the
        // ranked-candidate list to keep the tool result small.
        return ToolResultEnvelope.Success(new
        {
            status = "matched",
            query,
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
        });
    }

    private static string Ambiguous(string query, IReadOnlyCollection<LocationCandidate> candidates)
    {
        return ToolResultEnvelope.Failure(
            "ambiguous",
            "Several equally matching world-map locations were found.",
            "Use the player's current context to choose a candidate and call this tool again with its tooltipId and regionId, or ask the player to clarify.",
            new
            {
                status = "ambiguous",
                query,
                candidates = candidates.Select(ToRankedResult).ToArray()
            }
        );
    }

    private static string NotFound(string query, object[] allLocations)
    {
        return ToolResultEnvelope.Failure(
            "not_found",
            "No matching place was found in the current world map.",
            "Pick the closest place from allLocations (the player may use nicknames, pinyin, or partial names) and call this tool again with its tooltipId, or ask the player to clarify.",
            new
            {
                status = "not_found",
                query,
                allLocations
            }
        );
    }

    private static object ToCompactResult(LocationCandidate candidate)
    {
        return new
        {
            displayName = candidate.DisplayName,
            regionId = candidate.RegionId,
            areaId = candidate.AreaId,
            tooltipId = candidate.TooltipId
        };
    }

    private static object ToRankedResult(LocationCandidate candidate)
    {
        return new
        {
            displayName = candidate.DisplayName,
            regionId = candidate.RegionId,
            areaId = candidate.AreaId,
            tooltipId = candidate.TooltipId,
            mapX = candidate.MapX,
            mapY = candidate.MapY,
            matchScore = candidate.Score
        };
    }

    private static string GetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";
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
