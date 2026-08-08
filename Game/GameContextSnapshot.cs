using StardewValley;
using StardewModdingAPI;

namespace StardewWikiAgent.Game;

/// <summary>Immutable, low-volume game state captured on the SMAPI main thread.</summary>
public sealed class GameContextSnapshot
{
    public int Year { get; init; }
    public string Season { get; init; } = "unknown";
    public int DayOfMonth { get; init; }
    public int TimeOfDay { get; init; }
    public string Weather { get; init; } = "unknown";
    public string Location { get; init; } = "unknown";
    public int TileX { get; init; }
    public int TileY { get; init; }
    public string PlayerName { get; init; } = "unknown";
    public int Money { get; init; }
    public int Energy { get; init; }
    public int Health { get; init; }
    public string Language { get; init; } = "unknown";
    public bool IsMultiplayer { get; init; }
    public bool IsMainPlayer { get; init; }

    public static GameContextSnapshot Capture()
    {
        if (!Context.IsWorldReady || Game1.player is null)
            return new GameContextSnapshot();

        Farmer player = Game1.player;
        var location = player.currentLocation;
        string weather = Game1.isSnowing
            ? "snow"
            : Game1.isLightning
                ? "storm"
                : Game1.isRaining
                    ? "rain"
                    : "sun";

        return new GameContextSnapshot
        {
            Year = Game1.year,
            Season = Game1.currentSeason,
            DayOfMonth = Game1.dayOfMonth,
            TimeOfDay = Game1.timeOfDay,
            Weather = weather,
            Location = location?.NameOrUniqueName ?? "unknown",
            TileX = player.TilePoint.X,
            TileY = player.TilePoint.Y,
            PlayerName = player.Name,
            Money = player.Money,
            Energy = (int)player.Stamina,
            Health = player.health,
            Language = LocalizedContentManager.CurrentLanguageCode.ToString(),
            IsMultiplayer = Game1.IsMultiplayer,
            IsMainPlayer = Context.IsMainPlayer
        };
    }

    public string ToPromptText()
    {
        return $"year={this.Year}, season={this.Season}, day={this.DayOfMonth}, " +
               $"time={this.TimeOfDay}, weather={this.Weather}, location={this.Location}, " +
               $"tile=({this.TileX},{this.TileY}), player={this.PlayerName}, " +
               $"money={this.Money}, energy={this.Energy}, health={this.Health}, " +
               $"language={this.Language}, multiplayer={this.IsMultiplayer}, host={this.IsMainPlayer}";
    }

    /// <summary>A short context line for the prompt; richer state is fetched on demand via tools.</summary>
    public string ToCompactPromptText()
    {
        return $"year={this.Year}, season={this.Season}, day={this.DayOfMonth}, " +
               $"time={this.TimeOfDay}, weather={this.Weather}, location={this.Location}, " +
               $"language={this.Language}";
    }
}
