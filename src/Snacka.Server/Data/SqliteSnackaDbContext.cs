using Microsoft.EntityFrameworkCore;

namespace Snacka.Server.Data;

/// <summary>
/// The same model on SQLite, the default for self-hosting (#53). Its own type only so it gets
/// its own migrations (Migrations/Sqlite); services keep asking for <see cref="SnackaDbContext"/>.
/// </summary>
public sealed class SqliteSnackaDbContext : SnackaDbContext
{
    public SqliteSnackaDbContext(DbContextOptions<SqliteSnackaDbContext> options) : base(options) { }
}
