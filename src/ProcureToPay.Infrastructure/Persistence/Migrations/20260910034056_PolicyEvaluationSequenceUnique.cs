using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureToPay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PolicyEvaluationSequenceUniqueMigration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_PolicyEvaluationBundles_OrganizationId_SubjectId_SubjectVersion_EvaluationSequence",
                schema: "Policy",
                table: "PolicyEvaluationBundles",
                columns: new[] { "OrganizationId", "SubjectId", "SubjectVersion", "EvaluationSequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PolicyEvaluationBundles_OrganizationId_SubjectId_SubjectVersion_EvaluationSequence",
                schema: "Policy",
                table: "PolicyEvaluationBundles");
        }
    }
}
