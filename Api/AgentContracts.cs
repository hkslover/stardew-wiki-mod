using StardewWikiAgent.Game;

namespace StardewWikiAgent.Api;

/// <summary>How an extension tool is allowed to run.</summary>
public enum ToolExecutionAffinity
{
    BackgroundReadOnly,
    MainThreadReadOnly,
    MainThreadMutating
}

/// <summary>Public extension point for additional read-only AI tools.</summary>
public interface IAgentTool
{
    string Name { get; }
    string Description { get; }
    string ParametersSchemaJson { get; }
    ToolExecutionAffinity ExecutionAffinity { get; }
    Task<string> ExecuteAsync(string argumentsJson, GameContextSnapshot context, CancellationToken cancellationToken);
}

/// <summary>Reserved extension point for future game-changing actions.</summary>
/// <remarks>Actions must be proposed, confirmed by the player, and executed on the game thread.</remarks>
public interface IAgentAction
{
    string Name { get; }
    string Description { get; }
    string ParametersSchemaJson { get; }
    bool RequiresConfirmation { get; }
    bool HostOnly { get; }
    Task<string> ProposeAsync(string argumentsJson, GameContextSnapshot context, CancellationToken cancellationToken);
    Task ExecuteAsync(string argumentsJson, GameContextSnapshot context, CancellationToken cancellationToken);
}

/// <summary>Public API returned by the mod for future integrations.</summary>
public interface IStardewWikiAgentApi
{
    bool RegisterTool(IAgentTool tool);
    bool UnregisterTool(string name);
    bool RegisterAction(IAgentAction action);
    bool UnregisterAction(string name);
    IReadOnlyCollection<string> ToolNames { get; }
    IReadOnlyCollection<string> ActionNames { get; }
    Task<AgentAnswer> AskAsync(string question, CancellationToken cancellationToken = default);
}

/// <summary>Stable answer DTO for other mods.</summary>
public sealed class AgentAnswer
{
    public string Text { get; init; } = "";
    public IReadOnlyList<string> Sources { get; init; } = Array.Empty<string>();
    internal NavigationTarget? NavigationTarget { get; init; }
}
