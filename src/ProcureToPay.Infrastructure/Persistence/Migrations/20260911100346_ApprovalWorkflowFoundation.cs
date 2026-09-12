using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureToPay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApprovalWorkflowFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "Approval");

            migrationBuilder.CreateTable(
                name: "ApprovalAssignments",
                schema: "Approval",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssigneeUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssignedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Load = table.Column<int>(type: "int", nullable: false),
                    Cause = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    EligibilityEvidenceJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalAssignments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApprovalAuditEntries",
                schema: "Approval",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RequirementId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    TargetType = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScopeJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    BeforeJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AfterJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CorrelationReference = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalAuditEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApprovalCases",
                schema: "Approval",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubjectType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SubjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubjectVersion = table.Column<int>(type: "int", nullable: false),
                    Operation = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SourceSnapshotDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    WorkloadIssuer = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    WorkloadClientId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SubmissionKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SubmissionFingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    OriginatorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequesterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CorrelationReference = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancellationReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalCases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApprovalDecisions",
                schema: "Approval",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequirementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Action = table.Column<int>(type: "int", nullable: false),
                    Origin = table.Column<int>(type: "int", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DecisionKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Fingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DecisionDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AuthorityEvidenceDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EligibilityEvidenceJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CorrelationReference = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalDecisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApprovalDecisionTargets",
                schema: "Approval",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DecisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequirementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetVersion = table.Column<int>(type: "int", nullable: false),
                    MaterialSnapshotDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalDecisionTargets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApprovalOutboxEvents",
                schema: "Approval",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequirementId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TargetType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetVersion = table.Column<int>(type: "int", nullable: false),
                    MaterialSnapshotDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Result = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ContractVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CorrelationReference = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    LockOwner = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    LockedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalOutboxEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApprovalPrerequisites",
                schema: "Approval",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OwnerAdapterId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OwnerAdapterVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SourceControlType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SourceControlDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ParametersJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TargetsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    SignalKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    SignalFingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalPrerequisites", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApprovalPrerequisiteSignals",
                schema: "Approval",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrerequisiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SignalKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SignalFingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Satisfied = table.Column<bool>(type: "bit", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ActorType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EvidenceReference = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    EvidenceDigest = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CorrelationReference = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalPrerequisiteSignals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApprovalRequirements",
                schema: "Approval",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceRequirementKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    WorkflowRequirementKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    StageCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    AuthorityJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DecisionScopeJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    ExcludedUserIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TargetsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DependenciesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalRequirements", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApprovalSubmissionReservations",
                schema: "Approval",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkloadIssuer = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    WorkloadClientId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SubjectType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Operation = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SubmissionKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Fingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalSubmissionReservations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApprovalTasks",
                schema: "Approval",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequirementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CurrentAssigneeUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApprovalWorkflowStates",
                schema: "Approval",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LastReconciliationCompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastReconciliationOwner = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalWorkflowStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalAssignments_OrganizationId_AssigneeUserId_ReleasedAt",
                schema: "Approval",
                table: "ApprovalAssignments",
                columns: new[] { "OrganizationId", "AssigneeUserId", "ReleasedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalAssignments_TaskId",
                schema: "Approval",
                table: "ApprovalAssignments",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalAuditEntries_OrganizationId_OccurredAt",
                schema: "Approval",
                table: "ApprovalAuditEntries",
                columns: new[] { "OrganizationId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalAuditEntries_TargetType_TargetId_OccurredAt",
                schema: "Approval",
                table: "ApprovalAuditEntries",
                columns: new[] { "TargetType", "TargetId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalCases_OrganizationId_SubjectId_SubjectVersion",
                schema: "Approval",
                table: "ApprovalCases",
                columns: new[] { "OrganizationId", "SubjectId", "SubjectVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalCases_OrganizationId_WorkloadIssuer_WorkloadClientId_SubjectType_Operation_SubmissionKey",
                schema: "Approval",
                table: "ApprovalCases",
                columns: new[] { "OrganizationId", "WorkloadIssuer", "WorkloadClientId", "SubjectType", "Operation", "SubmissionKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalDecisions_OrganizationId_ActorUserId_DecidedAt",
                schema: "Approval",
                table: "ApprovalDecisions",
                columns: new[] { "OrganizationId", "ActorUserId", "DecidedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalDecisions_OrganizationId_ActorUserId_DecisionKey",
                schema: "Approval",
                table: "ApprovalDecisions",
                columns: new[] { "OrganizationId", "ActorUserId", "DecisionKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalDecisions_RequirementId",
                schema: "Approval",
                table: "ApprovalDecisions",
                column: "RequirementId");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalDecisionTargets_DecisionId",
                schema: "Approval",
                table: "ApprovalDecisionTargets",
                column: "DecisionId");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalDecisionTargets_RequirementId_TargetType_TargetId_TargetVersion_MaterialSnapshotDigest",
                schema: "Approval",
                table: "ApprovalDecisionTargets",
                columns: new[] { "RequirementId", "TargetType", "TargetId", "TargetVersion", "MaterialSnapshotDigest" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalOutboxEvents_CaseId",
                schema: "Approval",
                table: "ApprovalOutboxEvents",
                column: "CaseId");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalOutboxEvents_State_NextAttemptAt_CreatedAt",
                schema: "Approval",
                table: "ApprovalOutboxEvents",
                columns: new[] { "State", "NextAttemptAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalPrerequisites_CaseId_Key",
                schema: "Approval",
                table: "ApprovalPrerequisites",
                columns: new[] { "CaseId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalPrerequisites_OrganizationId_Status",
                schema: "Approval",
                table: "ApprovalPrerequisites",
                columns: new[] { "OrganizationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalPrerequisiteSignals_CaseId",
                schema: "Approval",
                table: "ApprovalPrerequisiteSignals",
                column: "CaseId");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalPrerequisiteSignals_OrganizationId_PrerequisiteId_SignalKey",
                schema: "Approval",
                table: "ApprovalPrerequisiteSignals",
                columns: new[] { "OrganizationId", "PrerequisiteId", "SignalKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequirements_CaseId_WorkflowRequirementKey",
                schema: "Approval",
                table: "ApprovalRequirements",
                columns: new[] { "CaseId", "WorkflowRequirementKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequirements_OrganizationId_Status",
                schema: "Approval",
                table: "ApprovalRequirements",
                columns: new[] { "OrganizationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalSubmissionReservations_CaseId",
                schema: "Approval",
                table: "ApprovalSubmissionReservations",
                column: "CaseId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalSubmissionReservations_OrganizationId_WorkloadIssuer_WorkloadClientId_SubjectType_Operation_SubmissionKey",
                schema: "Approval",
                table: "ApprovalSubmissionReservations",
                columns: new[] { "OrganizationId", "WorkloadIssuer", "WorkloadClientId", "SubjectType", "Operation", "SubmissionKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalTasks_CaseId_Status",
                schema: "Approval",
                table: "ApprovalTasks",
                columns: new[] { "CaseId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalTasks_OrganizationId_Status_CurrentAssigneeUserId",
                schema: "Approval",
                table: "ApprovalTasks",
                columns: new[] { "OrganizationId", "Status", "CurrentAssigneeUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalTasks_RequirementId",
                schema: "Approval",
                table: "ApprovalTasks",
                column: "RequirementId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalWorkflowStates_OrganizationId",
                schema: "Approval",
                table: "ApprovalWorkflowStates",
                column: "OrganizationId",
                unique: true);
        }

        /// <inheritdoc />
protected override void Down(MigrationBuilder migrationBuilder)
        {
            // SPEC 03 forbids destructive down migrations: reverting the application keeps the
            // Approval schema and its history, so a downgrade that would drop data is not supported.
            throw new NotSupportedException(
                "The Approval schema has no destructive downgrade; revert the application and keep the schema.");
        }
    }
}
