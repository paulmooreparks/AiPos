namespace PosKernel.Abstractions;

/// <summary>
/// Immutable monetary value represented as a decimal amount plus ISO 4217 currency code.
/// ARCHITECTURAL PRINCIPLE: Culture-neutral representation – no assumptions about fractional digits or formatting.
/// All rounding/formatting rules are applied by store-provided currency services, never here.
/// </summary>
/// <param name="Amount">Raw decimal monetary amount.</param>
/// <param name="Currency">ISO 4217 currency code (e.g., USD, SGD, JPY).</param>
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
