namespace PosKernel.Extensions.Core.Profiles;

/// <summary>
/// Provides immutable access to store profiles discovered from extension configuration.
/// Implementations MUST be fail-fast: invalid or duplicate profile data MUST throw during load/reload.
/// </summary>
public interface IStoreProfileProvider
{
    /// <summary>Returns all loaded store profiles (immutable snapshot).</summary>
    IReadOnlyList<StoreProfile> GetAll();
    /// <summary>Retrieves a profile by store id or throws if not found.</summary>
    StoreProfile GetById(string storeId);
    /// <summary>Reloads profile set from underlying sources atomically (fail-fast on any validation error).</summary>
    void Reload();
}

/// <summary>
/// Culture-neutral store profile contract. Contains only identifiers and currency/culture codes; no business rules or pricing logic.
/// </summary>
public sealed record StoreProfile(
    string StoreId,
    string DisplayName,
    string Currency,
    string Culture,
    int Version,
    IReadOnlyList<PaymentTenderType> PaymentTypes,
    string? DatabaseType = null,
    string? ConnectionString = null);

/// <summary>
/// Payment tender definition used only for validation at selection/tender time. No processing logic or assumptions about settlement networks.
/// </summary>
public sealed record PaymentTenderType(
    string Id,
    bool AllowsChange,
    bool RequiresExact);
