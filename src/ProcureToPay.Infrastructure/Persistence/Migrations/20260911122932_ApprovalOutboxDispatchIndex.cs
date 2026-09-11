using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureToPay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApprovalOutboxDispatchIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ApprovalOutboxEvents_OrganizationId_State_NextAttemptAt_CreatedAt",
                schema: "Approval",
                table: "ApprovalOutboxEvents",
                columns: new[] { "OrganizationId", "State", "NextAttemptAt", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ApprovalOutboxEvents_OrganizationId_State_NextAttemptAt_CreatedAt",
                schema: "Approval",
                table: "ApprovalOutboxEvents");
        }
    }
}
