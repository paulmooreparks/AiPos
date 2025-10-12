namespace PosKernel.Abstractions;

/// <summary>
/// Strongly typed identifier for a transaction (avoids raw Guid misuse).
/// </summary>
/// <param name="Value">Underlying Guid value.</param>
public readonly record struct TransactionId(Guid Value)
{
    /// <summary>Create a new unique transaction id.</summary>
    public static TransactionId New() => new(Guid.NewGuid());
    /// <summary>Returns 32-digit hex representation.</summary>
    public override string ToString() => Value.ToString("N");
}

/// <summary>
/// Opaque product identifier (SKU/PLU/UPC agnostic) – AI layer treats as string token.
/// </summary>
/// <param name="Value">Raw identifier string.</param>
public readonly record struct ProductId(string Value)
{
    /// <summary>Returns raw identifier string.</summary>
    public override string ToString() => Value;
}

/// <summary>
/// Stable identifier for a transaction line (line numbers can shift after voids/insertions).
/// </summary>
/// <param name="Value">Underlying Guid value.</param>
public readonly record struct LineItemId(Guid Value)
{
    /// <summary>Create a new line item id.</summary>
    public static LineItemId New() => new(Guid.NewGuid());
    /// <summary>Returns 32-digit hex representation.</summary>
    public override string ToString() => Value.ToString("N");
}
