using PosKernel.Client;
using PosKernel.Core.Domain;
using PosKernel.Core.Interfaces;
using PosKernel.Core.Services;
using PosKernel.Extensions.Core;
using PosKernel.Extensions.Configuration;
using PosKernel.Extensions.FoodService; // Use external FoodService extension
using PosKernel.Extensions.Core.Profiles;

// ARCHITECTURAL PRINCIPLE: Demo remains culture-neutral; store specific logic isolated in extension implementation.

// Phase 1 refactor: Use XferStoreProfileProvider from Extensions.Configuration (parsing & validation isolated from demo)
IStoreProfileProvider profileProvider;
try
{
	profileProvider = XferStoreProfileProvider.FromDefaultLocation();
}
catch (Exception ex)
{
	Console.WriteLine("FATAL: profile load failure: " + ex.Message);
	Environment.ExitCode = 2;
	return;
}
var profiles = profileProvider.GetAll();
if (profiles.Count == 0)
{
	Console.WriteLine("FATAL: zero store profiles discovered.");
	Environment.ExitCode = 3;
	return;
}

StoreProfile? activeProfile = null;
IStoreExtension? activeStore = null; // generalized to interface to support multiple store extension implementations

var sessionManager = new SessionManager();
var txService = new TransactionService();
IKernelEngine engine = new KernelEngine(sessionManager, txService);
IKernelClient client = new DirectKernelClient(engine);

Console.WriteLine("Kernel CLI Demo (Store Profiles) - type 'help' for commands. POC; no persistence, no concurrency.");

var sessionId = await client.CreateSessionAsync("TERM1", "OP1");
Console.WriteLine($"Session created: {sessionId}");
TransactionResult? current = null;

