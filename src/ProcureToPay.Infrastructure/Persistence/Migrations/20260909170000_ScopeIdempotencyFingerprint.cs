using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureToPay.Infrastructure.Persistence.Migrations;

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
