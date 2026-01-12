using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Snacka.Server.Data;

/// <summary>
/// Factory for creating DbContext at design time for EF Core migrations.
/// This allows running migrations without starting the full application.
/// Uses PostgreSQL to match production environment.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SnackaDbContext>
{
    public SnackaDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<SnackaDbContext>();

        // Use PostgreSQL for migrations - matches dev docker-compose (port 5435)
        optionsBuilder.UseNpgsql("Host=localhost;Port=5435;Database=snacka;Username=snacka;Password=snacka");

        return new SnackaDbContext(optionsBuilder.Options);
    }
}

/// <summary>
/// Factory for creating DataProtectionDbContext at design time for EF Core migrations.
/// </summary>
public class DataProtectionDesignTimeDbContextFactory : IDesignTimeDbContextFactory<DataProtectionDbContext>
{
    public DataProtectionDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<DataProtectionDbContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Port=5435;Database=snacka;Username=snacka;Password=snacka");
        return new DataProtectionDbContext(optionsBuilder.Options);
    }
}
