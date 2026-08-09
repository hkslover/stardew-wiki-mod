using StardewModdingAPI;

namespace StardewWikiAgent.Config;

/// <summary>Non-secret local configuration. Environment variables override these values.</summary>
public sealed class ModConfig
{
    public const int DefaultMaxResponseTokens = 8192;
    private const int LegacyDefaultMaxResponseTokens = 2048;

    public string BaseUrl { get; set; } = "http://localhost:8317/v1";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "deepseek-v4-flash";
    public string WikiApiUrl { get; set; } = "https://zh.stardewvalleywiki.com/mediawiki/api.php";
    public int RequestTimeoutSeconds { get; set; } = 90;
    public int MaxAgentSteps { get; set; } = 8;
    public int MaxAnswerCharacters { get; set; } = 1800;
    public int MaxResponseTokens { get; set; } = DefaultMaxResponseTokens;

    // Thinking budget for reasoning models (DeepSeek V4's reasoning_effort). Lower
    // values spend fewer billed reasoning tokens per query; "high" maximizes depth.
    public string ReasoningEffort { get; set; } = "medium";
    public bool IncludeGameContext { get; set; } = true;
    public bool EnableQuestLogTool { get; set; } = true;

    // Voice input (push-to-talk): hold the hotkey to record, then release it to
    // transcribe locally and send the recognized text through the normal ask flow.
    public bool EnableVoiceInput { get; set; } = true;
    public string VoiceHotkey { get; set; } = "V";
    public int VoiceMaxSeconds { get; set; } = 30;
    public int VoiceNumThreads { get; set; } = 2;

    internal void Validate(IMonitor monitor)
    {
        if (this.RequestTimeoutSeconds is < 5 or > 300)
        {
            monitor.Log("RequestTimeoutSeconds must be between 5 and 300; using 90.", LogLevel.Warn);
            this.RequestTimeoutSeconds = 90;
        }
        if (this.MaxAgentSteps is < 1 or > 12)
        {
            monitor.Log("MaxAgentSteps must be between 1 and 12; using 8.", LogLevel.Warn);
            this.MaxAgentSteps = 8;
        }
        if (this.MaxAnswerCharacters is < 200 or > 8000)
        {
            monitor.Log("MaxAnswerCharacters must be between 200 and 8000; using 1800.", LogLevel.Warn);
            this.MaxAnswerCharacters = 1800;
        }
        if (this.MaxResponseTokens == LegacyDefaultMaxResponseTokens)
        {
            monitor.Log(
                $"Migrating the old MaxResponseTokens default from {LegacyDefaultMaxResponseTokens} to {DefaultMaxResponseTokens}.",
                LogLevel.Info
            );
            this.MaxResponseTokens = DefaultMaxResponseTokens;
        }
        if (this.MaxResponseTokens is < 1024 or > 131072)
        {
            monitor.Log(
                $"MaxResponseTokens must be between 1024 and 131072; using {DefaultMaxResponseTokens}.",
                LogLevel.Warn
            );
            this.MaxResponseTokens = DefaultMaxResponseTokens;
        }
        if (!IsValidReasoningEffort(this.ReasoningEffort))
        {
            monitor.Log("ReasoningEffort must be one of low/medium/high; using medium.", LogLevel.Warn);
            this.ReasoningEffort = "medium";
        }
        if (this.VoiceMaxSeconds is < 3 or > 120)
        {
            monitor.Log("VoiceMaxSeconds must be between 3 and 120; using 30.", LogLevel.Warn);
            this.VoiceMaxSeconds = 30;
        }
        if (this.VoiceNumThreads is < 1 or > 8)
        {
            monitor.Log("VoiceNumThreads must be between 1 and 8; using 2.", LogLevel.Warn);
            this.VoiceNumThreads = 2;
        }
    }

    private static bool IsValidReasoningEffort(string value) =>
        value is not null
        && (value.Equals("low", StringComparison.OrdinalIgnoreCase)
            || value.Equals("medium", StringComparison.OrdinalIgnoreCase)
            || value.Equals("high", StringComparison.OrdinalIgnoreCase));
}
