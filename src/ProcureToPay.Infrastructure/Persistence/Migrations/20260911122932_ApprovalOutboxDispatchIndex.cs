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
            // SPEC 03 forbids destructive down migrations: reverting the application keeps the
            // Approval schema and its history, so a downgrade that would drop data is not supported.
            throw new NotSupportedException(
                "The Approval schema has no destructive downgrade; revert the application and keep the schema.");
        }
    }
}
