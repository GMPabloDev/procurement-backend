using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureToPay.Infrastructure.Persistence.Migrations
{
    [DbContext(typeof(ProcureToPayDbContext))]
    [Migration("20260909153608_PolicyActivationOrganizationAndIdempotency")]
    public partial class PolicyActivationOrganizationAndIdempotencyMigration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                schema: "Policy",
                table: "PolicyActivations",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_PolicyEvaluationBundles_IdempotencyFingerprint",
                schema: "Policy",
                table: "PolicyEvaluationBundles",
                column: "IdempotencyFingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PolicyActivations_OrganizationId_EffectiveFrom",
                schema: "Policy",
                table: "PolicyActivations",
                columns: new[] { "OrganizationId", "EffectiveFrom" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PolicyEvaluationBundles_IdempotencyFingerprint",
                schema: "Policy",
                table: "PolicyEvaluationBundles");

            migrationBuilder.DropIndex(
                name: "IX_PolicyActivations_OrganizationId_EffectiveFrom",
                schema: "Policy",
                table: "PolicyActivations");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                schema: "Policy",
                table: "PolicyActivations");
        }
    }
}
