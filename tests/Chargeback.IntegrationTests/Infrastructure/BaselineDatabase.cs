using Npgsql;

namespace Chargeback.IntegrationTests.Infrastructure;

/// <summary>Applies the approved SQL scripts (copied into the test output) to an ephemeral database.</summary>
public static class BaselineDatabase
{
    public static string BaselinePath => Path.Combine(AppContext.BaseDirectory, "Baseline", "CHARGEBACK_DIAGRAM_BASELINE.sql");

    public static string MigrationPath(string fileName) => Path.Combine(AppContext.BaseDirectory, "Migrations", fileName);

    /// <summary>Baseline only (0001) — used to simulate a database created before later migrations.</summary>
    public static Task ApplyAsync(string connectionString) => ExecuteScriptAsync(connectionString, BaselinePath);

    /// <summary>A complete install: baseline (0001) followed by every numbered migration in order.</summary>
    public static async Task ApplyAllAsync(string connectionString)
    {
        await ApplyAsync(connectionString);
        foreach (var migration in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Migrations"), "*.sql").Order(StringComparer.Ordinal))
        {
            await ExecuteScriptAsync(connectionString, migration);
        }
    }

    public static async Task ExecuteScriptAsync(string connectionString, string path)
    {
        var script = await File.ReadAllTextAsync(path);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(script, connection);
        await command.ExecuteNonQueryAsync();
    }
}
