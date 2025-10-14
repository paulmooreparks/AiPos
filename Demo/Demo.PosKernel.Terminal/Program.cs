using PosKernel.Client;
using PosKernel.Core.Domain;
using PosKernel.Core.Interfaces;
using PosKernel.Core.Services;
using PosKernel.Extensions.Core;
using PosKernel.Extensions.Configuration;
using PosKernel.Extensions.FoodService; // Food-service store extension (generic data-driven modifiers)
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
FoodServiceModifierRepository? activeModifierRepo = null; // present only for food-service extension (data-driven modifiers)
// Demo-layer modifier tracking: line number -> applied modifier ids (single quantity each for phase 1)
var lineModifiers = new Dictionary<int, List<string>>();

var sessionManager = new SessionManager();
var txService = new TransactionService();
IKernelEngine engine = new KernelEngine(sessionManager, txService, new DefaultPaymentRules());
IKernelClient client = new DirectKernelClient(engine);

Console.WriteLine("Kernel CLI Demo (Store Profiles) - type 'help' for commands. POC; no persistence, no concurrency.");

var sessionId = await client.CreateSessionAsync("TERM1", "OP1");
Console.WriteLine($"Session created: {sessionId}");
TransactionResult? current = null;

// Helper: route ALL monetary display through store currency formatter; never embed symbols/precision assumptions.
string Fmt(decimal amount)
{
	if (activeProfile == null || activeStore == null)
	{
		return amount.ToString(); // Startup phase before store selection; acceptable fallback for diagnostics only
	}
	return activeStore.CurrencyFormatter.FormatCurrency(amount, activeProfile.Currency, activeProfile.Culture);
}

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
								// REQUIRE DATA-DRIVEN EXTENSION: No in-memory demo fallback. Fail fast if no catalog DB available.
								string? resolvedCs = profile.ConnectionString;
								if (string.IsNullOrWhiteSpace(resolvedCs))
								{
									var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
									var derived = Path.Combine(home, ".poskernel", "extensions", "retail", profile.StoreId, "catalog", "retail_catalog.db");
									if (!File.Exists(derived))
									{
										throw new InvalidOperationException(
											$"Store profile '{profile.StoreId}' missing connectionString and derived catalog not found at '{derived}'. " +
											"Provide an explicit connectionString in profile or install the catalog. NO FALLBACK.");
									}
									resolvedCs = $"Data Source={derived}";
								}
								var ext = new FoodServiceStoreExtension(profile.StoreId, resolvedCs);
								activeStore = ext;
								activeModifierRepo = ext.ModifierRepository;
								lineModifiers.Clear();
								Console.WriteLine($"Active store: {profile.DisplayName} ({profile.StoreId}) Currency={profile.Currency} DB={(resolvedCs.Length > 60 ? resolvedCs[..60] + "..." : resolvedCs)} Modifiers={ext.ModifierRepository.AllModifiers.Count}");
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
				current = await client.AddLineItemAsync(sessionId, TxId(), productId, qty, unitPrice, validation.Product.Name, validation.Product.Description, null);
				// Preserve base unit price for later modifier delta calculations (do not rely on mutable UnitPrice which we adjust locally)
				if (current?.Transaction != null && current.Transaction.Lines.Count > 0)
				{
					var lastLine = current.Transaction.Lines[^1];
					if (!lastLine.Metadata.ContainsKey("baseUnit"))
					{
						lastLine.Metadata["baseUnit"] = unitPrice;
					}
				}
				DisplayResult(current);
				break;
			case "apply":
				// ARCHITECTURAL FIX: Modifiers must NOT mutate monetary values locally; kernel owns all calculations.
				RequireTx();
				if (parts.Length < 4) { Console.WriteLine("Usage: apply <line#> add|remove <modId>[,<modId>...]"); break; }
				if (!int.TryParse(parts[1], out var lineNo) || lineNo <= 0) { Console.WriteLine("Line# must be positive."); break; }
				var mode = parts[2].ToLowerInvariant();
				var modsCsv = string.Join(' ', parts.Skip(3));
				var modIds = modsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
				if (activeModifierRepo == null)
				{
					Console.WriteLine("No modifier catalog active for this store.");
					break;
				}
				if (current?.Transaction == null || lineNo > current.Transaction.Lines.Count)
				{
					Console.WriteLine("Line not found.");
					break;
				}
				if (!lineModifiers.TryGetValue(lineNo, out var applied))
				{
					applied = new List<string>();
					lineModifiers[lineNo] = applied;
				}
				if (mode == "add")
				{
					// ARCHITECTURAL PRINCIPLE: Kernel performs all math. We pass per-unit surcharge (or zero) and quantity mirrors parent.
					var tx = current!.Transaction!;
					if (lineNo > tx.Lines.Count) { Console.WriteLine("Line not found."); break; }
					var parent = tx.Lines[lineNo - 1];
					if (string.IsNullOrWhiteSpace(parent.LineItemId))
					{
						Console.WriteLine("Parent line missing stable LineItemId – cannot attach modifiers.");
						break;
					}
					foreach (var m in modIds)
					{
						var def = activeModifierRepo.Get(m);
						if (def == null) { Console.WriteLine($"Unknown modifier {m}"); continue; }
						if (applied.Contains(def.Id, StringComparer.OrdinalIgnoreCase))
						{
							Console.WriteLine($"Modifier {def.Id} already on line {lineNo}");
							continue;
						}
						// Quantity mirrors parent (policy B)
						var perUnit = def.AdjustmentKind == PriceAdjustmentKind.Surcharge ? def.Value : 0m;
						var addResult = await client.AddLineItemAsync(sessionId, TxId(), def.Id, parent.Quantity, perUnit, def.Name, def.Name, parent.LineItemId);
						if (!addResult.Success)
						{
							Console.WriteLine($"Failed adding modifier {def.Id}: {string.Join(';', addResult.Errors)}");
							continue;
						}
						current = addResult; // advance snapshot
						applied.Add(def.Id);
						Console.WriteLine($"Attached modifier {def.Id} ({def.Name}) as linked line to parent line {lineNo} (unit {perUnit})");
					}
				}
				else if (mode == "remove")
				{
					// Removal: auto-cascade void of child modifier lines using kernel void API
					var tx = current!.Transaction!;
					foreach (var m in modIds)
					{
						var idx = applied.FindIndex(x => x.Equals(m, StringComparison.OrdinalIgnoreCase));
						if (idx < 0) { Console.WriteLine($"Modifier {m} not on line {lineNo}"); continue; }
						// Find modifier child line(s) by ProductId and parent linkage
						var parent = tx.Lines[lineNo - 1];
						var children = tx.Lines.Where(l => !l.IsVoided && l.ParentLineItemId == parent.LineItemId && l.ProductIdString.Equals(m, StringComparison.OrdinalIgnoreCase)).ToList();
						if (children.Count == 0)
						{
							Console.WriteLine($"Modifier line for {m} not found (already removed?)");
							applied.RemoveAt(idx);
							continue;
						}
						foreach (var child in children)
						{
							var voidResult = await client.VoidLineItemAsync(sessionId, TxId(), child.LineItemId, "Modifier removed");
							if (!voidResult.Success)
							{
								Console.WriteLine($"Failed to void modifier {m}: {string.Join(';', voidResult.Errors)}");
							}
							else
							{
								current = voidResult;
								Console.WriteLine($"Voided modifier {m} from line {lineNo}");
							}
						}
						applied.RemoveAt(idx);
					}
				}
				else { Console.WriteLine("Mode must be add or remove."); }
				// FUTURE: send modifier delta to kernel once modifier pricing API exists.
				break;
			case "mods":
				EnsureStore();
				if (activeModifierRepo == null)
				{
					Console.WriteLine("No modifier catalog for active store.");
					break;
				}
				// Usage:
				//   mods [filter]              -> search all modifiers
				//   mods for <line#> [filter]  -> list only modifiers applicable to product at line#
				bool forLine = false;
				int targetLine = -1;
				List<string> argList = parts.Skip(1).ToList();
				if (argList.Count > 1 && string.Equals(argList[0], "for", StringComparison.OrdinalIgnoreCase))
				{
					forLine = true;
					if (!int.TryParse(argList[1], out targetLine) || targetLine <= 0)
					{
						Console.WriteLine("Usage: mods for <line#> [filter]");
						break;
					}
					argList = argList.Skip(2).ToList();
				}
				var term = argList.Count > 0 ? string.Join(' ', argList) : string.Empty;
				IReadOnlyList<FoodServiceModifier> mods;
				if (forLine)
				{
					if (current?.Transaction == null || targetLine > current.Transaction.Lines.Count)
					{
						Console.WriteLine("Line not found.");
						break;
					}
					var line = current.Transaction.Lines[targetLine - 1];
					// Attempt to filter to only applicable modifiers for that product SKU (line.ProductName used as fallback SKU surrogate if needed)
					var sku = line.ProductName; // NOTE: Real implementation should carry explicit SKU separate from display name.
					mods = string.IsNullOrWhiteSpace(term)
						? activeModifierRepo.AllModifiers.Where(m => activeModifierRepo.IsApplicable(sku, m.Id)).OrderBy(m=>m.DisplayOrder).ToList()
						: activeModifierRepo.AllModifiers.Where(m => (m.Id.Contains(term, StringComparison.OrdinalIgnoreCase) || m.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) && activeModifierRepo.IsApplicable(sku, m.Id)).OrderBy(m=>m.DisplayOrder).ToList();
					Console.WriteLine($"Applicable Modifiers for line {targetLine} ({sku}) ({mods.Count}){(string.IsNullOrWhiteSpace(term) ? string.Empty : " filtered")}: ");
				}
				else
				{
					mods = string.IsNullOrWhiteSpace(term)
						? activeModifierRepo.AllModifiers.OrderBy(m=>m.DisplayOrder).ToList()
						: activeModifierRepo.AllModifiers.Where(m => m.Id.Contains(term, StringComparison.OrdinalIgnoreCase) || m.Name.Contains(term, StringComparison.OrdinalIgnoreCase)).OrderBy(m=>m.DisplayOrder).ToList();
					Console.WriteLine($"Modifiers ({mods.Count}){(string.IsNullOrWhiteSpace(term) ? string.Empty : " filtered")}: ");
				}
				foreach (var m in mods)
				{
					var price = m.AdjustmentKind == PriceAdjustmentKind.Surcharge ? $"+{m.Value}" : "0";
					Console.WriteLine($"  {m.Id,-18} {m.Name,-25} {m.GroupCode,-12} {m.AdjustmentKind,-9} {price}");
				}
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
			case "report":
				RequireTx();
				if (current?.Transaction == null) { Console.WriteLine("No transaction."); break; }
				TransactionReport();
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
	Console.WriteLine($"State: {tx.State}  Lines: {tx.Lines.Count}  Total: {Fmt(tx.Total.Amount)}  Tendered: {Fmt(tx.Tendered.Amount)} Change: {Fmt(tx.ChangeDue.Amount)}");
	// Render lines hierarchically (indent based on DisplayIndentLevel)
	for (int i = 0; i < tx.Lines.Count; i++)
	{
	    var l = tx.Lines[i];
	    var indent = new string(' ', l.DisplayIndentLevel * 2);
	    Console.WriteLine($"  {i+1}. {indent}{l.ProductName} x{l.Quantity} @ {Fmt(l.UnitPrice.Amount)} = {Fmt(l.Extended.Amount)}");
	}
}

