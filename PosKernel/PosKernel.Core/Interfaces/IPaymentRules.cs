namespace PosKernel.Core.Interfaces;

/// <summary>
/// Defines kernel-level payment rules governing tender validation and change issuance.
/// ARCHITECTURAL PRINCIPLE: Kernel must not hardcode store/business assumptions (e.g. only cash can overpay);
/// these rules are injectable so extensions can supply culture / payment method specific policies.
/// </summary>
public interface IPaymentRules
{
    /// <summary>
    /// Normalize and validate an incoming tender type string. Returns a canonical representation
    /// (e.g. upper-case) if valid; returns null if invalid (caller should fail fast).
    /// Implementation MUST be pure (no side effects) and culture-neutral.
    /// </summary>
    string? NormalizeTenderType(string tenderType);

    /// <summary>
    /// Returns true if the rules permit issuing change (i.e. accepting an overpay that creates a change line)
    /// for the specified normalized tender type.
    /// </summary>
    bool CanIssueChange(string normalizedTenderType);
}
