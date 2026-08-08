using StardewModdingAPI;

namespace StardewWikiAgent.Config;

/// <summary>Non-secret local configuration. Environment variables override these values.</summary>
public sealed class ModConfig
{
    public string BaseUrl { get; set; } = "http://localhost:8317/v1";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-5.4-mini";
    public string WikiApiUrl { get; set; } = "https://zh.stardewvalleywiki.com/mediawiki/api.php";
    public int RequestTimeoutSeconds { get; set; } = 90;
    public int MaxAgentSteps { get; set; } = 6;
    public int MaxAnswerCharacters { get; set; } = 1800;
    public bool IncludeGameContext { get; set; } = true;

    internal void Validate(IMonitor monitor)
    {
        if (this.RequestTimeoutSeconds is < 5 or > 300)
        {
            monitor.Log("RequestTimeoutSeconds must be between 5 and 300; using 90.", LogLevel.Warn);
            this.RequestTimeoutSeconds = 90;
        }
        if (this.MaxAgentSteps is < 1 or > 12)
        {
            monitor.Log("MaxAgentSteps must be between 1 and 12; using 6.", LogLevel.Warn);
            this.MaxAgentSteps = 6;
        }
        if (this.MaxAnswerCharacters is < 200 or > 8000)
        {
            monitor.Log("MaxAnswerCharacters must be between 200 and 8000; using 1800.", LogLevel.Warn);
            this.MaxAnswerCharacters = 1800;
        }
    }
}
