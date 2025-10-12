using System.Collections.Concurrent;
using PosKernel.Core.Domain;
using PosKernel.Core.Interfaces;

namespace PosKernel.Core.Services;

/// <summary>
/// In-memory kernel engine implementing high-level transaction operations for the POC phase.
/// ARCHITECTURAL PRINCIPLE: All monetary aggregates come through here; AI/client layers never compute totals.
/// NOT PRODUCTION PERSISTENCE: No durability or concurrency safeguards beyond basic dictionary usage.
/// </summary>
public sealed class KernelEngine : IKernelEngine
{
    private readonly ISessionManager _sessions;
    private readonly ITransactionService _transactions;
    private readonly ConcurrentDictionary<string, Transaction> _transactionsById = new();

    /// <summary>Create a new kernel engine with required session + transaction services.</summary>
    public KernelEngine(ISessionManager sessions, ITransactionService transactions)
    {
        _sessions = sessions;
        _transactions = transactions;
    }

    /// <inheritdoc />
    public Task<string> CreateSessionAsync(string terminalId, string operatorId, CancellationToken cancellationToken = default)
        => _sessions.CreateSessionAsync(terminalId, operatorId, cancellationToken);

    /// <inheritdoc />
    public async Task<TransactionResult> StartTransactionAsync(string sessionId, string currency, CancellationToken cancellationToken = default)
    {
        await _sessions.ValidateSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var tx = _transactions.Begin(currency);
        _transactionsById[tx.Id.ToString()] = tx;
        return TransactionResult.Ok(tx);
    }

    /// <inheritdoc />
    public async Task<TransactionResult> AddLineItemAsync(string sessionId, string transactionId, string productId, int quantity, decimal unitPrice, string? productName = null, string? productDescription = null, CancellationToken cancellationToken = default)
    {
        await _sessions.ValidateSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var tx = GetExisting(transactionId);
        if (tx.State is TransactionState.EndOfTransaction or TransactionState.Voided)
        {
            return TransactionResult.Fail($"Cannot add items in state {tx.State}.");
        }
        var moneyUnit = new Money(unitPrice, tx.Currency);
        tx = _transactions.AddLine(tx, new ProductId(productId), quantity, moneyUnit, moneyUnit.Multiply(quantity));
        var line = tx.Lines.Last();
        if (!string.IsNullOrWhiteSpace(productName)) { line.ProductName = productName; }
        if (!string.IsNullOrWhiteSpace(productDescription)) { line.ProductDescription = productDescription; }
        // Transition from StartTransaction to ItemsPending automatically
        if (tx.State == TransactionState.StartTransaction)
        {
            tx.State = TransactionState.ItemsPending;
        }
        return TransactionResult.Ok(tx);
    }

    /// <inheritdoc />
    public async Task<TransactionResult> ProcessPaymentAsync(string sessionId, string transactionId, decimal amount, string paymentType = "cash", CancellationToken cancellationToken = default)
    {
        await _sessions.ValidateSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var tx = GetExisting(transactionId);
        if (tx.State != TransactionState.ItemsPending && tx.State != TransactionState.StartTransaction)
        {
            return TransactionResult.Fail($"Cannot process payment when state is {tx.State} – must have items pending.");
        }
        if (tx.Lines.Count == 0)
        {
            return TransactionResult.Fail("Cannot process payment with zero line items.");
        }
        var tendered = new Money(amount, tx.Currency);
        var change = tendered.Subtract(tx.Total);
        tx = _transactions.UpdateFromKernel(tx, tx.Total, tendered, change, TransactionState.EndOfTransaction);
        return TransactionResult.Ok(tx);
    }

    /// <inheritdoc />
    public async Task<TransactionResult> GetTransactionAsync(string sessionId, string transactionId, CancellationToken cancellationToken = default)
    {
        await _sessions.ValidateSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var tx = GetExisting(transactionId);
        return TransactionResult.Ok(tx);
    }

    /// <inheritdoc />
    public Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        => _sessions.CloseSessionAsync(sessionId, cancellationToken);

    private Transaction GetExisting(string transactionId)
    {
        if (!_transactionsById.TryGetValue(transactionId, out var tx))
        {
            throw new InvalidOperationException("Transaction not found.");
        }
        return tx;
    }
}
