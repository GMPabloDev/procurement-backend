using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureToPay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApprovalContractV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The legacy v1 ActorId column is preserved for history and made nullable: v2 writes the
            // typed actor union into the new nullable columns and never fills the legacy column.
            migrationBuilder.AlterColumn<Guid>(
                name: "ActorId",
                schema: "Approval",
                table: "ApprovalAuditEntries",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<string>(
                name: "OwnerWorkloadClientId",
                schema: "Approval",
                table: "ApprovalPrerequisites",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "OwnerWorkloadIssuer",
                schema: "Approval",
                table: "ApprovalPrerequisites",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "SourceCommandId",
                schema: "Approval",
                table: "ApprovalOutboxEvents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ResultSourceId",
                schema: "Approval",
                table: "ApprovalOutboxEvents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResultSourceKey",
                schema: "Approval",
                table: "ApprovalOutboxEvents",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResultSourceType",
                schema: "Approval",
                table: "ApprovalOutboxEvents",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CaseStatusAfter",
                schema: "Approval",
                table: "ApprovalDecisions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CaseVersionAfter",
                schema: "Approval",
                table: "ApprovalDecisions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RequirementStatusAfter",
                schema: "Approval",
                table: "ApprovalDecisions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TaskStatusAfter",
                schema: "Approval",
                table: "ApprovalDecisions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ActorSystemId",
                schema: "Approval",
                table: "ApprovalAuditEntries",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ActorUserId",
                schema: "Approval",
                table: "ApprovalAuditEntries",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActorWorkloadClientId",
                schema: "Approval",
                table: "ApprovalAuditEntries",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActorWorkloadIssuer",
                schema: "Approval",
                table: "ApprovalAuditEntries",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AutomaticEffectKey",
                schema: "Approval",
                table: "ApprovalAuditEntries",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CausedByAuditId",
                schema: "Approval",
                table: "ApprovalAuditEntries",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CausedByAuditStream",
                schema: "Approval",
                table: "ApprovalAuditEntries",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ApprovalReconciliationRuns",
                schema: "Approval",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Trigger = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReconciliationKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    TriggerAuditId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CursorCaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseOwner = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    LockedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FencingToken = table.Column<long>(type: "bigint", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RootAuditId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalReconciliationRuns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalOutboxEvents_SourceCommandId",
                schema: "Approval",
                table: "ApprovalOutboxEvents",
                column: "SourceCommandId");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalAuditEntries_OrganizationId_AutomaticEffectKey",
                schema: "Approval",
                table: "ApprovalAuditEntries",
                columns: new[] { "OrganizationId", "AutomaticEffectKey" },
                unique: true,
                filter: "[AutomaticEffectKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalReconciliationRuns_AdminKey",
                schema: "Approval",
                table: "ApprovalReconciliationRuns",
                columns: new[] { "OrganizationId", "ActorUserId", "ReconciliationKey" },
                unique: true,
                filter: "[ReconciliationKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalReconciliationRuns_OrganizationId_Status_RequestedAt",
                schema: "Approval",
                table: "ApprovalReconciliationRuns",
                columns: new[] { "OrganizationId", "Status", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalReconciliationRuns_TriggerAudit",
                schema: "Approval",
                table: "ApprovalReconciliationRuns",
                columns: new[] { "OrganizationId", "TriggerAuditId" },
                unique: true,
                filter: "[TriggerAuditId] IS NOT NULL");
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
