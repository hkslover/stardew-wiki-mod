using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.WorldMaps;

namespace StardewWikiAgent.Game;

/// <summary>Tracks one local navigation target and renders a compass arrow near the player.</summary>
internal sealed class NavigationService : IDisposable
{
    private const int ArrowWidth = 24;
    private const int ArrowHeight = 13;
    private const float ArrowOffset = 42f;

    private readonly IMonitor monitor;
    private NavigationTarget? target;
    private Vector2 direction;
    private bool hasDirection;
    private bool warnedDifferentRegion;
    private Texture2D? arrowTexture;

    public NavigationService(IMonitor monitor)
    {
        this.monitor = monitor;
    }

    public bool IsActive => this.target is not null;

    public string? TargetName => this.target?.DisplayName;

    public void Start(NavigationTarget target)
    {
        this.target = target;
        this.hasDirection = false;
        this.warnedDifferentRegion = false;
        this.monitor.Log($"Navigation started: {target.DisplayName} ({target.RegionId}/{target.AreaId}).", LogLevel.Debug);
        this.UpdateDirection();
    }

    public bool Stop()
    {
        bool wasActive = this.target is not null;
        this.target = null;
        this.hasDirection = false;
        this.warnedDifferentRegion = false;
        return wasActive;
    }

    public void Update(UpdateTickedEventArgs e)
    {
        if (this.target is null || !e.IsMultipleOf(6))
            return;

        this.UpdateDirection();
    }

    public void Render(object? sender, RenderedWorldEventArgs e)
    {
        if (this.target is null || !this.hasDirection || !Context.IsWorldReady
            || Game1.player is null || Game1.activeClickableMenu is not null)
            return;

        this.arrowTexture ??= CreateArrowTexture(Game1.graphics.GraphicsDevice);

        Rectangle playerBounds = Game1.player.GetBoundingBox();
        Vector2 footWorld = new(playerBounds.Center.X, playerBounds.Bottom);
        Vector2 footScreen = Game1.GlobalToLocal(Game1.viewport, footWorld);
        Vector2 drawPosition = footScreen + this.direction * ArrowOffset;
        float rotation = MathF.Atan2(this.direction.Y, this.direction.X);
        float pulse = 1.8f + 0.12f * MathF.Sin(Game1.ticks * 0.12f);

        e.SpriteBatch.Draw(
            this.arrowTexture,
            drawPosition,
            null,
            Color.White,
            rotation,
            new Vector2(ArrowWidth / 2f, ArrowHeight / 2f),
            pulse,
            SpriteEffects.None,
            1f
        );
    }

    public void Dispose()
    {
        this.arrowTexture?.Dispose();
        this.arrowTexture = null;
    }

    private void UpdateDirection()
    {
        NavigationTarget? currentTarget = this.target;
        if (currentTarget is null || !Context.IsWorldReady || Game1.player?.currentLocation is null)
        {
            this.hasDirection = false;
            return;
        }

        MapAreaPositionWithContext? positionResult = WorldMapManager.GetPositionData(
            Game1.player.currentLocation,
            Game1.player.TilePoint
        );
        if (positionResult is null)
        {
            this.hasDirection = false;
            return;
        }
        MapAreaPositionWithContext position = positionResult.Value;

        if (!string.Equals(position.Data.Region.Id, currentTarget.RegionId, StringComparison.Ordinal))
        {
            this.hasDirection = false;
            if (!this.warnedDifferentRegion)
            {
                this.warnedDifferentRegion = true;
                this.monitor.Log(
                    $"Navigation target '{currentTarget.DisplayName}' is in a different world-map region; arrow paused.",
                    LogLevel.Debug
                );
            }
            return;
        }

        this.warnedDifferentRegion = false;
        var currentMapPixel = position.GetMapPixelPosition();
        Vector2 difference = currentTarget.MapPixel - new Vector2(currentMapPixel.X, currentMapPixel.Y);
        if (difference.LengthSquared() <= currentTarget.ArrivalRadius * currentTarget.ArrivalRadius)
        {
            string displayName = currentTarget.DisplayName;
            this.Stop();
            Game1.addHUDMessage(HUDMessage.ForCornerTextbox($"已到达：{displayName}"));
            this.monitor.Log($"Navigation completed: {displayName}.", LogLevel.Debug);
            return;
        }

        this.direction = Vector2.Normalize(difference);
        this.hasDirection = true;
    }

    private static Texture2D CreateArrowTexture(GraphicsDevice graphicsDevice)
    {
        var texture = new Texture2D(graphicsDevice, ArrowWidth, ArrowHeight);
        var pixels = new Color[ArrowWidth * ArrowHeight];
        int centerY = ArrowHeight / 2;

        for (int x = 1; x <= 15; x++)
        {
            for (int y = centerY - 2; y <= centerY + 2; y++)
                SetPixel(pixels, x, y, x is 1 or 15 || y == centerY - 2 || y == centerY + 2 ? Color.DarkGoldenrod : Color.Gold);
        }

        for (int x = 13; x <= 22; x++)
        {
            int halfHeight = Math.Max(0, 5 - (x - 13) / 2);
            for (int y = centerY - halfHeight; y <= centerY + halfHeight; y++)
            {
                bool outline = x == 13 || x == 22 || y == centerY - halfHeight || y == centerY + halfHeight;
                SetPixel(pixels, x, y, outline ? Color.DarkGoldenrod : Color.Gold);
            }
        }

        texture.SetData(pixels);
        return texture;
    }

    private static void SetPixel(Color[] pixels, int x, int y, Color color)
    {
        if (x >= 0 && x < ArrowWidth && y >= 0 && y < ArrowHeight)
            pixels[y * ArrowWidth + x] = color;
    }
}
