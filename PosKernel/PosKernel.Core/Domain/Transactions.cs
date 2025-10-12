namespace PosKernel.Core.Domain;

/// <summary>
/// Canonical transaction state machine states used by AI and client layers to drive behavior.
/// ARCHITECTURAL PRINCIPLE: Lifecycle enforced in core domain (not in abstractions) so evolution does not leak across layers.
/// </summary>
public enum TransactionState
{
    /// <summary>Fresh transaction ready for first item.</summary>
    StartTransaction = 0,
    /// <summary>At least one item present; still building order.</summary>
    ItemsPending = 1,
    /// <summary>Payment complete; ready for receipt/close.</summary>
    EndOfTransaction = 2,
    /// <summary>Transaction was voided; terminal state.</summary>
    Voided = 3,
    /// <summary>Legacy alias for ItemsPending (do not use in new code).</summary>
    [Obsolete("Use ItemsPending instead")] Building = 1,
    /// <summary>Legacy alias for EndOfTransaction (do not use in new code).</summary>
    [Obsolete("Use EndOfTransaction instead")] Completed = 2
}

/// <summary>
/// Display/model representation of a line item – calculations happen in engine, not here.
/// </summary>
public sealed class TransactionLine
{
    /// <summary>Stable identifier (empty until assigned).</summary>
    public LineItemId LineId { get; init; } = new LineItemId(Guid.Empty);
    /// <summary>Assigned stable id string (preferred over line number).</summary>
    public string LineItemId { get; set; } = string.Empty;
    /// <summary>1-based dynamic line number (shifts after voids).</summary>
    public int LineNumber { get; set; }
    /// <summary>Parent line number (0 for root).</summary>
    public int ParentLineNumber { get; set; }
    /// <summary>Stable parent line identifier (blank for root).</summary>
    public string ParentLineItemId { get; set; } = string.Empty;
    /// <summary>Strongly typed product id.</summary>
    public ProductId ProductId { get; init; } = new("");
    /// <summary>Raw product id string for interoperability.</summary>
    public string ProductIdString { get; set; } = string.Empty;
    /// <summary>Ordered quantity.</summary>
    public int Quantity { get; set; }
    /// <summary>Unit price from engine.</summary>
    public Money UnitPrice { get; set; }
    /// <summary>Extended price from engine.</summary>
    public Money Extended { get; set; }
    /// <summary>Indent level for hierarchical display.</summary>
    public int DisplayIndentLevel { get; set; }
    /// <summary>True when voided.</summary>
    public bool IsVoided { get; set; }
    /// <summary>Optional void reason.</summary>
    public string? VoidReason { get; set; }
    /// <summary>Resolved product name.</summary>
    public string ProductName { get; set; } = string.Empty;
    /// <summary>Resolved product description.</summary>
    public string ProductDescription { get; set; } = string.Empty;
    /// <summary>Arbitrary metadata.</summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
    /// <summary>Create a line with zero monetary values for currency.</summary>
    public TransactionLine(string currency)
    {
        UnitPrice = Money.Zero(currency);
        Extended = Money.Zero(currency);
    }
    /// <summary>Parameterless constructor for serializers.</summary>
    public TransactionLine() { }
}

/// <summary>
/// Transaction aggregate – monetary values ALWAYS sourced from engine.
/// </summary>
public sealed class Transaction
{
    /// <summary>Stable transaction identifier.</summary>
    public TransactionId Id { get; init; } = TransactionId.New();
    /// <summary>Lifecycle state.</summary>
    public TransactionState State { get; set; } = TransactionState.StartTransaction;
    /// <summary>ISO 4217 currency code.</summary>
    public string Currency { get; init; }
    /// <summary>Ordered line items.</summary>
    public List<TransactionLine> Lines { get; } = new();
    /// <summary>Total amount.</summary>
    public Money Total { get; set; }
    /// <summary>Tendered amount.</summary>
    public Money Tendered { get; set; }
    /// <summary>Change due.</summary>
    public Money ChangeDue { get; set; }
    /// <summary>Create a transaction for a currency.</summary>
    public Transaction(string currency)
    {
        Currency = currency ?? throw new ArgumentNullException(nameof(currency));
        Total = Money.Zero(currency);
        Tendered = Money.Zero(currency);
        ChangeDue = Money.Zero(currency);
    }
    /// <summary>Apply kernel-calculated totals and state.</summary>
    public void UpdateFromKernel(Money total, Money tendered, Money changeDue, TransactionState state)
    {
        Total = total;
        Tendered = tendered;
        ChangeDue = changeDue;
        State = state;
    }
}

