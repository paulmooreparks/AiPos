using PosKernel.Core.Domain;
using PosKernel.Core.Interfaces;

namespace PosKernel.Client;

/// <summary>
/// Client abstraction for accessing kernel operations over various transports (Direct, NamedPipe, REST).
/// ARCHITECTURAL PRINCIPLE: Client layer contains ONLY transport/orchestration logic – no business rules, no formatting.
/// </summary>
public interface IKernelClient
{
    /// <summary>Create an operator session.</summary>
    Task<string> CreateSessionAsync(string terminalId, string operatorId, CancellationToken cancellationToken = default);
    /// <summary>Start a transaction.</summary>
    Task<TransactionResult> StartTransactionAsync(string sessionId, string currency, CancellationToken cancellationToken = default);
    /// <summary>Add an item.</summary>
    Task<TransactionResult> AddLineItemAsync(string sessionId, string transactionId, string productId, int quantity, decimal unitPrice, string? productName = null, string? productDescription = null, CancellationToken cancellationToken = default);
    /// <summary>Process payment.</summary>
    Task<TransactionResult> ProcessPaymentAsync(string sessionId, string transactionId, decimal amount, string paymentType = "cash", CancellationToken cancellationToken = default);
    /// <summary>Get transaction snapshot.</summary>
    Task<TransactionResult> GetTransactionAsync(string sessionId, string transactionId, CancellationToken cancellationToken = default);
    /// <summary>Close session.</summary>
    Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Extensibility hook for adding contextual headers / metadata when using remote transports.
/// Direct client ignores it but we keep the contract to avoid painting future transports into a corner.
/// </summary>
public interface IKernelClientContextEnricher
{
    /// <summary>Populate outbound metadata dictionary (e.g., correlation ids, auth tokens).</summary>
    void Enrich(IDictionary<string, string> metadata);
}
