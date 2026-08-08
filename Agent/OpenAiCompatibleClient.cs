using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using StardewModdingAPI;
using StardewWikiAgent.Config;

namespace StardewWikiAgent.Agent;

/// <summary>
/// Chat Completions adapter that speaks the OpenAI-compatible HTTP protocol directly.
/// Uses only BCL types (<c>HttpClient</c> + <c>System.Text.Json</c>) so the mod never
/// needs to ship framework or SDK assemblies alongside Stardew/SMAPI's .NET 6 process.
/// </summary>
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
        CancellationToken cancellationToken,
        string? requestId = null)
    {
        string endpoint = this.settings.BaseUrl.TrimEnd('/') + "/chat/completions";
        var payload = new Dictionary<string, object?>
        {
            ["model"] = this.settings.Model,
            ["messages"] = messages
        };
        // An empty tool list means the caller wants a tools-free completion (the
        // final forced-answer step). Omitting "tools" entirely keeps that intent
        // unambiguous across OpenAI-compatible gateways.
        if (tools.Count > 0)
        {
            payload["tools"] = tools;
            payload["tool_choice"] = toolChoice;
        }
        if (this.settings.IsDeepSeekV4)
        {
            // DeepSeek V4's OpenAI-compatible API caps output with max_tokens (the
            // separate CoT/reasoning tokens are counted independently) and adds
            // thinking-mode controls that standard OpenAI requests must not carry.
            // No temperature is sent in thinking mode.
            payload["max_tokens"] = this.settings.MaxResponseTokens;
            payload["thinking"] = new Dictionary<string, object?> { ["type"] = "enabled" };
            payload["reasoning_effort"] = this.settings.ReasoningEffort;
        }
        else
        {
            // Standard OpenAI-compatible endpoints: prefer max_completion_tokens
            // (the SDK-migration semantics) and a stable low temperature.
            payload["max_completion_tokens"] = this.settings.MaxResponseTokens;
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
            string prefix = requestId is null ? "" : $"[{requestId}] ";
            string reason = response.ReasonPhrase ?? response.StatusCode.ToString();
            this.monitor.Log($"{prefix}LLM HTTP {(int)response.StatusCode} ({reason}).", LogLevel.Warn);
            // Do not place the response body in the exception: compatible gateways
            // are free to echo prompts, credentials, or other sensitive data.
            throw new LlmHttpException(response.StatusCode, reason);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            string prefix = requestId is null ? "" : $"[{requestId}] ";
            this.monitor.Log($"{prefix}LLM returned invalid JSON.", LogLevel.Warn);
            throw new LlmProtocolException("The LLM endpoint returned invalid JSON.", ex);
        }

        // Validate the protocol envelope up front so callers get a contextual error
        // instead of a bare KeyNotFoundException/IndexOutOfRangeException later.
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("choices", out JsonElement choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            document.Dispose();
            string prefix = requestId is null ? "" : $"[{requestId}] ";
            this.monitor.Log($"{prefix}LLM response is missing a non-empty 'choices' array.", LogLevel.Warn);
            throw new LlmProtocolException("The LLM response is missing a non-empty 'choices' array.");
        }
        if (choices[0].ValueKind != JsonValueKind.Object
            || !choices[0].TryGetProperty("message", out JsonElement message)
            || message.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            string prefix = requestId is null ? "" : $"[{requestId}] ";
            this.monitor.Log($"{prefix}LLM response is missing a valid 'choices[0].message' object.", LogLevel.Warn);
            throw new LlmProtocolException("The LLM response is missing a valid 'choices[0].message' object.");
        }

        LogUsage(document, requestId);
        return document;
    }

    /// <summary>Logs the token usage the API attaches to every response, for diagnostics/tuning.</summary>
    private void LogUsage(JsonDocument document, string? requestId)
    {
        if (!document.RootElement.TryGetProperty("usage", out JsonElement usage)
            || usage.ValueKind != JsonValueKind.Object)
            return;

        int Read(string name) => usage.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int result) ? result : -1;

        int prompt = Read("prompt_tokens");
        int completion = Read("completion_tokens");
        int total = Read("total_tokens");
        int reasoning = usage.TryGetProperty("completion_tokens_details", out JsonElement details)
            && details.ValueKind == JsonValueKind.Object
            && details.TryGetProperty("reasoning_tokens", out JsonElement r)
            && r.ValueKind == JsonValueKind.Number
            && r.TryGetInt32(out int result) ? result : -1;

        string prefix = requestId is null ? "" : $"[{requestId}] ";
        this.monitor.Log(
            $"{prefix}LLM usage: prompt={prompt} completion={completion} (reasoning={reasoning}) total={total}, max_tokens={this.settings.MaxResponseTokens}.",
            LogLevel.Debug);
    }

}

/// <summary>Thrown when the LLM endpoint answers with a non-2xx HTTP status.</summary>
internal sealed class LlmHttpException : HttpRequestException
{
    public HttpStatusCode ResponseStatusCode { get; }

    public LlmHttpException(HttpStatusCode statusCode, string reasonPhrase, Exception? innerException = null)
        : base($"LLM request failed with {(int)statusCode} ({reasonPhrase}).", innerException)
    {
        this.ResponseStatusCode = statusCode;
    }
}

/// <summary>Thrown when the LLM endpoint returns a response that violates the expected protocol shape.</summary>
internal sealed class LlmProtocolException : Exception
{
    public LlmProtocolException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
