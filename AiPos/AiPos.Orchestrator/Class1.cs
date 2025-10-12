using System.Text.Json;
using AiPos.Core;
using PosKernel.Client;

namespace AiPos.Orchestrator;

/// <summary>
/// Minimal in-memory tool executor for early end-to-end wiring. NOT dynamic – explicit registrations only.
/// </summary>
public sealed class DirectToolExecutor : IToolExecutor
{
	private readonly Dictionary<string, Func<IReadOnlyDictionary<string, object>, CancellationToken, Task<string>>> _handlers;
	private readonly List<ToolDefinition> _definitions;

	/// <summary>Create a direct executor with explicit tool definitions and handlers.</summary>
	public DirectToolExecutor(IEnumerable<ToolDefinition> definitions,
		IEnumerable<(string name, Func<IReadOnlyDictionary<string, object>, CancellationToken, Task<string>> handler)> handlers)
	{
		_definitions = definitions.ToList();
		_handlers = handlers.ToDictionary(h => h.name, h => h.handler, StringComparer.OrdinalIgnoreCase);
	}

	/// <inheritdoc />
	public IReadOnlyList<ToolDefinition> GetAvailableTools() => _definitions;

	/// <inheritdoc />
	public Task<string> ExecuteToolAsync(string toolName, IReadOnlyDictionary<string, object> parameters, CancellationToken cancellationToken = default)
	{
		if (!_handlers.TryGetValue(toolName, out var handler))
		{
			throw new InvalidOperationException($"Tool not registered: {toolName}");
		}
		var definition = _definitions.FirstOrDefault(d => string.Equals(d.Name, toolName, StringComparison.OrdinalIgnoreCase));
		if (definition == null)
		{
			throw new InvalidOperationException($"Tool definition missing for registered handler: {toolName}");
		}
		var normalized = ValidateAndNormalize(definition, parameters);
		return handler(normalized, cancellationToken);
	}

	private IReadOnlyDictionary<string, object> ValidateAndNormalize(ToolDefinition def, IReadOnlyDictionary<string, object> provided)
	{
		// ARCHITECTURAL PRINCIPLE: Fail fast on first parameter violation with explicit message.
		Dictionary<string, object> result = new(StringComparer.OrdinalIgnoreCase);
		foreach (var p in def.Parameters)
		{
			if (!provided.TryGetValue(p.Name, out var raw))
			{
				if (p.Required)
				{
					throw new InvalidOperationException($"Tool '{def.Name}' missing required parameter '{p.Name}'.");
				}
				continue; // optional and absent
			}
			object converted = raw;
			try
			{
				converted = p.Type.ToLowerInvariant() switch
				{
					"string" => raw is string s ? s : raw.ToString()!,
					"int" => raw is int i ? i : Convert.ToInt32(raw, System.Globalization.CultureInfo.InvariantCulture),
					"decimal" => raw is decimal d ? d : Convert.ToDecimal(raw, System.Globalization.CultureInfo.InvariantCulture),
					_ => raw
				};
			}
			catch (Exception ex)
			{
				throw new InvalidOperationException($"Tool '{def.Name}' parameter '{p.Name}' expected {p.Type}: {ex.Message}");
			}
			result[p.Name] = converted;
		}
		// Include any extra parameters (not defined) – explicit design choice: they are ignored to surface definition drift? We instead fail.
		foreach (var extra in provided.Keys)
		{
			if (!def.Parameters.Any(q => string.Equals(q.Name, extra, StringComparison.OrdinalIgnoreCase)))
			{
				throw new InvalidOperationException($"Tool '{def.Name}' received unknown parameter '{extra}'.");
			}
		}
		return result;
	}
}

/// <summary>
/// Heuristic single-call orchestrator stub: extremely simple keyword routing. Replaced later by LLM.
/// </summary>
public sealed class AiOrchestrator : IAiOrchestrator
{
	private readonly IToolExecutor _tools;
    private readonly IStoreConfigurationProvider _config;

	/// <summary>Create orchestrator using provided tool executor.</summary>
	public AiOrchestrator(IToolExecutor tools, IStoreConfigurationProvider config)
	{
		_tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _config = config ?? throw new InvalidOperationException("Store configuration provider not supplied. Register IStoreConfigurationProvider to proceed.");
	}

	/// <inheritdoc />
	public async Task<string> ProcessCustomerInteractionAsync(string customerInput, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(customerInput))
		{
			return "I didn't catch that.";
		}

		// Trivial intent heuristics (placeholder for actual LLM prompt output parsing)
		var text = customerInput.Trim().ToLowerInvariant();
		string tool;
		Dictionary<string, object> args = new();

		if (text.StartsWith("new"))
		{
			tool = "start_transaction";
            var currency = _config.GetCurrency(); // Fail-fast if provider misconfigured
            args["currency"] = currency;
		}
		else if (text.StartsWith("add "))
		{
			// add COFFEE.SMALL x2
			var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length >= 2)
			{
				tool = "add_item";
				args["productId"] = parts[1];
				args["quantity"] = parts.Length >= 3 && int.TryParse(parts[2], out var q) ? q : 1;
				// ARCHITECTURAL PRINCIPLE: Orchestrator must NOT invent pricing. Handler will resolve from catalog.
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
