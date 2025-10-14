using PosKernel.Extensions.Core;

namespace PosKernel.Extensions.FoodService;

/// <summary>
/// Generic food-service store extension; underlying catalog implementation now FoodServiceCatalog;
/// modification infrastructure is fully data-driven via rule tables (no culture-specific codes in logic).
/// </summary>
public sealed class FoodServiceStoreExtension : IStoreExtension
{
    public IProductCatalog Catalog { get; }
    public IModificationService Modifications { get; }
    public ICurrencyFormatter CurrencyFormatter { get; } = new PassthroughCurrencyFormatter();
    public FoodServiceModifierRepository ModifierRepository { get; }

    public FoodServiceStoreExtension(string storeId, string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Connection string required for food-service store extension");
        }
        Catalog = new FoodServiceCatalog(storeId, connectionString); // existing product catalog (culture-neutral)
        ModifierRepository = new FoodServiceModifierRepository(connectionString);
        Modifications = new FoodServiceModificationService(ModifierRepository);
    }

    private sealed class PassthroughCurrencyFormatter : ICurrencyFormatter
    {
        public string FormatCurrency(decimal amount, string currency, string culture) => $"{currency} {amount}"; // Demo only; no localization logic
        public string GetCurrencySymbol(string currency) => currency;
        public int GetDecimalPlaces(string currency) => 2; // Demo assumption
    }
}

// --- Modifier domain records & repository ---

/// <summary>
/// Represents a modifier definition loaded from product_modifications and group membership tables.
/// </summary>
public sealed record FoodServiceModifier(
    string Id,
    string Name,
    string GroupCode,
    PriceAdjustmentKind AdjustmentKind,
    decimal Value,
    bool IsAutomatic,
    int DisplayOrder
);

public enum PriceAdjustmentKind { Free, Surcharge }

/// <summary>Modifier group metadata (single vs multi selection, required flag).</summary>
public sealed record FoodServiceModifierGroup(string Code, string Name, bool SingleSelect, bool IsRequired);

/// <summary>
/// Data-driven repository for modifier rules (applicability, implications, incompatibilities) sourced from SQLite tables:
///   product_modifications, product_modifier_applicability, modification_groups, modification_group_members,
///   modification_implications, modification_incompatibilities, modification_group_incompatibilities.
/// FAIL-FAST: Throws if expected tables are missing to expose integration errors early.
/// </summary>
public sealed class FoodServiceModifierRepository
{
    private readonly Dictionary<string, FoodServiceModifier> _modifiersById;
    private readonly Dictionary<string, FoodServiceModifierGroup> _groupsByCode;
    private readonly Dictionary<string, HashSet<string>> _modsByProduct = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _implications = new(StringComparer.OrdinalIgnoreCase); // source -> implied set
    private readonly Dictionary<string, HashSet<string>> _incompatibilities = new(StringComparer.OrdinalIgnoreCase); // mod -> incompatible mods
    private readonly Dictionary<string, HashSet<string>> _groupIncompatibilities = new(StringComparer.OrdinalIgnoreCase); // mod -> incompatible group codes
    private readonly Dictionary<string, List<FoodServiceModifier>> _modsByGroup = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<FoodServiceModifier> AllModifiers => _modifiersById.Values;
    public IReadOnlyCollection<FoodServiceModifierGroup> AllGroups => _groupsByCode.Values;

    public FoodServiceModifierRepository(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) { throw new ArgumentNullException(nameof(connectionString)); }
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        conn.Open();
        EnsureTable(conn, "product_modifications");
        EnsureTable(conn, "product_modifier_applicability");
        EnsureTable(conn, "modification_groups");
        EnsureTable(conn, "modification_group_members");
        // Optional rule tables may or may not exist; create empty maps if absent

