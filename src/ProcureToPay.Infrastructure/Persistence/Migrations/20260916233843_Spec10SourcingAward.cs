using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureToPay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Spec10SourcingAward : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SourcingAwards",
                schema: "Sourcing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProcessId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RfqId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CurrentVersion = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourcingAwards", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SourcingCurrentAwardLines",
                schema: "Sourcing",
                columns: table => new
                {
                    LineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineVersion = table.Column<int>(type: "int", nullable: false),
                    AwardId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AwardVersion = table.Column<int>(type: "int", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourcingCurrentAwardLines", x => x.LineId);
                });

            migrationBuilder.CreateTable(
                name: "SourcingAwardVersions",
                schema: "Sourcing",
                columns: table => new
                {
                    AwardId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProcessId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RfqId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProposalVersion = table.Column<int>(type: "int", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestVersion = table.Column<int>(type: "int", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierVersion = table.Column<int>(type: "int", nullable: false),
                    SelectionBasis = table.Column<int>(type: "int", nullable: false),
                    EvaluationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EvaluationVersion = table.Column<int>(type: "int", nullable: true),
                    EvaluationContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    WaiverFactsId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LinesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DocumentJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AwardKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    PredecessorVersion = table.Column<int>(type: "int", nullable: true),
                    Superseded = table.Column<bool>(type: "bit", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourcingAwardVersions", x => new { x.AwardId, x.Version });
                    table.ForeignKey(
                        name: "FK_SourcingAwardVersions_SourcingAwards_AwardId",
                        column: x => x.AwardId,
                        principalSchema: "Sourcing",
                        principalTable: "SourcingAwards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SourcingAwards_OrganizationId_ProcessId_SupplierId",
                schema: "Sourcing",
                table: "SourcingAwards",
                columns: new[] { "OrganizationId", "ProcessId", "SupplierId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourcingAwardVersions_OrganizationId_ActorUserId_AwardKey",
                schema: "Sourcing",
                table: "SourcingAwardVersions",
                columns: new[] { "OrganizationId", "ActorUserId", "AwardKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourcingAwardVersions_OrganizationId_RfqId",
                schema: "Sourcing",
                table: "SourcingAwardVersions",
                columns: new[] { "OrganizationId", "RfqId" });

            migrationBuilder.CreateIndex(
                name: "IX_SourcingCurrentAwardLines_AwardId_AwardVersion",
                schema: "Sourcing",
                table: "SourcingCurrentAwardLines",
                columns: new[] { "AwardId", "AwardVersion" });

            // SPEC 10 REQ-12/NFR-02: an award version is append-only (only the superseded flag may
            // advance) and a line can only belong to one current award, even for a direct writer.
            migrationBuilder.Sql(
                "CREATE TRIGGER [Sourcing].[TR_AwardVersions_AppendOnly] " +
                "ON [Sourcing].[SourcingAwardVersions] AFTER UPDATE, DELETE AS " +
                "BEGIN SET NOCOUNT ON; " +
                "IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted) " +
                "THROW 51160, 'Award versions are append-only.', 1; " +
                "IF EXISTS (SELECT 1 FROM deleted d JOIN inserted i " +
                "ON i.[AwardId] = d.[AwardId] AND i.[Version] = d.[Version] " +
                "WHERE i.[DocumentJson] <> d.[DocumentJson] OR i.[ContentDigest] <> d.[ContentDigest] " +
                "OR i.[LinesJson] <> d.[LinesJson] OR i.[SupplierId] <> d.[SupplierId] " +
                "OR i.[ProposalId] <> d.[ProposalId] OR i.[ProposalVersion] <> d.[ProposalVersion] " +
                "OR d.[Superseded] = 1) " +
                "THROW 51161, 'A published award version is immutable.', 1; END");
            migrationBuilder.Sql(
                "ALTER TABLE [Sourcing].[SourcingCurrentAwardLines] WITH CHECK ADD CONSTRAINT " +
                "[CK_CurrentAwardLines_PositiveVersion] CHECK ([AwardVersion] >= 1);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [Sourcing].[TR_AwardVersions_AppendOnly];");
            migrationBuilder.DropTable(
                name: "SourcingAwardVersions",
                schema: "Sourcing");

            migrationBuilder.DropTable(
                name: "SourcingCurrentAwardLines",
                schema: "Sourcing");

            migrationBuilder.DropTable(
                name: "SourcingAwards",
                schema: "Sourcing");
        }
    }
}