while (true)
{
	Console.Write("pos> ");
	var input = Console.ReadLine();
	if (input == null) { continue; }
	var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
	if (parts.Length == 0) { continue; }
	var cmd = parts[0].ToLowerInvariant();
	try
	{
			switch (cmd)
		{
			case "help":
				PrintHelp();
				break;
			case "stores":
				ListStores();
				break;
			case "profiles":
				if (parts.Length > 1 && parts[1].Equals("list", StringComparison.OrdinalIgnoreCase))
				{
					ListStores();
				}
				else
				{
					Console.WriteLine("Usage: profiles list");
				}
				break;
			case "active":
				if (activeProfile == null) { Console.WriteLine("No active store."); break; }
				Console.WriteLine($"Active: {activeProfile.StoreId} -> {activeProfile.DisplayName} ({activeProfile.Currency} {activeProfile.Culture})");
				break;
			case "reload":
				try
				{
					profileProvider.Reload();
					Console.WriteLine($"Profiles reloaded: {profileProvider.GetAll().Count} loaded @ {(profileProvider as PosKernel.Extensions.Configuration.XferStoreProfileProvider)?.LastReloadUtc:O}");
					// Re-select active profile reference if still present
					if (activeProfile != null)
					{
						var updated = profileProvider.GetAll().FirstOrDefault(p => p.StoreId == activeProfile.StoreId);
						if (updated == null)
						{
							Console.WriteLine("Previously active store no longer available.");
							activeProfile = null; activeStore = null; current = null;
						}
						else
						{
							activeProfile = updated;
							Console.WriteLine($"Active store preserved: {activeProfile.StoreId}");
						}
					}
				}
				catch (Exception rex)
				{
					Console.WriteLine("Reload failed: " + rex.Message);
				}
				break;
			case "use":
				if (parts.Length < 2) { Console.WriteLine("Usage: use <storeId>"); break; }
				var id = parts[1];
				var profile = profiles.FirstOrDefault(p => p.StoreId.Equals(id, StringComparison.OrdinalIgnoreCase));
				if (profile == null) { Console.WriteLine("Store not found."); break; }
				if (current?.Transaction != null && current.Transaction.State is TransactionState.ItemsPending or TransactionState.StartTransaction)
				{
					Console.WriteLine("Cannot switch store with open transaction. Complete or void it first.");
					break;
				}
				activeProfile = profile;
				// Simple selection: if storeId indicates kopitiam, instantiate Kopitiam sqlite-backed extension, else demo in-memory
				if (profile.StoreId.Contains("kopitiam", StringComparison.OrdinalIgnoreCase))
				{
					// ARCHITECTURAL PRINCIPLE: Store profile may supply connection string; demo derives default if absent (temporary convenience).
					// Future: remove derivation and require explicit configuration to avoid hidden coupling.
					// Prefer explicit connection string from profile if provided, else derive path.
					string? cs = profile.ConnectionString;
					if (string.IsNullOrWhiteSpace(cs))
					{
						// Derive default path (legacy behavior) to avoid breaking existing local setups.
						var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
						var derived = Path.Combine(home, ".poskernel", "extensions", "retail", profile.StoreId, "catalog", "retail_catalog.db");
						cs = $"Data Source={derived}";
					}
					activeStore = new KopitiamStoreExtension(profile.StoreId, cs);
					Console.WriteLine($"Active store (kopitiam): {profile.DisplayName} ({profile.StoreId}) Currency={profile.Currency} DB={(cs.Length > 60 ? cs[..60] + "..." : cs)}");
				}
				else
				{
					activeStore = new DemoStoreExtension(profile.Currency);
					Console.WriteLine($"Active store: {profile.DisplayName} ({profile.StoreId}) Currency={profile.Currency}");
				}
				break;
			case "new":
				EnsureStore();
				current = await client.StartTransactionAsync(sessionId, activeProfile!.Currency);
				DisplayResult(current);
				break;
			case "add":
				RequireTx();
				if (parts.Length < 2) { Console.WriteLine("Usage: add <productId> [qty]"); break; }
				var productId = parts[1];
				int qty = 1;
				if (parts.Length >= 3 && (!int.TryParse(parts[2], out qty) || qty <= 0)) { Console.WriteLine("Quantity must be positive integer."); break; }
				var validation = await activeStore!.Catalog.ValidateProductAsync(productId);
				if (!validation.IsValid || validation.Product == null)
				{
					Console.WriteLine($"Product invalid: {validation.ErrorMessage}");
					break;
				}
				var unitPrice = validation.Product.BasePrice;
				current = await client.AddLineItemAsync(sessionId, TxId(), productId, qty, unitPrice, validation.Product.Name, validation.Product.Description);
				DisplayResult(current);
				break;
			case "apply":
				RequireTx();
				if (parts.Length < 4) { Console.WriteLine("Usage: apply <line#> add|remove <modId>[,<modId>...]"); break; }
				if (!int.TryParse(parts[1], out var lineNo) || lineNo <= 0) { Console.WriteLine("Line# must be positive."); break; }
				var mode = parts[2].ToLowerInvariant();
				var modsCsv = string.Join(' ', parts.Skip(3));
				var modIds = modsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
				// Demo: we do not actually mutate modifiers (not yet implemented). Placeholder for Phase 2 logic.
				Console.WriteLine($"(mods {mode}) pending implementation: {string.Join(',', modIds)} on line {lineNo}");
				break;
			case "mods":
				EnsureStore();
				if (parts.Length < 2) { Console.WriteLine("Usage: mods <productId>"); break; }
				Console.WriteLine("(modifier listing placeholder – no modifier catalog in demo)");
				break;
			case "items":
				EnsureStore();
				// Syntax: items [filter words ...] [limit]
				// If the last token parses as an int, treat it as limit. Everything before is filter phrase.
				int? limit = null;
				if (parts.Length > 1)
				{
					var tail = parts[^1];
					if (int.TryParse(tail, out var l) && l > 0)
					{
						limit = l;
					}
				}
				string filter;
				if (parts.Length == 1)
				{
					filter = string.Empty;
				}
				else if (limit.HasValue && parts.Length > 2)
				{
					filter = string.Join(' ', parts.Skip(1).Take(parts.Length - 2));
				}
				else if (limit.HasValue && parts.Length == 2)
				{
					filter = string.Empty; // only limit specified
				}
				else
				{
					filter = string.Join(' ', parts.Skip(1));
				}
				await ListItems(filter, limit);
				break;
			case "payments":
				EnsureStore();
				ListPayments();
				break;
			case "void":
				RequireTx();
				if (parts.Length < 2) { Console.WriteLine("Usage: void <line#>"); break; }
				Console.WriteLine("(void line not yet implemented in kernel demo)");
				break;
			case "voidtx":
				RequireTx();
				Console.WriteLine("(void transaction not yet implemented in kernel demo)");
				break;
			case "pay":
				RequireTx();
				if (parts.Length < 3) { Console.WriteLine("Usage: pay <amount> <paymentType>"); break; }
				if (!decimal.TryParse(parts[1], out var amt) || amt < 0) { Console.WriteLine("Amount must be non-negative decimal."); break; }
				var payType = parts[2].ToLowerInvariant();
				EnsureStore();
				var pt = activeProfile!.PaymentTypes.FirstOrDefault(p => p.Id.Equals(payType, StringComparison.OrdinalIgnoreCase));
				if (pt == null) { Console.WriteLine("Unknown payment type. Use 'payments'."); break; }
				// Basic disallow-change logic: if payment disallows change and amount > due, reject.
				var due = current!.Transaction!.Total.Amount - current.Transaction.Tendered.Amount;
				if (!pt.AllowsChange && amt > due)
				{
					Console.WriteLine($"Payment type '{pt.Id}' disallows change. Amount must not exceed due {due}.");
					break;
				}
				if (pt.RequiresExact && amt != due)
				{
					Console.WriteLine($"Payment type '{pt.Id}' requires exact amount {due}.");
					break;
				}
				current = await client.ProcessPaymentAsync(sessionId, TxId(), amt, pt.Id);
				DisplayResult(current);
				break;
			case "show":
				RequireTx();
				current = await client.GetTransactionAsync(sessionId, TxId());
				DisplayResult(current);
				break;
			case "quit":
			case "exit":
				await client.CloseSessionAsync(sessionId);
				Console.WriteLine("Session closed. Bye.");
				Environment.ExitCode = 0;
				return;
			default:
				Console.WriteLine("Unknown command. Type 'help'.");
				break;
		}
	}
	catch (Exception ex)
	{
		Console.WriteLine($"ERROR: {ex.Message}");
	}
}

