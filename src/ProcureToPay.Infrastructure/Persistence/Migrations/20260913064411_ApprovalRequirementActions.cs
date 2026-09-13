using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureToPay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApprovalRequirementActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Additive v2 column: existing rows (none on a clean baseline) would reject every
            // decision fail-closed rather than silently allowing undeclared actions (REQ-02).
            migrationBuilder.AddColumn<string>(
                name: "ActionsJson",
                schema: "Approval",
                table: "ApprovalRequirements",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // SPEC 03 forbids destructive down migrations: dropping the declared actions of
            // persisted requirements would silently widen what decisions remain admissible.
            throw new NotSupportedException(
                "The Approval schema has no destructive downgrade; revert the application and keep the schema.");
        }
    }
}
