using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureToPay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Spec10SourcingCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "Sourcing");

            migrationBuilder.CreateTable(
                name: "QuotationLineScopes",
                schema: "Sourcing",
                columns: table => new
                {
                    QuotationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    LineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuotationLineScopes", x => new { x.QuotationId, x.Version, x.LineId });
                });

            migrationBuilder.CreateTable(
                name: "Quotations",
                schema: "Sourcing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RfqId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CurrentVersion = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Quotations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RfqDeadlineExtensions",
                schema: "Sourcing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RfqId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FromVersion = table.Column<int>(type: "int", nullable: false),
                    ToVersion = table.Column<int>(type: "int", nullable: false),
                    PreviousDeadline = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    NewDeadline = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RfqDeadlineExtensions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Rfqs",
                schema: "Sourcing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProcessId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestVersion = table.Column<int>(type: "int", nullable: false),
                    CurrentVersion = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rfqs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SourcingAttachments",
                schema: "Sourcing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RfqId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObjectKey = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Length = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourcingAttachments", x => new { x.Id, x.Version });
                });

            migrationBuilder.CreateTable(
                name: "SourcingAuditRecords",
                schema: "Sourcing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ActorJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    CauseJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ChangedFieldsJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    CorrelationReference = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    EffectKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    TargetJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourcingAuditRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SourcingCommands",
                schema: "Sourcing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommandType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CommandKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Fingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ResultId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ResultVersion = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourcingCommands", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SourcingOutbox",
                schema: "Sourcing",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DispatchedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourcingOutbox", x => x.EventId);
                });

            migrationBuilder.CreateTable(
                name: "SourcingProcesses",
                schema: "Sourcing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestVersion = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CancellationReason = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourcingProcesses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SourcingProcesses_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "Organization",
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SourcingTakeovers",
                schema: "Sourcing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestVersion = table.Column<int>(type: "int", nullable: false),
                    ProcessId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProcessVersion = table.Column<int>(type: "int", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RegisteredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReleaseReason = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourcingTakeovers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QuotationVersions",
                schema: "Sourcing",
                columns: table => new
                {
                    QuotationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RfqId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RfqVersion = table.Column<int>(type: "int", nullable: false),
                    RfqContentDigest = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierVersion = table.Column<int>(type: "int", nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    TermsJson = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    LinesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AttachmentsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReviewJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    ReviewStatus = table.Column<int>(type: "int", nullable: false),
                    Timeliness = table.Column<int>(type: "int", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RegisteredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PredecessorVersion = table.Column<int>(type: "int", nullable: true),
                    ContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuotationVersions", x => new { x.QuotationId, x.Version });
                    table.ForeignKey(
                        name: "FK_QuotationVersions_Quotations_QuotationId",
                        column: x => x.QuotationId,
                        principalSchema: "Sourcing",
                        principalTable: "Quotations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RfqVersions",
                schema: "Sourcing",
                columns: table => new
                {
                    RfqId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProcessId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestVersion = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    TermsJson = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    WeightsJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    LinesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OpenedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ResponseDeadline = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PredecessorVersion = table.Column<int>(type: "int", nullable: true),
                    ParametersJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RfqVersions", x => new { x.RfqId, x.Version });
                    table.ForeignKey(
                        name: "FK_RfqVersions_Rfqs_RfqId",
                        column: x => x.RfqId,
                        principalSchema: "Sourcing",
                        principalTable: "Rfqs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SourcingProcessLines",
                schema: "Sourcing",
                columns: table => new
                {
                    ProcessId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineVersion = table.Column<int>(type: "int", nullable: false),
                    LineContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RequestedQuantity = table.Column<decimal>(type: "decimal(38,12)", precision: 38, scale: 12, nullable: false),
                    UnitCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourcingProcessLines", x => new { x.ProcessId, x.LineId });
                    table.ForeignKey(
                        name: "FK_SourcingProcessLines_SourcingProcesses_ProcessId",
                        column: x => x.ProcessId,
                        principalSchema: "Sourcing",
                        principalTable: "SourcingProcesses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QuotationLineScopes_LineId",
                schema: "Sourcing",
                table: "QuotationLineScopes",
                column: "LineId");

            migrationBuilder.CreateIndex(
                name: "IX_Quotations_OrganizationId_RfqId",
                schema: "Sourcing",
                table: "Quotations",
                columns: new[] { "OrganizationId", "RfqId" });

            migrationBuilder.CreateIndex(
                name: "IX_Quotations_RfqId_SupplierId",
                schema: "Sourcing",
                table: "Quotations",
                columns: new[] { "RfqId", "SupplierId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QuotationVersions_OrganizationId_RfqId",
                schema: "Sourcing",
                table: "QuotationVersions",
                columns: new[] { "OrganizationId", "RfqId" });

            migrationBuilder.CreateIndex(
                name: "IX_RfqDeadlineExtensions_RfqId_ToVersion",
                schema: "Sourcing",
                table: "RfqDeadlineExtensions",
                columns: new[] { "RfqId", "ToVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Rfqs_OrganizationId_RequestId",
                schema: "Sourcing",
                table: "Rfqs",
                columns: new[] { "OrganizationId", "RequestId" });

            migrationBuilder.CreateIndex(
                name: "IX_Rfqs_ProcessId",
                schema: "Sourcing",
                table: "Rfqs",
                column: "ProcessId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RfqVersions_OrganizationId_ProcessId",
                schema: "Sourcing",
                table: "RfqVersions",
                columns: new[] { "OrganizationId", "ProcessId" });

            migrationBuilder.CreateIndex(
                name: "IX_SourcingAttachments_OrganizationId_RfqId",
                schema: "Sourcing",
                table: "SourcingAttachments",
                columns: new[] { "OrganizationId", "RfqId" });

            migrationBuilder.CreateIndex(
                name: "IX_SourcingAuditRecords_OrganizationId_Action_EffectKey",
                schema: "Sourcing",
                table: "SourcingAuditRecords",
                columns: new[] { "OrganizationId", "Action", "EffectKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourcingCommands_OrganizationId_ActorUserId_CommandType_CommandKey",
                schema: "Sourcing",
                table: "SourcingCommands",
                columns: new[] { "OrganizationId", "ActorUserId", "CommandType", "CommandKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourcingOutbox_OrganizationId_OccurredAt",
                schema: "Sourcing",
                table: "SourcingOutbox",
                columns: new[] { "OrganizationId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SourcingProcesses_OrganizationId_RequestId",
                schema: "Sourcing",
                table: "SourcingProcesses",
                columns: new[] { "OrganizationId", "RequestId" });

            migrationBuilder.CreateIndex(
                name: "IX_SourcingProcesses_RequestId_Active",
                schema: "Sourcing",
                table: "SourcingProcesses",
                column: "RequestId",
                unique: true,
                filter: "[State] <> 4");

            migrationBuilder.CreateIndex(
                name: "IX_SourcingTakeovers_ProcessId",
                schema: "Sourcing",
                table: "SourcingTakeovers",
                column: "ProcessId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourcingTakeovers_RequestId_Live",
                schema: "Sourcing",
                table: "SourcingTakeovers",
                column: "RequestId",
                unique: true,
                filter: "[ReleasedAt] IS NULL");

            // SPEC 10 NFR-02/NFR-03: version rows and audit are append-only, and the invariants of
            // REQ-02/REQ-03 hold even for a direct SQL writer, not just for the application service.
            migrationBuilder.Sql(
                "ALTER TABLE [Sourcing].[RfqVersions] WITH CHECK ADD CONSTRAINT " +
                "[CK_RfqVersions_VersionMonotonic] CHECK (" +
                "([Version] = 1 AND [PredecessorVersion] IS NULL) OR " +
                "([Version] > 1 AND [PredecessorVersion] = [Version] - 1));");
            migrationBuilder.Sql(
                "ALTER TABLE [Sourcing].[RfqVersions] WITH CHECK ADD CONSTRAINT " +
                "[CK_RfqVersions_OpenedAt] CHECK (" +
                "([Status] = 1 AND [OpenedAt] IS NULL) OR ([Status] <> 1 AND [OpenedAt] IS NOT NULL));");
            migrationBuilder.Sql(
                "ALTER TABLE [Sourcing].[QuotationVersions] WITH CHECK ADD CONSTRAINT " +
                "[CK_QuotationVersions_VersionMonotonic] CHECK (" +
                "([Version] = 1 AND [PredecessorVersion] IS NULL) OR " +
                "([Version] > 1 AND [PredecessorVersion] = [Version] - 1));");
            migrationBuilder.Sql(
                "ALTER TABLE [Sourcing].[QuotationVersions] WITH CHECK ADD CONSTRAINT " +
                "[CK_QuotationVersions_RegisteredAfterReceived] CHECK ([RegisteredAt] >= [ReceivedAt]);");
            migrationBuilder.Sql(
                "ALTER TABLE [Sourcing].[SourcingProcessLines] WITH CHECK ADD CONSTRAINT " +
                "[CK_SourcingProcessLines_PositiveQuantity] CHECK ([RequestedQuantity] > 0);");
            migrationBuilder.Sql(
                "CREATE TRIGGER [Sourcing].[TR_ProcessLines_AppendOnly] " +
                "ON [Sourcing].[SourcingProcessLines] AFTER UPDATE, DELETE AS " +
                "BEGIN SET NOCOUNT ON; " +
                "IF EXISTS (SELECT 1 FROM deleted) " +
                "THROW 51110, 'Sourcing process lines are append-only.', 1; END");
            migrationBuilder.Sql(
                "CREATE TRIGGER [Sourcing].[TR_RfqVersions_AppendOnly] " +
                "ON [Sourcing].[RfqVersions] AFTER UPDATE, DELETE AS " +
                "BEGIN SET NOCOUNT ON; " +
                "IF EXISTS (SELECT 1 FROM deleted) " +
                "THROW 51111, 'RFQ versions are append-only.', 1; END");
            migrationBuilder.Sql(
                "CREATE TRIGGER [Sourcing].[TR_RfqExtensions_AppendOnly] " +
                "ON [Sourcing].[RfqDeadlineExtensions] AFTER UPDATE, DELETE AS " +
                "BEGIN SET NOCOUNT ON; " +
                "IF EXISTS (SELECT 1 FROM deleted) " +
                "THROW 51112, 'RFQ deadline extensions are append-only.', 1; END");
            migrationBuilder.Sql(
                "CREATE TRIGGER [Sourcing].[TR_QuotationVersions_AppendOnly] " +
                "ON [Sourcing].[QuotationVersions] AFTER UPDATE, DELETE AS " +
                "BEGIN SET NOCOUNT ON; " +
                "IF EXISTS (SELECT 1 FROM deleted) " +
                "THROW 51113, 'Quotation versions are append-only.', 1; END");
            migrationBuilder.Sql(
                "CREATE TRIGGER [Sourcing].[TR_QuotationScopes_AppendOnly] " +
                "ON [Sourcing].[QuotationLineScopes] AFTER UPDATE, DELETE AS " +
                "BEGIN SET NOCOUNT ON; " +
                "IF EXISTS (SELECT 1 FROM deleted) " +
                "THROW 51114, 'Quotation line scopes are append-only.', 1; END");
            migrationBuilder.Sql(
                "CREATE TRIGGER [Sourcing].[TR_SourcingAudit_AppendOnly] " +
                "ON [Sourcing].[SourcingAuditRecords] AFTER UPDATE, DELETE AS " +
                "BEGIN SET NOCOUNT ON; " +
                "IF EXISTS (SELECT 1 FROM deleted) " +
                "THROW 51115, 'Sourcing audit is append-only.', 1; END");
            // A staged attachment may only be confirmed once and the sealed row is immutable.
            migrationBuilder.Sql(
                "CREATE TRIGGER [Sourcing].[TR_Attachments_Seal] " +
                "ON [Sourcing].[SourcingAttachments] AFTER UPDATE, DELETE AS " +
                "BEGIN SET NOCOUNT ON; " +
                "IF EXISTS (SELECT 1 FROM deleted WHERE [State] = 2) " +
                "THROW 51116, 'Confirmed sourcing attachments are immutable.', 1; " +
                "IF EXISTS (SELECT 1 FROM deleted d JOIN inserted i ON i.[Id] = d.[Id] " +
                "AND i.[Version] = d.[Version] WHERE d.[State] <> 1 OR i.[State] <> 2 OR " +
                "i.[Sha256] <> d.[Sha256] OR i.[Length] <> d.[Length] OR i.[ObjectKey] <> d.[ObjectKey]) " +
                "THROW 51117, 'A staged sourcing attachment can only be confirmed.', 1; END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [Sourcing].[TR_Attachments_Seal];");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [Sourcing].[TR_SourcingAudit_AppendOnly];");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [Sourcing].[TR_QuotationScopes_AppendOnly];");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [Sourcing].[TR_QuotationVersions_AppendOnly];");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [Sourcing].[TR_RfqExtensions_AppendOnly];");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [Sourcing].[TR_RfqVersions_AppendOnly];");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [Sourcing].[TR_ProcessLines_AppendOnly];");
            migrationBuilder.DropTable(
                name: "QuotationLineScopes",
                schema: "Sourcing");

            migrationBuilder.DropTable(
                name: "QuotationVersions",
                schema: "Sourcing");

            migrationBuilder.DropTable(
                name: "RfqDeadlineExtensions",
                schema: "Sourcing");

            migrationBuilder.DropTable(
                name: "RfqVersions",
                schema: "Sourcing");

            migrationBuilder.DropTable(
                name: "SourcingAttachments",
                schema: "Sourcing");

            migrationBuilder.DropTable(
                name: "SourcingAuditRecords",
                schema: "Sourcing");

            migrationBuilder.DropTable(
                name: "SourcingCommands",
                schema: "Sourcing");

            migrationBuilder.DropTable(
                name: "SourcingOutbox",
                schema: "Sourcing");

            migrationBuilder.DropTable(
                name: "SourcingProcessLines",
                schema: "Sourcing");

            migrationBuilder.DropTable(
                name: "SourcingTakeovers",
                schema: "Sourcing");

            migrationBuilder.DropTable(
                name: "Quotations",
                schema: "Sourcing");

            migrationBuilder.DropTable(
                name: "Rfqs",
                schema: "Sourcing");

            migrationBuilder.DropTable(
                name: "SourcingProcesses",
                schema: "Sourcing");
        }
    }
}