        _groupsByCode = LoadGroups(conn);
        _modifiersById = LoadModifiers(conn);
        LoadGroupMemberships(conn, _modifiersById, _groupsByCode, _modsByGroup);
        LoadApplicability(conn, _modsByProduct, _modifiersById);
        LoadImplications(conn, _implications);
        LoadIncompatibilities(conn, _incompatibilities);
        LoadGroupIncompatibilities(conn, _groupIncompatibilities);
    }

    private static void EnsureTable(Microsoft.Data.Sqlite.SqliteConnection conn, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name = $n";
        cmd.Parameters.AddWithValue("$n", tableName);
        var exists = cmd.ExecuteScalar();
        if (exists == null)
        {
            throw new InvalidOperationException($"Required table '{tableName}' not found. Migration / seed step missing.");
        }
    }

    private static Dictionary<string, FoodServiceModifierGroup> LoadGroups(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        var dict = new Dictionary<string, FoodServiceModifierGroup>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT code,name,selection_type,is_required FROM modification_groups";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var code = reader.GetString(0);
            var name = reader.GetString(1);
            var selectionType = reader.GetString(2);
            var isRequired = !reader.IsDBNull(3) && reader.GetInt32(3) == 1;
            bool single = selectionType.Equals("single", StringComparison.OrdinalIgnoreCase);
            dict[code] = new FoodServiceModifierGroup(code, name, single, isRequired);
        }
        return dict;
    }

    private static Dictionary<string, FoodServiceModifier> LoadModifiers(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        var dict = new Dictionary<string, FoodServiceModifier>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT modification_id,name,modification_type,price_adjustment_type,base_price_cents,is_automatic,display_order,is_active FROM product_modifications WHERE is_active = 1";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            var name = reader.GetString(1);
            var groupPlaceholder = reader.GetString(2); // legacy column (modification_type) – actual group resolved via membership
            var adjustType = reader.GetString(3);
            decimal value = 0m;
            if (!reader.IsDBNull(4)) { try { value = reader.GetInt32(4) / 100m; } catch { } }
            bool isAutomatic = !reader.IsDBNull(5) && reader.GetBoolean(5);
            int displayOrder = !reader.IsDBNull(6) ? reader.GetInt32(6) : 0;
            if (!reader.IsDBNull(7) && !reader.GetBoolean(7)) { continue; }
            var kind = adjustType.ToUpperInvariant() switch
            {
                "FREE" => PriceAdjustmentKind.Free,
                "SURCHARGE" => PriceAdjustmentKind.Surcharge,
                _ => throw new InvalidOperationException($"Unsupported price_adjustment_type '{adjustType}' for modifier '{id}'")
            };
            // Group is assigned later via membership; use placeholder until then (kept for backward compatibility)
            dict[id] = new FoodServiceModifier(id, name, groupPlaceholder, kind, value, isAutomatic, displayOrder);
        }
        return dict;
    }

    private static void LoadGroupMemberships(Microsoft.Data.Sqlite.SqliteConnection conn, Dictionary<string, FoodServiceModifier> mods, Dictionary<string, FoodServiceModifierGroup> groups, Dictionary<string, List<FoodServiceModifier>> modsByGroup)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT modification_id, group_code FROM modification_group_members";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var modId = reader.GetString(0);
            var group = reader.GetString(1);
            if (!mods.TryGetValue(modId, out var existing) || !groups.ContainsKey(group)) { continue; }
            // Replace record with correct group code (assume single group membership semantics for now)
            mods[modId] = existing with { GroupCode = group };
            if (!modsByGroup.TryGetValue(group, out var list))
            {
                list = new List<FoodServiceModifier>();
                modsByGroup[group] = list;
            }
            list.Add(mods[modId]);
        }
        // Sort per group for deterministic behavior
        foreach (var kvp in modsByGroup)
        {
            kvp.Value.Sort((a, b) => a.DisplayOrder.CompareTo(b.DisplayOrder));
        }
    }

    private static void LoadApplicability(Microsoft.Data.Sqlite.SqliteConnection conn, Dictionary<string, HashSet<string>> target, Dictionary<string, FoodServiceModifier> mods)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sku, modification_id FROM product_modifier_applicability WHERE is_active = 1";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var sku = reader.GetString(0);
            var mid = reader.GetString(1);
            if (!mods.ContainsKey(mid)) { continue; }
            if (!target.TryGetValue(sku, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                target[sku] = set;
            }
            set.Add(mid);
        }
    }

    private static void LoadImplications(Microsoft.Data.Sqlite.SqliteConnection conn, Dictionary<string, HashSet<string>> map)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT source_modification_id, implied_modification_id FROM modification_implications";
        try
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var s = reader.GetString(0);
                var i = reader.GetString(1);
                if (!map.TryGetValue(s, out var set)) { set = new HashSet<string>(StringComparer.OrdinalIgnoreCase); map[s] = set; }
                set.Add(i);
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // Table absent – treat as zero implications (fail-fast handled earlier if critical)
        }
    }

    private static void LoadIncompatibilities(Microsoft.Data.Sqlite.SqliteConnection conn, Dictionary<string, HashSet<string>> map)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT modification_id, incompatible_modification_id FROM modification_incompatibilities";
        try
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var a = reader.GetString(0);
                var b = reader.GetString(1);
                if (!map.TryGetValue(a, out var set)) { set = new HashSet<string>(StringComparer.OrdinalIgnoreCase); map[a] = set; }
                set.Add(b);
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { }
    }

    private static void LoadGroupIncompatibilities(Microsoft.Data.Sqlite.SqliteConnection conn, Dictionary<string, HashSet<string>> map)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT modification_id, incompatible_group_code FROM modification_group_incompatibilities";
        try
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var mod = reader.GetString(0);
                var group = reader.GetString(1);
                if (!map.TryGetValue(mod, out var set)) { set = new HashSet<string>(StringComparer.OrdinalIgnoreCase); map[mod] = set; }
                set.Add(group);
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { }
    }

    public FoodServiceModifier? Get(string id) => string.IsNullOrWhiteSpace(id) ? null : (_modifiersById.TryGetValue(id, out var m) ? m : null);
    public bool IsApplicable(string productSku, string modifierId) => _modsByProduct.TryGetValue(productSku, out var set) && set.Contains(modifierId);
    public IEnumerable<string> GetImplied(string modifierId) => _implications.TryGetValue(modifierId, out var set) ? set : Array.Empty<string>();
    public IEnumerable<string> GetIncompatibleMods(string modifierId) => _incompatibilities.TryGetValue(modifierId, out var set) ? set : Array.Empty<string>();
    public IEnumerable<string> GetIncompatibleGroups(string modifierId) => _groupIncompatibilities.TryGetValue(modifierId, out var set) ? set : Array.Empty<string>();
    public FoodServiceModifierGroup? GetGroup(string code) => string.IsNullOrWhiteSpace(code) ? null : (_groupsByCode.TryGetValue(code, out var g) ? g : null);
}

