namespace AiPos.Core;

/// <summary>
/// Describes a tool the orchestrator can invoke in a single-call execution path.
/// ARCHITECTURAL PRINCIPLE: Declarative catalog – no dynamic discovery magic that could hide missing registrations.
/// </summary>
public sealed record ToolDefinition(
	string Name,
	string Category,
	string Description,
	IReadOnlyList<ToolParameter> Parameters
);

/// <summary>Parameter metadata for tool invocation.</summary>
public sealed record ToolParameter(string Name, string Type, bool Required, string Description);

/// <summary>Executes registered tools by name with raw parameter bag.</summary>
public interface IToolExecutor
{
	/// <summary>Execute a tool by name. Fail fast if unknown or parameter mismatch.</summary>
	Task<string> ExecuteToolAsync(string toolName, IReadOnlyDictionary<string, object> parameters, CancellationToken cancellationToken = default);
	/// <summary>Return immutable list of available tool definitions.</summary>
	IReadOnlyList<ToolDefinition> GetAvailableTools();
}

/// <summary>
/// Single-call AI orchestrator: takes customer input, selects a tool (or none) and returns a response.
/// </summary>
public interface IAiOrchestrator
{
	/// <summary>Process a customer utterance producing a response (and performing side effects via tools).</summary>
	Task<string> ProcessCustomerInteractionAsync(string customerInput, CancellationToken cancellationToken = default);
}

// ARCHITECTURAL PRINCIPLE: Configuration must be explicit and fail-fast if absent – no silent defaults.
/// <summary>
/// Provides access to store-level configuration values required by AI orchestration and tool handlers.
/// ARCHITECTURAL PRINCIPLE: Must be explicitly registered; absence indicates missing store wiring and MUST fail fast.
/// </summary>
public interface IStoreConfigurationProvider
{
	/// <summary>Gets the ISO 4217 currency code (e.g., USD, SGD, JPY). Must be a 3-letter upper-case code.</summary>
	string GetCurrency();
	/// <summary>Gets the culture code (e.g., en-US, fr-FR) used for presentation/localization decisions.</summary>
	string GetCulture();
}

/// <summary>
/// Minimal in-memory store configuration provider used for early POC wiring.
/// Throws if constructed with invalid values to expose misconfiguration early.
/// </summary>
/// <summary>
/// Simple immutable configuration provider backed by constructor parameters. Intended ONLY for early POC / test wiring.
/// </summary>
public sealed class InMemoryStoreConfigurationProvider : IStoreConfigurationProvider
{
	private readonly string _currency;
	private readonly string _culture;
	/// <summary>Create provider with explicit currency and culture (validates inputs).</summary>
	/// <param name="currency">3-letter ISO currency code.</param>
	/// <param name="culture">Culture identifier (e.g., en-US).</param>
	public InMemoryStoreConfigurationProvider(string currency, string culture)
	{
		if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
		{
			throw new InvalidOperationException("Store configuration invalid: currency must be 3-letter ISO code.");
		}
		if (string.IsNullOrWhiteSpace(culture))
		{
			throw new InvalidOperationException("Store configuration invalid: culture must be specified.");
		}
		_currency = currency.ToUpperInvariant();
		_culture = culture;
	}
	/// <inheritdoc />
	public string GetCurrency() => _currency;
	/// <inheritdoc />
	public string GetCulture() => _culture;
}