// REMOVED LOCAL PRICE RECALCULATION: Kernel must be sole source of financial truth.

void TransactionReport()
{
	var tx = current!.Transaction!;
	Console.WriteLine("================ TRANSACTION REPORT ================");
	Console.WriteLine($"Transaction: {tx.Id}  State: {tx.State}  Currency: {tx.Currency}");
	for (int i = 0; i < tx.Lines.Count; i++)
	{
	    var line = tx.Lines[i];
	    if (line.IsVoided) { continue; }
	    var indent = new string(' ', line.DisplayIndentLevel * 2);
	    Console.WriteLine($"{i+1,2}. {indent}{line.ProductName} x{line.Quantity} unit {Fmt(line.UnitPrice.Amount)} line {Fmt(line.Extended.Amount)}");
	}
	Console.WriteLine("----------------------------------------------------");
	// ARCHITECTURAL PRINCIPLE: Do NOT recompute financial aggregates in the client. Trust kernel fields.
	Console.WriteLine($"Merchandise Total:  {Fmt(tx.Total.Amount)}");
	Console.WriteLine($"Tendered:           {Fmt(tx.Tendered.Amount)}");
	Console.WriteLine($"Change Due:         {Fmt(tx.ChangeDue.Amount)}");
	Console.WriteLine($"Balance Due:        {Fmt(tx.BalanceDue.Amount)}");
	if (tx.State == TransactionState.EndOfTransaction && tx.BalanceDue.Amount != 0m)
	{
		Console.WriteLine("INTEGRITY ERROR: Non-zero balance on closed transaction (client detected). Kernel should have prevented this.");
	}
	Console.WriteLine("====================================================");
}

