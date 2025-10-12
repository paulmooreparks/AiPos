using PosKernel.Core.Domain;

namespace PosKernel.Core.Interfaces;

/// <summary>
/// High level kernel engine interface consumed by client layer and AI tools.
/// ARCHITECTURAL PRINCIPLE: Provides authoritative operations; monetary calculations occur inside implementation,
/// not in callers. All parameters are culture-neutral.
/// </summary>
public interface IKernelEngine
{
    /// <summary>Create a new operator session for a terminal.</summary>
    Task<string> CreateSessionAsync(string terminalId, string operatorId, CancellationToken cancellationToken = default);
    /// <summary>Start a new transaction (currency validated externally by configuration/extension layers).</summary>
    Task<TransactionResult> StartTransactionAsync(string sessionId, string currency, CancellationToken cancellationToken = default);
    /// <summary>Add a line item with pre-validated product and authoritative unit price (raw decimal, no formatting).</summary>
    Task<TransactionResult> AddLineItemAsync(string sessionId, string transactionId, string productId, int quantity, decimal unitPrice, string? productName = null, string? productDescription = null, CancellationToken cancellationToken = default);
    /// <summary>Process payment and transition to EndOfTransaction if valid.</summary>
    Task<TransactionResult> ProcessPaymentAsync(string sessionId, string transactionId, decimal amount, string paymentType = "cash", CancellationToken cancellationToken = default);
    /// <summary>Retrieve current transaction snapshot.</summary>
    Task<TransactionResult> GetTransactionAsync(string sessionId, string transactionId, CancellationToken cancellationToken = default);
    /// <summary>Close an operator session so no further work can be performed.</summary>
    Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default);
}
