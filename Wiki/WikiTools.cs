using System.Text.Json;
using StardewWikiAgent.Agent;
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

    public async Task<string> ExecuteAsync(string argumentsJson, GameContextSnapshot context, CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument args = JsonDocument.Parse(argumentsJson);
            string query = GetString(args.RootElement, "query").Trim();
            if (query.Length == 0)
            {
                return ToolResultEnvelope.Failure(
                    "empty_query",
                    "The Wiki search query is empty.",
                    "Call wiki_search again with a concise Chinese game term."
                );
            }

            int limit = GetInt(args.RootElement, "limit", 5);
            string result = await this.wiki.SearchAsync(query, limit, cancellationToken);
            return WikiToolResult.Wrap(result, isSearch: true);
        }
        catch (JsonException)
        {
            return ToolResultEnvelope.Failure(
                "invalid_arguments",
                "The tool arguments are not valid JSON.",
                "Call wiki_search again with a JSON object containing a string query."
            );
        }
        catch (HttpRequestException ex)
        {
            return ToolResultEnvelope.Failure(
                "http_error",
                $"The Chinese Stardew Valley Wiki request failed: {ex.Message}",
                "The Wiki may be temporarily unavailable. Explain that the fact could not be verified."
            );
        }
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

    public async Task<string> ExecuteAsync(string argumentsJson, GameContextSnapshot context, CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument args = JsonDocument.Parse(argumentsJson);
            string page = GetString(args.RootElement, "page").Trim();
            if (page.Length == 0)
            {
                return ToolResultEnvelope.Failure(
                    "empty_query",
                    "The Wiki page title is empty.",
                    "Call wiki_search first, then call wiki_read with an exact returned page title."
                );
            }

            string? section = GetOptionalString(args.RootElement, "section");
            string? focus = GetOptionalString(args.RootElement, "focus");
            string result = await this.wiki.ReadAsync(page, section, focus, cancellationToken);
            return WikiToolResult.Wrap(result, isSearch: false);
        }
        catch (JsonException)
        {
            return ToolResultEnvelope.Failure(
                "invalid_arguments",
                "The tool arguments are not valid JSON.",
                "Call wiki_read again with a JSON object containing a string page title."
            );
        }
        catch (HttpRequestException ex)
        {
            return ToolResultEnvelope.Failure(
                "http_error",
                $"The Chinese Stardew Valley Wiki request failed: {ex.Message}",
                "The Wiki may be temporarily unavailable. Explain that the fact could not be verified."
            );
        }
    }

    private static string GetString(JsonElement args, string name) =>
        args.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static string? GetOptionalString(JsonElement args, string name) =>
        args.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

internal static class WikiToolResult
{
    public static string Wrap(string json, bool isSearch)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.TryGetProperty("error", out JsonElement error))
        {
            string message = error.ValueKind == JsonValueKind.String
                ? error.GetString() ?? "The Wiki returned an error."
                : "The Wiki returned an error.";
            return ToolResultEnvelope.Failure(
                "not_found",
                message,
                isSearch
                    ? "Try a shorter or alternative Chinese keyword."
                    : "Call wiki_search for the subject, then retry wiki_read with an exact returned page title."
            );
        }

        if (isSearch
            && root.TryGetProperty("results", out JsonElement results)
            && results.ValueKind == JsonValueKind.Array
            && results.GetArrayLength() == 0)
        {
            return ToolResultEnvelope.Failure(
                "not_found",
                "The Wiki search returned no matching pages.",
                "Try a shorter or alternative Chinese keyword.",
                root.Clone()
            );
        }

        return ToolResultEnvelope.Success(root.Clone());
    }
}