void RequireTx()
{
	if (current?.Transaction == null)
	{
		throw new InvalidOperationException("No active transaction. Use 'new'.");
	}
}

string TxId() => current!.Transaction!.Id.ToString();

void DisplayResult(TransactionResult? result)
{
	if (result == null)
	{
		Console.WriteLine("(null result)");
		return;
	}
	if (!result.Success)
	{
		Console.WriteLine("FAILED: " + string.Join("; ", result.Errors));
		return;
	}
	var tx = result.Transaction!;
	Console.WriteLine($"State: {tx.State}  Lines: {tx.Lines.Count}  Total: {tx.Total.Amount} {tx.Currency}  Tendered: {tx.Tendered.Amount} Change: {tx.ChangeDue.Amount}");
	for (int i = 0; i < tx.Lines.Count; i++)
	{
		var l = tx.Lines[i];
		Console.WriteLine($"  {i+1}. {l.ProductName} x{l.Quantity} @ {l.UnitPrice.Amount} = {l.Extended.Amount}");
	}
}

void PrintHelp()
{
	Console.WriteLine("Commands:");
	Console.WriteLine("  stores                        - list store profiles");
	Console.WriteLine("  active                        - show active store profile");
	Console.WriteLine("  reload                        - reload profiles from configuration");
	Console.WriteLine("  use <storeId>                 - activate store profile");
	Console.WriteLine("  items [filter words] [limit]  - list items (no limit by default; optional numeric limit last token)");
	Console.WriteLine("  mods <productId>              - list modifiers (placeholder)");
	Console.WriteLine("  payments                      - list payment types");
	Console.WriteLine("  new                           - start new transaction");
	Console.WriteLine("  add <productId> [qty]         - add item");
	Console.WriteLine("  apply <line#> add|remove <mods> - apply/remove modifiers (placeholder)");
	Console.WriteLine("  void <line#>                  - void line (placeholder)");
	Console.WriteLine("  voidtx                        - void transaction (placeholder)");
	Console.WriteLine("  pay <amount> <paymentType>    - tender payment");
	Console.WriteLine("  show                          - show current transaction");
	Console.WriteLine("  quit|exit                     - close session and exit");
}

void ListStores()
{
	Console.WriteLine("Stores (configuration-driven):");
	foreach (var p in profiles)
	{
		var active = activeProfile != null && p.StoreId == activeProfile.StoreId ? "*" : " ";
		Console.WriteLine($" {active} {p.StoreId,-12} {p.DisplayName} ({p.Currency} {p.Culture}) payments=[{string.Join(',', p.PaymentTypes.Select(pt => pt.Id))}]");
	}
}

async Task ListItems(string filter, int? limit)
{
	var catalog = activeStore!.Catalog;
	IReadOnlyList<ProductInfo> list;
	var searchTerm = string.Equals(filter, "all", StringComparison.OrdinalIgnoreCase) ? string.Empty : filter;
	// Unlimited: pass very large number when no limit specified (demo-safe). Avoid INT.MaxValue to prevent provider overflow risk.
	var max = limit ?? 10000; // explicit or generous default
	list = await catalog.SearchProductsAsync(searchTerm, max);
	Console.WriteLine("Items:");
	foreach (var p in list)
	{
		Console.WriteLine($"  {p.Sku,-15} {p.Name} @ {p.BasePrice} {activeProfile!.Currency}");
	}
}

void ListPayments()
{
	Console.WriteLine("Payment Types:");
	foreach (var pt in activeProfile!.PaymentTypes)
	{
		Console.WriteLine($"  {pt.Id,-12} change={(pt.AllowsChange ? "yes" : "no")}, exact={(pt.RequiresExact ? "yes" : "no")}");
	}
}

void EnsureStore()
{
	if (activeProfile == null || activeStore == null)
	{
		throw new InvalidOperationException("No active store selected. Use 'stores' then 'use <storeId>'.");
	}
}

sealed class DemoStoreExtension : IStoreExtension
{
	public IProductCatalog Catalog { get; }
	public IModificationService Modifications { get; } = new DemoMods();
	public ICurrencyFormatter CurrencyFormatter { get; } = new DemoCurrencyFormatter();

	public DemoStoreExtension(string currency = "USD")
	{
		Catalog = new DemoCatalog();
	}
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
		var list = _products.Where(p => p.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) || p.Sku.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)).Take(maxResults).ToList();
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
	public string FormatCurrency(decimal amount, string currency, string culture) => $"{currency} {amount}"; // Demo only – no localization
	public string GetCurrencySymbol(string currency) => currency;
	public int GetDecimalPlaces(string currency) => 2; // Demo assumption
}

// Support types for Phase 0.5 (would move to separate files in non-demo code)
// Records moved to StoreProfiles/Models.cs (public) for loader accessibility.

