using PosKernel.Abstractions;
using PosKernel.Core.Domain;

namespace PosKernel.Core.Services;

/// <summary>
/// ARCHITECTURAL PLACEHOLDER: Validates currency codes against configured store registry.
/// Fail fast if unsupported currency encountered.
/// </summary>
public interface ICurrencyValidator
{
    /// <summary>Validate currency code against supported registry (fail fast on unsupported).</summary>
    void Validate(string currency);
}

/// <summary>In-memory currency validator using a provided supported currency set.</summary>
public sealed class CurrencyValidator : ICurrencyValidator
{
    private readonly HashSet<string> _supported;
    /// <summary>Create a currency validator over a set of supported currency codes.</summary>
    /// <param name="supported">Collection of supported ISO 4217 codes.</param>
    public CurrencyValidator(IEnumerable<string> supported)
    {
        _supported = new HashSet<string>(supported ?? throw new ArgumentNullException(nameof(supported)), StringComparer.OrdinalIgnoreCase);
    }
    /// <inheritdoc />
    public void Validate(string currency)
    {
        if (string.IsNullOrWhiteSpace(currency) || !_supported.Contains(currency))
        {
            throw new InvalidOperationException("Currency formatting service not available or currency unsupported. Register proper configuration.");
        }
    }
}

/// <summary>
/// ARCHITECTURAL PLACEHOLDER: Central configuration provider (culture neutral values only).
/// </summary>
public interface IKernelConfiguration
{
    /// <summary>List of supported ISO 4217 currency codes.</summary>
    IReadOnlyCollection<string> SupportedCurrencies { get; }
}

/// <summary>In-memory implementation of kernel configuration for POC/testing.</summary>
public sealed class InMemoryKernelConfiguration : IKernelConfiguration
{
    /// <summary>Configured supported currency codes.</summary>
    public IReadOnlyCollection<string> SupportedCurrencies { get; }
    /// <summary>Create configuration with given supported currency codes.</summary>
    public InMemoryKernelConfiguration(params string[] currencies)
    {
        SupportedCurrencies = currencies.Length == 0 ? Array.Empty<string>() : currencies;
    }
}

/// <summary>ARCHITECTURAL PLACEHOLDER: Audit sink for security/audit events.</summary>
public interface IAuditSink
{
    /// <summary>Record an audit event with optional structured data.</summary>
    void Record(string eventType, string message, IDictionary<string, object>? data = null);
}

/// <summary>No-op audit sink placeholder.</summary>
public sealed class NullAuditSink : IAuditSink
{
    /// <inheritdoc />
    public void Record(string eventType, string message, IDictionary<string, object>? data = null)
    {
        // Intentionally no-op for POC; will be replaced by structured logging provider.
    }
}

/// <summary>ARCHITECTURAL PLACEHOLDER: Basic metrics collection interface.</summary>
public interface IKernelMetrics
{
    /// <summary>Increment counter (names are implementation defined).</summary>
    void Increment(string counterName);
}

/// <summary>No-op metrics implementation.</summary>
public sealed class NullKernelMetrics : IKernelMetrics
{
    /// <inheritdoc />
    public void Increment(string counterName) { }
}

/// <summary>ARCHITECTURAL PLACEHOLDER: Simple role validator (RBAC stub).</summary>
public interface IRoleValidator
{
    /// <summary>Demand that session possesses required role (fail or throw otherwise).</summary>
    void Demand(string sessionId, string requiredRole);
}

/// <summary>Permissive role validator used for early POC phases.</summary>
public sealed class AllowAllRoleValidator : IRoleValidator
{
    /// <inheritdoc />
    public void Demand(string sessionId, string requiredRole) { }
}
