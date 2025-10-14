using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiPos.Agentic;
using AiPos.Core;
using AiPos.Orchestrator;
using Microsoft.Data.Sqlite;
using PosKernel.Client;
using PosKernel.Core.Domain;
using PosKernel.Core.Interfaces;
using PosKernel.Core.Services;
using PosKernel.Extensions.Core;

// ARCHITECTURAL PRINCIPLE: Explicit wiring & fail-fast. Optional real data via AIPOS_CATALOG_DB; fallback is demo catalog.
// This demo avoids a DI container so missing dependencies surface immediately.
/// <summary>
/// Legacy minimal terminal (non-TUI) demo harness retained temporarily.
/// Prefer using Demo.AiPos.Tui for the primary interactive experience.
/// </summary>
public static class Program
{
    /// <summary>
    /// Entry point for minimal console demo. Use TUI project for full experience.
    /// </summary>
    /// <param name="args">Command line arguments.</param>
    public static async Task Main(string[] args)
    {
        var store = TryCreateSqliteBackedStore() ?? new DemoStoreExtension();

        var sessionManager = new SessionManager();
        var txService = new TransactionService();
    IKernelEngine engine = new KernelEngine(sessionManager, txService, new DefaultPaymentRules());
        IKernelClient kernelClient = new DirectKernelClient(engine);
        var sessionId = await kernelClient.CreateSessionAsync("TERM1", "OP1");

        TransactionResult? current = null;

        string MapNatural(string input) => input.StartsWith("new", StringComparison.OrdinalIgnoreCase) ? "new" : input;
        void EnsureActive()
        {
            if (current?.Transaction == null)
            {
                throw new InvalidOperationException("No active transaction. Say 'new'.");
            }
        }
        string TxId() => current!.Transaction!.Id.ToString();
        string Render()
        {
            if (current == null) { return "(null)"; }
            if (!current.Success) { return "FAIL: " + string.Join("; ", current.Errors); }
            var tx = current.Transaction!;
            var fmt = store.CurrencyFormatter;
            // TODO: obtain culture from store configuration; demo uses en-US for expediency.
            string F(decimal amt) => fmt.FormatCurrency(amt, tx.Currency, "en-US");
            var lines = new List<string>
            {
                $"State:{tx.State} Lines:{tx.Lines.Count} Total:{F(tx.Total.Amount)} Tendered:{F(tx.Tendered.Amount)} Change:{F(tx.ChangeDue.Amount)}"
            };
            for (int i = 0; i < tx.Lines.Count; i++)
            {
                var l = tx.Lines[i];
                lines.Add($"  {i+1}. {l.ProductName} x{l.Quantity} @{F(l.UnitPrice.Amount)} = {F(l.Extended.Amount)}");
            }
            return string.Join(Environment.NewLine, lines);
        }

        var toolDefs = new List<ToolDefinition>
        {
            new("start_transaction", "pos", "Start a new transaction", new []{ new ToolParameter("currency","string", true, "ISO 4217 currency code") }),
            new("add_item", "pos", "Add an item", new []{ new ToolParameter("productId","string", true, "Product id"), new ToolParameter("quantity","int", true, "Quantity") }),
            new("pay", "pos", "Process payment", new []{ new ToolParameter("amount","decimal", true, "Amount tendered") }),
            new("show", "pos", "Show current transaction", Array.Empty<ToolParameter>())
        };

        var handlers = new List<(string, Func<IReadOnlyDictionary<string, object>, CancellationToken, Task<string>>)>()
        {
            ("start_transaction", async (p, ct) => { var currency = (string)p["currency"]; current = await kernelClient.StartTransactionAsync(sessionId, currency, ct); return Render(); }),
            ("add_item", async (p, ct) => {
                EnsureActive();
                var productId = (string)p["productId"]; var qty = Convert.ToInt32(p["quantity"]);
                var validation = await store.Catalog.ValidateProductAsync(productId, ct);
                if (!validation.IsValid || validation.Product == null) { return $"Product invalid: {validation.ErrorMessage}"; }
                decimal unitPrice = validation.Product.BasePrice; // Mods pricing deferred (P1)
                current = await kernelClient.AddLineItemAsync(sessionId, TxId(), productId, qty, unitPrice, validation.Product.Name, validation.Product.Description, null, ct);
                return Render();
            }),
            ("pay", async (p, ct) => { EnsureActive(); var amount = Convert.ToDecimal(p["amount"]); current = await kernelClient.ProcessPaymentAsync(sessionId, TxId(), amount, "cash", ct); return Render(); }),
            ("show", async (p, ct) => { EnsureActive(); current = await kernelClient.GetTransactionAsync(sessionId, TxId(), ct); return Render(); })
        };

        IToolExecutor executor = new DirectToolExecutor(toolDefs, handlers);
        var storeConfig = new InMemoryStoreConfigurationProvider("USD", "en-US"); // demo default
        var orchestrator = new AiOrchestrator(executor, storeConfig);
        IAgenticServer server = new AgenticServerHost(orchestrator);

        Console.WriteLine("AI POS (Single-Call) – commands: new | add COFFEE.SMALL 2 | pay 5 | show | quit");
        Console.WriteLine($"Session: {sessionId}");

        while (true)
        {
            Console.Write("ai> ");
            var input = Console.ReadLine();
            if (input == null) { continue; }
            var trimmed = input.Trim();
            if (trimmed.Equals("quit", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("exit", StringComparison.OrdinalIgnoreCase))
            {
                await kernelClient.CloseSessionAsync(sessionId);
                Console.WriteLine("Session closed.");
                break;
            }
            try
            {
                var response = await server.HandleAsync(MapNatural(trimmed));
                Console.WriteLine(response);
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR: " + ex.Message);
            }
        }
    }

    private static IStoreExtension? TryCreateSqliteBackedStore()
    {
        var path = Environment.GetEnvironmentVariable("AIPOS_CATALOG_DB");
        if (string.IsNullOrWhiteSpace(path)) { return null; }
        if (!File.Exists(path))
        {
            Console.WriteLine($"WARNING: AIPOS_CATALOG_DB set but file not found: {path}. Using demo in-memory catalog.");
            return null;
        }
        Console.WriteLine($"Using SQLite catalog at: {path}");
        return new SqliteStoreExtension(path);
    }
}

// --- Demo / fallback implementations ---
sealed class DemoStoreExtension : IStoreExtension
{
    public IProductCatalog Catalog { get; } = new DemoCatalog();
    public IModificationService Modifications { get; } = new DemoMods();
    public ICurrencyFormatter CurrencyFormatter { get; } = new DemoCurrencyFormatter();
}

sealed class DemoCatalog : IProductCatalog
{
    private static readonly List<ProductInfo> _products = new()
    {
        new ProductInfo { Sku = "COFFEE.SMALL", Name = "Small Coffee", Description = "Fresh brew", Category = "DRINK", BasePrice = 3.50m },
        new ProductInfo { Sku = "COFFEE.LARGE", Name = "Large Coffee", Description = "Fresh brew large", Category = "DRINK", BasePrice = 4.50m },
        new ProductInfo { Sku = "MUFFIN.BLUE", Name = "Blueberry Muffin", Description = "Bakery item", Category = "FOOD", BasePrice = 2.95m }
    };
    public Task<ProductValidationResult> ValidateProductAsync(string productId, CancellationToken cancellationToken = default)
    {
        var p = _products.FirstOrDefault(p => string.Equals(p.Sku, productId, StringComparison.OrdinalIgnoreCase));
        if (p == null)
        {
            return Task.FromResult(new ProductValidationResult { IsValid = false, ErrorMessage = "Not found" });
        }
        return Task.FromResult(new ProductValidationResult { IsValid = true, Product = p, EffectivePrice = p.BasePrice });
    }
    public Task<IReadOnlyList<ProductInfo>> SearchProductsAsync(string searchTerm, int maxResults = 50, CancellationToken cancellationToken = default)
    {
        var list = _products.Where(p => p.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) || p.Sku.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
            .Take(maxResults).ToList();
        return Task.FromResult((IReadOnlyList<ProductInfo>)list);
    }
    public Task<IReadOnlyList<ProductInfo>> GetPopularItemsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult((IReadOnlyList<ProductInfo>)_products.Take(3).ToList());
}

sealed class DemoMods : IModificationService
{
    public Task<ModificationValidationResult> ValidateModificationsAsync(string productId, IReadOnlyList<ModificationSelection> selections, CancellationToken cancellationToken = default)
        => Task.FromResult(new ModificationValidationResult { IsValid = true, TotalExtraPrice = 0m });
    public Task<decimal> CalculateModificationTotalAsync(IReadOnlyList<ModificationSelection> selections, CancellationToken cancellationToken = default)
        => Task.FromResult(0m);
}

sealed class DemoCurrencyFormatter : ICurrencyFormatter
{
    public string FormatCurrency(decimal amount, string currency, string culture) => $"{currency} {amount}"; // POC only; do NOT ship.
    public string GetCurrencySymbol(string currency) => currency;
    public int GetDecimalPlaces(string currency) => 2;
}

// --- SQLite-backed implementations (optional real data path) ---
sealed class SqliteStoreExtension : IStoreExtension
{
    public IProductCatalog Catalog { get; }
    public IModificationService Modifications { get; } = new DemoMods(); // P1: real modifications
    public ICurrencyFormatter CurrencyFormatter { get; } = new DemoCurrencyFormatter(); // P1: store-specific formatting
    public SqliteStoreExtension(string dbPath) { Catalog = new SqliteCatalog(dbPath); }
}

sealed class SqliteCatalog : IProductCatalog
{
    private readonly string _dbPath;
    public SqliteCatalog(string dbPath)
    {
        _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
        if (!File.Exists(_dbPath))
        {
            throw new InvalidOperationException($"SQLite catalog not found at '{_dbPath}'. Set AIPOS_CATALOG_DB.");
        }
    }

