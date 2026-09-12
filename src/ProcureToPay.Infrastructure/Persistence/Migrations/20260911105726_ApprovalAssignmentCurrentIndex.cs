using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureToPay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApprovalAssignmentCurrentIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ApprovalAssignments_TaskId",
                schema: "Approval",
                table: "ApprovalAssignments");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalAssignments_TaskId_Current",
                schema: "Approval",
                table: "ApprovalAssignments",
                column: "TaskId",
                unique: true,
                filter: "[ReleasedAt] IS NULL");
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