/// <summary>
/// Standard result envelope for kernel operations providing transaction plus diagnostic context.
/// </summary>
/// <summary>Result wrapper conveying success, transaction snapshot, and diagnostic messages.</summary>
public sealed record TransactionResult
(
    bool Success,
    Transaction? Transaction,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings
)
{
    /// <summary>Create success result with optional warnings.</summary>
    public static TransactionResult Ok(Transaction tx, IEnumerable<string>? warnings = null) => new(true, tx, Array.Empty<string>(), (warnings ?? Array.Empty<string>()).ToList());
    /// <summary>Create failure result.</summary>
    public static TransactionResult Fail(params string[] errors) => new(false, null, errors, Array.Empty<string>());
}

// Identifier value types relocated from Abstractions for alignment with architecture doc placing domain types together.
/// <summary>Strongly typed identifier for a transaction (avoids raw Guid misuse).</summary>
/// <param name="Value">Underlying Guid value.</param>
public readonly record struct TransactionId(Guid Value)
{
    /// <summary>Create a new unique transaction id.</summary>
    public static TransactionId New() => new(Guid.NewGuid());
    /// <summary>Returns 32-digit hex representation.</summary>
    public override string ToString() => Value.ToString("N");
}
/// <summary>Opaque product identifier (SKU/PLU/UPC agnostic) – AI layer treats as string token.</summary>
/// <param name="Value">Raw identifier string.</param>
public readonly record struct ProductId(string Value)
{
    /// <summary>Returns raw identifier string.</summary>
    public override string ToString() => Value;
}
/// <summary>Stable identifier for a transaction line (line numbers can shift after voids/insertions).</summary>
/// <param name="Value">Underlying Guid value.</param>
public readonly record struct LineItemId(Guid Value)
{
    /// <summary>Create a new line item id.</summary>
    public static LineItemId New() => new(Guid.NewGuid());
    /// <summary>Returns 32-digit hex representation.</summary>
    public override string ToString() => Value.ToString("N");
}

/// <summary>
/// Immutable monetary value represented as a decimal amount plus ISO 4217 currency code.
/// ARCHITECTURAL PRINCIPLE: Culture-neutral representation – no assumptions about fractional digits or formatting.
/// All rounding/formatting rules are applied by store-provided currency services, never here.
/// </summary>
public readonly record struct Money(decimal Amount, string Currency)
{
    /// <summary>Create a zero-valued money for the specified currency.</summary>
    public static Money Zero(string currency) => new(0m, currency);
    /// <summary>Non-localized display form used strictly for debugging/logging.</summary>
    public override string ToString() => $"{Currency} {Amount}";
    /// <summary>Add two money values of the same currency.</summary>
    public Money Add(Money other) => Currency != other.Currency ? throw new InvalidOperationException("Currency mismatch") : new(Amount + other.Amount, Currency);
    /// <summary>Subtract another money value of the same currency.</summary>
    public Money Subtract(Money other) => Currency != other.Currency ? throw new InvalidOperationException("Currency mismatch") : new(Amount - other.Amount, Currency);
    /// <summary>Multiply amount by scalar without rounding assumptions.</summary>
    public Money Multiply(decimal multiplier) => new(Amount * multiplier, Currency);
    /// <summary>Divide amount by scalar (throws on divide-by-zero).</summary>
    public Money Divide(decimal divisor) => divisor == 0 ? throw new DivideByZeroException("Cannot divide money by zero") : new(Amount / divisor, Currency);
}
