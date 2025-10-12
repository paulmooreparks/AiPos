using System.Globalization;
using AiPos.Agentic;
using AiPos.Core;
using AiPos.Orchestrator;
using PosKernel.Client;
using PosKernel.Core.Domain;
using PosKernel.Core.Interfaces;
using PosKernel.Core.Services;
using PosKernel.Extensions.Core;
using Terminal.Gui;
using Microsoft.Data.Sqlite;

// ARCHITECTURAL PRINCIPLE: UI layer only. All business logic stays in kernel + orchestrator + extensions.
// We replicate only layout, interaction flow, and color scheme (simplified) from old TUI.

namespace Demo.AiPos.Tui;

internal static class Program
{
	private static IStoreExtension _store = default!; // fail-fast usage after init
	private static IKernelClient _kernelClient = default!;
	private static string _sessionId = string.Empty;
	private static TransactionResult? _current;
	private static IAgenticServer _server = default!; // Single-call processing host

	// UI components
	private static TextView _chatView = null!;
	private static TextView _logView = null!;
	private static TextView _receiptView = null!;
	private static TextField _input = null!;

	public static async Task<int> Main(string[] args)
	{
		try
		{
			InitStore();
			InitKernel();
			var root = BuildUi();
			Application.Run(root);
			await _kernelClient.CloseSessionAsync(_sessionId);
			Application.Shutdown();
			return 0;
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine("FATAL: " + ex.Message);
			return 1;
		}
	}

	// --- Initialization ---
	private static void InitStore()
	{
		var sqlitePath = Environment.GetEnvironmentVariable("AIPOS_CATALOG_DB");
		if (!string.IsNullOrWhiteSpace(sqlitePath))
		{
			if (!File.Exists(sqlitePath))
			{
				throw new InvalidOperationException($"AIPOS_CATALOG_DB='{sqlitePath}' not found. Provide valid path or unset env var.");
			}
			_store = new SqliteStoreExtension(sqlitePath);
		}
		else
		{
			_store = new DemoStoreExtension();
		}
	}

	private static void InitKernel()
	{
		var sessionManager = new SessionManager();
		var txService = new TransactionService();
		IKernelEngine engine = new KernelEngine(sessionManager, txService);
		_kernelClient = new DirectKernelClient(engine);
		_sessionId = _kernelClient.CreateSessionAsync("TUI", "OP1").GetAwaiter().GetResult();

		// Tools / handlers
		var toolDefs = new List<ToolDefinition>
		{
			new("start_transaction","pos","Start new transaction", new[]{ new ToolParameter("currency","string",true,"ISO 4217 currency") }),
			new("add_item","pos","Add line item", new[]{ new ToolParameter("productId","string",true,"SKU"), new ToolParameter("quantity","int",true,"Qty") }),
			new("pay","pos","Process payment", new[]{ new ToolParameter("amount","decimal",true,"Amount tendered") }),
			new("show","pos","Show transaction", Array.Empty<ToolParameter>())
		};

		var handlers = new List<(string, Func<IReadOnlyDictionary<string, object>, CancellationToken, Task<string>>)> {
			("start_transaction", async (p, ct) => {
				var currency = (string)p["currency"];
				_current = await _kernelClient.StartTransactionAsync(_sessionId, currency, ct);
				return Render();
			}),
			("add_item", async (p, ct) => {
				EnsureActive();
				var sku = (string)p["productId"]; var qty = Convert.ToInt32(p["quantity"], CultureInfo.InvariantCulture);
				var val = await _store.Catalog.ValidateProductAsync(sku, ct);
				if (!val.IsValid || val.Product == null) { return $"Invalid product: {val.ErrorMessage}"; }
				var unit = val.Product.BasePrice;
				_current = await _kernelClient.AddLineItemAsync(_sessionId, TxId(), sku, qty, unit, val.Product.Name, val.Product.Description, ct);
				return Render();
			}),
			("pay", async (p, ct) => {
				EnsureActive();
				var amount = Convert.ToDecimal(p["amount"], CultureInfo.InvariantCulture);
				_current = await _kernelClient.ProcessPaymentAsync(_sessionId, TxId(), amount, "cash", ct);
				return Render();
			}),
			("show", async (p, ct) => { EnsureActive(); _current = await _kernelClient.GetTransactionAsync(_sessionId, TxId(), ct); return Render(); })
		};
		IToolExecutor executor = new DirectToolExecutor(toolDefs, handlers);
		var config = new InMemoryStoreConfigurationProvider("USD", "en-US"); // FIXME: derive from store selection in future version
		var orchestrator = new AiOrchestrator(executor, config);
		_server = new AgenticServerHost(orchestrator);
	}

