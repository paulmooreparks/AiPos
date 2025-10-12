using PosKernel.Core.Domain;

namespace PosKernel.Core.Services;

/// <summary>
/// Interface for mutating <see cref="Transaction"/> aggregates using authoritative
/// values produced by the kernel engine. This layer performs NO calculations,
/// enforcing the architectural boundary that all monetary math occurs in the kernel.
/// </summary>
public interface ITransactionService
{
    /// <summary>
    /// Begins a new transaction with the supplied currency (must come from store configuration).
    /// </summary>
    Transaction Begin(string currency);

    /// <summary>
    /// Adds a line item using already-calculated unit and extended prices.
    /// </summary>
    Transaction AddLine(Transaction tx, ProductId productId, int qty, Money unitPrice, Money extendedPrice);

    /// <summary>
    /// Updates total, tendered, change due, and state as provided by the kernel.
    /// </summary>
    Transaction UpdateFromKernel(Transaction tx, Money total, Money tendered, Money changeDue, TransactionState state);
}
