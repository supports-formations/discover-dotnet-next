using System.Reflection;
using Microsoft.Data.SqlClient;

namespace StarWars.Seeder;

public static class Database
{
    public static async Task WaitForServerAsync(string masterConnection, CancellationToken ct)
    {
        const int maxAttempts = 60;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var cn = new SqlConnection(masterConnection);
                await cn.OpenAsync(ct);
                return;
            }
            catch (SqlException) when (attempt < maxAttempts)
            {
                if (attempt == 1) Console.WriteLine("⏳ En attente de SQL Server (le conteneur démarre)…");
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
        }
    }

    public static async Task DropAsync(string masterConnection, string database, CancellationToken ct)
    {
        await ExecuteAsync(masterConnection, $"""
            IF DB_ID(N'{database}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{database}];
            END
            """, ct);
    }

    public static Task CreateDatabaseAsync(string masterConnection, string database, double scale, CancellationToken ct) =>
        ExecuteAsync(masterConnection, ReadScript("01-database.sql").Replace("$(DbName)", database)
                .Replace("$(DataSizeMb)", Math.Max(256, (int)(8192 * scale)).ToString())
                .Replace("$(LogSizeMb)", Math.Max(128, (int)(2048 * scale)).ToString()), ct);

    public static Task CreateTablesAsync(string dbConnection, CancellationToken ct) =>
        ExecuteAsync(dbConnection, ReadScript("02-tables.sql"), ct);

    public static Task CreateIndexesAndConstraintsAsync(string dbConnection, CancellationToken ct) =>
        ExecuteAsync(dbConnection, ReadScript("03-indexes-and-constraints.sql"), ct);

    public static async Task<long> CountAsync(string dbConnection, string table, CancellationToken ct)
    {
        // sys.dm_db_partition_stats : instantané, sans scanner la table (COUNT(*) sur 10 M de lignes prendrait plusieurs secondes)
        await using var cn = new SqlConnection(dbConnection);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            SELECT COALESCE(SUM(row_count), 0)
            FROM sys.dm_db_partition_stats
            WHERE object_id = OBJECT_ID(@t) AND index_id IN (0, 1)
            """, cn);
        cmd.Parameters.AddWithValue("@t", $"dbo.{table}");
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    public static async Task TruncateAsync(string dbConnection, string table, CancellationToken ct) =>
        await ExecuteAsync(dbConnection, $"TRUNCATE TABLE dbo.[{table}]", ct);

    public static async Task<string> DatabaseSizeAsync(string dbConnection, CancellationToken ct)
    {
        await using var cn = new SqlConnection(dbConnection);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT CAST(SUM(used_page_count) * 8.0 / 1024 / 1024 AS decimal(10,2)) FROM sys.dm_db_partition_stats", cn);
        return $"{await cmd.ExecuteScalarAsync(ct)} Go";
    }

    private static async Task ExecuteAsync(string connection, string sql, CancellationToken ct)
    {
        await using var cn = new SqlConnection(connection);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 0 };
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string ReadScript(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        var resource = asm.GetManifestResourceNames().Single(n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase));
        using var reader = new StreamReader(asm.GetManifestResourceStream(resource)!);
        return reader.ReadToEnd();
    }
}
