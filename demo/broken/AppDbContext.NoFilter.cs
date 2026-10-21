using Microsoft.EntityFrameworkCore;

namespace MultitenantApp;

/// <summary>
/// BROKEN VARIANT (do not compile together with the real AppDbContext):
/// the global tenant query filter was quietly removed. Reads now return
/// every tenant's rows. The generated probe suite must FAIL loudly here —
/// CrossTenantReadProbes catches the missing filter by construction.
/// The SaveChanges guard is still intact, so SaveGuardProbes stay green:
/// the suite bites on exactly the mechanism that regressed.
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
        // Deliberately broken: no HasQueryFilter — every tenant sees every row.
    }

    public override int SaveChanges()
    {
        GuardTenant();
        return base.SaveChanges();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardTenant();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardTenant();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        GuardTenant();
        return base.SaveChangesAsync(cancellationToken);
    }

    private void GuardTenant()
    {
        if (CurrentTenantId is null)
        {
            throw new InvalidOperationException("SaveChanges refused: no tenant context (fail-closed).");
        }

        foreach (var entry in ChangeTracker.Entries()
                     .Where(e => e.State is EntityState.Added or EntityState.Modified))
        {
            var tenant = entry.Property("TenantId").CurrentValue as string;
            if (tenant != CurrentTenantId)
            {
                throw new InvalidOperationException(
                    $"SaveChanges refused: entity '{entry.Metadata.Name}' belongs to tenant '{tenant}' " +
                    $"but the current tenant is '{CurrentTenantId}'.");
            }
        }
    }
}
