using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureToPay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Spec08BudgetLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "Budget");

            migrationBuilder.CreateTable(
                name: "AuditRecords",
                schema: "Budget",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorJson = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    CauseStream = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    CauseAuditId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Action = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TargetType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PreviousVersion = table.Column<int>(type: "int", nullable: true),
                    NewVersion = table.Column<int>(type: "int", nullable: false),
                    DeltasJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    CorrelationReference = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MovementProducerRegistrations",
                schema: "Budget",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Operation = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ContractVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ProducerId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    WorkloadIssuer = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    WorkloadClientId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MovementProducerRegistrations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Operations",
                schema: "Budget",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    OperationKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Fingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceVersion = table.Column<int>(type: "int", nullable: false),
                    SourceDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ActorJson = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    CauseStream = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    CauseAuditId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReasonCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Result = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CorrelationReference = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Operations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Positions",
                schema: "Budget",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CostCenterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FiscalYear = table.Column<int>(type: "int", nullable: false),
                    SpendCategoryCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PositionKeyDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CurrentAllocationVersion = table.Column<int>(type: "int", nullable: false),
                    CurrentBalanceVersion = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Positions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PrerequisiteAttempts",
                schema: "Budget",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrerequisiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrerequisiteKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestVersion = table.Column<int>(type: "int", nullable: false),
                    ParametersJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    ParametersDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceControlDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RequestKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ReserveKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SignalKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CompensateKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    RequestOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReserveOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReverseOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SignalResult = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    EvidenceDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LastErrorCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    DueAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    LeaseOwner = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FencingToken = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrerequisiteAttempts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PrerequisiteProcessorRegistrations",
                schema: "Budget",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AdapterId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    AdapterVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ProcessorId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrerequisiteProcessorRegistrations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseRequestReleaseAttempts",
                schema: "Budget",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestVersion = table.Column<int>(type: "int", nullable: false),
                    CaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReleaseKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ReleaseOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    LastErrorCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseRequestReleaseAttempts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AllocationVersions",
                schema: "Budget",
                columns: table => new
                {
                    PositionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CostCenterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CostCenterVersion = table.Column<int>(type: "int", nullable: false),
                    SpendCategoryCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SpendCategoryVersion = table.Column<int>(type: "int", nullable: false),
                    SpendCategoryDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AllocatedAmount = table.Column<decimal>(type: "decimal(38,12)", precision: 38, scale: 12, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    PredecessorVersion = table.Column<int>(type: "int", nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AllocationKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Fingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AllocationVersions", x => new { x.PositionId, x.Version });
                    table.ForeignKey(
                        name: "FK_AllocationVersions_Positions_PositionId",
                        column: x => x.PositionId,
                        principalSchema: "Budget",
                        principalTable: "Positions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Balances",
                schema: "Budget",
                columns: table => new
                {
                    PositionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BalanceVersion = table.Column<int>(type: "int", nullable: false),
                    AllocationVersion = table.Column<int>(type: "int", nullable: false),
                    Allocated = table.Column<decimal>(type: "decimal(38,12)", precision: 38, scale: 12, nullable: false),
                    Reserved = table.Column<decimal>(type: "decimal(38,12)", precision: 38, scale: 12, nullable: false),
                    Committed = table.Column<decimal>(type: "decimal(38,12)", precision: 38, scale: 12, nullable: false),
                    Consumed = table.Column<decimal>(type: "decimal(38,12)", precision: 38, scale: 12, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Balances", x => x.PositionId);
                    table.ForeignKey(
                        name: "FK_Balances_Positions_PositionId",
                        column: x => x.PositionId,
                        principalSchema: "Budget",
                        principalTable: "Positions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Movements",
                schema: "Budget",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PositionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AllocationVersionAtPosting = table.Column<int>(type: "int", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(38,12)", precision: 38, scale: 12, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    ParentMovementId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TargetVersion = table.Column<int>(type: "int", nullable: true),
                    TargetMaterialSnapshotDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    TargetType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    BalanceVersionAfter = table.Column<int>(type: "int", nullable: false),
                    AllocatedBefore = table.Column<decimal>(type: "decimal(38,12)", precision: 38, scale: 12, nullable: false),
                    ReservedBefore = table.Column<decimal>(type: "decimal(38,12)", precision: 38, scale: 12, nullable: false),
                    CommittedBefore = table.Column<decimal>(type: "decimal(38,12)", precision: 38, scale: 12, nullable: false),
                    ConsumedBefore = table.Column<decimal>(type: "decimal(38,12)", precision: 38, scale: 12, nullable: false),
                    AllocatedAfter = table.Column<decimal>(type: "decimal(38,12)", precision: 38, scale: 12, nullable: false),
                    ReservedAfter = table.Column<decimal>(type: "decimal(38,12)", precision: 38, scale: 12, nullable: false),
                    CommittedAfter = table.Column<decimal>(type: "decimal(38,12)", precision: 38, scale: 12, nullable: false),
                    ConsumedAfter = table.Column<decimal>(type: "decimal(38,12)", precision: 38, scale: 12, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Movements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Movements_Operations_OperationId",
                        column: x => x.OperationId,
                        principalSchema: "Budget",
                        principalTable: "Operations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Movements_Positions_PositionId",
                        column: x => x.PositionId,
                        principalSchema: "Budget",
                        principalTable: "Positions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AllocationVersions_OrganizationId_PositionId_AllocationKey",
                schema: "Budget",
                table: "AllocationVersions",
                columns: new[] { "OrganizationId", "PositionId", "AllocationKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditRecords_OrganizationId_TargetType_TargetId",
                schema: "Budget",
                table: "AuditRecords",
                columns: new[] { "OrganizationId", "TargetType", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "IX_MovementProducerRegistrations_Operation_ContractVersion_SourceType",
                schema: "Budget",
                table: "MovementProducerRegistrations",
                columns: new[] { "Operation", "ContractVersion", "SourceType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Movements_OperationId",
                schema: "Budget",
                table: "Movements",
                column: "OperationId");

            migrationBuilder.CreateIndex(
                name: "IX_Movements_OrganizationId_PositionId",
                schema: "Budget",
                table: "Movements",
                columns: new[] { "OrganizationId", "PositionId" });

            migrationBuilder.CreateIndex(
                name: "IX_Movements_OrganizationId_TargetId",
                schema: "Budget",
                table: "Movements",
                columns: new[] { "OrganizationId", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "IX_Movements_ParentMovementId",
                schema: "Budget",
                table: "Movements",
                column: "ParentMovementId");

            migrationBuilder.CreateIndex(
                name: "IX_Movements_PositionId",
                schema: "Budget",
                table: "Movements",
                column: "PositionId");

            migrationBuilder.CreateIndex(
                name: "IX_Operations_OrganizationId",
                schema: "Budget",
                table: "Operations",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Operations_OrganizationId_SourceType_SourceId",
                schema: "Budget",
                table: "Operations",
                columns: new[] { "OrganizationId", "SourceType", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_Positions_OrganizationId_CostCenterId_FiscalYear",
                schema: "Budget",
                table: "Positions",
                columns: new[] { "OrganizationId", "CostCenterId", "FiscalYear" });

            migrationBuilder.CreateIndex(
                name: "IX_Positions_OrganizationId_PositionKeyDigest",
                schema: "Budget",
                table: "Positions",
                columns: new[] { "OrganizationId", "PositionKeyDigest" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrerequisiteAttempts_PrerequisiteId",
                schema: "Budget",
                table: "PrerequisiteAttempts",
                column: "PrerequisiteId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrerequisiteAttempts_State_NextAttemptAt",
                schema: "Budget",
                table: "PrerequisiteAttempts",
                columns: new[] { "State", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PrerequisiteProcessorRegistrations_AdapterId_AdapterVersion",
                schema: "Budget",
                table: "PrerequisiteProcessorRegistrations",
                columns: new[] { "AdapterId", "AdapterVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRequestReleaseAttempts_OrganizationId_ReleaseKey",
                schema: "Budget",
                table: "PurchaseRequestReleaseAttempts",
                columns: new[] { "OrganizationId", "ReleaseKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRequestReleaseAttempts_OrganizationId_RequestId_RequestVersion",
                schema: "Budget",
                table: "PurchaseRequestReleaseAttempts",
                columns: new[] { "OrganizationId", "RequestId", "RequestVersion" },
                unique: true);

            // SPEC 08 NFR-01: allocation revisions and movements are append-only, and the root
            // pointers may only advance to a row that already exists. These invariants hold even for
            // a direct SQL writer, not just for the application service.
            migrationBuilder.Sql(
                "ALTER TABLE [Budget].[AllocationVersions] WITH CHECK ADD CONSTRAINT " +
                "[CK_AllocationVersions_VersionMonotonic] CHECK (" +
                "([Version] = 1 AND [PredecessorVersion] IS NULL) OR " +
                "([Version] > 1 AND [PredecessorVersion] = [Version] - 1));");
            migrationBuilder.Sql(
                "ALTER TABLE [Budget].[AllocationVersions] WITH CHECK ADD CONSTRAINT " +
                "[CK_AllocationVersions_NonNegative] CHECK ([AllocatedAmount] >= 0);");
            migrationBuilder.Sql(
                "ALTER TABLE [Budget].[Balances] WITH CHECK ADD CONSTRAINT " +
                "[CK_Balances_NonNegative] CHECK ([Allocated] >= 0 AND [Reserved] >= 0 AND " +
                "[Committed] >= 0 AND [Consumed] >= 0);");
            migrationBuilder.Sql(
                "ALTER TABLE [Budget].[Movements] WITH CHECK ADD CONSTRAINT " +
                "[CK_Movements_PositiveAmount] CHECK ([Amount] > 0);");
            migrationBuilder.Sql(
                "CREATE TRIGGER [Budget].[TR_AllocationVersions_AppendOnly] " +
                "ON [Budget].[AllocationVersions] AFTER UPDATE, DELETE AS " +
                "BEGIN SET NOCOUNT ON; " +
                "IF EXISTS (SELECT 1 FROM deleted) " +
                "THROW 51010, 'Budget allocation versions are append-only.', 1; END");
            migrationBuilder.Sql(
                "CREATE TRIGGER [Budget].[TR_Movements_AppendOnly] " +
                "ON [Budget].[Movements] AFTER UPDATE, DELETE AS " +
                "BEGIN SET NOCOUNT ON; " +
                "IF EXISTS (SELECT 1 FROM deleted) " +
                "THROW 51011, 'Budget movements are append-only.', 1; END");
            migrationBuilder.Sql(
                "CREATE TRIGGER [Budget].[TR_Operations_AppendOnly] " +
                "ON [Budget].[Operations] AFTER UPDATE, DELETE AS " +
                "BEGIN SET NOCOUNT ON; " +
                "IF EXISTS (SELECT 1 FROM deleted) " +
                "THROW 51012, 'Budget operations are append-only.', 1; END");
            migrationBuilder.Sql(
                "CREATE TRIGGER [Budget].[TR_BudgetAudit_AppendOnly] " +
                "ON [Budget].[AuditRecords] AFTER UPDATE, DELETE AS " +
                "BEGIN SET NOCOUNT ON; " +
                "IF EXISTS (SELECT 1 FROM deleted) " +
                "THROW 51013, 'Budget audit records are append-only.', 1; END");
            // A successor revision must be exactly the next version of its position and keep an
            // existing predecessor, so history cannot skip a version or start at an orphan.
            migrationBuilder.Sql(
                "CREATE TRIGGER [Budget].[TR_AllocationVersions_PredecessorExists] " +
                "ON [Budget].[AllocationVersions] AFTER INSERT AS " +
                "BEGIN SET NOCOUNT ON; " +
                "IF EXISTS (SELECT 1 FROM inserted i WHERE " +
                "(i.Version > 1 AND i.Version <> (SELECT r.[CurrentAllocationVersion] + 1 " +
                "FROM [Budget].[Positions] r WHERE r.[Id] = i.[PositionId])) OR " +
                "(i.Version > 1 AND NOT EXISTS (SELECT 1 FROM [Budget].[AllocationVersions] v " +
                "WHERE v.[PositionId] = i.[PositionId] AND v.[Version] = i.[PredecessorVersion]))) " +
                "THROW 51014, 'A budget allocation revision must succeed the current one and keep its predecessor.', 1; END");
            // The root keeps its identity. An allocation revision advances the allocation pointer by
            // exactly one version to an existing row and never touches the balance pointer; a
            // movement advances the balance pointer by exactly one and never touches identity nor the
            // allocation pointer.
            migrationBuilder.Sql(
                "CREATE TRIGGER [Budget].[TR_Positions_IdentityStable] " +
                "ON [Budget].[Positions] AFTER UPDATE AS " +
                "BEGIN SET NOCOUNT ON; " +
                "IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON i.Id = d.Id WHERE " +
                "i.OrganizationId <> d.OrganizationId OR i.CostCenterId <> d.CostCenterId OR " +
                "i.FiscalYear <> d.FiscalYear OR i.SpendCategoryCode <> d.SpendCategoryCode OR " +
                "i.PositionKeyDigest <> d.PositionKeyDigest OR " +
                "i.CurrentAllocationVersion < d.CurrentAllocationVersion OR " +
                "i.CurrentAllocationVersion > d.CurrentAllocationVersion + 1 OR " +
                "i.CurrentBalanceVersion < d.CurrentBalanceVersion OR " +
                "i.CurrentBalanceVersion > d.CurrentBalanceVersion + 1 OR " +
                "NOT EXISTS (SELECT 1 FROM [Budget].[AllocationVersions] v " +
                "WHERE v.[PositionId] = i.[Id] AND v.[Version] = i.[CurrentAllocationVersion])) " +
                "THROW 51015, 'The budget position identity is stable and its pointers advance to existing rows.', 1; END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // SPEC 08 keeps the ledger on rollback: the application stops posting new movements but
            // the history stays readable and auditable.
            throw new NotSupportedException(
                "The budget ledger has no destructive downgrade; revert the application and keep the schema.");
        }
    }
}