	private static Toplevel BuildUi()
	{
		Application.Init();
		var root = new Toplevel();
		var scheme = new ColorScheme
		{
			Normal = new Terminal.Gui.Attribute(Color.White, Color.Black),
			HotNormal = new Terminal.Gui.Attribute(Color.BrightYellow, Color.Black),
			Focus = new Terminal.Gui.Attribute(Color.Black, Color.Gray)
		};
		root.ColorScheme = scheme;

		var chatFrame = new FrameView { Title = "Chat", X = 0, Y = 0, Width = Dim.Percent(60), Height = Dim.Fill(3) };
		var receiptFrame = new FrameView { Title = "Receipt", X = Pos.Right(chatFrame), Y = 0, Width = Dim.Fill(), Height = Dim.Percent(55) };
		var logFrame = new FrameView { Title = "Logs", X = Pos.Right(chatFrame), Y = Pos.Bottom(receiptFrame), Width = Dim.Fill(), Height = Dim.Fill(3) };
		var inputFrame = new FrameView { Title = "Input", X = 0, Y = Pos.AnchorEnd(3), Width = Dim.Fill(), Height = 3 };

		_chatView = new TextView { ReadOnly = true, WordWrap = true, X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
		_receiptView = new TextView { ReadOnly = true, X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
		_logView = new TextView { ReadOnly = true, X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
		_input = new TextField { Text = string.Empty, X = 1, Y = 0, Width = Dim.Fill(1) };

		chatFrame.Add(_chatView);
		receiptFrame.Add(_receiptView);
		logFrame.Add(_logView);
		inputFrame.Add(_input);

		_input.KeyDown += async (sender, e) =>
		{
			if (e.KeyCode == KeyCode.Enter)
			{
				var text = _input.Text?.ToString()?.Trim();
				_input.Text = string.Empty;
				if (!string.IsNullOrWhiteSpace(text))
				{
					AppendChat($"You: {text}");
					try
					{
						var response = await _server.HandleAsync(text);
						AppendChat($"AI: {response}");
						UpdateReceipt();
					}
					catch (Exception ex)
					{
						AppendLog("ERROR: " + ex.Message);
					}
				}
				e.Handled = true;
			}
		};

		root.Add(chatFrame, receiptFrame, logFrame, inputFrame);
		AppendLog("Session started: " + _sessionId);
		AppendChat("System: Type 'new' to start, 'add <SKU> <qty>', 'pay <amount>'");
		return root;
	}

	// --- Helpers ---
	private static void SafeUi(Action act)
	{
		// Before Application.Run, Application.Top may be null; invoke directly.
		if (Application.Top == null)
		{
			act();
		}
		else
		{
			Application.Invoke(act);
		}
	}
	private static void AppendChat(string line) => SafeUi(() => { _chatView.Text += line + Environment.NewLine; });
	private static void AppendLog(string line) => SafeUi(() => { _logView.Text += line + Environment.NewLine; });
	private static void UpdateReceipt() => SafeUi(() => { _receiptView.Text = Render(); });

	private static void EnsureActive()
	{
		if (_current?.Transaction == null)
		{
			throw new InvalidOperationException("No active transaction. Use 'start_transaction' (type 'new').");
		}
	}
	private static string TxId() => _current!.Transaction!.Id.ToString();

	private static string Render()
	{
		if (_current == null) { return "(null)"; }
		if (!_current.Success) { return "FAIL: " + string.Join("; ", _current.Errors); }
		var tx = _current.Transaction!;
		var fmt = _store.CurrencyFormatter;
		string F(decimal amt) => fmt.FormatCurrency(amt, tx.Currency, "en-US"); // TODO culture service
		var sb = new System.Text.StringBuilder();
		sb.AppendLine($"State: {tx.State}");
		sb.AppendLine($"Lines: {tx.Lines.Count} Total: {F(tx.Total.Amount)} Tendered: {F(tx.Tendered.Amount)} Change: {F(tx.ChangeDue.Amount)}");
		for (int i = 0; i < tx.Lines.Count; i++)
		{
			var l = tx.Lines[i];
			sb.AppendLine($"{i + 1}. {l.ProductName} x{l.Quantity} @{F(l.UnitPrice.Amount)} = {F(l.Extended.Amount)}");
		}
		return sb.ToString();
	}
}

// --- Store Extension Implementations (minimal) ---
file sealed class DemoStoreExtension : IStoreExtension
{
	public IProductCatalog Catalog { get; } = new DemoCatalog();
	public IModificationService Modifications { get; } = new DemoMods();
	public ICurrencyFormatter CurrencyFormatter { get; } = new DemoCurrencyFormatter();
}

file sealed class DemoCatalog : IProductCatalog
{
	private static readonly List<ProductInfo> _products = new()
	{
		new ProductInfo { Sku = "COFFEE.SMALL", Name = "Small Coffee", Description = "Fresh brew", Category = "DRINK", BasePrice = 3.50m },
		new ProductInfo { Sku = "COFFEE.LARGE", Name = "Large Coffee", Description = "Fresh brew large", Category = "DRINK", BasePrice = 4.50m },
		new ProductInfo { Sku = "TEA.BLACK", Name = "Black Tea", Description = "Hot tea", Category = "DRINK", BasePrice = 2.25m },
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
	public Task<IReadOnlyList<ProductInfo>> GetPopularItemsAsync(CancellationToken cancellationToken = default) => Task.FromResult((IReadOnlyList<ProductInfo>)_products.Take(3).ToList());
}

file sealed class DemoMods : IModificationService
{
	public Task<ModificationValidationResult> ValidateModificationsAsync(string productId, IReadOnlyList<ModificationSelection> selections, CancellationToken cancellationToken = default) => Task.FromResult(new ModificationValidationResult { IsValid = true, TotalExtraPrice = 0m });
	public Task<decimal> CalculateModificationTotalAsync(IReadOnlyList<ModificationSelection> selections, CancellationToken cancellationToken = default) => Task.FromResult(0m);
}

file sealed class DemoCurrencyFormatter : ICurrencyFormatter
{
	public string FormatCurrency(decimal amount, string currency, string culture) => $"{currency} {amount}"; // Placeholder: replace with real culture-specific service
	public string GetCurrencySymbol(string currency) => currency;
	public int GetDecimalPlaces(string currency) => 2;
}

file sealed class SqliteStoreExtension : IStoreExtension
{
	public IProductCatalog Catalog { get; }
	public IModificationService Modifications { get; } = new DemoMods();
	public ICurrencyFormatter CurrencyFormatter { get; } = new DemoCurrencyFormatter();
	public SqliteStoreExtension(string dbPath) { Catalog = new SqliteCatalog(dbPath); }
}

file sealed class SqliteCatalog : IProductCatalog
{
	private readonly string _dbPath;
	public SqliteCatalog(string dbPath)
	{
		_dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
		if (!File.Exists(_dbPath)) { throw new InvalidOperationException($"SQLite catalog not found at '{_dbPath}'"); }
	}
	public async Task<ProductValidationResult> ValidateProductAsync(string productId, CancellationToken cancellationToken = default)
	{
		await using var conn = new SqliteConnection($"Data Source={_dbPath}");
		await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using var cmd = conn.CreateCommand();
		cmd.CommandText = @"SELECT sku, name, description, category, base_price, is_active FROM products WHERE sku=@id LIMIT 1";
		cmd.Parameters.AddWithValue("@id", productId);
		await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		if (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			if (!r.GetBoolean(5)) { return new ProductValidationResult { IsValid = false, ErrorMessage = "Inactive product" }; }
			var info = new ProductInfo {
				Sku = r.GetString(0), Name = r.GetString(1), Description = r.IsDBNull(2)? string.Empty: r.GetString(2), Category = r.GetString(3), BasePrice = r.GetDecimal(4), IsActive = true };
			return new ProductValidationResult { IsValid = true, Product = info, EffectivePrice = info.BasePrice };
		}
		return new ProductValidationResult { IsValid = false, ErrorMessage = "Not found" };
	}
	public async Task<IReadOnlyList<ProductInfo>> SearchProductsAsync(string searchTerm, int maxResults = 50, CancellationToken cancellationToken = default)
	{
		await using var conn = new SqliteConnection($"Data Source={_dbPath}");
		await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using var cmd = conn.CreateCommand();
		if (string.IsNullOrWhiteSpace(searchTerm))
		{
			cmd.CommandText = @"SELECT sku,name,description,category,base_price,is_active FROM products WHERE is_active=1 ORDER BY name LIMIT @max";
		}
		else
		{
			cmd.CommandText = @"SELECT sku,name,description,category,base_price,is_active FROM products WHERE is_active=1 AND (sku LIKE '%'||@q||'%' OR name LIKE '%'||@q||'%') ORDER BY name LIMIT @max";
			cmd.Parameters.AddWithValue("@q", searchTerm);
		}
		cmd.Parameters.AddWithValue("@max", maxResults);
		var list = new List<ProductInfo>();
		await using var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await r.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			if (!r.GetBoolean(5)) { continue; }
			list.Add(new ProductInfo { Sku = r.GetString(0), Name = r.GetString(1), Description = r.IsDBNull(2)? string.Empty: r.GetString(2), Category = r.GetString(3), BasePrice = r.GetDecimal(4), IsActive = true });
		}
		return list;
	}
	public Task<IReadOnlyList<ProductInfo>> GetPopularItemsAsync(CancellationToken cancellationToken = default) => SearchProductsAsync(string.Empty, 10, cancellationToken);
}
