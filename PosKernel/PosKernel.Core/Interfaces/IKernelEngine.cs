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
        /// <summary>
        /// Add a line item with pre-validated product and authoritative unit price (raw decimal, no formatting).
        /// When <paramref name="parentLineItemId"/> is supplied the new line is treated as a LINKED (NRF/ARTS) item –
        /// a child modification/supplement to the parent merchandise line. Kernel performs ONLY structural validation; ALL
        /// pricing for modifications (including surcharge/discount math) must already be reflected in <paramref name="unitPrice"/>.
        /// ARCHITECTURAL PRINCIPLE: Kernel aggregates treat linked items identically to root item lines; linkage is strictly
        /// structural for hierarchy/receipt rendering and upstream AI guidance (no alternate math path).
        /// </summary>
        /// <param name="sessionId">Operator session identifier (validated for existence and openness).</param>
        /// <param name="transactionId">Target transaction identifier.</param>
        /// <param name="productId">Store/extension supplied product identifier (culture-neutral token).</param>
        /// <param name="quantity">Ordered quantity (must be positive integer).</param>
        /// <param name="unitPrice">Authoritative unit price (already validated; raw decimal without formatting).</param>
        /// <param name="productName">Optional resolved product display name.</param>
        /// <param name="productDescription">Optional resolved product description.</param>
        /// <param name="parentLineItemId">Optional stable identifier of parent line (for modifier / linked items). Null for root merchandise lines.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        Task<TransactionResult> AddLineItemAsync(string sessionId, string transactionId, string productId, int quantity, decimal unitPrice, string? productName = null, string? productDescription = null, string? parentLineItemId = null, CancellationToken cancellationToken = default);
    /// <summary>Process payment and transition to EndOfTransaction if valid.</summary>
    Task<TransactionResult> ProcessPaymentAsync(string sessionId, string transactionId, decimal amount, string paymentType, CancellationToken cancellationToken = default);
    /// <summary>Retrieve current transaction snapshot.</summary>
    Task<TransactionResult> GetTransactionAsync(string sessionId, string transactionId, CancellationToken cancellationToken = default);
    /// <summary>
    /// Void a merchandise (Item) line by its stable line item id. Implements AUTO-CASCADE policy: all descendant linked
    /// (modifier) lines are voided atomically. Financial totals are recalculated centrally and integrity re-asserted.
    /// Fails fast if transaction is already closed/voided, if line does not exist, or if line already voided.
    /// </summary>
    /// <param name="sessionId">Operator session id.</param>
    /// <param name="transactionId">Transaction identifier.</param>
    /// <param name="lineItemId">Stable line item identifier to void.</param>
    /// <param name="reason">Optional human-readable void reason.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<TransactionResult> VoidLineItemAsync(string sessionId, string transactionId, string lineItemId, string? reason = null, CancellationToken cancellationToken = default);
    /// <summary>Close an operator session so no further work can be performed.</summary>
    Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default);
}
