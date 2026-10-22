using Microsoft.EntityFrameworkCore;

namespace MultitenantApp;

/// <summary>
/// BROKEN VARIANT (do not compile together with the real AppDbContext):
/// the SaveChanges tenant guard was quietly removed. Cross-tenant writes
/// now persist silently. The generated probe suite must FAIL loudly here —
/// SaveGuardProbes catches the missing guard by construction. The query
/// filter is intact, so CrossTenantReadProbes stay green: the suite bites
/// on exactly the mechanism that regressed.
/// </summary>
public class AppDbContext : DbContext
{
    public string? CurrentTenantId { get; set; }

    public AppDbContext(DbContextOptions options) : base(options)
    {
    }

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Invoice> Invoices => Set<Invoice>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<Order>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<Invoice>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
    }
}
