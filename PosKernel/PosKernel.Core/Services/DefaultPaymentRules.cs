using PosKernel.Core.Interfaces;

namespace PosKernel.Core.Services;

/// <summary>
/// Default implementation preserving prior behavior: only CASH may produce change (overpay allowed),
/// any non-empty tender string is considered valid. Extensions should supply a richer implementation
/// enumerating allowed tenders and their capabilities.
/// </summary>
public sealed class DefaultPaymentRules : IPaymentRules
{
    /// <summary>
    /// Validates a tender type string. Returns trimmed value if non-empty; otherwise null to indicate invalid.
    /// </summary>
    public string? NormalizeTenderType(string tenderType)
    {
        if (string.IsNullOrWhiteSpace(tenderType))
        {
            return null; // KernelEngine will fail fast with descriptive error.
        }
        return tenderType.Trim();
    }

    /// <summary>
    /// Returns true only when tender is CASH (case-insensitive) preserving legacy behavior allowing change.
    /// </summary>
    public bool CanIssueChange(string normalizedTenderType)
    {
        return string.Equals(normalizedTenderType, "cash", StringComparison.OrdinalIgnoreCase);
    }
}
