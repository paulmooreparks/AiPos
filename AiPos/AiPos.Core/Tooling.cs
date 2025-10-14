namespace AiPos.Core;

// Moved from Class1.cs: consolidated tool-related abstractions.

/// <summary>
/// Describes a tool the orchestrator can invoke in a single-call execution path.
/// ARCHITECTURAL PRINCIPLE: Declarative catalog – no dynamic discovery magic that could hide missing registrations.
/// </summary>
public sealed record ToolDefinition(
	string Name,
	string Category,
	string Description,
	IReadOnlyList<ToolParameter> Parameters
);

/// <summary>Parameter metadata for tool invocation.</summary>
public sealed record ToolParameter(string Name, string Type, bool Required, string Description);

/// <summary>Executes registered tools by name with raw parameter bag.</summary>
public interface IToolExecutor
{
	/// <summary>Execute a tool by name. Fail fast if unknown or parameter mismatch.</summary>
	Task<string> ExecuteToolAsync(string toolName, IReadOnlyDictionary<string, object> parameters, CancellationToken cancellationToken = default);
	/// <summary>Return immutable list of available tool definitions.</summary>
	IReadOnlyList<ToolDefinition> GetAvailableTools();
}

/// <summary>
/// Single-call AI orchestrator: takes customer input, selects a tool (or none) and returns a response.
/// </summary>
public interface IAiOrchestrator
{
	/// <summary>Process a customer utterance producing a response (and performing side effects via tools).</summary>
	Task<string> ProcessCustomerInteractionAsync(string customerInput, CancellationToken cancellationToken = default);
}
