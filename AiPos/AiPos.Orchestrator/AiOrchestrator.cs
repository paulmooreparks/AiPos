using AiPos.Core;

namespace AiPos.Orchestrator;

/// <summary>
/// Heuristic single-call orchestrator stub: extremely simple keyword routing. Replaced later by LLM.
/// </summary>
public sealed class AiOrchestrator : IAiOrchestrator
{
	private readonly IToolExecutor _tools;
	private readonly IStoreConfigurationProvider _config;

	/// <summary>
	/// Initializes a new instance of the <see cref="AiOrchestrator"/>.
	/// ARCHITECTURAL PRINCIPLE: Enforces fail-fast dependency requirements – no silent defaults.
	/// </summary>
	/// <param name="tools">Tool execution abstraction used to enumerate and invoke POS operations.</param>
	/// <param name="config">Store configuration provider – must supply currency and other store parameters.</param>
	/// <exception cref="ArgumentNullException">Thrown when <paramref name="tools"/> is null.</exception>
	/// <exception cref="InvalidOperationException">Thrown when <paramref name="config"/> is null (fail-fast configuration breach).</exception>
	public AiOrchestrator(IToolExecutor tools, IStoreConfigurationProvider config)
	{
		_tools = tools ?? throw new ArgumentNullException(nameof(tools));
		_config = config ?? throw new InvalidOperationException("Store configuration provider not supplied. Register IStoreConfigurationProvider to proceed.");
	}

	/// <summary>
	/// Processes raw customer input and maps it deterministically to a single tool invocation.
	/// SINGLE-CALL PATTERN: This stub performs a synchronous (from orchestration perspective) mapping to one kernel/tool call
	/// to preserve the architecture's anti-hallucination guarantee. No multi-step reasoning here – replaced later by LLM layer.
	/// </summary>
	/// <param name="customerInput">Natural language input from a customer-facing channel.</param>
	/// <param name="cancellationToken">Cancellation token for cooperative cancellation.</param>
	/// <returns>Human-readable string result from the executed tool or validation feedback.</returns>
	public async Task<string> ProcessCustomerInteractionAsync(string customerInput, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(customerInput))
		{
			return "I didn't catch that.";
		}
		var text = customerInput.Trim().ToLowerInvariant();
		string tool;
		Dictionary<string, object> args = new();
		if (text.StartsWith("new"))
		{
			tool = "start_transaction";
			var currency = _config.GetCurrency();
			args["currency"] = currency;
		}
		else if (text.StartsWith("add "))
		{
			var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length >= 2)
			{
				tool = "add_item";
				args["productId"] = parts[1];
				args["quantity"] = parts.Length >= 3 && int.TryParse(parts[2], out var q) ? q : 1;
			}
			else
			{
				return "Need product id after add.";
			}
		}
		else if (text.StartsWith("pay"))
		{
			tool = "pay";
			var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			args["amount"] = parts.Length >= 2 && decimal.TryParse(parts[1], out var amt) ? amt : 0m;
		}
		else if (text.StartsWith("show"))
		{
			tool = "show";
		}
		else
		{
			return "Unsupported request in stub orchestrator.";
		}
		var result = await _tools.ExecuteToolAsync(tool, args, cancellationToken).ConfigureAwait(false);
		return result;
	}
}