void PrintHelp()
{
	Console.WriteLine("Commands:");
	Console.WriteLine("  stores                        - list store profiles");
	Console.WriteLine("  active                        - show active store profile");
	Console.WriteLine("  reload                        - reload profiles from configuration");
	Console.WriteLine("  use <storeId>                 - activate store profile");
	Console.WriteLine("  items [filter words] [limit]  - list items (no limit by default; optional numeric limit last token)");
	Console.WriteLine("  mods [filter]                 - list modifiers (store extension provided)");
	Console.WriteLine("  payments                      - list payment types");
	Console.WriteLine("  new                           - start new transaction");
	Console.WriteLine("  add <productId> [qty]         - add item");
	Console.WriteLine("  apply <line#> add|remove <mods> - apply/remove modifiers");
	Console.WriteLine("  void <line#>                  - void line (placeholder)");
	Console.WriteLine("  voidtx                        - void transaction (placeholder)");
	Console.WriteLine("  pay <amount> <paymentType>    - tender payment");
	Console.WriteLine("  show                          - show current transaction");
	Console.WriteLine("  report                        - detailed transaction report (incl. modifiers)");
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
		Console.WriteLine($"  {p.Sku,-15} {p.Name} @ {Fmt(p.BasePrice)}");
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

// In-memory demo extension removed – all operations require data-driven store extension (fail-fast if unavailable).
// (Helper relocated above type declarations to satisfy top-level ordering requirements)

// Support types for Phase 0.5 (would move to separate files in non-demo code)
// Records moved to StoreProfiles/Models.cs (public) for loader accessibility.

