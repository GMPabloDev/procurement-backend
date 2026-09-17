using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureToPay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Spec10SourcingAwardLineage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SourcingAwards_OrganizationId_ProcessId_SupplierId",
                schema: "Sourcing",
                table: "SourcingAwards");

            migrationBuilder.CreateIndex(
                name: "IX_SourcingAwards_OrganizationId_ProcessId",
                schema: "Sourcing",
                table: "SourcingAwards",
                columns: new[] { "OrganizationId", "ProcessId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SourcingAwards_OrganizationId_ProcessId",
                schema: "Sourcing",
                table: "SourcingAwards");

            migrationBuilder.CreateIndex(
                name: "IX_SourcingAwards_OrganizationId_ProcessId_SupplierId",
                schema: "Sourcing",
                table: "SourcingAwards",
                columns: new[] { "OrganizationId", "ProcessId", "SupplierId" },
                unique: true);
        }
    }
}
