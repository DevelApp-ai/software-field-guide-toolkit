using Microsoft.EntityFrameworkCore;

namespace MultitenantApp;

/// <summary>
/// The tenant-isolating baseline the probes defend. Two mechanisms:
/// a global query filter per entity (reads) and a SaveChanges guard (writes),
/// both driven by the ambient tenant on the context instance. Both fail
/// closed when the tenant context is absent.
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

    /// <summary>
    /// Fail-closed write guard: with no tenant context, nothing is written;
    /// with a tenant context, only that tenant's rows are written.
    /// </summary>
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
