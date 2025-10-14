using Microsoft.Data.Sqlite;
using PosKernel.Extensions.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PosKernel.Extensions.FoodService;

/// <summary>
/// SQLite-backed catalog for generic FoodService stores (formerly KopitiamCatalog).
/// ARCHITECTURAL PRINCIPLE: Culture-neutral data access. Fails fast on structural or connectivity issues.
/// </summary>
public sealed class FoodServiceCatalog : IProductCatalog
{
    private readonly string _connectionString;
    private readonly string _storeId;

    public FoodServiceCatalog(string storeId, string connectionString)
    {
        _storeId = storeId ?? throw new ArgumentNullException(nameof(storeId));
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Connection string required for FoodServiceCatalog");
        }
        _connectionString = NormalizeConnectionString(connectionString);
        // Fail-fast existence check for file-based Data Source
        if (_connectionString.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
        {
            var path = _connectionString.Substring("Data Source=".Length).Trim();
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"Food service catalog database not found at {path}");
            }
        }
    }

    public async Task<ProductValidationResult> ValidateProductAsync(string productId, CancellationToken cancellationToken = default)
    {
        var p = await GetProductBySkuAsync(productId, cancellationToken);
        if (p == null)
        {
            return new ProductValidationResult { IsValid = false, ErrorMessage = "Not found" };
        }
        return new ProductValidationResult { IsValid = true, Product = p, EffectivePrice = p.BasePrice };
    }

    public async Task<IReadOnlyList<ProductInfo>> SearchProductsAsync(string searchTerm, int maxResults = 50, CancellationToken cancellationToken = default)
    {
        var list = new List<ProductInfo>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            cmd.CommandText = $"SELECT sku,name,description,category_id,base_price,is_active FROM products WHERE is_active = 1 LIMIT {maxResults}";
        }
        else
        {
            cmd.CommandText = $"SELECT sku,name,description,category_id,base_price,is_active FROM products WHERE is_active = 1 AND (name LIKE @t OR sku LIKE @t) LIMIT {maxResults}";
            cmd.Parameters.AddWithValue("@t", "%" + searchTerm + "%");
        }
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(ReadProduct(reader));
        }
        return list;
    }

    public async Task<IReadOnlyList<ProductInfo>> GetPopularItemsAsync(CancellationToken cancellationToken = default)
    {
        return await SearchProductsAsync(string.Empty, 5, cancellationToken);
    }

    private async Task<ProductInfo?> GetProductBySkuAsync(string sku, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sku,name,description,category_id,base_price,is_active FROM products WHERE sku = @sku";
        cmd.Parameters.AddWithValue("@sku", sku);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return ReadProduct(reader);
        }
        return null;
    }

    private static ProductInfo ReadProduct(SqliteDataReader r)
    {
        var sku = r.GetString(0);
        var name = r.GetString(1);
        var desc = r.IsDBNull(2) ? string.Empty : r.GetString(2);
        var category = r.IsDBNull(3) ? string.Empty : r.GetString(3); // category_id; could join later for name
        decimal price = 0m;
        try
        {
            price = r.GetDecimal(4); // base_price (REAL)
        }
        catch
        {
            // Legacy fallback if schema uses cents
            try
            {
                if (!r.IsDBNull(4))
                {
                    var cents = r.GetInt32(4);
                    price = cents / 100m;
                }
            }
            catch { }
        }
        bool isActive = true;
        if (r.FieldCount > 5 && !r.IsDBNull(5))
        {
            try { isActive = r.GetBoolean(5); } catch { }
        }
        return new ProductInfo { Sku = sku, Name = name, Description = desc, Category = category, BasePrice = price, IsActive = isActive };
    }

    private static string NormalizeConnectionString(string cs)
    {
        // Allow bare path like C:\path\file.db or ~/... and convert to Data Source= form
        if (!cs.Contains('=') && File.Exists(ExpandHome(cs)))
        {
            return "Data Source=" + ExpandHome(cs);
        }
        if (cs.StartsWith("~"))
        {
            return "Data Source=" + ExpandHome(cs);
        }
        return cs;
    }

    private static string ExpandHome(string path)
    {
        if (!path.StartsWith("~"))
        {
            return path;
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.GetFullPath(Path.Combine(home, path.TrimStart('~').TrimStart('/', '\\')));
    }
}
