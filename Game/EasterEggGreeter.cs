using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace StardewWikiAgent.Game;

/// <summary>
/// A private, hardcoded easter egg: shows warm morning and evening greetings, but only on
/// one specific Steam account. No configuration and no-op for anyone else.
/// </summary>
internal sealed class EasterEggGreeter
{
    /// <summary>In-game time (18:00) at which the evening greeting fires.</summary>
    private const int EveningTime = 1800;

    /// <summary>The only SteamID64 that ever sees this easter egg.</summary>
    // private const ulong TargetSteamId = 76561199821457254UL;
    /// test account: 76561198821457254
    private const ulong TargetSteamId = 76561198799057771UL;

    /// <summary>The only in-game farmer name that ever sees this easter egg.</summary>
    private const string TargetName = "冬哥";

    private const string Signature = "snow";

    private static readonly string[] MorningMessages =
    {
        "早安呀，今天也要开开心心地照顾农场哦～",
        "新的一天开始啦，记得先吃早餐，我会惦记你的。",
        "早上好，愿今天的星露谷阳光正好，你的心情也很好。",
        "睁开眼又是新的一天，希望今天也有很多小幸运。"
    };

    private static readonly string[] EveningMessages =
    {
        "天色渐晚，别把自己累坏了，早点休息哦。",
        "傍晚啦，今天辛苦了，送你一个远程抱抱。",
        "夕阳很好看，也想提醒你晚上早点睡～",
        "又到傍晚了，不管今天过得怎样，都有人惦记你。"
    };

    private readonly IMonitor monitor;
    private readonly Random random = new();
    private bool eveningShownToday;

    public EasterEggGreeter(IMonitor monitor)
    {
        this.monitor = monitor;
    }

    public void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        this.eveningShownToday = false;
        if (this.IsTarget())
            this.Show(MorningMessages);
    }

    public void OnTimeChanged(object? sender, TimeChangedEventArgs e)
    {
        if (this.eveningShownToday || e.NewTime < EveningTime)
            return;
        this.eveningShownToday = true;
        if (this.IsTarget())
            this.Show(EveningMessages);
    }

    private bool IsTarget()
    {
        return Context.IsWorldReady
            && SteamIdentity.TryGetSteamId(out ulong steamId)
            && steamId == TargetSteamId;
    }

    private void Show(string[] messages)
    {
        string body = messages[this.random.Next(messages.Length)];
        Game1.addHUDMessage(HUDMessage.ForCornerTextbox($"{TargetName} {body}  —— {Signature}"));
        this.monitor.Log("Easter egg greeting shown.", LogLevel.Trace);
    }
}
