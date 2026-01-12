using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Snacka.Server.Data;

/// <summary>
/// DbContext for storing ASP.NET Core Data Protection keys in the database.
/// This ensures keys persist across container restarts, preventing users from
/// being logged out when the server is redeployed.
/// </summary>
public class DataProtectionDbContext : DbContext, IDataProtectionKeyContext
{
    public DataProtectionDbContext(DbContextOptions<DataProtectionDbContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// The collection of data protection keys stored in the database.
    /// </summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
}
