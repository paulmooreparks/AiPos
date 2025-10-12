using PosKernel.Abstractions;

namespace PosKernel.Core.Interfaces;

/// <summary>
/// Manages operator sessions. Transactions are scoped to a session (terminal + operator) to enforce isolation
/// and audit boundaries.
/// </summary>
public interface ISessionManager
{
    /// <summary>Create a new session for a terminal/operator (returns session id).</summary>
    Task<string> CreateSessionAsync(string terminalId, string operatorId, CancellationToken cancellationToken = default);
    /// <summary>Validate that a session id exists (fail-fast if not).</summary>
    Task ValidateSessionAsync(string sessionId, CancellationToken cancellationToken = default);
    /// <summary>Close a session; subsequent operations must fail.</summary>
    Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default);
}
