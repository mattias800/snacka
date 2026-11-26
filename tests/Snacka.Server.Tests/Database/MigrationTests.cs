using Microsoft.EntityFrameworkCore;
using Snacka.Server.Data;

namespace Snacka.Server.Tests.Database;

/// <summary>
/// Each database has its own migrations. A model change without a migration for both would break
/// whichever one was forgotten, on its next startup.
/// </summary>
[TestClass]
public class MigrationTests
{
    [TestMethod]
    public void PostgresMigrations_MatchTheModel()
    {
        // No connection is made: the model is compared with the migrations' snapshot.
        var options = new DbContextOptionsBuilder<SnackaDbContext>().UseNpgsql("Host=unused").Options;
        using var db = new SnackaDbContext(options);
        Assert.IsFalse(db.Database.HasPendingModelChanges(),
            "Add a migration: dotnet ef migrations add Name --project src/Snacka.Server --context SnackaDbContext");
    }

    [TestMethod]
    public void SqliteMigrations_MatchTheModel()
    {
        var options = new DbContextOptionsBuilder<SqliteSnackaDbContext>().UseSqlite("Data Source=:memory:").Options;
        using var db = new SqliteSnackaDbContext(options);
        Assert.IsFalse(db.Database.HasPendingModelChanges(),
            "Add a migration: dotnet ef migrations add Name --project src/Snacka.Server --context SqliteSnackaDbContext --output-dir Migrations/Sqlite");
    }
}
