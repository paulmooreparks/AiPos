using System.Collections.Concurrent;

namespace PosKernel.Core.Services;

/// <summary>
/// In-memory implementation of session management (POC / non-persistent). Provides minimal validation and
/// fail-fast semantics for unknown or closed sessions.
/// </summary>
public sealed class SessionManager : Interfaces.ISessionManager
{
    private readonly ConcurrentDictionary<string, (string Terminal, string Operator, DateTime CreatedUtc, bool Closed)> _sessions = new();

    /// <inheritdoc />
    public Task<string> CreateSessionAsync(string terminalId, string operatorId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(terminalId)) { throw new InvalidOperationException("Terminal ID required."); }
        if (string.IsNullOrWhiteSpace(operatorId)) { throw new InvalidOperationException("Operator ID required."); }
        var id = Guid.NewGuid().ToString("N");
        _sessions[id] = (terminalId, operatorId, DateTime.UtcNow, false);
        return Task.FromResult(id);
    }

    /// <inheritdoc />
    public Task ValidateSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var record) || record.Closed)
        {
            throw new InvalidOperationException("Session not found or closed.");
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (_sessions.TryGetValue(sessionId, out var record))
        {
            _sessions[sessionId] = (record.Terminal, record.Operator, record.CreatedUtc, true);
        }
        return Task.CompletedTask;
    }
}
