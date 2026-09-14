using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureToPay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Spec06PurchaseRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "PurchaseRequest");

            migrationBuilder.CreateTable(
                name: "PurchaseRequestApprovalResults",
                schema: "PurchaseRequest",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestVersion = table.Column<int>(type: "int", nullable: false),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    LineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineVersion = table.Column<int>(type: "int", nullable: false),
                    MaterialSnapshotDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Result = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    DecisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseRequestApprovalResults", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseRequestCommands",
                schema: "PurchaseRequest",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommandType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CommandKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Fingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestVersion = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseRequestCommands", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseRequestCompletenessManifests",
                schema: "PurchaseRequest",
                columns: table => new
                {
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestVersion = table.Column<int>(type: "int", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ReferenceAttestationDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PolicyManifestDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DomainAttestationDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LinesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseRequestCompletenessManifests", x => new { x.RequestId, x.RequestVersion });
                });

            migrationBuilder.CreateTable(
                name: "PurchaseRequestLifecycleEvents",
                schema: "PurchaseRequest",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestVersion = table.Column<int>(type: "int", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    BeforeStatus = table.Column<int>(type: "int", nullable: true),
                    AfterStatus = table.Column<int>(type: "int", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WorkloadIssuer = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    WorkloadClientId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ReasonCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    CorrelationReference = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseRequestLifecycleEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseRequestLineVersions",
                schema: "PurchaseRequest",
                columns: table => new
                {
                    LineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineVersion = table.Column<int>(type: "int", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EstimatedGrossAmount = table.Column<decimal>(type: "decimal(38,12)", precision: 38, scale: 12, nullable: false),
                    TransactionCurrency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    BaseAmount = table.Column<decimal>(type: "decimal(38,12)", precision: 38, scale: 12, nullable: false),
                    BaseCurrency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    FiscalYear = table.Column<int>(type: "int", nullable: false),
                    PurchaseType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SpendCategoryJson = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    CostCenterJson = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    CostCenterDepartmentJson = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    BeneficiaryDepartmentJson = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    RequestedForUserJson = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    SupplierJson = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PreferredProductJson = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RequiredProductJson = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ContractRequired = table.Column<bool>(type: "bit", nullable: false),
                    NonStandardTerms = table.Column<bool>(type: "bit", nullable: false),
                    AgreementStatus = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    NeedSummary = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    RiskAnswersJson = table.Column<string>(type: "nvarchar(max)", maxLength: 200000, nullable: false),
                    FxAttestationJson = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseRequestLineVersions", x => new { x.LineId, x.LineVersion });
                });

            migrationBuilder.CreateTable(
                name: "PurchaseRequestReferenceAttestations",
                schema: "PurchaseRequest",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestVersion = table.Column<int>(type: "int", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AssertionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Digest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseRequestReferenceAttestations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseRequestRevisionDeltas",
                schema: "PurchaseRequest",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FromRequestVersion = table.Column<int>(type: "int", nullable: false),
                    ToRequestVersion = table.Column<int>(type: "int", nullable: false),
                    DeltaJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RevisionKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Fingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseRequestRevisionDeltas", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseRequests",
                schema: "PurchaseRequest",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequesterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityVersion = table.Column<int>(type: "int", nullable: false),
                    CurrentVersion = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseRequestSubmissionAttempts",
                schema: "PurchaseRequest",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequesterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestVersion = table.Column<int>(type: "int", nullable: false),
                    SubmissionKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Fingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    PolicySetVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PolicyEvaluationBundleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PolicyResultDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ApprovalCaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PreviousCaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApprovalContractVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ErrorCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseRequestSubmissionAttempts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseRequestVersionLines",
                schema: "PurchaseRequest",
                columns: table => new
                {
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestVersion = table.Column<int>(type: "int", nullable: false),
                    LineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineVersion = table.Column<int>(type: "int", nullable: false),
                    ContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseRequestVersionLines", x => new { x.RequestId, x.RequestVersion, x.LineId });
                });

            migrationBuilder.CreateTable(
                name: "PurchaseRequestVersions",
                schema: "PurchaseRequest",
                columns: table => new
                {
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    PredecessorVersion = table.Column<int>(type: "int", nullable: true),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequesterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityVersion = table.Column<int>(type: "int", nullable: false),
                    BusinessJustification = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RevisionKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseRequestVersions", x => new { x.RequestId, x.Version });
                });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRequestApprovalResults_EventId_ContractVersion_SourceType_SourceId_LineId_LineVersion",
                schema: "PurchaseRequest",
                table: "PurchaseRequestApprovalResults",
                columns: new[] { "EventId", "ContractVersion", "SourceType", "SourceId", "LineId", "LineVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRequestApprovalResults_RequestId_RequestVersion",
                schema: "PurchaseRequest",
                table: "PurchaseRequestApprovalResults",
                columns: new[] { "RequestId", "RequestVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRequestCommands_OrganizationId_ActorUserId_CommandType_CommandKey",
                schema: "PurchaseRequest",
                table: "PurchaseRequestCommands",
                columns: new[] { "OrganizationId", "ActorUserId", "CommandType", "CommandKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRequestLifecycleEvents_RequestId_RequestVersion_OccurredAt",
                schema: "PurchaseRequest",
                table: "PurchaseRequestLifecycleEvents",
                columns: new[] { "RequestId", "RequestVersion", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRequestLineVersions_OrganizationId_RequestId",
                schema: "PurchaseRequest",
                table: "PurchaseRequestLineVersions",
                columns: new[] { "OrganizationId", "RequestId" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRequestReferenceAttestations_RequestId_RequestVersion",
                schema: "PurchaseRequest",
                table: "PurchaseRequestReferenceAttestations",
                columns: new[] { "RequestId", "RequestVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRequestRevisionDeltas_OrganizationId_RevisionKey",
                schema: "PurchaseRequest",
                table: "PurchaseRequestRevisionDeltas",
                columns: new[] { "OrganizationId", "RevisionKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRequestRevisionDeltas_RequestId_ToRequestVersion",
                schema: "PurchaseRequest",
                table: "PurchaseRequestRevisionDeltas",
                columns: new[] { "RequestId", "ToRequestVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRequests_OrganizationId_RequesterId_Status",
                schema: "PurchaseRequest",
                table: "PurchaseRequests",
                columns: new[] { "OrganizationId", "RequesterId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRequestSubmissionAttempts_OrganizationId_RequesterId_SubmissionKey",
                schema: "PurchaseRequest",
                table: "PurchaseRequestSubmissionAttempts",
                columns: new[] { "OrganizationId", "RequesterId", "SubmissionKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRequestSubmissionAttempts_RequestId_RequestVersion",
                schema: "PurchaseRequest",
                table: "PurchaseRequestSubmissionAttempts",
                columns: new[] { "RequestId", "RequestVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRequestVersionLines_RequestId_RequestVersion_LineVersion",
                schema: "PurchaseRequest",
                table: "PurchaseRequestVersionLines",
                columns: new[] { "RequestId", "RequestVersion", "LineVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRequestVersions_OrganizationId_RequestId",
                schema: "PurchaseRequest",
                table: "PurchaseRequestVersions",
                columns: new[] { "OrganizationId", "RequestId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PurchaseRequestApprovalResults",
                schema: "PurchaseRequest");

            migrationBuilder.DropTable(
                name: "PurchaseRequestCommands",
                schema: "PurchaseRequest");

            migrationBuilder.DropTable(
                name: "PurchaseRequestCompletenessManifests",
                schema: "PurchaseRequest");

            migrationBuilder.DropTable(
                name: "PurchaseRequestLifecycleEvents",
                schema: "PurchaseRequest");

            migrationBuilder.DropTable(
                name: "PurchaseRequestLineVersions",
                schema: "PurchaseRequest");

            migrationBuilder.DropTable(
                name: "PurchaseRequestReferenceAttestations",
                schema: "PurchaseRequest");

            migrationBuilder.DropTable(
                name: "PurchaseRequestRevisionDeltas",
                schema: "PurchaseRequest");

            migrationBuilder.DropTable(
                name: "PurchaseRequests",
                schema: "PurchaseRequest");

            migrationBuilder.DropTable(
                name: "PurchaseRequestSubmissionAttempts",
                schema: "PurchaseRequest");

            migrationBuilder.DropTable(
                name: "PurchaseRequestVersionLines",
                schema: "PurchaseRequest");

            migrationBuilder.DropTable(
                name: "PurchaseRequestVersions",
                schema: "PurchaseRequest");
        }
    }
}
