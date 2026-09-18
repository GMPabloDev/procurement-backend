using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureToPay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Spec11PurchaseOrderCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "PurchaseOrders");

            migrationBuilder.CreateTable(
                name: "AwardConsumptionClaims",
                schema: "PurchaseOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AwardId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AwardVersion = table.Column<int>(type: "int", nullable: false),
                    AwardContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ClaimKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Fingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PoNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CoveredLinesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AwardSnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    WorkloadIssuer = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    WorkloadClientId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReleaseReason = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AwardConsumptionClaims", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DirectPurchaseAuthorizations",
                schema: "PurchaseOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestVersion = table.Column<int>(type: "int", nullable: false),
                    AuthorizationKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Fingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierVersion = table.Column<int>(type: "int", nullable: false),
                    PolicyBundleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PolicyBundleVersion = table.Column<int>(type: "int", nullable: false),
                    PolicyBundleDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CoveredLinesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CoveredTargetsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    DocumentJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AuthorizedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancellationReason = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectPurchaseAuthorizations", x => new { x.Id, x.Version });
                });

            migrationBuilder.CreateTable(
                name: "ProcurementSupportingDocuments",
                schema: "PurchaseOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestVersion = table.Column<int>(type: "int", nullable: false),
                    CoveredTargetsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FileRefJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BusinessType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    SuccessorOfId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SupersededReason = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Length = table.Column<long>(type: "bigint", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DocumentJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcurementSupportingDocuments", x => new { x.Id, x.Version });
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrderAmendmentRoots",
                schema: "PurchaseOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CurrentVersion = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrderAmendmentRoots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrderAmendments",
                schema: "PurchaseOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    PredecessorVersion = table.Column<int>(type: "int", nullable: true),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BasePoVersion = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    SuccessorAwardId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SuccessorAwardVersion = table.Column<int>(type: "int", nullable: true),
                    SuccessorAwardDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ApprovalCaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApprovalCaseVersion = table.Column<int>(type: "int", nullable: true),
                    ApprovalDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    AppliedPoVersion = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AppliedPoVersionNumber = table.Column<int>(type: "int", nullable: true),
                    DocumentJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CommandKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrderAmendments", x => new { x.Id, x.Version });
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrderAudit",
                schema: "PurchaseOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WorkloadIssuer = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    WorkloadClientId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Action = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Cause = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    TargetType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetVersion = table.Column<int>(type: "int", nullable: false),
                    ChangedFieldsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EffectKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CorrelationReference = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrderAudit", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrderBudgetAttempts",
                schema: "PurchaseOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PoVersion = table.Column<int>(type: "int", nullable: false),
                    AmendmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Operation = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    OperationKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ReleaseKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ParentsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OperationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ReleaseOperationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    MovementRefsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ReleaseMovementRefsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    LeaseOwner = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FencingToken = table.Column<long>(type: "bigint", nullable: false),
                    LastErrorCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrderBudgetAttempts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrderCommands",
                schema: "PurchaseOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CommandKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CommandType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Fingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ResultRef = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrderCommands", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrderLinePointers",
                schema: "PurchaseOrders",
                columns: table => new
                {
                    RequestLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestLineVersion = table.Column<int>(type: "int", nullable: false),
                    PoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PoVersion = table.Column<int>(type: "int", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrderLinePointers", x => new { x.RequestLineId, x.RequestLineVersion, x.PoId });
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrderNumberSequences",
                schema: "PurchaseOrders",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LastValue = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrderNumberSequences", x => x.OrganizationId);
                    table.ForeignKey(
                        name: "FK_PurchaseOrderNumberSequences_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "Organization",
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrderOutbox",
                schema: "PurchaseOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestVersion = table.Column<int>(type: "int", nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DispatchedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DispatchAttempts = table.Column<int>(type: "int", nullable: false),
                    LastErrorCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrderOutbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrders",
                schema: "PurchaseOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PoNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestVersion = table.Column<int>(type: "int", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierVersion = table.Column<int>(type: "int", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityVersion = table.Column<int>(type: "int", nullable: false),
                    CurrentVersion = table.Column<int>(type: "int", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PurchaseOrders_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "Organization",
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseRequestLineProjections",
                schema: "PurchaseOrders",
                columns: table => new
                {
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestVersion = table.Column<int>(type: "int", nullable: false),
                    LineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineVersion = table.Column<int>(type: "int", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Projection = table.Column<int>(type: "int", nullable: false),
                    ConsumerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConsumerVersion = table.Column<int>(type: "int", nullable: false),
                    ConsumerDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ConsumerType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ConsumerState = table.Column<int>(type: "int", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseRequestLineProjections", x => new { x.RequestId, x.RequestVersion, x.LineId, x.LineVersion });
                });

            migrationBuilder.CreateTable(
                name: "PurchaseRequestLineTakeovers",
                schema: "PurchaseOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestVersion = table.Column<int>(type: "int", nullable: false),
                    RequestContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineVersion = table.Column<int>(type: "int", nullable: false),
                    LineContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Owner = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    ConsumerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConsumerVersion = table.Column<int>(type: "int", nullable: false),
                    ConsumerDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ConsumerType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PredecessorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PredecessorVersion = table.Column<int>(type: "int", nullable: true),
                    PredecessorDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReleaseReason = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseRequestLineTakeovers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseRequestOrderingEvidence",
                schema: "PurchaseOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestVersion = table.Column<int>(type: "int", nullable: false),
                    PolicyBundleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PolicyBundleVersion = table.Column<int>(type: "int", nullable: false),
                    PolicyBundleDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CaseVersion = table.Column<int>(type: "int", nullable: false),
                    CaseDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CoveredTargetsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RequirementsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BudgetEvidenceRefsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ResultRefsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DocumentJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CheckedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseRequestOrderingEvidence", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SupportingDocumentOwnerAttempts",
                schema: "PurchaseOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrerequisiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubjectType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    OwnerAdapterId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OwnerAdapterVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestVersion = table.Column<int>(type: "int", nullable: false),
                    TargetsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ParametersDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AllowedDocumentTypesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MinimumCount = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SignalResult = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    CheckKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SignalKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    EvidenceDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    DueAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    LeaseOwner = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
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
                    table.PrimaryKey("PK_SupportingDocumentOwnerAttempts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SupportingDocumentOwnerEvidence",
                schema: "PurchaseOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrerequisiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Result = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SignalKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    DocumentJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupportingDocumentOwnerEvidence", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SupportingDocumentProcessorRegistrations",
                schema: "PurchaseOrders",
                columns: table => new
                {
                    AdapterId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    AdapterVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ProcessorId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    WorkloadIssuer = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    WorkloadClientId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupportingDocumentProcessorRegistrations", x => new { x.AdapterId, x.AdapterVersion });
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrderVersions",
                schema: "PurchaseOrders",
                columns: table => new
                {
                    PoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    PredecessorVersion = table.Column<int>(type: "int", nullable: true),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PoNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestVersion = table.Column<int>(type: "int", nullable: false),
                    RequestContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProposalVersion = table.Column<int>(type: "int", nullable: false),
                    ProposalContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AwardId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AwardVersion = table.Column<int>(type: "int", nullable: false),
                    AwardContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ClaimId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierVersion = table.Column<int>(type: "int", nullable: false),
                    LegalEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LegalEntityVersion = table.Column<int>(type: "int", nullable: false),
                    SourceAmount = table.Column<decimal>(type: "decimal(38,12)", precision: 38, scale: 12, nullable: false),
                    SourceCurrency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    BaseAmount = table.Column<decimal>(type: "decimal(38,12)", precision: 38, scale: 12, nullable: false),
                    BaseCurrency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    DeliveryJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    TermsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LinesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BudgetOperationRefsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LineParametersJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ApprovalCaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApprovalCaseVersion = table.Column<int>(type: "int", nullable: true),
                    ApprovalDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    OrderingEvidenceDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    AmendmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AmendmentVersion = table.Column<int>(type: "int", nullable: true),
                    IssuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DocumentJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrderVersions", x => new { x.PoId, x.Version });
                    table.ForeignKey(
                        name: "FK_PurchaseOrderVersions_PurchaseOrders_PoId",
                        column: x => x.PoId,
                        principalSchema: "PurchaseOrders",
                        principalTable: "PurchaseOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AwardConsumptionClaims_Award_Live",
                schema: "PurchaseOrders",
                table: "AwardConsumptionClaims",
                columns: new[] { "OrganizationId", "AwardId" },
                unique: true,
                filter: "[State] < 3");

            migrationBuilder.CreateIndex(
                name: "IX_AwardConsumptionClaims_Key",
                schema: "PurchaseOrders",
                table: "AwardConsumptionClaims",
                columns: new[] { "OrganizationId", "ClaimKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AwardConsumptionClaims_PoId",
                schema: "PurchaseOrders",
                table: "AwardConsumptionClaims",
                column: "PoId");

            migrationBuilder.CreateIndex(
                name: "IX_DirectPurchaseAuthorizations_Key",
                schema: "PurchaseOrders",
                table: "DirectPurchaseAuthorizations",
                columns: new[] { "OrganizationId", "AuthorizationKey" },
                unique: true,
                filter: "[Version] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_DirectPurchaseAuthorizations_OrganizationId_RequestId_State",
                schema: "PurchaseOrders",
                table: "DirectPurchaseAuthorizations",
                columns: new[] { "OrganizationId", "RequestId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_ProcurementSupportingDocuments_OrganizationId_RequestId_RequestVersion",
                schema: "PurchaseOrders",
                table: "ProcurementSupportingDocuments",
                columns: new[] { "OrganizationId", "RequestId", "RequestVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_ProcurementSupportingDocuments_OrganizationId_Sha256_Length",
                schema: "PurchaseOrders",
                table: "ProcurementSupportingDocuments",
                columns: new[] { "OrganizationId", "Sha256", "Length" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderAmendmentRoots_PoId",
                schema: "PurchaseOrders",
                table: "PurchaseOrderAmendmentRoots",
                column: "PoId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderAmendments_AppliedPoVersionNumber",
                schema: "PurchaseOrders",
                table: "PurchaseOrderAmendments",
                column: "AppliedPoVersionNumber");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderAmendments_CommandKey",
                schema: "PurchaseOrders",
                table: "PurchaseOrderAmendments",
                columns: new[] { "OrganizationId", "CommandKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderAmendments_OrganizationId_PoId",
                schema: "PurchaseOrders",
                table: "PurchaseOrderAmendments",
                columns: new[] { "OrganizationId", "PoId" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderAudit_OccurredAt",
                schema: "PurchaseOrders",
                table: "PurchaseOrderAudit",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderAudit_OrganizationId_TargetType_TargetId",
                schema: "PurchaseOrders",
                table: "PurchaseOrderAudit",
                columns: new[] { "OrganizationId", "TargetType", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderBudgetAttempts_OperationKey",
                schema: "PurchaseOrders",
                table: "PurchaseOrderBudgetAttempts",
                column: "OperationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderBudgetAttempts_Po_Operation",
                schema: "PurchaseOrders",
                table: "PurchaseOrderBudgetAttempts",
                columns: new[] { "PoId", "PoVersion", "Operation" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderCommands_Key",
                schema: "PurchaseOrders",
                table: "PurchaseOrderCommands",
                columns: new[] { "OrganizationId", "CommandKey", "CommandType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderLinePointers_Line_Active",
                schema: "PurchaseOrders",
                table: "PurchaseOrderLinePointers",
                columns: new[] { "RequestLineId", "RequestLineVersion" },
                unique: true,
                filter: "[State] <> 5");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderLinePointers_PoId",
                schema: "PurchaseOrders",
                table: "PurchaseOrderLinePointers",
                column: "PoId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderOutbox_DispatchedAt_OccurredAt",
                schema: "PurchaseOrders",
                table: "PurchaseOrderOutbox",
                columns: new[] { "DispatchedAt", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderOutbox_IdempotencyKey",
                schema: "PurchaseOrders",
                table: "PurchaseOrderOutbox",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_Organization_Number",
                schema: "PurchaseOrders",
                table: "PurchaseOrders",
                columns: new[] { "OrganizationId", "PoNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_OrganizationId_RequestId_RequestVersion",
                schema: "PurchaseOrders",
                table: "PurchaseOrders",
                columns: new[] { "OrganizationId", "RequestId", "RequestVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderVersions_ClaimId",
                schema: "PurchaseOrders",
                table: "PurchaseOrderVersions",
                column: "ClaimId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderVersions_OrganizationId_State",
                schema: "PurchaseOrders",
                table: "PurchaseOrderVersions",
                columns: new[] { "OrganizationId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderVersions_PoId_Version",
                schema: "PurchaseOrders",
                table: "PurchaseOrderVersions",
                columns: new[] { "PoId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderVersions_RequestId_RequestVersion",
                schema: "PurchaseOrders",
                table: "PurchaseOrderVersions",
                columns: new[] { "RequestId", "RequestVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRequestLineProjections_OrganizationId_RequestId",
                schema: "PurchaseOrders",
                table: "PurchaseRequestLineProjections",
                columns: new[] { "OrganizationId", "RequestId" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRequestLineTakeovers_ConsumerType_ConsumerId",
                schema: "PurchaseOrders",
                table: "PurchaseRequestLineTakeovers",
                columns: new[] { "ConsumerType", "ConsumerId" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRequestLineTakeovers_Line_Active",
                schema: "PurchaseOrders",
                table: "PurchaseRequestLineTakeovers",
                columns: new[] { "LineId", "LineVersion" },
                unique: true,
                filter: "[State] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRequestLineTakeovers_OrganizationId_RequestId_RequestVersion",
                schema: "PurchaseOrders",
                table: "PurchaseRequestLineTakeovers",
                columns: new[] { "OrganizationId", "RequestId", "RequestVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRequestOrderingEvidence_Version",
                schema: "PurchaseOrders",
                table: "PurchaseRequestOrderingEvidence",
                columns: new[] { "OrganizationId", "RequestId", "RequestVersion", "CaseVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupportingDocumentOwnerAttempts_PrerequisiteId",
                schema: "PurchaseOrders",
                table: "SupportingDocumentOwnerAttempts",
                column: "PrerequisiteId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupportingDocumentOwnerAttempts_State_NextAttemptAt",
                schema: "PurchaseOrders",
                table: "SupportingDocumentOwnerAttempts",
                columns: new[] { "State", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SupportingDocumentOwnerEvidence_PrerequisiteId",
                schema: "PurchaseOrders",
                table: "SupportingDocumentOwnerEvidence",
                column: "PrerequisiteId",
                unique: true);
            // SPEC 11 NFR-01/NFR-02: version, claim, evidence and audit rows are append-only, so a
            // corrupted or hostile writer cannot rewrite published history.
            foreach (var (table, id) in new (string Table, int Id)[]
                     {
                         ("PurchaseOrderVersions", 51200),
                         ("PurchaseOrderAmendments", 51201),
                         ("PurchaseOrderAudit", 51203),
                         ("SupportingDocumentOwnerEvidence", 51204),
                         ("PurchaseRequestOrderingEvidence", 51205),
                         ("ProcurementSupportingDocuments", 51206),
                         ("DirectPurchaseAuthorizations", 51207)
                     })
            {
                migrationBuilder.Sql(
                    $"CREATE TRIGGER [PurchaseOrders].[TR_{table}_AppendOnly] " +
                    $"ON [PurchaseOrders].[{table}] AFTER UPDATE, DELETE AS " +
                    "BEGIN SET NOCOUNT ON; " +
                    "IF EXISTS (SELECT 1 FROM deleted) " +
                    $"THROW {id}, '{table} are append-only.', 1; END");
            }

            // REQ-01: a claim advances CLAIMED -> ISSUED | RELEASED | CONSUMED_CANCELLED, so only the
            // state machine may update the row; its identity, evidence and covered set are immutable.
            migrationBuilder.Sql(
                "CREATE TRIGGER [PurchaseOrders].[TR_AwardConsumptionClaims_State] " +
                "ON [PurchaseOrders].[AwardConsumptionClaims] AFTER UPDATE, DELETE AS " +
                "BEGIN SET NOCOUNT ON; " +
                "IF EXISTS (SELECT 1 FROM deleted) " +
                "BEGIN " +
                "IF NOT EXISTS (SELECT 1 FROM inserted) " +
                "THROW 51202, 'Award consumption claims are append-only.', 1; " +
                "IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON i.[Id] = d.[Id] WHERE " +
                "i.[AwardId] <> d.[AwardId] OR i.[AwardVersion] <> d.[AwardVersion] OR " +
                "i.[AwardContentDigest] <> d.[AwardContentDigest] OR i.[ClaimKey] <> d.[ClaimKey] OR " +
                "i.[Fingerprint] <> d.[Fingerprint] OR i.[PoId] <> d.[PoId] OR " +
                "i.[PoNumber] <> d.[PoNumber] OR i.[CoveredLinesJson] <> d.[CoveredLinesJson] OR " +
                "i.[AwardSnapshotJson] <> d.[AwardSnapshotJson] OR " +
                "i.[WorkloadIssuer] <> d.[WorkloadIssuer] OR " +
                "i.[WorkloadClientId] <> d.[WorkloadClientId] OR i.[ActorUserId] <> d.[ActorUserId] OR " +
                "i.[ClaimedAt] <> d.[ClaimedAt]) " +
                "THROW 51202, 'Award consumption claims are append-only.', 1; " +
                "END END");

            // REQ-02: only the first version has no predecessor and every successor advances exactly
            // one step; an issued version always carries its approval, evidence and instant.
            migrationBuilder.Sql(
                "ALTER TABLE [PurchaseOrders].[PurchaseOrderVersions] WITH CHECK ADD CONSTRAINT " +
                "[CK_PurchaseOrderVersions_VersionMonotonic] CHECK (" +
                "([Version] = 1 AND [PredecessorVersion] IS NULL) OR " +
                "([Version] > 1 AND [PredecessorVersion] = [Version] - 1));");
            migrationBuilder.Sql(
                "ALTER TABLE [PurchaseOrders].[PurchaseOrderVersions] WITH CHECK ADD CONSTRAINT " +
                "[CK_PurchaseOrderVersions_IssuedEvidence] CHECK (" +
                "([State] <> 4) OR ([IssuedAt] IS NOT NULL AND [ApprovalCaseId] IS NOT NULL AND " +
                "[OrderingEvidenceDigest] IS NOT NULL));");
            migrationBuilder.Sql(
                "ALTER TABLE [PurchaseOrders].[PurchaseOrderVersions] WITH CHECK ADD CONSTRAINT " +
                "[CK_PurchaseOrderVersions_PositiveAmounts] CHECK ([SourceAmount] >= 0 AND [BaseAmount] >= 0);");
            migrationBuilder.Sql(
                "ALTER TABLE [PurchaseOrders].[PurchaseOrderAmendments] WITH CHECK ADD CONSTRAINT " +
                "[CK_PurchaseOrderAmendments_VersionMonotonic] CHECK (" +
                "([Version] = 1 AND [PredecessorVersion] IS NULL) OR " +
                "([Version] > 1 AND [PredecessorVersion] = [Version] - 1));");
            migrationBuilder.Sql(
                "ALTER TABLE [PurchaseOrders].[PurchaseRequestLineTakeovers] WITH CHECK ADD CONSTRAINT " +
                "[CK_PurchaseRequestLineTakeovers_VersionPositive] CHECK ([Version] > 0);");

            // SPEC 11 REQ-10: every historical request-wide sourcing takeover is expanded to exactly
            // one row per line of its process line set. The legacy rows stay as history.
            migrationBuilder.Sql(
                "INSERT INTO [PurchaseOrders].[PurchaseRequestLineTakeovers] " +
                "([Id], [OrganizationId], [RequestId], [RequestVersion], [RequestContentDigest], " +
                "[LineId], [LineVersion], [LineContentDigest], [Owner], [State], [Version], " +
                "[ConsumerId], [ConsumerVersion], [ConsumerDigest], [ConsumerType], " +
                "[PredecessorId], [PredecessorVersion], [PredecessorDigest], [ActorUserId], " +
                "[OccurredAt], [ReleasedAt], [ReleaseReason]) " +
                "SELECT NEWID(), takeover.[OrganizationId], takeover.[RequestId], takeover.[RequestVersion], " +
                "requestVersion.[ContentDigest], line.[LineId], line.[LineVersion], line.[LineContentDigest], " +
                "2, CASE WHEN takeover.[ReleasedAt] IS NULL THEN 1 ELSE 2 END, 1, " +
                "takeover.[ProcessId], takeover.[ProcessVersion], " +
                "LOWER(CONVERT(varchar(64), HASHBYTES('SHA2_256', " +
                "'SOURCING:' + CONVERT(varchar(36), takeover.[ProcessId]) + ':' + " +
                "CONVERT(varchar(11), takeover.[ProcessVersion])), 2)), 'SOURCING', " +
                "NULL, NULL, NULL, takeover.[ActorUserId], takeover.[RegisteredAt], " +
                "takeover.[ReleasedAt], takeover.[ReleaseReason] " +
                "FROM [Sourcing].[SourcingTakeovers] AS takeover " +
                "JOIN [Sourcing].[SourcingProcessLines] AS line ON line.[ProcessId] = takeover.[ProcessId] " +
                "JOIN [PurchaseRequest].[PurchaseRequestVersions] AS requestVersion " +
                "ON requestVersion.[RequestId] = takeover.[RequestId] " +
                "AND requestVersion.[Version] = takeover.[RequestVersion];");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP TRIGGER IF EXISTS [PurchaseOrders].[TR_AwardConsumptionClaims_State];");
            foreach (var table in new[]
                     {
                         "PurchaseOrderVersions", "PurchaseOrderAmendments",
                         "PurchaseOrderAudit", "SupportingDocumentOwnerEvidence",
                         "PurchaseRequestOrderingEvidence", "ProcurementSupportingDocuments",
                         "DirectPurchaseAuthorizations"
                     })
            {
                migrationBuilder.Sql($"DROP TRIGGER IF EXISTS [PurchaseOrders].[TR_{table}_AppendOnly];");
            }

            migrationBuilder.DropTable(
                name: "AwardConsumptionClaims",
                schema: "PurchaseOrders");

            migrationBuilder.DropTable(
                name: "DirectPurchaseAuthorizations",
                schema: "PurchaseOrders");

            migrationBuilder.DropTable(
                name: "ProcurementSupportingDocuments",
                schema: "PurchaseOrders");

            migrationBuilder.DropTable(
                name: "PurchaseOrderAmendmentRoots",
                schema: "PurchaseOrders");

            migrationBuilder.DropTable(
                name: "PurchaseOrderAmendments",
                schema: "PurchaseOrders");

            migrationBuilder.DropTable(
                name: "PurchaseOrderAudit",
                schema: "PurchaseOrders");

            migrationBuilder.DropTable(
                name: "PurchaseOrderBudgetAttempts",
                schema: "PurchaseOrders");

            migrationBuilder.DropTable(
                name: "PurchaseOrderCommands",
                schema: "PurchaseOrders");

            migrationBuilder.DropTable(
                name: "PurchaseOrderLinePointers",
                schema: "PurchaseOrders");

            migrationBuilder.DropTable(
                name: "PurchaseOrderNumberSequences",
                schema: "PurchaseOrders");

            migrationBuilder.DropTable(
                name: "PurchaseOrderOutbox",
                schema: "PurchaseOrders");

            migrationBuilder.DropTable(
                name: "PurchaseOrderVersions",
                schema: "PurchaseOrders");

            migrationBuilder.DropTable(
                name: "PurchaseRequestLineProjections",
                schema: "PurchaseOrders");

            migrationBuilder.DropTable(
                name: "PurchaseRequestLineTakeovers",
                schema: "PurchaseOrders");

            migrationBuilder.DropTable(
                name: "PurchaseRequestOrderingEvidence",
                schema: "PurchaseOrders");

            migrationBuilder.DropTable(
                name: "SupportingDocumentOwnerAttempts",
                schema: "PurchaseOrders");

            migrationBuilder.DropTable(
                name: "SupportingDocumentOwnerEvidence",
                schema: "PurchaseOrders");

            migrationBuilder.DropTable(
                name: "SupportingDocumentProcessorRegistrations",
                schema: "PurchaseOrders");

            migrationBuilder.DropTable(
                name: "PurchaseOrders",
                schema: "PurchaseOrders");
        }
    }
}
