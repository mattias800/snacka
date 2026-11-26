using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Snacka.Server.Data;

namespace Snacka.Server.Tests;

/// <summary>Databases for service-level tests: SQLite in memory, so queries run as real SQL.</summary>
public static class TestDbContextFactory
{
    /// <summary>A fresh, migrated database, private to the returned context.</summary>
    public static SnackaDbContext Create()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<SqliteSnackaDbContext>().UseSqlite(connection).Options;
        var context = new SqliteSnackaDbContext(options);
        context.Database.Migrate();
        return context;
    }
}
