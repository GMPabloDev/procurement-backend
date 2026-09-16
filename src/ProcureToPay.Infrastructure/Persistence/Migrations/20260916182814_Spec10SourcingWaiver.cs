using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureToPay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Spec10SourcingWaiver : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SourcingWaiverFacts",
                schema: "Sourcing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProcessId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RfqId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RfqVersion = table.Column<int>(type: "int", nullable: false),
                    PrerequisiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequirementKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    BaseBundleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BaseResultDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PolicyVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PolicyContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ManifestDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    From = table.Column<int>(type: "int", nullable: false),
                    To = table.Column<int>(type: "int", nullable: false),
                    Floor = table.Column<int>(type: "int", nullable: false),
                    TargetsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DocumentJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CommandKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ApprovalCaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApprovalRequirementId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourcingWaiverFacts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SourcingWaiverFacts_OrganizationId_ActorUserId_CommandKey",
                schema: "Sourcing",
                table: "SourcingWaiverFacts",
                columns: new[] { "OrganizationId", "ActorUserId", "CommandKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourcingWaiverFacts_OrganizationId_RfqId",
                schema: "Sourcing",
                table: "SourcingWaiverFacts",
                columns: new[] { "OrganizationId", "RfqId" });
            // SPEC 10 REQ-05/NFR-02: the recalculated facts are append-only evidence, and only the
            // link to the approval case may be filled in after the case exists.
            migrationBuilder.Sql(
                "ALTER TABLE [Sourcing].[SourcingWaiverFacts] WITH CHECK ADD CONSTRAINT " +
                "[CK_WaiverFacts_Reduction] CHECK ([Floor] >= 1 AND [To] >= [Floor] AND [To] < [From]);");
            migrationBuilder.Sql(
                "CREATE TRIGGER [Sourcing].[TR_WaiverFacts_AppendOnly] " +
                "ON [Sourcing].[SourcingWaiverFacts] AFTER UPDATE, DELETE AS " +
                "BEGIN SET NOCOUNT ON; " +
                "IF EXISTS (SELECT 1 FROM deleted d LEFT JOIN inserted i " +
                "ON i.[Id] = d.[Id] WHERE i.[Id] IS NULL OR " +
                "i.[DocumentJson] <> d.[DocumentJson] OR i.[ContentDigest] <> d.[ContentDigest]) " +
                "THROW 51140, 'Waiver facts are append-only.', 1; END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [Sourcing].[TR_WaiverFacts_AppendOnly];");
            migrationBuilder.DropTable(
                name: "SourcingWaiverFacts",
                schema: "Sourcing");
        }
    }
}
