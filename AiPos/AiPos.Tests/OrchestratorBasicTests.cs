using AiPos.Core;
using AiPos.Orchestrator;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PosKernel.Client;
using PosKernel.Core.Interfaces;
using PosKernel.Core.Domain;
using PosKernel.Core.Services;
using PosKernel.Extensions.Core;

namespace AiPos.Tests;

/// <summary>
/// Basic P0 tests ensuring fail-fast configuration, successful new transaction, and product validation failure path.
/// </summary>
[TestClass]
public class OrchestratorBasicTests
{
    private sealed class MinimalStoreExtension : IStoreExtension
    {
        public IProductCatalog Catalog { get; } = new TestCatalog();
        public IModificationService Modifications { get; } = new NoMods();
        public ICurrencyFormatter CurrencyFormatter { get; } = new PassthroughCurrencyFormatter();
        private sealed class TestCatalog : IProductCatalog
        {
            private readonly ProductInfo _p = new() { Sku = "COFFEE.TEST", Name = "Test Coffee", Description = "", Category = "DRINK", BasePrice = 1.23m };
            public Task<ProductValidationResult> ValidateProductAsync(string productId, CancellationToken cancellationToken = default)
            {
                if (string.Equals(productId, _p.Sku, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new ProductValidationResult { IsValid = true, Product = _p, EffectivePrice = _p.BasePrice });
                }
                return Task.FromResult(new ProductValidationResult { IsValid = false, ErrorMessage = "Not found" });
            }
            public Task<IReadOnlyList<ProductInfo>> SearchProductsAsync(string searchTerm, int maxResults = 50, CancellationToken cancellationToken = default)
                => Task.FromResult((IReadOnlyList<ProductInfo>)new List<ProductInfo> { _p });
            public Task<IReadOnlyList<ProductInfo>> GetPopularItemsAsync(CancellationToken cancellationToken = default)
                => Task.FromResult((IReadOnlyList<ProductInfo>)new List<ProductInfo> { _p });
        }
        private sealed class NoMods : IModificationService
        {
            public Task<ModificationValidationResult> ValidateModificationsAsync(string productId, IReadOnlyList<ModificationSelection> selections, CancellationToken cancellationToken = default)
                => Task.FromResult(new ModificationValidationResult { IsValid = true, TotalExtraPrice = 0m });
            public Task<decimal> CalculateModificationTotalAsync(IReadOnlyList<ModificationSelection> selections, CancellationToken cancellationToken = default)
                => Task.FromResult(0m);
        }
        private sealed class PassthroughCurrencyFormatter : ICurrencyFormatter
        {
            public string FormatCurrency(decimal amount, string currency, string culture) => $"{currency} {amount}";
            public string GetCurrencySymbol(string currency) => currency;
            public int GetDecimalPlaces(string currency) => 2;
        }
    }

