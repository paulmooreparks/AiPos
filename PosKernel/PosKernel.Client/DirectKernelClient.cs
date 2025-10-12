using PosKernel.Core.Domain;
using PosKernel.Core.Interfaces;

namespace PosKernel.Client;

/// <summary>
/// Direct in-process kernel client used for initial POC. Wraps <see cref="IKernelEngine"/> without adding logic.
/// Provides a stable façade so future transports (NamedPipe, REST) can slot in without changing callers.
/// </summary>
public sealed class DirectKernelClient : IKernelClient
{
    private readonly IKernelEngine _engine;
    private readonly IEnumerable<IKernelClientContextEnricher> _enrichers;

    /// <summary>Create a direct client.</summary>
    public DirectKernelClient(IKernelEngine engine, IEnumerable<IKernelClientContextEnricher>? enrichers = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _enrichers = enrichers ?? Array.Empty<IKernelClientContextEnricher>();
    }

    /// <inheritdoc />
    public Task<string> CreateSessionAsync(string terminalId, string operatorId, CancellationToken cancellationToken = default)
        => _engine.CreateSessionAsync(terminalId, operatorId, cancellationToken);

    /// <inheritdoc />
    public Task<TransactionResult> StartTransactionAsync(string sessionId, string currency, CancellationToken cancellationToken = default)
        => _engine.StartTransactionAsync(sessionId, currency, cancellationToken);

    /// <inheritdoc />
    public Task<TransactionResult> AddLineItemAsync(string sessionId, string transactionId, string productId, int quantity, decimal unitPrice, string? productName = null, string? productDescription = null, CancellationToken cancellationToken = default)
        => _engine.AddLineItemAsync(sessionId, transactionId, productId, quantity, unitPrice, productName, productDescription, cancellationToken);

    /// <inheritdoc />
    public Task<TransactionResult> ProcessPaymentAsync(string sessionId, string transactionId, decimal amount, string paymentType = "cash", CancellationToken cancellationToken = default)
        => _engine.ProcessPaymentAsync(sessionId, transactionId, amount, paymentType, cancellationToken);

    /// <inheritdoc />
    public Task<TransactionResult> GetTransactionAsync(string sessionId, string transactionId, CancellationToken cancellationToken = default)
        => _engine.GetTransactionAsync(sessionId, transactionId, cancellationToken);

    /// <inheritdoc />
    public Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        => _engine.CloseSessionAsync(sessionId, cancellationToken);

    // ARCHITECTURAL PLACEHOLDER: Future remote transports will use metadata enrichment.
    private Dictionary<string, string> BuildMetadata()
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var enricher in _enrichers)
        {
            enricher.Enrich(meta);
        }
        return meta;
    }
}
