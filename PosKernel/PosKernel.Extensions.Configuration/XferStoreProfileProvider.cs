using ParksComputing.Xfer.Lang;
using ParksComputing.Xfer.Lang.Attributes; // Use real XferPropertyAttribute from package
using PosKernel.Extensions.Core.Profiles;
using System.Collections.Concurrent;
using System.Reflection;

namespace PosKernel.Extensions.Configuration;

/// <summary>
/// XferLang-backed implementation of <see cref="IStoreProfileProvider"/>.
/// Fail-fast: constructor and Reload throw on ANY validation error.
/// </summary>
/// <summary>
/// XferLang-backed store profile provider. Loads an index (.xfer) that references individual store profile .xfer files.
/// Parsing and validation occur eagerly (constructor / Reload). Any structural problem throws <see cref="InvalidOperationException"/>.
/// </summary>
public sealed class XferStoreProfileProvider : IStoreProfileProvider
{
    private readonly string _indexPath;
    private readonly object _lock = new();
    private ImmutableSnapshot _snapshot = new([], new Dictionary<string, StoreProfile>(StringComparer.OrdinalIgnoreCase));
    /// <summary>UTC timestamp of last successful reload.</summary>
    public DateTime LastReloadUtc { get; private set; }

    private XferStoreProfileProvider(string indexPath)
    {
        _indexPath = indexPath;
        Reload();
    }

