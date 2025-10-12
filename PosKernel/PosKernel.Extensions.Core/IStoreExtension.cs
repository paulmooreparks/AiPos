using PosKernel.Abstractions;
using System.Collections.ObjectModel;

namespace PosKernel.Extensions.Core;

// ARCHITECTURAL FIX: Split monolithic store extension surface into segregated interfaces.

/// <summary>
/// Catalog operations for validating and discovering products (culture-neutral identifiers, store-specific data behind interface).
/// </summary>
public interface IProductCatalog
{
    /// <summary>Validate a product for sale returning authoritative data and effective price.</summary>
    Task<ProductValidationResult> ValidateProductAsync(string productId, CancellationToken cancellationToken = default);
    /// <summary>Search for products using store-specific matching logic.</summary>
    Task<IReadOnlyList<ProductInfo>> SearchProductsAsync(string searchTerm, int maxResults = 50, CancellationToken cancellationToken = default);
    /// <summary>Return popular or recommended products for upsell operations.</summary>
    Task<IReadOnlyList<ProductInfo>> GetPopularItemsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Modification validation and pricing operations (e.g., extras/add-ons) with no currency formatting assumptions.
/// </summary>
public interface IModificationService
{
    /// <summary>Validate selected modifications against product rules.</summary>
    Task<ModificationValidationResult> ValidateModificationsAsync(string productId, IReadOnlyList<ModificationSelection> selections, CancellationToken cancellationToken = default);
    /// <summary>Calculate additional price impact of modifications (raw decimal, no formatting).</summary>
    Task<decimal> CalculateModificationTotalAsync(IReadOnlyList<ModificationSelection> selections, CancellationToken cancellationToken = default);
}

/// <summary>
/// Composition root exposed to kernel when loading a store extension package. Provides service instances.
/// </summary>
/// <summary>
/// Composition root for a store extension – provides segregated catalog, modification, and currency services.
/// </summary>
public interface IStoreExtension
{
    /// <summary>Product catalog operations.</summary>
    IProductCatalog Catalog { get; }
    /// <summary>Modification validation and pricing operations.</summary>
    IModificationService Modifications { get; }
    /// <summary>Currency formatting and decimal place resolution service.</summary>
    ICurrencyFormatter CurrencyFormatter { get; }
}

/// <summary>
/// Currency formatting abstraction. All currency symbol resolution, decimal place logic, and localized formatting
/// is delegated to store provided implementation. Kernel never assumes 2 decimals or a fixed symbol.
/// </summary>
public interface ICurrencyFormatter
{
    /// <summary>Formats a decimal amount for display in the specified currency and culture.</summary>
    string FormatCurrency(decimal amount, string currency, string culture);
    /// <summary>Returns the currency symbol for a currency code.</summary>
    string GetCurrencySymbol(string currency);
    /// <summary>Returns number of fractional decimal places for currency (e.g. 0 for JPY, 3 for BHD).</summary>
    int GetDecimalPlaces(string currency);
}


/// <summary>Canonical product information returned by store catalogs.</summary>
public sealed record ProductInfo
{
    /// <summary>Stock keeping or catalog identifier.</summary>
    public string Sku { get; init; } = string.Empty;
    /// <summary>Display name (already localized if appropriate).</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>Display description (already localized if appropriate).</summary>
    public string Description { get; init; } = string.Empty;
    /// <summary>Logical category (store defined taxonomy).</summary>
    public string Category { get; init; } = string.Empty;
    /// <summary>Base price (pre-modification) in store currency.</summary>
    public decimal BasePrice { get; init; }
    /// <summary>Whether product currently active/available.</summary>
    public bool IsActive { get; init; } = true;
}

/// <summary>Result of validating a product for sale.</summary>
public sealed record ProductValidationResult
{
    /// <summary>True if product valid for sale.</summary>
    public bool IsValid { get; init; }
    /// <summary>Error message when invalid (never user localized at this layer).</summary>
    public string? ErrorMessage { get; init; }
    /// <summary>Canonical product info when valid.</summary>
    public ProductInfo? Product { get; init; }
    /// <summary>Effective price (base plus defaults) in store currency.</summary>
    public decimal EffectivePrice { get; init; }
    /// <summary>Available modification group identifiers.</summary>
    public IReadOnlyList<string> AvailableModificationGroups { get; init; } = Array.Empty<string>();
}

/// <summary>Represents a selected modification option (e.g., extra shot).</summary>
public sealed record ModificationSelection
{
    /// <summary>Modification group identifier (e.g., MILK, SYRUP).</summary>
    public string Group { get; init; } = string.Empty;
    /// <summary>Modification code within group (e.g., OAT, VANILLA).</summary>
    public string Code { get; init; } = string.Empty;
    /// <summary>Quantity selected (default 1).</summary>
    public int Quantity { get; init; } = 1;
}

/// <summary>Result of validating modifications set.</summary>
public sealed record ModificationValidationResult
{
    /// <summary>True if modifications valid for the product.</summary>
    public bool IsValid { get; init; }
    /// <summary>Error message if invalid.</summary>
    public string? ErrorMessage { get; init; }
    /// <summary>Total additional price from modifications.</summary>
    public decimal TotalExtraPrice { get; init; }
}