    public async Task<ProductValidationResult> ValidateProductAsync(string productId, CancellationToken cancellationToken = default)
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT p.sku, p.name, p.description, c.name, p.base_price, p.is_active
                              FROM products p
                              JOIN categories c ON p.category_id = c.id
                              WHERE p.sku = @id LIMIT 1";
        cmd.Parameters.AddWithValue("@id", productId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var info = new ProductInfo
            {
                Sku = reader.GetString(0),
                Name = reader.GetString(1),
                Description = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                Category = reader.GetString(3),
                BasePrice = reader.GetDecimal(4),
                IsActive = reader.GetBoolean(5)
            };
            return new ProductValidationResult { IsValid = true, Product = info, EffectivePrice = info.BasePrice };
        }
        return new ProductValidationResult { IsValid = false, ErrorMessage = $"Product not found: {productId}" };
    }

    public async Task<IReadOnlyList<ProductInfo>> SearchProductsAsync(string searchTerm, int maxResults = 50, CancellationToken cancellationToken = default)
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            cmd.CommandText = @"SELECT p.sku, p.name, p.description, c.name, p.base_price, p.is_active
                                 FROM products p JOIN categories c ON p.category_id = c.id
                                 WHERE p.is_active = 1 ORDER BY p.name LIMIT @max";
        }
        else
        {
            cmd.CommandText = @"SELECT p.sku, p.name, p.description, c.name, p.base_price, p.is_active
                                 FROM products p JOIN categories c ON p.category_id = c.id
                                 WHERE p.is_active = 1 AND (p.sku LIKE '%' || @q || '%' OR p.name LIKE '%' || @q || '%')
                                 ORDER BY p.name LIMIT @max";
            cmd.Parameters.AddWithValue("@q", searchTerm);
        }
        cmd.Parameters.AddWithValue("@max", maxResults);
        var list = new List<ProductInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new ProductInfo
            {
                Sku = reader.GetString(0),
                Name = reader.GetString(1),
                Description = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                Category = reader.GetString(3),
                BasePrice = reader.GetDecimal(4),
                IsActive = reader.GetBoolean(5)
            });
        }
        return list;
    }

    public Task<IReadOnlyList<ProductInfo>> GetPopularItemsAsync(CancellationToken cancellationToken = default)
        => SearchProductsAsync(string.Empty, 10, cancellationToken);
}
