using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace PosKernel.Extensions.Core.Migrations;

// ARCHITECTURAL PRINCIPLE: Fail fast on schema drift or missing migrations. No silent defaults.
// This lightweight migration infrastructure intentionally avoids external dependencies (e.g. EF Core)
// to keep store extension packaging minimal while providing deterministic, checksum validated execution.

/// <summary>
/// Provides embedded migration scripts for a store extension and exposes current expected version.
/// Implementations must return scripts in strictly ascending Version order without gaps starting at 1.
/// </summary>
public interface IStoreMigrationInfo
{
    /// <summary>Store identifier (used only for diagnostics).</summary>
    string StoreName { get; }
    /// <summary>Expected final schema version after applying all scripts.</summary>
    int TargetVersion { get; }
    /// <summary>Returns ordered collection of migration scripts.</summary>
    IReadOnlyList<StoreMigrationScript> GetScripts();
}

/// <summary>Represents a single migration script (versioned, named, raw SQL text).</summary>
public sealed record StoreMigrationScript(int Version, string Name, string Sql, string? PrecomputedSha256 = null)
{
    /// <summary>
    /// Computes SHA256 checksum for the script content (or returns precomputed value if supplied).
    /// Used to detect tampering or divergence between applied migrations and code-embedded scripts.
    /// </summary>
    public string ComputeSha256()
    {
        if (!string.IsNullOrEmpty(PrecomputedSha256))
        {
            return PrecomputedSha256!;
        }
        using var sha = SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(Sql);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash);
    }
}

/// <summary>
/// Executes migrations against a SQLite database enforcing ordering, idempotency, checksum integrity,
/// and backup before first mutation. Minimal surface to keep extension initialization deterministic.
/// </summary>
public sealed class MigrationRunner
{
    private readonly string _databasePath;
    private readonly IStoreMigrationInfo _migrationInfo;
    private readonly Action<string> _log; // Simple injection to avoid ILogger dependency at this layer

    /// <summary>
    /// Creates a new migration runner for a specific store database.
    /// </summary>
    /// <param name="databasePath">Full path to SQLite database file (must already exist; creation handled elsewhere).</param>
    /// <param name="migrationInfo">Migration script provider defining ordered scripts and target version.</param>
    /// <param name="log">Optional logging callback (stdout or structured logger hook).</param>
    public MigrationRunner(string databasePath, IStoreMigrationInfo migrationInfo, Action<string>? log = null)
    {
        _databasePath = databasePath ?? throw new ArgumentNullException(nameof(databasePath));
        _migrationInfo = migrationInfo ?? throw new ArgumentNullException(nameof(migrationInfo));
        _log = log ?? (_ => { });
    }

    /// <summary>
    /// Ensures database is migrated to target version. Creates schema_version table if absent (legacy adoption path),
    /// verifies applied scripts and checksums, then applies any pending scripts inside individual transactions.
    /// </summary>
    public async Task EnsureMigratedAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_databasePath))
        {
            throw new InvalidOperationException($"Database file not found at '{_databasePath}'. Cannot run migrations for store '{_migrationInfo.StoreName}'.");
        }

        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await EnsureVersionTableAsync(connection, cancellationToken).ConfigureAwait(false);

        var applied = await LoadAppliedMigrationsAsync(connection, cancellationToken).ConfigureAwait(false);
        var scripts = _migrationInfo.GetScripts();

        ValidateScriptSequence(scripts);
        ValidateAppliedAgainstScripts(applied, scripts);

        var pending = scripts.Where(s => !applied.ContainsKey(s.Version)).OrderBy(s => s.Version).ToList();
        if (pending.Count == 0)
        {
            _log($"MIGRATIONS: Store '{_migrationInfo.StoreName}' database already at version {_migrationInfo.TargetVersion}.");
            return;
        }

        CreateBackup();

        foreach (var script in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ApplyScriptAsync(connection, script, cancellationToken).ConfigureAwait(false);
            _log($"MIGRATIONS: Applied V{script.Version} - {script.Name}");
        }
    }

    private async Task EnsureVersionTableAsync(SqliteConnection connection, CancellationToken ct)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = @"CREATE TABLE IF NOT EXISTS schema_version (
            version INTEGER PRIMARY KEY,
            script_name TEXT NOT NULL,
            applied_utc TEXT NOT NULL,
            checksum TEXT NOT NULL
        );";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<Dictionary<int, (string Name, string Checksum)>> LoadAppliedMigrationsAsync(SqliteConnection connection, CancellationToken ct)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT version, script_name, checksum FROM schema_version ORDER BY version";
        var result = new Dictionary<int, (string, string)>();
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var version = reader.GetInt32(0);
            var name = reader.GetString(1);
            var checksum = reader.GetString(2);
            result[version] = (name, checksum);
        }
        return result;
    }

    private static void ValidateScriptSequence(IReadOnlyList<StoreMigrationScript> scripts)
    {
        if (scripts.Count == 0)
        {
            throw new InvalidOperationException("No migration scripts provided (GetScripts returned empty). Cannot determine schema version.");
        }
        for (int i = 0; i < scripts.Count; i++)
        {
            var expectedVersion = i + 1; // must start at 1
            if (scripts[i].Version != expectedVersion)
            {
                throw new InvalidOperationException($"Migration scripts must be contiguous starting at 1. Expected version {expectedVersion} at index {i} but found {scripts[i].Version}.");
            }
        }
    }

    private static void ValidateAppliedAgainstScripts(Dictionary<int, (string Name, string Checksum)> applied, IReadOnlyList<StoreMigrationScript> scripts)
    {
        foreach (var kvp in applied)
        {
            var version = kvp.Key;
            if (version < 1 || version > scripts.Count)
            {
                throw new InvalidOperationException($"Database has unknown migration version {version} not present in current code scripts. Refuse to continue.");
            }
            var script = scripts[version - 1];
            var expectedChecksum = script.ComputeSha256();
            if (!string.Equals(expectedChecksum, kvp.Value.Checksum, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Checksum mismatch for applied migration V{version} ({script.Name}). Expected {expectedChecksum} but database has {kvp.Value.Checksum}. Potential tampering or script modification. Abort.");
            }
        }
    }

    private async Task ApplyScriptAsync(SqliteConnection connection, StoreMigrationScript script, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();
        }
        using var tx = connection.BeginTransaction(); // SqliteTransaction for strong typing
        try
        {
            var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = script.Sql;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO schema_version(version, script_name, applied_utc, checksum) VALUES ($v,$n,$t,$c)";
            insert.Parameters.AddWithValue("$v", script.Version);
            insert.Parameters.AddWithValue("$n", script.Name);
            insert.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
            insert.Parameters.AddWithValue("$c", script.ComputeSha256());
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private void CreateBackup()
    {
        // Only create a backup once per run (guard by presence of .bak with timestamp pattern)
        var directory = Path.GetDirectoryName(_databasePath)!;
        var fileName = Path.GetFileNameWithoutExtension(_databasePath);
        var extension = Path.GetExtension(_databasePath);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var backupPath = Path.Combine(directory, $"{fileName}_pre_migration_{timestamp}{extension}.bak");
        File.Copy(_databasePath, backupPath, overwrite: false);
        _log($"MIGRATIONS: Created backup at {backupPath}");
    }
}
