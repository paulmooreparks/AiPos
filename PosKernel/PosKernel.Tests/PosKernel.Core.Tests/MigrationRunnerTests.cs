using System.Text;
using Microsoft.Data.Sqlite;
using PosKernel.Extensions.Core.Migrations;

namespace PosKernel.Core.Tests;

// ARCHITECTURAL TEST: Verifies fail-fast deterministic migration execution.
[TestClass]
public class MigrationRunnerTests
{
    private sealed class TestMigrationInfo : IStoreMigrationInfo
    {
        public string StoreName => "TestStore";
        public int TargetVersion => 1;
        public IReadOnlyList<StoreMigrationScript> GetScripts() => new[]
        {
            new StoreMigrationScript(1, "InitialSchema", "CREATE TABLE products(id INTEGER PRIMARY KEY, name TEXT NOT NULL);")
        };
    }

    [TestMethod]
    public async Task AppliesInitialMigration_WhenDatabaseEmpty()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"migration_test_{Guid.NewGuid():N}.db");
        using (File.Create(dbPath)) { }

        var info = new TestMigrationInfo();
        var runner = new MigrationRunner(dbPath, info);
        await runner.EnsureMigratedAsync();

        // Validate schema_version row inserted
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM schema_version WHERE version=1";
    var scalar = await cmd.ExecuteScalarAsync();
    var count = Convert.ToInt64(scalar ?? 0);
    Assert.AreEqual(1L, count, "Expected migration version row not found.");
    }

    [TestMethod]
    public async Task IsIdempotent_WhenRunTwice()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"migration_test_{Guid.NewGuid():N}.db");
        using (File.Create(dbPath)) { }

        var info = new TestMigrationInfo();
        var runner = new MigrationRunner(dbPath, info);
        await runner.EnsureMigratedAsync();
        await runner.EnsureMigratedAsync(); // second run should no-op

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM schema_version";
    var scalar = await cmd.ExecuteScalarAsync();
    var count = Convert.ToInt64(scalar ?? 0);
    Assert.AreEqual(1L, count, "Migration should not create duplicate rows on second run.");
    }
}