/// <summary>
/// Generic modification validation + pricing implementing IModificationService using FoodServiceModifierRepository.
/// No hardcoded modifier codes – all behavior inferred from DB.
/// </summary>
public sealed class FoodServiceModificationService : IModificationService
{
    private readonly FoodServiceModifierRepository _repo;
    public FoodServiceModificationService(FoodServiceModifierRepository repo)
    {
        _repo = repo;
    }

    public Task<ModificationValidationResult> ValidateModificationsAsync(string productId, IReadOnlyList<ModificationSelection> selections, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = ValidateInternal(productId, selections);
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ModificationValidationResult { IsValid = false, ErrorMessage = ex.Message });
        }
    }

    public Task<decimal> CalculateModificationTotalAsync(IReadOnlyList<ModificationSelection> selections, CancellationToken cancellationToken = default)
    {
        // Pricing uses same path but skips validation errors (throws if unknown mod to force caller to validate first).
        var map = new Dictionary<string, (FoodServiceModifier mod, int qty)>(StringComparer.OrdinalIgnoreCase);
        foreach (var sel in selections)
        {
            var mod = _repo.Get(sel.Code) ?? throw new InvalidOperationException($"Unknown modifier '{sel.Code}'");
            if (!map.TryGetValue(mod.Id, out var tuple)) { tuple = (mod, 0); }
            map[mod.Id] = (mod, checked(tuple.qty + Math.Max(sel.Quantity, 1)));
        }
        decimal total = 0m;
        foreach (var kv in map.Values)
        {
            if (kv.mod.AdjustmentKind == PriceAdjustmentKind.Surcharge)
            {
                total += kv.mod.Value * kv.qty;
            }
        }
        return Task.FromResult(total);
    }

    private ModificationValidationResult ValidateInternal(string productId, IReadOnlyList<ModificationSelection> selections)
    {
        var finalMods = new Dictionary<string, (FoodServiceModifier mod, int qty)>(StringComparer.OrdinalIgnoreCase);
        var groupsChosen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // 1. Validate direct selections
        foreach (var sel in selections)
        {
            if (string.IsNullOrWhiteSpace(sel.Code))
            {
                return new ModificationValidationResult { IsValid = false, ErrorMessage = "Modifier code missing." };
            }
            var mod = _repo.Get(sel.Code);
            if (mod == null)
            {
                return new ModificationValidationResult { IsValid = false, ErrorMessage = $"Unknown modifier '{sel.Code}'." };
            }
            if (!_repo.IsApplicable(productId, mod.Id))
            {
                return new ModificationValidationResult { IsValid = false, ErrorMessage = $"Modifier '{mod.Id}' not applicable to product '{productId}'." };
            }
            // Group verification
            if (!string.IsNullOrWhiteSpace(sel.Group) && !mod.GroupCode.Equals(sel.Group, StringComparison.OrdinalIgnoreCase))
            {
                return new ModificationValidationResult { IsValid = false, ErrorMessage = $"Modifier '{mod.Id}' supplied under wrong group '{sel.Group}'." };
            }
            Bump(finalMods, mod, sel.Quantity);
            BumpGroup(groupsChosen, mod.GroupCode);
        }

        // 2. Apply implications (breadth-first)
        var queue = new Queue<string>(finalMods.Keys);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var implied in _repo.GetImplied(current))
            {
                if (!finalMods.ContainsKey(implied))
                {
                    var mod = _repo.Get(implied);
                    if (mod == null) { continue; } // Silently ignore unknown implied to keep fail-fast earlier
                    if (!_repo.IsApplicable(productId, mod.Id))
                    {
                        return new ModificationValidationResult { IsValid = false, ErrorMessage = $"Implied modifier '{mod.Id}' not applicable to product '{productId}'." };
                    }
                    Bump(finalMods, mod, 1);
                    BumpGroup(groupsChosen, mod.GroupCode);
                    queue.Enqueue(mod.Id);
                }
            }
        }

        // 3. Incompatibilities (modifier-level)
        foreach (var kv in finalMods)
        {
            var modId = kv.Key;
            foreach (var inc in _repo.GetIncompatibleMods(modId))
            {
                if (finalMods.ContainsKey(inc))
                {
                    return new ModificationValidationResult { IsValid = false, ErrorMessage = $"Modifiers '{modId}' and '{inc}' cannot be combined." };
                }
            }
        }

        // 4. Group incompatibilities
        foreach (var kv in finalMods)
        {
            foreach (var g in _repo.GetIncompatibleGroups(kv.Key))
            {
                if (groupsChosen.ContainsKey(g))
                {
                    return new ModificationValidationResult { IsValid = false, ErrorMessage = $"Modifier '{kv.Key}' incompatible with group '{g}'." };
                }
            }
        }

        // 5. Single-select enforcement
        foreach (var group in _repo.AllGroups)
        {
            if (group.SingleSelect)
            {
                // Count actual distinct modifiers chosen in this group
                int count = finalMods.Values.Count(v => v.mod.GroupCode.Equals(group.Code, StringComparison.OrdinalIgnoreCase));
                if (count > 1)
                {
                    return new ModificationValidationResult { IsValid = false, ErrorMessage = $"Group '{group.Code}' allows only one selection." };
                }
            }
        }

        // 6. Required groups must have at least one selection (data-driven, no hardcoding)
        foreach (var group in _repo.AllGroups.Where(g => g.IsRequired))
        {
            bool any = finalMods.Values.Any(v => v.mod.GroupCode.Equals(group.Code, StringComparison.OrdinalIgnoreCase));
            if (!any)
            {
                return new ModificationValidationResult { IsValid = false, ErrorMessage = $"Required modification group '{group.Code}' has no selection." };
            }
        }

        // 7. Compute price
        decimal extra = 0m;
        foreach (var entry in finalMods.Values)
        {
            if (entry.mod.AdjustmentKind == PriceAdjustmentKind.Surcharge)
            {
                extra += entry.mod.Value * entry.qty;
            }
        }
        return new ModificationValidationResult { IsValid = true, TotalExtraPrice = extra };
    }

    private static void Bump(Dictionary<string, (FoodServiceModifier mod, int qty)> map, FoodServiceModifier mod, int quantity)
    {
        if (quantity <= 0) { quantity = 1; }
        if (!map.TryGetValue(mod.Id, out var existing)) { existing = (mod, 0); }
        map[mod.Id] = (mod, checked(existing.qty + quantity));
    }
    private static void BumpGroup(Dictionary<string, int> groups, string groupCode)
    {
        if (!groups.TryGetValue(groupCode, out var c)) { c = 0; }
        groups[groupCode] = c + 1;
    }
}
