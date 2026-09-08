using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ProcureToPay.Infrastructure.Persistence;

public sealed class ProcureToPayDbContextFactory : IDesignTimeDbContextFactory<ProcureToPayDbContext>
{
    public ProcureToPayDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ProcureToPayDbContext>()
            .UseSqlServer(
                "Server=localhost,1433;Database=ProcureToPay_DesignTime;Integrated Security=True;TrustServerCertificate=True")
            .Options;

        return new ProcureToPayDbContext(options);
    }
}
