using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureToPay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Spec05PolicyExceptionRequirementFk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddForeignKey(
                name: "FK_PolicyExceptionRequests_ApprovalRequirements_RequirementId",
                schema: "Approval",
                table: "PolicyExceptionRequests",
                column: "RequirementId",
                principalSchema: "Approval",
                principalTable: "ApprovalRequirements",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // SPEC 05 forbids destructive down migrations: reverting the application keeps the
            // Approval schema and its policy exception history.
            throw new NotSupportedException(
                "The Approval schema has no destructive downgrade; revert the application and keep the schema.");
        }
    }
}
