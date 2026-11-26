using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Snacka.Server.Data;

/// <summary>
/// Picks the database (#53). With no connection string the server keeps everything in an SQLite
/// file in its data directory, so hosting needs no database server. A PostgreSQL connection string
/// (<c>Host=…</c>), as the Docker setup uses, selects PostgreSQL; <c>Data Source=…</c> selects SQLite.
/// </summary>
public static class DatabaseSetup
{
    public static bool IsSqlite(string connectionString) =>
        connectionString.TrimStart().StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase) ||
        connectionString.TrimStart().StartsWith("Filename=", StringComparison.OrdinalIgnoreCase);

    public static void AddSnackaDatabase(this IServiceCollection services, string? connectionString, string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = $"Data Source={Path.Combine(dataDirectory, "snacka.db")}";

        var dataProtection = services.AddDataProtection().SetApplicationName("Snacka");
        if (IsSqlite(connectionString))
        {
            services.AddDbContext<SnackaDbContext, SqliteSnackaDbContext>(options => options.UseSqlite(connectionString));
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")));
        }
        else
        {
            services.AddDbContext<SnackaDbContext>(options => options.UseNpgsql(connectionString));
            // Keys in the database survive container restarts.
            services.AddDbContext<DataProtectionDbContext>(options => options.UseNpgsql(connectionString));
            dataProtection.PersistKeysToDbContext<DataProtectionDbContext>();
        }
    }

    /// <summary>Creates or upgrades the schema. SQLite also gets write-ahead logging, so reads don't wait for writes.</summary>
    public static void MigrateSnackaDatabase(this IServiceProvider services)
    {
        var db = services.GetRequiredService<SnackaDbContext>();
        db.Database.Migrate();
        if (db.Database.IsSqlite())
            db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        services.GetService<DataProtectionDbContext>()?.Database.Migrate();
    }
}
