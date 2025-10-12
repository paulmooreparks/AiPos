using AiPos.Core;

namespace AiPos.Agentic;

/// <summary>
/// Placeholder agentic server abstraction. Later will host role-based tool exposure and security gateway.
/// </summary>
public interface IAgenticServer
{
	/// <summary>Process a raw user/agent message through orchestrator.</summary>
	Task<string> HandleAsync(string input, CancellationToken cancellationToken = default);
}

/// <summary>Minimal implementation wiring straight to IAiOrchestrator.</summary>
public sealed class AgenticServerHost : IAgenticServer
{
	private readonly IAiOrchestrator _orchestrator;
	/// <summary>Create host.</summary>
	public AgenticServerHost(IAiOrchestrator orchestrator) => _orchestrator = orchestrator;
	/// <summary>Forward to orchestrator.</summary>
	public Task<string> HandleAsync(string input, CancellationToken cancellationToken = default) => _orchestrator.ProcessCustomerInteractionAsync(input, cancellationToken);
}
