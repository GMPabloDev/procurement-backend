using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureToPay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApprovalDecisionEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RequirementVersion",
                schema: "Approval",
                table: "ApprovalDecisions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TaskVersion",
                schema: "Approval",
                table: "ApprovalDecisions",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequirementVersion",
                schema: "Approval",
                table: "ApprovalDecisions");

            migrationBuilder.DropColumn(
                name: "TaskVersion",
                schema: "Approval",
                table: "ApprovalDecisions");
        }
    }
}
