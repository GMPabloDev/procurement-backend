using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ProcureToPay.Infrastructure.Persistence;

#nullable disable

namespace ProcureToPay.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ProcureToPayDbContext))]
[Migration("20260909170000_ScopeIdempotencyFingerprint")]
public partial class ScopeIdempotencyFingerprint : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_PolicyEvaluationBundles_IdempotencyFingerprint",
            schema: "Policy",
            table: "PolicyEvaluationBundles");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_PolicyEvaluationBundles_IdempotencyFingerprint",
            schema: "Policy",
            table: "PolicyEvaluationBundles",
            column: "IdempotencyFingerprint",
            unique: true);
    }
}
