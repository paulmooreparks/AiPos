using PosKernel.Core.Domain;

namespace PosKernel.Core.Services;

/// <summary>
/// Default implementation of <see cref="ITransactionService"/> providing culture-neutral,
/// fail-fast mutation helpers for <see cref="Transaction"/> objects. Performs ONLY structural
/// mutations with values already calculated by the authoritative kernel engine (no math here).
/// </summary>
public sealed class TransactionService : ITransactionService
{
    /// <summary>
    /// Begins a new transaction with the specified currency supplied by store configuration.
    /// </summary>
    /// <param name="currency">ISO 4217 currency code (must be non-empty).</param>
    /// <returns>New <see cref="Transaction"/> instance.</returns>
    /// <exception cref="InvalidOperationException">Thrown when currency is missing.</exception>
    public Transaction Begin(string currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
        {
            throw new InvalidOperationException("Currency must be provided by store configuration - none supplied.");
        }
        return new(currency);
    }

    /// <summary>
    /// Adds a line to the transaction using already-calculated monetary values from the kernel.
    /// </summary>
    /// <param name="tx">Target transaction.</param>
    /// <param name="productId">Product identifier.</param>
    /// <param name="qty">Quantity (as passed from kernel).</param>
    /// <param name="unitPrice">Unit price (authoritative value).</param>
    /// <param name="extendedPrice">Extended price (authoritative value).</param>
    /// <returns>The mutated transaction (same instance for fluent usage).</returns>
    /// <exception cref="ArgumentNullException">If transaction is null.</exception>
    /// <exception cref="InvalidOperationException">If currency mismatch detected.</exception>
    public Transaction AddLine(Transaction tx, ProductId productId, int qty, Money unitPrice, Money extendedPrice)
    {
        if (tx == null)
        {
            throw new ArgumentNullException(nameof(tx));
        }
        if (unitPrice.Currency != tx.Currency || extendedPrice.Currency != tx.Currency)
        {
            throw new InvalidOperationException("Currency mismatch between transaction and line item values.");
        }
        var line = new TransactionLine(tx.Currency)
        {
            LineType = TransactionLineType.Item,
            ProductId = productId,
            Quantity = qty,
            UnitPrice = unitPrice,
            Extended = extendedPrice
        };
        // Assign dynamic line number (1-based sequence excluding voided lines for simplicity)
        line.LineNumber = tx.Lines.Count + 1;
        // Assign stable identifier if not already populated
        if (string.IsNullOrWhiteSpace(line.LineItemId))
        {
            line.LineItemId = LineItemId.New().ToString();
        }
        tx.Lines.Add(line);
        return tx;
    }

    /// <summary>
    /// Updates kernel-provided totals and state on the transaction (no calculations performed).
    /// </summary>
    /// <param name="tx">Target transaction.</param>
    /// <param name="total">Authoritative total.</param>
    /// <param name="tendered">Authoritative tendered amount.</param>
    /// <param name="changeDue">Authoritative change due.</param>
    /// <param name="state">New transaction state.</param>
    /// <returns>The mutated transaction.</returns>
    /// <exception cref="ArgumentNullException">If transaction is null.</exception>
    /// <exception cref="InvalidOperationException">If currency mismatch detected.</exception>
    public Transaction UpdateFromKernel(Transaction tx, Money total, Money tendered, Money changeDue, TransactionState state)
    {
        if (tx == null)
        {
            throw new ArgumentNullException(nameof(tx));
        }
        if (total.Currency != tx.Currency || tendered.Currency != tx.Currency || changeDue.Currency != tx.Currency)
        {
            throw new InvalidOperationException("Currency mismatch in kernel totals update.");
        }
        tx.UpdateFromKernel(total, tendered, changeDue, state);
        return tx;
    }
}
