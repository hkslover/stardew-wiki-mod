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
            ["tools"] = tools,
            ["max_tokens"] = this.settings.MaxAnswerCharacters
        };
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
        return JsonDocument.Parse(body);
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
