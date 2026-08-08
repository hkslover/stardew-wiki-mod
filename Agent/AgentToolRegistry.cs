using System.Collections.Concurrent;
using System.Text.Json;
using OpenAI.Chat;
using StardewModdingAPI;
using StardewWikiAgent.Api;
using StardewWikiAgent.Game;
using StardewWikiAgent.Threading;

namespace StardewWikiAgent.Agent;

internal sealed class AgentToolRegistry
{
    private readonly ConcurrentDictionary<string, IAgentTool> tools = new(StringComparer.Ordinal);
    private readonly IMonitor monitor;
    private readonly MainThreadDispatcher dispatcher;

    public AgentToolRegistry(IMonitor monitor, MainThreadDispatcher dispatcher)
    {
        this.monitor = monitor;
        this.dispatcher = dispatcher;
    }

    public bool Register(IAgentTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (string.IsNullOrWhiteSpace(tool.Name) || tool.Name.Any(c => !(char.IsLetterOrDigit(c) || c == '_')))
            throw new ArgumentException("Tool names may contain only letters, numbers, and underscores.", nameof(tool));
        try
        {
            using JsonDocument _ = JsonDocument.Parse(tool.ParametersSchemaJson);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Tool '{tool.Name}' has invalid JSON parameters schema.", nameof(tool), ex);
        }
        if (!this.tools.TryAdd(tool.Name, tool))
        {
            this.monitor.Log($"AI tool '{tool.Name}' is already registered.", LogLevel.Warn);
            return false;
        }
        if (tool.ExecutionAffinity == ToolExecutionAffinity.MainThreadMutating)
        {
            this.tools.TryRemove(tool.Name, out _);
            this.monitor.Log($"Rejected mutating AI tool '{tool.Name}'; register it as an IAgentAction instead.", LogLevel.Error);
            return false;
        }
        return true;
    }

    public bool Unregister(string name) => this.tools.TryRemove(name, out _);

    public IReadOnlyCollection<string> ToolNames => this.tools.Keys.OrderBy(name => name).ToArray();

    public IReadOnlyList<IAgentTool> Snapshot() => this.tools.Values.OrderBy(tool => tool.Name).ToArray();

    public bool TryGetAffinity(string name, out ToolExecutionAffinity affinity)
    {
        if (this.tools.TryGetValue(name, out IAgentTool? tool))
        {
            affinity = tool.ExecutionAffinity;
            return true;
        }

        affinity = ToolExecutionAffinity.MainThreadReadOnly;
        return false;
    }

    public async Task<string> ExecuteAsync(
        string name,
        string argumentsJson,
        GameContextSnapshot context,
        CancellationToken ct,
        string? requestId = null)
    {
        if (!this.tools.TryGetValue(name, out IAgentTool? tool))
        {
            return ToolResultEnvelope.Failure(
                "unknown_tool",
                $"Unknown tool: {name}",
                "Use only a tool name included in the current tools list."
            );
        }
        try
        {
            if (tool.ExecutionAffinity == ToolExecutionAffinity.MainThreadReadOnly)
            {
                Task<string> nested = await this.dispatcher.InvokeAsync(
                    () => tool.ExecuteAsync(argumentsJson, context, ct)
                );
                return await nested;
            }
            return await tool.ExecuteAsync(argumentsJson, context, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            string prefix = requestId is null ? "" : $"[{requestId}] ";
            this.monitor.Log($"{prefix}AI tool '{name}' failed: {ex.Message}", LogLevel.Warn);
            return ToolResultEnvelope.Failure(
                "tool_exception",
                $"Tool '{name}' failed with {ex.GetType().Name}: {ex.Message}",
                "Use the error context to retry with corrected arguments, choose another tool, or explain the limitation."
            );
        }
    }

    public IReadOnlyList<ChatTool> OpenAiDefinitions()
    {
        return this.Snapshot()
            .Select(tool => ChatTool.CreateFunctionTool(
                tool.Name,
                tool.Description,
                BinaryData.FromString(tool.ParametersSchemaJson)
            ))
            .ToArray();
    }
}
