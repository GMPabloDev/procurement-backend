using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureToPay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Spec04ApprovalEvolution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ApprovalDecisions_OrganizationId_ActorUserId_DecisionKey",
                schema: "Approval",
                table: "ApprovalDecisions");

            migrationBuilder.AddColumn<Guid>(
                name: "DelegationId",
                schema: "Approval",
                table: "ApprovalReconciliationRuns",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DelegationScheduledAt",
                schema: "Approval",
                table: "ApprovalReconciliationRuns",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DelegationTransition",
                schema: "Approval",
                table: "ApprovalReconciliationRuns",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DelegationVersion",
                schema: "Approval",
                table: "ApprovalReconciliationRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "TaskVersion",
                schema: "Approval",
                table: "ApprovalDecisions",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<int>(
                name: "RequirementVersion",
                schema: "Approval",
                table: "ApprovalDecisions",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AlterColumn<string>(
                name: "Fingerprint",
                schema: "Approval",
                table: "ApprovalDecisions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64);

            migrationBuilder.AlterColumn<string>(
                name: "DecisionKey",
                schema: "Approval",
                table: "ApprovalDecisions",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(128)",
                oldMaxLength: 128);

            migrationBuilder.AlterColumn<Guid>(
                name: "ActorUserId",
                schema: "Approval",
                table: "ApprovalDecisions",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<string>(
                name: "ActorType",
                schema: "Approval",
                table: "ApprovalDecisions",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "EvidenceId",
                schema: "Approval",
                table: "ApprovalDecisions",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<int>(
                name: "EvidenceVersion",
                schema: "Approval",
                table: "ApprovalDecisions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "RootHumanDecisionId",
                schema: "Approval",
                table: "ApprovalDecisions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceDecisionId",
                schema: "Approval",
                table: "ApprovalDecisions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DelegationId",
                schema: "Approval",
                table: "ApprovalAssignments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DelegationVersion",
                schema: "Approval",
                table: "ApprovalAssignments",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ApprovalDelegations",
                schema: "Approval",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DelegatorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DelegateeUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    DecisionScopeJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    ValidFrom = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ValidTo = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ActorType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    DelegationCommandKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Fingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RootAuditId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    ActivatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ExpiredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalDelegations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApprovalDelegationTransitionJobs",
                schema: "Approval",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DelegationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DelegationVersion = table.Column<int>(type: "int", nullable: false),
                    Transition = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ScheduledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LeaseOwner = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    LockedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FencingToken = table.Column<long>(type: "bigint", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalDelegationTransitionJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CaseSupersessions",
                schema: "Approval",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PreviousCaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PreviousCaseVersion = table.Column<int>(type: "int", nullable: false),
                    NewCaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupersessionKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Fingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    WorkloadIssuer = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    WorkloadClientId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    TargetMappingJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseSupersessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DecisionAuthorityEvidences",
                schema: "Approval",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RootHumanDecisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Digest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EvidenceJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DecisionAuthorityEvidences", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DecisionCarryForwardRecords",
                schema: "Approval",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceDecisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RootHumanDecisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NewDecisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EvidenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EvidenceVersion = table.Column<int>(type: "int", nullable: false),
                    NewCaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NewRequirementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NewRequirementKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SourceRequirementContractDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NewRequirementContractDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ProofDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceTargetJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    NewTargetJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TargetMappingJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DecisionCarryForwardRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DecisionEvidenceRevocations",
                schema: "Approval",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EvidenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EvidenceVersion = table.Column<int>(type: "int", nullable: false),
                    DecisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RevocationKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Fingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ActorType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorWorkloadIssuer = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    ActorWorkloadClientId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ReasonCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DecisionEvidenceRevocations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalReconciliationRuns_DelegationTransition",
                schema: "Approval",
                table: "ApprovalReconciliationRuns",
                columns: new[] { "OrganizationId", "DelegationId", "DelegationVersion", "DelegationTransition", "DelegationScheduledAt" },
                unique: true,
                filter: "[DelegationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalDecisions_EvidenceId",
                schema: "Approval",
                table: "ApprovalDecisions",
                column: "EvidenceId");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalDecisions_OrganizationId_ActorUserId_DecisionKey",
                schema: "Approval",
                table: "ApprovalDecisions",
                columns: new[] { "OrganizationId", "ActorUserId", "DecisionKey" },
                unique: true,
                filter: "[DecisionKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalAssignments_DelegationId",
                schema: "Approval",
                table: "ApprovalAssignments",
                column: "DelegationId");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalDelegations_OrganizationId_ActorType_ActorUserId_DelegationCommandKey",
                schema: "Approval",
                table: "ApprovalDelegations",
                columns: new[] { "OrganizationId", "ActorType", "ActorUserId", "DelegationCommandKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalDelegations_OrganizationId_DelegateeUserId_Status",
                schema: "Approval",
                table: "ApprovalDelegations",
                columns: new[] { "OrganizationId", "DelegateeUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalDelegations_OrganizationId_DelegatorUserId_Role_Status",
                schema: "Approval",
                table: "ApprovalDelegations",
                columns: new[] { "OrganizationId", "DelegatorUserId", "Role", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalDelegationTransitionJobs_DelegationId_Transition_ScheduledAt",
                schema: "Approval",
                table: "ApprovalDelegationTransitionJobs",
                columns: new[] { "DelegationId", "Transition", "ScheduledAt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalDelegationTransitionJobs_OrganizationId_Status_ScheduledAt",
                schema: "Approval",
                table: "ApprovalDelegationTransitionJobs",
                columns: new[] { "OrganizationId", "Status", "ScheduledAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CaseSupersessions_NewCaseId",
                schema: "Approval",
                table: "CaseSupersessions",
                column: "NewCaseId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CaseSupersessions_OrganizationId_WorkloadIssuer_WorkloadClientId_SupersessionKey",
                schema: "Approval",
                table: "CaseSupersessions",
                columns: new[] { "OrganizationId", "WorkloadIssuer", "WorkloadClientId", "SupersessionKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CaseSupersessions_PreviousCaseId",
                schema: "Approval",
                table: "CaseSupersessions",
                column: "PreviousCaseId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DecisionAuthorityEvidences_OrganizationId_Status",
                schema: "Approval",
                table: "DecisionAuthorityEvidences",
                columns: new[] { "OrganizationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_DecisionAuthorityEvidences_RootHumanDecisionId",
                schema: "Approval",
                table: "DecisionAuthorityEvidences",
                column: "RootHumanDecisionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DecisionCarryForwardRecords_EvidenceId",
                schema: "Approval",
                table: "DecisionCarryForwardRecords",
                column: "EvidenceId");

            migrationBuilder.CreateIndex(
                name: "IX_DecisionCarryForwardRecords_NewDecisionId",
                schema: "Approval",
                table: "DecisionCarryForwardRecords",
                column: "NewDecisionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DecisionCarryForwardRecords_NewRequirementId",
                schema: "Approval",
                table: "DecisionCarryForwardRecords",
                column: "NewRequirementId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DecisionCarryForwardRecords_SourceDecisionId",
                schema: "Approval",
                table: "DecisionCarryForwardRecords",
                column: "SourceDecisionId");

            migrationBuilder.CreateIndex(
                name: "IX_DecisionEvidenceRevocations_EvidenceId",
                schema: "Approval",
                table: "DecisionEvidenceRevocations",
                column: "EvidenceId");

            migrationBuilder.CreateIndex(
                name: "IX_DecisionEvidenceRevocations_OrganizationId_EvidenceId_RevocationKey",
                schema: "Approval",
                table: "DecisionEvidenceRevocations",
                columns: new[] { "OrganizationId", "EvidenceId", "RevocationKey" },
                unique: true);

            // Migration backfill (SPEC 04 Migración, CA-06): every existing human decision gets its
            // stable DecisionAuthorityEvidence identity 1:1 from its own digest and inline JSON.
            // The historical decision digest is never recalculated or rewritten.
            migrationBuilder.Sql(
                """
                UPDATE [Approval].[ApprovalDecisions]
                SET [ActorType] = N'HUMAN'
                WHERE [ActorType] = N'';

                INSERT INTO [Approval].[DecisionAuthorityEvidences]
                    ([Id], [OrganizationId], [RootHumanDecisionId], [Digest], [EvidenceJson], [Version], [Status], [CreatedAt], [RevokedAt])
                SELECT NEWID(), d.[OrganizationId], d.[Id], d.[AuthorityEvidenceDigest], d.[EligibilityEvidenceJson], 1, N'VALID', d.[DecidedAt], NULL
                FROM [Approval].[ApprovalDecisions] d
                WHERE NOT EXISTS (
                    SELECT 1 FROM [Approval].[DecisionAuthorityEvidences] e
                    WHERE e.[RootHumanDecisionId] = d.[Id]);

                UPDATE d
                SET d.[EvidenceId] = e.[Id],
                    d.[EvidenceVersion] = 1,
                    d.[RootHumanDecisionId] = d.[Id]
                FROM [Approval].[ApprovalDecisions] d
                JOIN [Approval].[DecisionAuthorityEvidences] e ON e.[RootHumanDecisionId] = d.[Id]
                WHERE d.[EvidenceId] = '00000000-0000-0000-0000-000000000000';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApprovalDelegations",
                schema: "Approval");

            migrationBuilder.DropTable(
                name: "ApprovalDelegationTransitionJobs",
                schema: "Approval");

            migrationBuilder.DropTable(
                name: "CaseSupersessions",
                schema: "Approval");

            migrationBuilder.DropTable(
                name: "DecisionAuthorityEvidences",
                schema: "Approval");

            migrationBuilder.DropTable(
                name: "DecisionCarryForwardRecords",
                schema: "Approval");

            migrationBuilder.DropTable(
                name: "DecisionEvidenceRevocations",
                schema: "Approval");

            migrationBuilder.DropIndex(
                name: "IX_ApprovalReconciliationRuns_DelegationTransition",
                schema: "Approval",
                table: "ApprovalReconciliationRuns");

            migrationBuilder.DropIndex(
                name: "IX_ApprovalDecisions_EvidenceId",
                schema: "Approval",
                table: "ApprovalDecisions");

            migrationBuilder.DropIndex(
                name: "IX_ApprovalDecisions_OrganizationId_ActorUserId_DecisionKey",
                schema: "Approval",
                table: "ApprovalDecisions");

            migrationBuilder.DropIndex(
                name: "IX_ApprovalAssignments_DelegationId",
                schema: "Approval",
                table: "ApprovalAssignments");

            migrationBuilder.DropColumn(
                name: "DelegationId",
                schema: "Approval",
                table: "ApprovalReconciliationRuns");

            migrationBuilder.DropColumn(
                name: "DelegationScheduledAt",
                schema: "Approval",
                table: "ApprovalReconciliationRuns");

            migrationBuilder.DropColumn(
                name: "DelegationTransition",
                schema: "Approval",
                table: "ApprovalReconciliationRuns");

            migrationBuilder.DropColumn(
                name: "DelegationVersion",
                schema: "Approval",
                table: "ApprovalReconciliationRuns");

            migrationBuilder.DropColumn(
                name: "ActorType",
                schema: "Approval",
                table: "ApprovalDecisions");

            migrationBuilder.DropColumn(
                name: "EvidenceId",
                schema: "Approval",
                table: "ApprovalDecisions");

            migrationBuilder.DropColumn(
                name: "EvidenceVersion",
                schema: "Approval",
                table: "ApprovalDecisions");

            migrationBuilder.DropColumn(
                name: "RootHumanDecisionId",
                schema: "Approval",
                table: "ApprovalDecisions");

            migrationBuilder.DropColumn(
                name: "SourceDecisionId",
                schema: "Approval",
                table: "ApprovalDecisions");

            migrationBuilder.DropColumn(
                name: "DelegationId",
                schema: "Approval",
                table: "ApprovalAssignments");

            migrationBuilder.DropColumn(
                name: "DelegationVersion",
                schema: "Approval",
                table: "ApprovalAssignments");

            migrationBuilder.AlterColumn<int>(
                name: "TaskVersion",
                schema: "Approval",
                table: "ApprovalDecisions",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "RequirementVersion",
                schema: "Approval",
                table: "ApprovalDecisions",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Fingerprint",
                schema: "Approval",
                table: "ApprovalDecisions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "DecisionKey",
                schema: "Approval",
                table: "ApprovalDecisions",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(128)",
                oldMaxLength: 128,
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "ActorUserId",
                schema: "Approval",
                table: "ApprovalDecisions",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalDecisions_OrganizationId_ActorUserId_DecisionKey",
                schema: "Approval",
                table: "ApprovalDecisions",
                columns: new[] { "OrganizationId", "ActorUserId", "DecisionKey" },
                unique: true);
        }
    }
}
