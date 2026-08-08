using StardewModdingAPI;

namespace StardewWikiAgent.Config;

internal sealed class AgentSettings
{
    public string BaseUrl { get; private init; } = "";
    public string ApiKey { get; private init; } = "";
    public string Model { get; private init; } = "";
    public string WikiApiUrl { get; private init; } = "";
    public TimeSpan RequestTimeout { get; private init; }
    public int MaxAgentSteps { get; private init; }
    public int MaxAnswerCharacters { get; private init; }
    public int MaxResponseTokens { get; private init; }
    public bool IncludeGameContext { get; private init; }
    public bool IsDeepSeekV4 => this.Model.Contains("deepseek-v4", StringComparison.OrdinalIgnoreCase);
    public bool IsConfigured => Uri.TryCreate(this.BaseUrl, UriKind.Absolute, out Uri? baseUri)
        && (baseUri.Scheme == Uri.UriSchemeHttp || baseUri.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrWhiteSpace(this.Model);

    public static AgentSettings From(ModConfig config, IMonitor monitor)
    {
        config.Validate(monitor);
        string Env(string name, string fallback) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

        string baseUrl = Env("OPENAI_BASE_URL", config.BaseUrl).TrimEnd('/');
        string model = Env("OPENAI_MODEL", config.Model);
        string key = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? config.ApiKey;
        string wiki = Environment.GetEnvironmentVariable("STARDEW_WIKI_API_URL") is { Length: > 0 } wikiEnv
            ? wikiEnv
            : config.WikiApiUrl;

        if (!Uri.TryCreate(wiki, UriKind.Absolute, out Uri? wikiUri)
            || wikiUri.Host.EndsWith("stardewvalleywiki.com", StringComparison.OrdinalIgnoreCase) == false)
        {
            monitor.Log("WikiApiUrl must point to stardewvalleywiki.com; using the official Chinese API.", LogLevel.Warn);
            wiki = "https://zh.stardewvalleywiki.com/mediawiki/api.php";
        }

        return new AgentSettings
        {
            BaseUrl = baseUrl,
            ApiKey = key,
            Model = model,
            WikiApiUrl = wiki,
            RequestTimeout = TimeSpan.FromSeconds(config.RequestTimeoutSeconds),
            MaxAgentSteps = config.MaxAgentSteps,
            MaxAnswerCharacters = config.MaxAnswerCharacters,
            MaxResponseTokens = config.MaxResponseTokens,
            IncludeGameContext = config.IncludeGameContext
        };
    }
}
