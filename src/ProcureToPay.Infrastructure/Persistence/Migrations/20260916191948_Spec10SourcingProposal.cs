using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureToPay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Spec10SourcingProposal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SourcingProposals",
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
                    table.PrimaryKey("PK_SourcingProposals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SourcingProposalVersions",
                schema: "Sourcing",
                columns: table => new
                {
                    ProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProcessId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RfqId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestVersion = table.Column<int>(type: "int", nullable: false),
                    RequestRefJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    RequestBundleRefJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    SelectionBasis = table.Column<int>(type: "int", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierVersion = table.Column<int>(type: "int", nullable: false),
                    EvaluationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EvaluationVersion = table.Column<int>(type: "int", nullable: true),
                    EvaluationContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    WaiverFactsId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LineIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TermsJson = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    DocumentJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ManifestJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ManifestDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    SubmissionKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ApprovalCaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ErrorCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CommandKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourcingProposalVersions", x => new { x.ProposalId, x.Version });
                    table.ForeignKey(
                        name: "FK_SourcingProposalVersions_SourcingProposals_ProposalId",
                        column: x => x.ProposalId,
                        principalSchema: "Sourcing",
                        principalTable: "SourcingProposals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SourcingProposals_OrganizationId_RfqId_SupplierId",
                schema: "Sourcing",
                table: "SourcingProposals",
                columns: new[] { "OrganizationId", "RfqId", "SupplierId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourcingProposalVersions_OrganizationId_ActorUserId_CommandKey",
                schema: "Sourcing",
                table: "SourcingProposalVersions",
                columns: new[] { "OrganizationId", "ActorUserId", "CommandKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourcingProposalVersions_OrganizationId_RfqId",
                schema: "Sourcing",
                table: "SourcingProposalVersions",
                columns: new[] { "OrganizationId", "RfqId" });

            // SPEC 10 REQ-10/NFR-02: proposal versions are append-only except for the approval
            // bookkeeping of their case, and the RFQ route demands its evaluation reference.
            migrationBuilder.Sql(
                "ALTER TABLE [Sourcing].[SourcingProposalVersions] WITH CHECK ADD CONSTRAINT " +
                "[CK_ProposalVersions_EvaluationRef] CHECK (" +
                "([SelectionBasis] = 1 AND [EvaluationId] IS NOT NULL AND [EvaluationVersion] IS NOT NULL) OR " +
                "([SelectionBasis] = 2 AND [EvaluationId] IS NULL AND [EvaluationVersion] IS NULL));");
            migrationBuilder.Sql(
                "CREATE TRIGGER [Sourcing].[TR_ProposalVersions_AppendOnly] " +
                "ON [Sourcing].[SourcingProposalVersions] AFTER UPDATE, DELETE AS " +
                "BEGIN SET NOCOUNT ON; " +
                "IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted) " +
                "THROW 51150, 'Proposal versions are append-only.', 1; " +
                // Only the approval bookkeeping may be filled in or advanced after the write.
                "IF EXISTS (SELECT 1 FROM deleted d JOIN inserted i " +
                "ON i.[ProposalId] = d.[ProposalId] AND i.[Version] = d.[Version] " +
                "WHERE i.[DocumentJson] <> d.[DocumentJson] OR i.[ContentDigest] <> d.[ContentDigest] " +
                "OR i.[ManifestJson] <> d.[ManifestJson] OR i.[ManifestDigest] <> d.[ManifestDigest] " +
                "OR i.[LineIdsJson] <> d.[LineIdsJson] OR i.[TermsJson] <> d.[TermsJson] " +
                "OR i.[SupplierId] <> d.[SupplierId] OR i.[SupplierVersion] <> d.[SupplierVersion] " +
                "OR i.[RequestId] <> d.[RequestId] OR i.[RequestVersion] <> d.[RequestVersion]) " +
                "THROW 51151, 'A written proposal version is immutable.', 1; END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [Sourcing].[TR_ProposalVersions_AppendOnly];");
            migrationBuilder.DropTable(
                name: "SourcingProposalVersions",
                schema: "Sourcing");

            migrationBuilder.DropTable(
                name: "SourcingProposals",
                schema: "Sourcing");
        }
    }
}
