using AiPos.Core;

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
		Dictionary<string, object> result = new(StringComparer.OrdinalIgnoreCase);
		foreach (var p in def.Parameters)
		{
			if (!provided.TryGetValue(p.Name, out var raw))
			{
				if (p.Required)
				{
					throw new InvalidOperationException($"Tool '{def.Name}' missing required parameter '{p.Name}'.");
				}
				continue;
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
