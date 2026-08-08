using System.Net;
using OpenAI;
using OpenAI.Chat;
using StardewModdingAPI;
using StardewWikiAgent.Config;
using System.ClientModel;
using System.ClientModel.Primitives;

#pragma warning disable OPENAI001, SCME0001 // Intentional SDK extension points for optional auth and provider fields.

namespace StardewWikiAgent.Agent;

/// <summary>
/// Chat Completions adapter backed by the official OpenAI .NET SDK. Provider-specific
/// fields are applied through the SDK's JSON patch support instead of a handwritten
/// request serializer.
/// </summary>
internal sealed class OpenAiCompatibleClient
{
    private readonly AgentSettings settings;
    private readonly IMonitor monitor;
    private readonly ChatClient? client;

    public OpenAiCompatibleClient(AgentSettings settings, IMonitor monitor)
    {
        this.settings = settings;
        this.monitor = monitor;
        if (!settings.IsConfigured)
            return;

        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(settings.BaseUrl.TrimEnd('/')),
            NetworkTimeout = Timeout.InfiniteTimeSpan
        };
        AuthenticationPolicy authentication = string.IsNullOrWhiteSpace(settings.ApiKey)
            ? NoAuthenticationPolicy.Instance
            : ApiKeyAuthenticationPolicy.CreateBearerAuthorizationPolicy(new ApiKeyCredential(settings.ApiKey));
        this.client = new ChatClient(settings.Model, authentication, options);
    }

    public async Task<ChatCompletion> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatTool> tools,
        string toolChoice,
        CancellationToken cancellationToken,
        string? requestId = null)
    {
        var options = new ChatCompletionOptions();
        foreach (ChatTool tool in tools)
            options.Tools.Add(tool);

        if (tools.Count > 0)
        {
            options.ToolChoice = string.Equals(toolChoice, "required", StringComparison.OrdinalIgnoreCase)
                ? ChatToolChoice.CreateRequiredChoice()
                : ChatToolChoice.CreateAutoChoice();
        }

        if (this.settings.IsDeepSeekV4)
        {
            // DeepSeek's OpenAI-compatible API uses max_tokens and adds thinking
            // controls that aren't part of the standard ChatCompletionOptions model.
            options.Patch.Set("$.max_tokens"u8, this.settings.MaxResponseTokens);
            options.Patch.Set("$.thinking"u8, BinaryData.FromString("""{"type":"enabled"}"""));
            options.Patch.Set("$.reasoning_effort"u8, "high");
        }
        else
        {
            options.MaxOutputTokenCount = this.settings.MaxResponseTokens;
            options.Temperature = 0.2f;
        }

        try
        {
            ChatClient configuredClient = this.client
                ?? throw new InvalidOperationException("The LLM client is not configured.");
            ChatCompletion completion = await configuredClient.CompleteChatAsync(messages, options, cancellationToken);
            this.LogUsage(completion, requestId);
            return completion;
        }
        catch (ClientResultException ex)
        {
            string prefix = requestId is null ? "" : $"[{requestId}] ";
            HttpStatusCode statusCode = (HttpStatusCode)ex.Status;
            this.monitor.Log($"{prefix}LLM HTTP {ex.Status} ({statusCode}).", LogLevel.Warn);
            throw new LlmHttpException(statusCode, ex.Message, ex);
        }
    }

    /// <summary>Logs token usage reported by the provider for diagnostics and tuning.</summary>
    private void LogUsage(ChatCompletion completion, string? requestId)
    {
        ChatTokenUsage? usage = completion.Usage;
        if (usage is null)
            return;

        int reasoning = usage.OutputTokenDetails?.ReasoningTokenCount ?? -1;
        string prefix = requestId is null ? "" : $"[{requestId}] ";
        this.monitor.Log(
            $"{prefix}LLM usage: prompt={usage.InputTokenCount} completion={usage.OutputTokenCount} " +
            $"(reasoning={reasoning}) total={usage.TotalTokenCount}, max_tokens={this.settings.MaxResponseTokens}.",
            LogLevel.Debug
        );
    }

    /// <summary>Preserves support for local OpenAI-compatible endpoints that don't require a key.</summary>
    private sealed class NoAuthenticationPolicy : AuthenticationPolicy
    {
        public static NoAuthenticationPolicy Instance { get; } = new();

        public override void Process(
            PipelineMessage message,
            IReadOnlyList<PipelinePolicy> pipeline,
            int currentIndex)
        {
            ProcessNext(message, pipeline, currentIndex);
        }

        public override ValueTask ProcessAsync(
            PipelineMessage message,
            IReadOnlyList<PipelinePolicy> pipeline,
            int currentIndex)
        {
            return ProcessNextAsync(message, pipeline, currentIndex);
        }
    }
}

internal sealed class LlmHttpException : HttpRequestException
{
    public HttpStatusCode ResponseStatusCode { get; }

    public LlmHttpException(HttpStatusCode statusCode, string responseBody, Exception? innerException = null)
        : base($"LLM request failed with {(int)statusCode} ({statusCode}). {responseBody}", innerException)
    {
        this.ResponseStatusCode = statusCode;
    }
}

#pragma warning restore OPENAI001, SCME0001
