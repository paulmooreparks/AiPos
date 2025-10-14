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
    private readonly IPaymentRules _paymentRules;
    private readonly ConcurrentDictionary<string, Transaction> _transactionsById = new();

    /// <summary>Create a new kernel engine with required session + transaction services.</summary>
    public KernelEngine(ISessionManager sessions, ITransactionService transactions, IPaymentRules paymentRules)
    {
        _sessions = sessions;
        _transactions = transactions;
        _paymentRules = paymentRules ?? throw new InvalidOperationException("Payment rules service not provided. Register IPaymentRules in DI / construction.");
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
    public async Task<TransactionResult> AddLineItemAsync(string sessionId, string transactionId, string productId, int quantity, decimal unitPrice, string? productName = null, string? productDescription = null, string? parentLineItemId = null, CancellationToken cancellationToken = default)
    {
        await _sessions.ValidateSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var tx = GetExisting(transactionId);
        if (tx.State is TransactionState.EndOfTransaction or TransactionState.Voided)
        {
            return TransactionResult.Fail($"Cannot add items in state {tx.State}.");
        }
        // Validate quantity & unit price (kernel-level enforcement; clients MUST NOT supply invalid values)
        if (quantity <= 0)
        {
            return TransactionResult.Fail("Quantity must be positive.");
        }
        if (unitPrice < 0m)
        {
            return TransactionResult.Fail("Unit price cannot be negative.");
        }
        var moneyUnit = new Money(unitPrice, tx.Currency);
        tx = _transactions.AddLine(tx, new ProductId(productId), quantity, moneyUnit, moneyUnit.Multiply(quantity));
        var line = tx.Lines.Last();
        // Assign stable line item id if not already (NRF linked item model expects stable identifiers for hierarchy)
        if (string.IsNullOrWhiteSpace(line.LineItemId))
        {
            line.LineItemId = LineItemId.New().ToString();
        }
        // Linkage handling for modifier/child lines
        if (!string.IsNullOrWhiteSpace(parentLineItemId))
        {
            // Validate parent existence
            var parent = tx.Lines.FirstOrDefault(l => l.LineItemId == parentLineItemId);
            if (parent == null)
            {
                return TransactionResult.Fail($"Parent line item '{parentLineItemId}' not found for linkage.");
            }
            line.ParentLineItemId = parent.LineItemId;
            line.ParentLineNumber = parent.LineNumber; // parent dynamic line number (may shift after voids)
            line.DisplayIndentLevel = parent.DisplayIndentLevel + 1;
        }
        else
        {
            line.DisplayIndentLevel = 0;
        }
        if (!string.IsNullOrWhiteSpace(productName)) { line.ProductName = productName; }
        if (!string.IsNullOrWhiteSpace(productDescription)) { line.ProductDescription = productDescription; }
        // Transition from StartTransaction to ItemsPending automatically
        if (tx.State == TransactionState.StartTransaction)
        {
            tx.State = TransactionState.ItemsPending;
        }
        // ARCHITECTURAL FIX: Recalculate totals centrally after every mutation (NO client math allowed)
        RecalculateTotals(tx);
        AssertIntegrity(tx);
        return TransactionResult.Ok(tx);
    }

    /// <inheritdoc />
    public async Task<TransactionResult> ProcessPaymentAsync(string sessionId, string transactionId, decimal amount, string paymentType, CancellationToken cancellationToken = default)
    {
        await _sessions.ValidateSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var tx = GetExisting(transactionId);
        if (tx.State is TransactionState.EndOfTransaction or TransactionState.Voided)
        {
            return TransactionResult.Fail($"Cannot process payment when state is {tx.State}.");
        }
        if (tx.Lines.Count == 0)
        {
            return TransactionResult.Fail("Cannot process payment with zero line items.");
        }
        if (amount < 0m)
        {
            return TransactionResult.Fail("Payment amount cannot be negative.");
        }
        if (string.IsNullOrWhiteSpace(paymentType))
        {
            return TransactionResult.Fail("Payment type required. Kernel does not supply defaults – obtain valid tender methods from store extension.");
        }
        // Normalize & validate via payment rules abstraction
        var normalizedTender = _paymentRules.NormalizeTenderType(paymentType);
        if (normalizedTender == null)
        {
            return TransactionResult.Fail("Invalid payment type per payment rules service.");
        }
        // Recompute merchandise total first (ignoring tenders/change)
        RecalculateTotals(tx);
        AssertIntegrity(tx);

        // Append tender line (quantity fixed at 1 for payments)
        var tenderMoney = new Money(-amount, tx.Currency); // NRF-style: tender recorded as negative (cash received)
        var tenderLine = new TransactionLine(tx.Currency)
        {
            LineType = TransactionLineType.Tender,
            TenderType = normalizedTender,
            Quantity = 1,
            UnitPrice = tenderMoney,
            Extended = tenderMoney,
            ProductName = $"TENDER:{normalizedTender}",
            ProductDescription = "Payment tender"
        };
        tenderLine.LineNumber = tx.Lines.Count + 1;
        tenderLine.LineItemId = LineItemId.New().ToString();
        tx.Lines.Add(tenderLine);

        // Derive aggregates again including tender
        RecalculateTotals(tx);

        // Determine if fully paid: tendered (positive aggregate) >= total
        if (tx.Tendered.Amount >= tx.Total.Amount)
        {
            var overpay = tx.Tendered.Amount - tx.Total.Amount;
            if (overpay > 0m)
            {
                // ARCHITECTURAL PRINCIPLE: Delegate change issuance policy to injected payment rules (no hardcoded 'cash').
                if (!_paymentRules.CanIssueChange(normalizedTender))
                {
                    return TransactionResult.Fail("Overpayment not allowed for this tender per payment rules policy.");
                }
                var changeMoney = new Money(overpay, tx.Currency); // change is positive amount out
                var changeLine = new TransactionLine(tx.Currency)
                {
                    LineType = TransactionLineType.Change,
                    TenderType = normalizedTender, // provenance
                    Quantity = 1,
                    UnitPrice = changeMoney,
                    Extended = changeMoney,
                    ProductName = "CHANGE",
                    ProductDescription = "Change returned"
                };
                changeLine.LineNumber = tx.Lines.Count + 1;
                changeLine.LineItemId = LineItemId.New().ToString();
                tx.Lines.Add(changeLine);
                RecalculateTotals(tx);
            }
            tx.State = TransactionState.EndOfTransaction;
        }

        AssertIntegrity(tx);
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
    public async Task<TransactionResult> VoidLineItemAsync(string sessionId, string transactionId, string lineItemId, string? reason = null, CancellationToken cancellationToken = default)
    {
        await _sessions.ValidateSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var tx = GetExisting(transactionId);
        if (tx.State is TransactionState.EndOfTransaction or TransactionState.Voided)
        {
            return TransactionResult.Fail($"Cannot void lines in state {tx.State}.");
        }
        if (string.IsNullOrWhiteSpace(lineItemId))
        {
            return TransactionResult.Fail("Line item id required.");
        }
        var target = tx.Lines.FirstOrDefault(l => l.LineItemId == lineItemId);
        if (target == null)
        {
            return TransactionResult.Fail($"Line item '{lineItemId}' not found.");
        }
        if (target.IsVoided)
        {
            return TransactionResult.Fail("Line already voided.");
        }
        // AUTO-CASCADE: collect all descendants (direct + indirect) by stable ParentLineItemId linkage
        var toVoid = new List<TransactionLine>();
        var queue = new Queue<string>();
        queue.Enqueue(target.LineItemId);
        var visited = new HashSet<string>();
        while (queue.Count > 0)
        {
            var currentId = queue.Dequeue();
            if (!visited.Add(currentId)) { continue; }
            var currentLine = tx.Lines.FirstOrDefault(l => l.LineItemId == currentId);
            if (currentLine != null)
            {
                toVoid.Add(currentLine);
                foreach (var child in tx.Lines.Where(l => !l.IsVoided && l.ParentLineItemId == currentId))
                {
                    queue.Enqueue(child.LineItemId);
                }
            }
        }
        // Apply void flags atomically (in-memory) then recalc
        foreach (var line in toVoid)
        {
            line.IsVoided = true;
            if (!string.IsNullOrWhiteSpace(reason))
            {
                line.VoidReason = reason;
            }
        }
        RecalculateTotals(tx);
        AssertIntegrity(tx);
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

    // ===== INTEGRITY HELPERS =====
    private static void RecalculateTotals(Transaction tx)
    {
        decimal merchandise = 0m;
        decimal tendered = 0m; // positive aggregate of all tender lines (tender lines store negative amounts)
        decimal change = 0m;   // positive aggregate of change lines
        foreach (var line in tx.Lines)
        {
            if (line.IsVoided) { continue; }
            switch (line.LineType)
            {
                case TransactionLineType.Item:
                    // Integrity: line extended must equal unit * qty
                    if (line.Extended.Amount != line.UnitPrice.Amount * line.Quantity)
                    {
                        throw new InvalidOperationException("Line extended mismatch (Item) – financial integrity violation.");
                    }
                    merchandise += line.Extended.Amount;
                    break;
                case TransactionLineType.Tender:
                    // Tender lines are stored as negative amounts reflecting cash/card in; invert sign for aggregate
                    if (line.Extended.Amount >= 0m)
                    {
                        throw new InvalidOperationException("Tender line must have negative amount.");
                    }
                    tendered += -line.Extended.Amount;
                    break;
                case TransactionLineType.Change:
                    if (line.Extended.Amount < 0m)
                    {
                        throw new InvalidOperationException("Change line must have positive amount.");
                    }
                    change += line.Extended.Amount;
                    break;
            }
        }
        tx.Total = new Money(merchandise, tx.Currency);
        tx.Tendered = new Money(tendered, tx.Currency);
        tx.ChangeDue = new Money(change, tx.Currency);
        tx.BalanceDue = new Money(tx.Total.Amount - tx.Tendered.Amount + tx.ChangeDue.Amount, tx.Currency);
    }

    private static void AssertIntegrity(Transaction tx)
    {
        // Merchandise total must match sum of item lines only
        var itemSum = tx.Lines.Where(l => !l.IsVoided && l.LineType == TransactionLineType.Item).Sum(l => l.Extended.Amount);
        if (itemSum != tx.Total.Amount)
        {
            throw new InvalidOperationException($"FINANCIAL INTEGRITY VIOLATION: Stored merchandise total {tx.Total.Amount} != recomputed {itemSum} for transaction {tx.Id}.");
        }
        // Change cannot exceed tendered - total (overpay)
        var overpay = Math.Max(tx.Tendered.Amount - tx.Total.Amount, 0m);
        if (tx.ChangeDue.Amount > overpay)
        {
            throw new InvalidOperationException("FINANCIAL INTEGRITY VIOLATION: Change exceeds overpayment.");
        }
        // Cannot have change when not fully paid
        if (tx.ChangeDue.Amount > 0m && tx.Tendered.Amount < tx.Total.Amount)
        {
            throw new InvalidOperationException("FINANCIAL INTEGRITY VIOLATION: Change present before full payment.");
        }
        // ARCHITECTURAL PRINCIPLE: Sign semantics -> Items positive, Tenders negative, Change positive.
        // Derived invariant: BalanceDue = Total - Tendered + ChangeDue must be 0 only when fully paid (EndOfTransaction).
        var balanceDue = tx.Total.Amount - tx.Tendered.Amount + tx.ChangeDue.Amount;
        if (tx.State == TransactionState.EndOfTransaction && balanceDue != 0m)
        {
            throw new InvalidOperationException("FINANCIAL INTEGRITY VIOLATION: Closed transaction has non-zero balance due.");
        }
        if (tx.BalanceDue.Amount != balanceDue)
        {
            throw new InvalidOperationException("FINANCIAL INTEGRITY VIOLATION: BalanceDue property drift detected.");
        }
    }
}
