using PosKernel.Extensions.Core;

namespace PosKernel.Extensions.FoodService;

/// <summary>
/// Store extension wiring Kopitiam catalog into kernel abstractions.
/// </summary>
public sealed class KopitiamStoreExtension : IStoreExtension
{
    public IProductCatalog Catalog { get; }
    public IModificationService Modifications { get; } = new NoopModifications();
    public ICurrencyFormatter CurrencyFormatter { get; } = new PassthroughCurrencyFormatter();

    public KopitiamStoreExtension(string storeId, string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Connection string required for KopitiamStoreExtension");
        }
        Catalog = new KopitiamCatalog(storeId, connectionString);
    }

    private sealed class NoopModifications : IModificationService
    {
        public Task<ModificationValidationResult> ValidateModificationsAsync(string productId, IReadOnlyList<ModificationSelection> selections, CancellationToken cancellationToken = default)
            => Task.FromResult(new ModificationValidationResult { IsValid = true, TotalExtraPrice = 0m });
        public Task<decimal> CalculateModificationTotalAsync(IReadOnlyList<ModificationSelection> selections, CancellationToken cancellationToken = default)
            => Task.FromResult(0m);
    }

    private sealed class PassthroughCurrencyFormatter : ICurrencyFormatter
    {
        public string FormatCurrency(decimal amount, string currency, string culture) => $"{currency} {amount}"; // Demo only; no localization logic
        public string GetCurrencySymbol(string currency) => currency;
        public int GetDecimalPlaces(string currency) => 2; // Demo assumption
    }
}
