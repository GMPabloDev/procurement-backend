using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureToPay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Spec10SourcingEvaluation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SourcingEvaluations",
                schema: "Sourcing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RfqId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CurrentVersion = table.Column<int>(type: "int", nullable: false),
                    ConsumedVersion = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourcingEvaluations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SourcingFxSnapshots",
                schema: "Sourcing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RfqId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BaseCurrency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    SourceCurrency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    Rate = table.Column<decimal>(type: "decimal(38,12)", precision: 38, scale: 12, nullable: false),
                    EffectiveAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SourceReference = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    AttachmentJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    DocumentJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourcingFxSnapshots", x => new { x.Id, x.Version });
                });

            migrationBuilder.CreateTable(
                name: "SourcingManualScores",
                schema: "Sourcing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RfqId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineVersion = table.Column<int>(type: "int", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierVersion = table.Column<int>(type: "int", nullable: false),
                    Criterion = table.Column<int>(type: "int", nullable: false),
                    Score = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    Justification = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    EvidenceJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourcingManualScores", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QuoteEvaluationVersions",
                schema: "Sourcing",
                columns: table => new
                {
                    EvaluationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RfqId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RfqVersion = table.Column<int>(type: "int", nullable: false),
                    RfqContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    BaseCurrency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    DocumentJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RecommendationsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    QuotationRefsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FxSnapshotRefsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteEvaluationVersions", x => new { x.EvaluationId, x.Version });
                    table.ForeignKey(
                        name: "FK_QuoteEvaluationVersions_SourcingEvaluations_EvaluationId",
                        column: x => x.EvaluationId,
                        principalSchema: "Sourcing",
                        principalTable: "SourcingEvaluations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QuoteEvaluationVersions_OrganizationId_RfqId",
                schema: "Sourcing",
                table: "QuoteEvaluationVersions",
                columns: new[] { "OrganizationId", "RfqId" });

            migrationBuilder.CreateIndex(
                name: "IX_SourcingEvaluations_OrganizationId",
                schema: "Sourcing",
                table: "SourcingEvaluations",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_SourcingEvaluations_RfqId",
                schema: "Sourcing",
                table: "SourcingEvaluations",
                column: "RfqId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourcingFxSnapshots_OrganizationId_RfqId_SourceCurrency",
                schema: "Sourcing",
                table: "SourcingFxSnapshots",
                columns: new[] { "OrganizationId", "RfqId", "SourceCurrency" });

            migrationBuilder.CreateIndex(
                name: "IX_SourcingManualScores_OrganizationId_RfqId_LineId_SupplierId_Criterion",
                schema: "Sourcing",
                table: "SourcingManualScores",
                columns: new[] { "OrganizationId", "RfqId", "LineId", "SupplierId", "Criterion" });

            // SPEC 10 NFR-02/NFR-08: the evaluation, its snapshots and its manual scores are
            // append-only, and a rate or a score outside its contract range cannot be written at all.
            migrationBuilder.Sql(
                "ALTER TABLE [Sourcing].[SourcingFxSnapshots] WITH CHECK ADD CONSTRAINT " +
                "[CK_FxSnapshots_PositiveRate] CHECK ([Rate] > 0);");
            migrationBuilder.Sql(
                "ALTER TABLE [Sourcing].[SourcingManualScores] WITH CHECK ADD CONSTRAINT " +
                "[CK_ManualScores_Range] CHECK ([Score] >= 0 AND [Score] <= 100);");
            migrationBuilder.Sql(
                "CREATE TRIGGER [Sourcing].[TR_FxSnapshots_AppendOnly] " +
                "ON [Sourcing].[SourcingFxSnapshots] AFTER UPDATE, DELETE AS " +
                "BEGIN SET NOCOUNT ON; " +
                "IF EXISTS (SELECT 1 FROM deleted) " +
                "THROW 51120, 'FX snapshots are append-only.', 1; END");
            migrationBuilder.Sql(
                "CREATE TRIGGER [Sourcing].[TR_ManualScores_AppendOnly] " +
                "ON [Sourcing].[SourcingManualScores] AFTER UPDATE, DELETE AS " +
                "BEGIN SET NOCOUNT ON; " +
                "IF EXISTS (SELECT 1 FROM deleted) " +
                "THROW 51121, 'Manual scores are append-only.', 1; END");
            migrationBuilder.Sql(
                "CREATE TRIGGER [Sourcing].[TR_EvaluationVersions_AppendOnly] " +
                "ON [Sourcing].[QuoteEvaluationVersions] AFTER UPDATE, DELETE AS " +
                "BEGIN SET NOCOUNT ON; " +
                "IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted) " +
                "THROW 51122, 'Evaluation versions are append-only.', 1; " +
                // The single admitted update marks a current version as consumed by a proposal (REQ-07).
                "IF EXISTS (SELECT 1 FROM deleted d JOIN inserted i " +
                "ON i.[EvaluationId] = d.[EvaluationId] AND i.[Version] = d.[Version] " +
                "WHERE d.[ConsumedAt] IS NOT NULL OR i.[ConsumedAt] IS NULL " +
                "OR i.[DocumentJson] <> d.[DocumentJson] OR i.[ContentDigest] <> d.[ContentDigest] " +
                "OR i.[RfqId] <> d.[RfqId] OR i.[RfqVersion] <> d.[RfqVersion] " +
                "OR i.[RfqContentDigest] <> d.[RfqContentDigest] OR i.[BaseCurrency] <> d.[BaseCurrency] " +
                "OR i.[RecommendationsJson] <> d.[RecommendationsJson] " +
                "OR i.[QuotationRefsJson] <> d.[QuotationRefsJson] " +
                "OR i.[FxSnapshotRefsJson] <> d.[FxSnapshotRefsJson]) " +
                "THROW 51123, 'A consumed evaluation version is immutable.', 1; END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [Sourcing].[TR_EvaluationVersions_AppendOnly];");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [Sourcing].[TR_ManualScores_AppendOnly];");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [Sourcing].[TR_FxSnapshots_AppendOnly];");
            migrationBuilder.DropTable(
                name: "QuoteEvaluationVersions",
                schema: "Sourcing");

            migrationBuilder.DropTable(
                name: "SourcingFxSnapshots",
                schema: "Sourcing");

            migrationBuilder.DropTable(
                name: "SourcingManualScores",
                schema: "Sourcing");

            migrationBuilder.DropTable(
                name: "SourcingEvaluations",
                schema: "Sourcing");
        }
    }
}
