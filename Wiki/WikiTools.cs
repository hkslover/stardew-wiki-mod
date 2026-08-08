using System.Text.Json;
using StardewWikiAgent.Api;
using StardewWikiAgent.Game;

namespace StardewWikiAgent.Wiki;

internal sealed class WikiSearchTool : IAgentTool
{
    private readonly MediaWikiClient wiki;
    public WikiSearchTool(MediaWikiClient wiki) => this.wiki = wiki;
    public string Name => "wiki_search";
    public string Description => "搜索中文 Stardew Valley Wiki，返回相关页面、章节、摘要和来源链接。";
    public string ParametersSchemaJson => "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"},\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":8}},\"required\":[\"query\"]}";
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
    public string Description => "读取中文 Stardew Valley Wiki 页面或指定章节；省略 section 时返回章节目录。";
    public string ParametersSchemaJson => "{\"type\":\"object\",\"properties\":{\"page\":{\"type\":\"string\"},\"section\":{\"type\":\"string\"},\"focus\":{\"type\":\"string\"}},\"required\":[\"page\"]}";
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
