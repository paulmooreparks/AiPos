namespace AiPos.Core;

// Moved from Class1.cs: store configuration provider abstractions & in-memory implementation.

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
