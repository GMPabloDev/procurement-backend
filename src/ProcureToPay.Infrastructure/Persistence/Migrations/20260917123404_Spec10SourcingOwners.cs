using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureToPay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Spec10SourcingOwners : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SourcingOwnerAttempts",
                schema: "Sourcing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrerequisiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubjectType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    OwnerAdapterId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OwnerAdapterVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ProcessId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RfqId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TargetsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    SignalResult = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    CheckKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SignalKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    EvidenceDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    DueAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    LeaseOwner = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FencingToken = table.Column<long>(type: "bigint", nullable: false),
                    LastErrorCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AbandonedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourcingOwnerAttempts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SourcingOwnerEvidence",
                schema: "Sourcing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrerequisiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Result = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    SignalKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DocumentJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourcingOwnerEvidence", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SourcingPrerequisiteProcessorRegistrations",
                schema: "Sourcing",
                columns: table => new
                {
                    AdapterId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    AdapterVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ProcessorId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    WorkloadIssuer = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    WorkloadClientId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourcingPrerequisiteProcessorRegistrations", x => new { x.AdapterId, x.AdapterVersion });
                });

            migrationBuilder.CreateIndex(
                name: "IX_SourcingOwnerAttempts_DueAt",
                schema: "Sourcing",
                table: "SourcingOwnerAttempts",
                column: "DueAt");

            migrationBuilder.CreateIndex(
                name: "IX_SourcingOwnerAttempts_PrerequisiteId",
                schema: "Sourcing",
                table: "SourcingOwnerAttempts",
                column: "PrerequisiteId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourcingOwnerAttempts_State",
                schema: "Sourcing",
                table: "SourcingOwnerAttempts",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_SourcingOwnerEvidence_OrganizationId_PrerequisiteId_SignalKey",
                schema: "Sourcing",
                table: "SourcingOwnerEvidence",
                columns: new[] { "OrganizationId", "PrerequisiteId", "SignalKey" },
                unique: true);

            // SPEC 10 REQ-13/NFR-02: the owner evidence is append-only, so a satisfied prerequisite
            // keeps the exact document that justified its signal.
            migrationBuilder.Sql(
                "CREATE TRIGGER [Sourcing].[TR_OwnerEvidence_AppendOnly] " +
                "ON [Sourcing].[SourcingOwnerEvidence] AFTER UPDATE, DELETE AS " +
                "BEGIN SET NOCOUNT ON; " +
                "IF EXISTS (SELECT 1 FROM deleted) " +
                "THROW 51170, 'Owner evidence is append-only.', 1; END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [Sourcing].[TR_OwnerEvidence_AppendOnly];");
            migrationBuilder.DropTable(
                name: "SourcingOwnerAttempts",
                schema: "Sourcing");

            migrationBuilder.DropTable(
                name: "SourcingOwnerEvidence",
                schema: "Sourcing");

            migrationBuilder.DropTable(
                name: "SourcingPrerequisiteProcessorRegistrations",
                schema: "Sourcing");
        }
    }
}
