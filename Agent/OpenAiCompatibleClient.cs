using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using StardewModdingAPI;
using StardewWikiAgent.Config;

namespace StardewWikiAgent.Agent;

internal sealed class OpenAiCompatibleClient
{
    private readonly AgentSettings settings;
    private readonly IMonitor monitor;
    private readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public OpenAiCompatibleClient(AgentSettings settings, IMonitor monitor)
    {
        this.settings = settings;
        this.monitor = monitor;
        this.http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("StardewWikiAgent", "0.1"));
    }

    public async Task<JsonDocument> CompleteAsync(
        IReadOnlyList<Dictionary<string, object?>> messages,
        IReadOnlyList<object> tools,
        string toolChoice,
        CancellationToken cancellationToken)
    {
        string endpoint = this.settings.BaseUrl.TrimEnd('/') + "/chat/completions";
        var payload = new Dictionary<string, object?>
        {
            ["model"] = this.settings.Model,
            ["messages"] = messages,
            // max_tokens caps output tokens only (not characters): DeepSeek counts
            // ~0.6 token per Chinese character, and thinking-mode reasoning_tokens
            // draw from the same budget, so this is a token count, not MaxAnswerCharacters.
            ["max_tokens"] = this.settings.MaxResponseTokens
        };
        // An empty tool list means the caller wants a tools-free completion (the
        // final forced-answer step). Omitting "tools" entirely is the only way to
        // stop a tool call that works across gateways — DeepSeek V4 ignores
        // tool_choice, so tool_choice=none would not prevent one there.
        if (tools.Count > 0)
            payload["tools"] = tools;
        if (this.settings.IsDeepSeekV4)
        {
            // DeepSeek V4 thinking mode defaults to high, but send both values
            // explicitly so the behavior is stable across compatible gateways.
            payload["thinking"] = new Dictionary<string, object?> { ["type"] = "enabled" };
            payload["reasoning_effort"] = "high";
            // DeepSeek V4 thinking-mode integrations may reject tool_choice;
            // omitting it leaves the API's tool selection at auto.
        }
        else
        {
            if (tools.Count > 0)
                payload["tool_choice"] = toolChoice;
            payload["temperature"] = 0.2;
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(this.settings.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", this.settings.ApiKey);

        using HttpResponseMessage response = await this.http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            this.monitor.Log($"LLM HTTP {(int)response.StatusCode} ({response.StatusCode}).", LogLevel.Warn);
            throw new LlmHttpException(response.StatusCode, body);
        }
        JsonDocument document = JsonDocument.Parse(body);
        LogUsage(document);
        return document;
    }

    /// <summary>Logs the token usage the API attaches to every response, for diagnostics/tuning.</summary>
    private void LogUsage(JsonDocument document)
    {
        if (!document.RootElement.TryGetProperty("usage", out JsonElement usage)
            || usage.ValueKind != JsonValueKind.Object)
            return;

        int Read(string name) => usage.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : -1;

        int prompt = Read("prompt_tokens");
        int completion = Read("completion_tokens");
        int total = Read("total_tokens");
        int reasoning = usage.TryGetProperty("completion_tokens_details", out JsonElement details)
            && details.ValueKind == JsonValueKind.Object
            && details.TryGetProperty("reasoning_tokens", out JsonElement r)
            && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : -1;

        this.monitor.Log(
            $"LLM usage: prompt={prompt} completion={completion} (reasoning={reasoning}) total={total}, max_tokens={this.settings.MaxResponseTokens}.",
            LogLevel.Debug);
    }
}

internal sealed class LlmHttpException : HttpRequestException
{
    public HttpStatusCode ResponseStatusCode { get; }

    public LlmHttpException(HttpStatusCode statusCode, string responseBody)
        : base($"LLM request failed with {(int)statusCode} ({statusCode}).")
    {
        this.ResponseStatusCode = statusCode;
    }
}