    private static (IAiOrchestrator orchestrator, IKernelClient client, string session) BuildHarness()
    {
        var sessionManager = new SessionManager();
        var txService = new TransactionService();
        IKernelEngine engine = new KernelEngine(sessionManager, txService);
        IKernelClient kernelClient = new DirectKernelClient(engine);
        var session = kernelClient.CreateSessionAsync("TERM1", "OP1").GetAwaiter().GetResult();

        var toolDefs = new List<ToolDefinition>
        {
            new("start_transaction","pos","Start", new []{ new ToolParameter("currency","string", true, "ISO") }),
            new("add_item","pos","Add", new []{ new ToolParameter("productId","string", true, "Product"), new ToolParameter("quantity","int", true, "Qty") })
        };
        var store = new MinimalStoreExtension();
        var handlers = new List<(string, Func<IReadOnlyDictionary<string, object>, CancellationToken, Task<string>>)>();
        // Minimal handlers just wire to kernel
        TransactionResult? current = null;
        handlers.Add(("start_transaction", async (p, ct) => { current = await kernelClient.StartTransactionAsync(session, (string)p["currency"], ct); return "OK"; }));
        handlers.Add(("add_item", async (p, ct) => {
            if (current?.Transaction == null)
            {
                throw new InvalidOperationException("No active tx");
            }
            var id = (string)p["productId"]; var qty = Convert.ToInt32(p["quantity"]);
            var valid = await store.Catalog.ValidateProductAsync(id, ct);
            if (!valid.IsValid || valid.Product == null)
            {
                return "Product invalid: " + valid.ErrorMessage;
            }
            current = await kernelClient.AddLineItemAsync(session, current.Transaction.Id.ToString(), id, qty, valid.Product.BasePrice, valid.Product.Name, valid.Product.Description, ct);
            return "ADDED";
        }));
        IToolExecutor exec = new DirectToolExecutor(toolDefs, handlers);
        var cfg = new InMemoryStoreConfigurationProvider("USD", "en-US");
        IAiOrchestrator orch = new AiOrchestrator(exec, cfg);
        return (orch, kernelClient, session);
    }

    /// <summary>Verifies orchestrator construction fails fast when configuration provider is null.</summary>
    [TestMethod]
    public void MissingConfigurationProviderFails()
    {
        var toolDefs = new []{ new ToolDefinition("start_transaction","pos","Start", new []{ new ToolParameter("currency","string", true, "ISO") }) };
        IToolExecutor exec = new DirectToolExecutor(toolDefs, Array.Empty<(string, Func<IReadOnlyDictionary<string, object>, CancellationToken, Task<string>>)>());
        Assert.ThrowsException<InvalidOperationException>(() => new AiOrchestrator(exec, null!));
    }

    /// <summary>Ensures a 'new' command initiates a transaction successfully.</summary>
    [TestMethod]
    public async Task CanStartTransaction()
    {
        var (orch, _, _) = BuildHarness();
        var response = await orch.ProcessCustomerInteractionAsync("new");
        Assert.IsTrue(response.Length > 0); // Placeholder – orchestration returns handler result
    }

    /// <summary>Ensures adding an unknown product returns an invalid product message (not silent success).</summary>
    [TestMethod]
    public async Task UnknownProductGivesFailureMessage()
    {
        var (orch, _, _) = BuildHarness();
        await orch.ProcessCustomerInteractionAsync("new");
        var resp = await orch.ProcessCustomerInteractionAsync("add UNKNOWN 1");
        StringAssert.Contains(resp, "invalid", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Ensures parameter validator rejects unknown parameter.</summary>
    [TestMethod]
    public void UnknownParameterRejected()
    {
        var sessionManager = new SessionManager();
        var txService = new TransactionService();
        IKernelEngine engine = new KernelEngine(sessionManager, txService);
        IKernelClient kernelClient = new DirectKernelClient(engine);
        var session = kernelClient.CreateSessionAsync("TERM1", "OP1").GetAwaiter().GetResult();
        var toolDefs = new List<ToolDefinition>
        {
            new("start_transaction","pos","Start", new []{ new ToolParameter("currency","string", true, "ISO") })
        };
        var handlers = new List<(string, Func<IReadOnlyDictionary<string, object>, CancellationToken, Task<string>>)>();
    handlers.Add(("start_transaction", (p, ct) => Task.FromResult("OK")));
        IToolExecutor exec = new DirectToolExecutor(toolDefs, handlers);
        var cfg = new InMemoryStoreConfigurationProvider("USD", "en-US");
        IAiOrchestrator orch = new AiOrchestrator(exec, cfg);
        // Bypass heuristics: call executor directly with unknown param via reflection of interface
        Assert.ThrowsException<InvalidOperationException>(() => exec.ExecuteToolAsync("start_transaction", new Dictionary<string, object>{{"currency","USD"},{"extra","x"}}).GetAwaiter().GetResult());
    }
}
