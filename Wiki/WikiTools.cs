using System.Text.Json;
using StardewWikiAgent.Api;
using StardewWikiAgent.Game;

namespace StardewWikiAgent.Wiki;

internal sealed class WikiSearchTool : IAgentTool
{
    private readonly MediaWikiClient wiki;
    public WikiSearchTool(MediaWikiClient wiki) => this.wiki = wiki;
    public string Name => "wiki_search";
    public string Description => "Search the Chinese Stardew Valley Wiki (zh.stardewvalleywiki.com). Returns matching pages, section titles, snippets, and source URLs. Use this first to locate the right page before calling wiki_read.";
    public string ParametersSchemaJson => "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"description\":\"Search terms; the Chinese name works best (e.g. 防风草, 艾米丽).\"},\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":8,\"description\":\"Max number of results to return (default 5).\"}},\"required\":[\"query\"]}";
    public ToolExecutionAffinity ExecutionAffinity => ToolExecutionAffinity.BackgroundReadOnly;

    public Task<string> ExecuteAsync(string argumentsJson, GameContextSnapshot context, CancellationToken cancellationToken)
    {
        using JsonDocument args = JsonDocument.Parse(argumentsJson);
        string query = GetString(args.RootElement, "query");
        int limit = GetInt(args.RootElement, "limit", 5);
        return this.wiki.SearchAsync(query, limit, cancellationToken);
    }

    private static string GetString(JsonElement args, string name) =>
        args.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static int GetInt(JsonElement args, string name, int fallback) =>
        args.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result) ? result : fallback;
}

internal sealed class WikiReadTool : IAgentTool
{
    private readonly MediaWikiClient wiki;
    public WikiReadTool(MediaWikiClient wiki) => this.wiki = wiki;
    public string Name => "wiki_read";
    public string Description => "Read a page from the Chinese Stardew Valley Wiki, or one of its sections. Omit 'section' to get the page's section outline first, then call again with the chosen section to read its body.";
    public string ParametersSchemaJson => "{\"type\":\"object\",\"properties\":{\"page\":{\"type\":\"string\",\"description\":\"Exact page title as returned by wiki_search.\"},\"section\":{\"type\":\"string\",\"description\":\"Optional section title or index to read; omit to get the section outline. Use section=0 for the intro.\"},\"focus\":{\"type\":\"string\",\"description\":\"Optional keyword to focus the extracted text on the part relevant to the question.\"}},\"required\":[\"page\"]}";
    public ToolExecutionAffinity ExecutionAffinity => ToolExecutionAffinity.BackgroundReadOnly;

    public Task<string> ExecuteAsync(string argumentsJson, GameContextSnapshot context, CancellationToken cancellationToken)
    {
        using JsonDocument args = JsonDocument.Parse(argumentsJson);
        string page = GetString(args.RootElement, "page");
        string? section = GetOptionalString(args.RootElement, "section");
        string? focus = GetOptionalString(args.RootElement, "focus");
        return this.wiki.ReadAsync(page, section, focus, cancellationToken);
    }

    private static string GetString(JsonElement args, string name) =>
        args.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static string? GetOptionalString(JsonElement args, string name) =>
        args.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
