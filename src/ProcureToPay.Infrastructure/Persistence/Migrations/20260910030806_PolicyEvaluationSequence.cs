using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureToPay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PolicyEvaluationSequenceMigration : Migration
    {
        /// <inheritdoc />
        // pi-lens-ignore: CS0111 (stale migration cache; companion partial is unique)
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "EvaluationSequence",
                schema: "Policy",
                table: "PolicyEvaluationBundles",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        // pi-lens-ignore: CS0111 (stale migration cache; companion partial is unique)
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EvaluationSequence",
                schema: "Policy",
                table: "PolicyEvaluationBundles");
        }
    }
}
