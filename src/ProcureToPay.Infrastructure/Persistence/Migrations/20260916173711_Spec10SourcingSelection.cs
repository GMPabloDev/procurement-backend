using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureToPay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Spec10SourcingSelection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SourcingSelections",
                schema: "Sourcing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProcessId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RfqId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CurrentVersion = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourcingSelections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SourcingSelectionVersions",
                schema: "Sourcing",
                columns: table => new
                {
                    SelectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProcessId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RfqId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineVersion = table.Column<int>(type: "int", nullable: false),
                    LineContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierVersion = table.Column<int>(type: "int", nullable: false),
                    QuotationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuotationVersion = table.Column<int>(type: "int", nullable: false),
                    QuotationContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EvaluationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EvaluationVersion = table.Column<int>(type: "int", nullable: true),
                    EvaluationContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Recommended = table.Column<bool>(type: "bit", nullable: false),
                    DeviationJustification = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourcingSelectionVersions", x => new { x.SelectionId, x.Version });
                    table.ForeignKey(
                        name: "FK_SourcingSelectionVersions_SourcingSelections_SelectionId",
                        column: x => x.SelectionId,
                        principalSchema: "Sourcing",
                        principalTable: "SourcingSelections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SourcingSelections_OrganizationId_RfqId_LineId",
                schema: "Sourcing",
                table: "SourcingSelections",
                columns: new[] { "OrganizationId", "RfqId", "LineId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourcingSelectionVersions_OrganizationId_RfqId",
                schema: "Sourcing",
                table: "SourcingSelectionVersions",
                columns: new[] { "OrganizationId", "RfqId" });

            // SPEC 10 REQ-09/NFR-02: the selection versions are append-only and the deviation
            // justification is mandatory exactly when the selected supplier is not recommended.
            migrationBuilder.Sql(
                "ALTER TABLE [Sourcing].[SourcingSelectionVersions] WITH CHECK ADD CONSTRAINT " +
                "[CK_SelectionVersions_Deviation] CHECK (" +
                "([Recommended] = 1 AND [DeviationJustification] IS NULL) OR " +
                "([Recommended] = 0 AND [DeviationJustification] IS NOT NULL));");
            migrationBuilder.Sql(
                "CREATE TRIGGER [Sourcing].[TR_SelectionVersions_AppendOnly] " +
                "ON [Sourcing].[SourcingSelectionVersions] AFTER UPDATE, DELETE AS " +
                "BEGIN SET NOCOUNT ON; " +
                "IF EXISTS (SELECT 1 FROM deleted) " +
                "THROW 51130, 'Selection versions are append-only.', 1; END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [Sourcing].[TR_SelectionVersions_AppendOnly];");
            migrationBuilder.DropTable(
                name: "SourcingSelectionVersions",
                schema: "Sourcing");

            migrationBuilder.DropTable(
                name: "SourcingSelections",
                schema: "Sourcing");
        }
    }
}
