using Microsoft.EntityFrameworkCore;

namespace ProcureToPay.Infrastructure.Persistence;

public sealed class ProcureToPayDbContext(DbContextOptions<ProcureToPayDbContext> options)
    : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Module entity configurations must explicitly map their tables to a module schema.
        // Example: builder.ToTable("Suppliers", "Procurement");
        base.OnModelCreating(modelBuilder);
    }
}