    /// <summary>
    /// Creates a provider using the default profile index path: ~/.poskernel/profiles.xfer
    /// </summary>
    public static XferStoreProfileProvider FromDefaultLocation()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var index = Path.Combine(home, ".poskernel", "profiles.xfer");
        return new XferStoreProfileProvider(index);
    }

    /// <summary>
    /// Creates a provider using an explicit index path (facilitates testing with temp directories).
    /// </summary>
    public static XferStoreProfileProvider FromIndexPath(string indexPath)
    {
        if (string.IsNullOrWhiteSpace(indexPath))
        {
            throw new ArgumentException("Index path required", nameof(indexPath));
        }
        return new XferStoreProfileProvider(indexPath);
    }

    /// <summary>Returns immutable snapshot list of loaded profiles.</summary>
    public IReadOnlyList<StoreProfile> GetAll() => _snapshot.List;
    /// <summary>Gets a specific profile or throws if not found (fail-fast).</summary>
    public StoreProfile GetById(string storeId)
    {
        if (_snapshot.ById.TryGetValue(storeId, out var p))
        {
            return p;
        }
        throw new InvalidOperationException($"Store profile '{storeId}' not found. Available: {string.Join(',', _snapshot.List.Select(x => x.StoreId))}");
    }

    /// <summary>
    /// Reloads the index and all referenced profile files atomically; replaces in-memory snapshot on success.
    /// Throws if any file is missing, malformed, or violates validation rules.
    /// </summary>
    public void Reload()
    {
        lock (_lock)
        {
            if (!File.Exists(_indexPath))
            {
                throw new InvalidOperationException($"profiles index not found: {_indexPath}. Create profiles.xfer first.");
            }
            var indexText = File.ReadAllText(_indexPath);
            ProfilesIndex? index;
            try
            {
                index = XferConvert.Deserialize<ProfilesIndex>(indexText);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to parse profiles.xfer at '{_indexPath}'. Parser reported: {ex.Message}. " +
                    "Correct the syntax (ensure valid Xfer root object/array/tuple) and retry.", ex);
            }
            if (index == null)
            {
                throw new InvalidOperationException("Failed to parse profiles.xfer (null document returned).");
            }
            if (index.Files == null || index.Files.Count == 0)
            {
                throw new InvalidOperationException("profiles.xfer lists zero profile files (files {}).");
            }
            var list = new List<StoreProfile>();
            var dict = new Dictionary<string, StoreProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in index.Files)
            {
                var path = ResolvePath(kvp.Value.Path);
                if (!File.Exists(path))
                {
                    throw new InvalidOperationException($"Profile file missing: {path}");
                }
                var text = File.ReadAllText(path);
                var dto = XferConvert.Deserialize<StoreProfileXfer>(text) ?? throw new InvalidOperationException($"Could not parse profile file {path}");
                Validate(dto, path);
                var profile = Map(dto);
                if (dict.ContainsKey(profile.StoreId))
                {
                    throw new InvalidOperationException($"Duplicate storeId '{profile.StoreId}' detected.");
                }
                dict[profile.StoreId] = profile;
                list.Add(profile);
            }
            _snapshot = new ImmutableSnapshot(list.AsReadOnly(), dict);
            LastReloadUtc = DateTime.UtcNow;
        }
    }

    private static StoreProfile Map(StoreProfileXfer dto)
    {
        var payments = dto.PaymentTypes!.Select(p => new PaymentTenderType(p.Key, p.Value.AllowsChange, p.Value.RequiresExact)).ToList().AsReadOnly();
        return new StoreProfile(dto.StoreId!, dto.DisplayName!, dto.Currency!, dto.Culture!, dto.Version, payments, dto.Database?.Type, dto.Database?.ConnectionString);
    }

    private static void Validate(StoreProfileXfer dto, string path)
    {
        List<string> errors = new();
        if (string.IsNullOrWhiteSpace(dto.StoreId))
        {
            errors.Add("storeId missing");
        }
        if (string.IsNullOrWhiteSpace(dto.DisplayName))
        {
            errors.Add("displayName missing");
        }
        if (string.IsNullOrWhiteSpace(dto.Currency))
        {
            errors.Add("currency missing");
        }
        if (string.IsNullOrWhiteSpace(dto.Culture))
        {
            errors.Add("culture missing");
        }
        if (dto.Version <= 0)
        {
            errors.Add("version must be > 0");
        }
        if (dto.PaymentTypes == null || dto.PaymentTypes.Count == 0)
        {
            errors.Add("paymentTypes empty");
        }
        // Optional database block validation (if present must have both type and connectionString)
        if (dto is { Database: not null })
        {
            if (string.IsNullOrWhiteSpace(dto.Database.Type))
            {
                errors.Add("database.type missing");
            }
            if (string.IsNullOrWhiteSpace(dto.Database.ConnectionString))
            {
                errors.Add("database.connectionString missing");
            }
        }
        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"Invalid profile {path}: {string.Join(", ", errors)}");
        }
    }

    private static string ResolvePath(string raw)
    {
        if (raw.StartsWith("~"))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.GetFullPath(Path.Combine(home, raw.TrimStart('~').TrimStart('/', '\\')));
        }
        return Path.GetFullPath(raw);
    }

    private sealed record ImmutableSnapshot(IReadOnlyList<StoreProfile> List, Dictionary<string, StoreProfile> ById);

    // Xfer DTOs (internal to implementation)
    private sealed class ProfilesIndex
    {
        [XferProperty("files")] public Dictionary<string, ProfileFileRef> Files { get; set; } = new();
    }

    private sealed class ProfileFileRef
    {
        [XferProperty("path")] public string Path { get; set; } = string.Empty;
    }

    private sealed class StoreProfileXfer
    {
        [XferProperty("storeId")] public string StoreId { get; set; } = string.Empty;
        [XferProperty("displayName")] public string DisplayName { get; set; } = string.Empty;
        [XferProperty("currency")] public string Currency { get; set; } = string.Empty;
        [XferProperty("culture")] public string Culture { get; set; } = string.Empty;
        [XferProperty("version")] public int Version { get; set; }
        [XferProperty("paymentTypes")] public Dictionary<string, PaymentTypeXfer>? PaymentTypes { get; set; }
        [XferProperty("database")] public DatabaseConfig? Database { get; set; }
    }

    private sealed class PaymentTypeXfer
    {
        [XferProperty("allowsChange")] public bool AllowsChange { get; set; }
        [XferProperty("requiresExact")] public bool RequiresExact { get; set; }
    }

    private sealed class DatabaseConfig
    {
        [XferProperty("type")] public string Type { get; set; } = string.Empty;
        [XferProperty("connectionString")] public string ConnectionString { get; set; } = string.Empty;
    }
}
