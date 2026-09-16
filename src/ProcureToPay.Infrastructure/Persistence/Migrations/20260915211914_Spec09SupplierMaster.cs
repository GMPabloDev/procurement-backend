using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureToPay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Spec09SupplierMaster : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "Supplier");

            migrationBuilder.AddColumn<string>(
                name: "ContractVersion",
                schema: "PurchaseRequest",
                table: "PurchaseRequestCompletenessManifests",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                // SPEC 09 REQ-09: rows attested before this spec keep the v1 contract so their
                // manifests and digests stay verifiable.
                defaultValue: "purchase-request-completeness-manifest/v1");

            migrationBuilder.AddColumn<string>(
                name: "SupplierFactSnapshotsJson",
                schema: "PurchaseRequest",
                table: "PurchaseRequestCompletenessManifests",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ApprovedSupplierCatalogEntries",
                schema: "Supplier",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SpendCategoryCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CurrentVersion = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovedSupplierCatalogEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SupplierAgreementAttachments",
                schema: "Supplier",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
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
                    table.PrimaryKey("PK_SupplierAgreementAttachments", x => new { x.Id, x.Version });
                });

            migrationBuilder.CreateTable(
                name: "SupplierApprovalResults",
                schema: "Supplier",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContractVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetVersion = table.Column<int>(type: "int", nullable: false),
                    Result = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierApprovalResults", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SupplierAuditRecords",
                schema: "Supplier",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ActorJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    CauseJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ChangedFieldsJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    CorrelationReference = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    EffectKey = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    TargetJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierAuditRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SupplierBankingDefaults",
                schema: "Supplier",
                columns: table => new
                {
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    BankingDetailId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierBankingDefaults", x => new { x.SupplierId, x.Currency });
                });

            migrationBuilder.CreateTable(
                name: "SupplierBankingDetails",
                schema: "Supplier",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierBankingDetails", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SupplierChangeProposals",
                schema: "Supplier",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BaseVersion = table.Column<int>(type: "int", nullable: true),
                    CandidateVersion = table.Column<int>(type: "int", nullable: true),
                    ChangeKind = table.Column<int>(type: "int", nullable: false),
                    SensitiveFieldsJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    RequestedStatus = table.Column<int>(type: "int", nullable: true),
                    EditorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    ChangeKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Fingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ApprovalCaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RequirementKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CaseContractVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ErrorCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierChangeProposals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SupplierFiscalIdentities",
                schema: "Supplier",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CountryCode = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    TaxIdKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TaxId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IdentityKeyDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    FirstApprovedVersion = table.Column<int>(type: "int", nullable: true),
                    LastApprovedVersion = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierFiscalIdentities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SupplierPolicyFactSnapshots",
                schema: "Supplier",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestVersion = table.Column<int>(type: "int", nullable: false),
                    LineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineVersion = table.Column<int>(type: "int", nullable: false),
                    LineContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SupplierVersion = table.Column<int>(type: "int", nullable: true),
                    SpendCategoryJson = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ProductJson = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CatalogEntryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CatalogEntryVersion = table.Column<int>(type: "int", nullable: true),
                    CatalogContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PreferredSupplier = table.Column<bool>(type: "bit", nullable: false),
                    AgreementStatus = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    EvaluatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SnapshotDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierPolicyFactSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SupplierPrerequisiteAttempts",
                schema: "Supplier",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PrerequisiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierVersion = table.Column<int>(type: "int", nullable: false),
                    TargetsJson = table.Column<string>(type: "nvarchar(max)", maxLength: 100000, nullable: false),
                    State = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    SignalResult = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    CheckKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SignalKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    EvidenceDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    DueAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    LeaseOwner = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
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
                    table.PrimaryKey("PK_SupplierPrerequisiteAttempts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SupplierPrerequisiteProcessorRegistrations",
                schema: "Supplier",
                columns: table => new
                {
                    AdapterId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    AdapterVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ProcessorId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierPrerequisiteProcessorRegistrations", x => new { x.AdapterId, x.AdapterVersion });
                });

            migrationBuilder.CreateTable(
                name: "SupplierStatusOutbox",
                schema: "Supplier",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierVersion = table.Column<int>(type: "int", nullable: false),
                    PreviousStatus = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    DispatchedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierStatusOutbox", x => x.EventId);
                });

            migrationBuilder.CreateTable(
                name: "ApprovedSupplierCatalogVersions",
                schema: "Supplier",
                columns: table => new
                {
                    CatalogEntryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierVersion = table.Column<int>(type: "int", nullable: false),
                    SpendCategoryJson = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ProductJson = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    NegotiatedPrice = table.Column<decimal>(type: "decimal(38,12)", precision: 38, scale: 12, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    UnitCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ExternalContractReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ValidFrom = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ValidTo = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AttachmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttachmentVersion = table.Column<int>(type: "int", nullable: false),
                    ContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PredecessorVersion = table.Column<int>(type: "int", nullable: true),
                    ProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApprovalCaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovedSupplierCatalogVersions", x => new { x.CatalogEntryId, x.Version });
                    table.ForeignKey(
                        name: "FK_ApprovedSupplierCatalogVersions_ApprovedSupplierCatalogEntries_CatalogEntryId",
                        column: x => x.CatalogEntryId,
                        principalSchema: "Supplier",
                        principalTable: "ApprovedSupplierCatalogEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SupplierBankingVersions",
                schema: "Supplier",
                columns: table => new
                {
                    BankingDetailId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountHolder = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    BankName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    BankCountryCode = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    AccountType = table.Column<int>(type: "int", nullable: false),
                    MaskedSuffix = table.Column<string>(type: "nvarchar(4)", maxLength: 4, nullable: false),
                    Ciphertext = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Nonce = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Tag = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    KeyVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PredecessorVersion = table.Column<int>(type: "int", nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    ChangeKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierBankingVersions", x => new { x.BankingDetailId, x.Version });
                    table.ForeignKey(
                        name: "FK_SupplierBankingVersions_SupplierBankingDetails_BankingDetailId",
                        column: x => x.BankingDetailId,
                        principalSchema: "Supplier",
                        principalTable: "SupplierBankingDetails",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Suppliers",
                schema: "Supplier",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FiscalIdentityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperationalVersion = table.Column<int>(type: "int", nullable: true),
                    WorkingProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Suppliers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Suppliers_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "Organization",
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Suppliers_SupplierFiscalIdentities_FiscalIdentityId",
                        column: x => x.FiscalIdentityId,
                        principalSchema: "Supplier",
                        principalTable: "SupplierFiscalIdentities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupplierVersions",
                schema: "Supplier",
                columns: table => new
                {
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PredecessorVersion = table.Column<int>(type: "int", nullable: true),
                    LegalName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    TradeName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CountryCode = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    TaxId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AddressesJson = table.Column<string>(type: "nvarchar(max)", maxLength: 100000, nullable: false),
                    ContactsJson = table.Column<string>(type: "nvarchar(max)", maxLength: 100000, nullable: false),
                    PaymentTermsJson = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    SupportedCurrenciesJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    CategoriesSuppliedJson = table.Column<string>(type: "nvarchar(max)", maxLength: 100000, nullable: false),
                    PerformanceJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    BankingRefsJson = table.Column<string>(type: "nvarchar(max)", maxLength: 20000, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    RiskStatus = table.Column<int>(type: "int", nullable: false),
                    ContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApprovalCaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ChangeKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierVersions", x => new { x.SupplierId, x.Version });
                    table.ForeignKey(
                        name: "FK_SupplierVersions_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalSchema: "Supplier",
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovedSupplierCatalogEntries_OrganizationId_SupplierId_SpendCategoryCode_ProductId",
                schema: "Supplier",
                table: "ApprovedSupplierCatalogEntries",
                columns: new[] { "OrganizationId", "SupplierId", "SpendCategoryCode", "ProductId" },
                unique: true,
                filter: "[ProductId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovedSupplierCatalogVersions_AttachmentId",
                schema: "Supplier",
                table: "ApprovedSupplierCatalogVersions",
                column: "AttachmentId");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovedSupplierCatalogVersions_OrganizationId_SupplierId",
                schema: "Supplier",
                table: "ApprovedSupplierCatalogVersions",
                columns: new[] { "OrganizationId", "SupplierId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierAgreementAttachments_OrganizationId_SupplierId",
                schema: "Supplier",
                table: "SupplierAgreementAttachments",
                columns: new[] { "OrganizationId", "SupplierId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierApprovalResults_EventId_ContractVersion_TargetId",
                schema: "Supplier",
                table: "SupplierApprovalResults",
                columns: new[] { "EventId", "ContractVersion", "TargetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierAuditRecords_OrganizationId_Action_EffectKey",
                schema: "Supplier",
                table: "SupplierAuditRecords",
                columns: new[] { "OrganizationId", "Action", "EffectKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierBankingDetails_OrganizationId_SupplierId",
                schema: "Supplier",
                table: "SupplierBankingDetails",
                columns: new[] { "OrganizationId", "SupplierId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierBankingVersions_OrganizationId_SupplierId",
                schema: "Supplier",
                table: "SupplierBankingVersions",
                columns: new[] { "OrganizationId", "SupplierId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierChangeProposals_ApprovalCaseId",
                schema: "Supplier",
                table: "SupplierChangeProposals",
                column: "ApprovalCaseId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierChangeProposals_OrganizationId_EditorUserId_ChangeKey",
                schema: "Supplier",
                table: "SupplierChangeProposals",
                columns: new[] { "OrganizationId", "EditorUserId", "ChangeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierChangeProposals_OrganizationId_SupplierId",
                schema: "Supplier",
                table: "SupplierChangeProposals",
                columns: new[] { "OrganizationId", "SupplierId" },
                unique: true,
                filter: "[State] IN (1, 2)");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierFiscalIdentities_OrganizationId_IdentityKeyDigest",
                schema: "Supplier",
                table: "SupplierFiscalIdentities",
                columns: new[] { "OrganizationId", "IdentityKeyDigest" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierFiscalIdentities_SupplierId",
                schema: "Supplier",
                table: "SupplierFiscalIdentities",
                column: "SupplierId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPolicyFactSnapshots_RequestId_RequestVersion_LineId",
                schema: "Supplier",
                table: "SupplierPolicyFactSnapshots",
                columns: new[] { "RequestId", "RequestVersion", "LineId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPrerequisiteAttempts_DueAt",
                schema: "Supplier",
                table: "SupplierPrerequisiteAttempts",
                column: "DueAt");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPrerequisiteAttempts_PrerequisiteId",
                schema: "Supplier",
                table: "SupplierPrerequisiteAttempts",
                column: "PrerequisiteId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierPrerequisiteAttempts_State",
                schema: "Supplier",
                table: "SupplierPrerequisiteAttempts",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_FiscalIdentityId",
                schema: "Supplier",
                table: "Suppliers",
                column: "FiscalIdentityId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_OrganizationId",
                schema: "Supplier",
                table: "Suppliers",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierStatusOutbox_OrganizationId_OccurredAt",
                schema: "Supplier",
                table: "SupplierStatusOutbox",
                columns: new[] { "OrganizationId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierVersions_OrganizationId_SupplierId",
                schema: "Supplier",
                table: "SupplierVersions",
                columns: new[] { "OrganizationId", "SupplierId" });

            // The real processor of active-supplier-owner/v1 is registered exactly once; readiness
            // compares its identity with the workload Approval resolved and persisted (REQ-10).
            migrationBuilder.Sql(
                "INSERT INTO [Supplier].[SupplierPrerequisiteProcessorRegistrations] " +
                "([AdapterId], [AdapterVersion], [ProcessorId]) VALUES " +
                "('active-supplier-owner', 'v1', 'supplier-domain');");
            migrationBuilder.Sql(
                "CREATE TRIGGER [Supplier].[TR_SupplierVersions_AppendOnly] " +
                "ON [Supplier].[SupplierVersions] AFTER UPDATE, DELETE AS " +
                "BEGIN SET NOCOUNT ON; " +
                "IF EXISTS (SELECT 1 FROM deleted) " +
                "THROW 51100, 'Supplier versions are append-only.', 1; END");
            migrationBuilder.Sql(
                "CREATE TRIGGER [Supplier].[TR_SupplierBankingVersions_AppendOnly] " +
                "ON [Supplier].[SupplierBankingVersions] AFTER UPDATE, DELETE AS " +
                "BEGIN SET NOCOUNT ON; " +
                "IF EXISTS (SELECT 1 FROM deleted) " +
                "THROW 51100, 'Supplier banking versions are append-only.', 1; END");
            migrationBuilder.Sql(
                "CREATE TRIGGER [Supplier].[TR_ApprovedSupplierCatalogVersions_AppendOnly] " +
                "ON [Supplier].[ApprovedSupplierCatalogVersions] AFTER UPDATE, DELETE AS " +
                "BEGIN SET NOCOUNT ON; " +
                "IF EXISTS (SELECT 1 FROM deleted) " +
                "THROW 51100, 'Approved supplier catalog versions are append-only.', 1; END");
            migrationBuilder.Sql(
                "CREATE TRIGGER [Supplier].[TR_SupplierAudit_AppendOnly] " +
                "ON [Supplier].[SupplierAuditRecords] AFTER UPDATE, DELETE AS " +
                "BEGIN SET NOCOUNT ON; " +
                "IF EXISTS (SELECT 1 FROM deleted) " +
                "THROW 51100, 'Supplier audit records are append-only.', 1; END");
            migrationBuilder.Sql(
                "CREATE TRIGGER [Supplier].[TR_SupplierVersions_PredecessorExists] " +
                "ON [Supplier].[SupplierVersions] AFTER INSERT AS " +
                "BEGIN SET NOCOUNT ON; " +
                "IF EXISTS (SELECT 1 FROM inserted i WHERE " +
                "(i.Version > 1 AND NOT EXISTS (SELECT 1 FROM [Supplier].[SupplierVersions] v " +
                "WHERE v.[SupplierId] = i.[SupplierId] AND v.[Version] = i.[PredecessorVersion]))) " +
                "THROW 51102, 'A supplier version must keep its predecessor.', 1; END");
            migrationBuilder.Sql(
                "CREATE TRIGGER [Supplier].[TR_ApprovedSupplierCatalogVersions_PredecessorExists] " +
                "ON [Supplier].[ApprovedSupplierCatalogVersions] AFTER INSERT AS " +
                "BEGIN SET NOCOUNT ON; " +
                "IF EXISTS (SELECT 1 FROM inserted i WHERE " +
                "(i.Version > 1 AND NOT EXISTS (SELECT 1 FROM [Supplier].[ApprovedSupplierCatalogVersions] v " +
                "WHERE v.[CatalogEntryId] = i.[CatalogEntryId] AND v.[Version] = i.[PredecessorVersion]))) " +
                "THROW 51102, 'A catalog version must keep its predecessor.', 1; END");
            migrationBuilder.Sql(
                "CREATE TRIGGER [Supplier].[TR_Suppliers_PointerStable] " +
                "ON [Supplier].[Suppliers] AFTER UPDATE AS " +
                "BEGIN SET NOCOUNT ON; " +
                "IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON i.[Id] = d.[Id] WHERE " +
                "i.[OrganizationId] <> d.[OrganizationId] OR i.[FiscalIdentityId] <> d.[FiscalIdentityId] OR " +
                "(d.[OperationalVersion] IS NOT NULL AND " +
                "(i.[OperationalVersion] IS NULL OR i.[OperationalVersion] < d.[OperationalVersion]))) " +
                "THROW 51103, 'The supplier identity is stable and its approved pointer never moves backwards.', 1; " +
                "IF EXISTS (SELECT 1 FROM inserted i WHERE i.[OperationalVersion] IS NOT NULL AND NOT EXISTS (" +
                "SELECT 1 FROM [Supplier].[SupplierVersions] v " +
                "WHERE v.[SupplierId] = i.[Id] AND v.[Version] = i.[OperationalVersion])) " +
                "THROW 51103, 'The approved supplier pointer must reference an existing version.', 1; END");
            migrationBuilder.Sql(
                "CREATE TRIGGER [Supplier].[TR_ApprovedSupplierCatalogEntries_PointerStable] " +
                "ON [Supplier].[ApprovedSupplierCatalogEntries] AFTER UPDATE AS " +
                "BEGIN SET NOCOUNT ON; " +
                "IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON i.[Id] = d.[Id] WHERE " +
                "i.[OrganizationId] <> d.[OrganizationId] OR i.[SupplierId] <> d.[SupplierId] OR " +
                "i.[SpendCategoryCode] <> d.[SpendCategoryCode] OR " +
                "((i.[ProductId] IS NULL AND d.[ProductId] IS NOT NULL) OR " +
                "(i.[ProductId] IS NOT NULL AND d.[ProductId] IS NULL)) OR " +
                "(i.[CurrentVersion] < d.[CurrentVersion])) " +
                "THROW 51103, 'The catalog selector is stable and its pointer never moves backwards.', 1; END");
            migrationBuilder.Sql(
                "CREATE TRIGGER [Supplier].[TR_SupplierPrerequisiteAttempts_Terminal] " +
                "ON [Supplier].[SupplierPrerequisiteAttempts] AFTER UPDATE AS " +
                "BEGIN SET NOCOUNT ON; " +
                "IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON i.[Id] = d.[Id] WHERE " +
                "d.[State] = 'COMPLETED' AND i.[State] <> 'COMPLETED') " +
                "THROW 51104, 'A completed supplier attempt is terminal.', 1; END");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApprovedSupplierCatalogVersions",
                schema: "Supplier");

            migrationBuilder.DropTable(
                name: "SupplierAgreementAttachments",
                schema: "Supplier");

            migrationBuilder.DropTable(
                name: "SupplierApprovalResults",
                schema: "Supplier");

            migrationBuilder.DropTable(
                name: "SupplierAuditRecords",
                schema: "Supplier");

            migrationBuilder.DropTable(
                name: "SupplierBankingDefaults",
                schema: "Supplier");

            migrationBuilder.DropTable(
                name: "SupplierBankingVersions",
                schema: "Supplier");

            migrationBuilder.DropTable(
                name: "SupplierChangeProposals",
                schema: "Supplier");

            migrationBuilder.DropTable(
                name: "SupplierPolicyFactSnapshots",
                schema: "Supplier");

            migrationBuilder.DropTable(
                name: "SupplierPrerequisiteAttempts",
                schema: "Supplier");

            migrationBuilder.DropTable(
                name: "SupplierPrerequisiteProcessorRegistrations",
                schema: "Supplier");

            migrationBuilder.DropTable(
                name: "SupplierStatusOutbox",
                schema: "Supplier");

            migrationBuilder.DropTable(
                name: "SupplierVersions",
                schema: "Supplier");

            migrationBuilder.DropTable(
                name: "ApprovedSupplierCatalogEntries",
                schema: "Supplier");

            migrationBuilder.DropTable(
                name: "SupplierBankingDetails",
                schema: "Supplier");

            migrationBuilder.DropTable(
                name: "Suppliers",
                schema: "Supplier");

            migrationBuilder.DropTable(
                name: "SupplierFiscalIdentities",
                schema: "Supplier");

            // SPEC 09 NFR-01: supplier history is append-only and the pointers only ever move to an
            // existing successor, so no procedure can rewrite an approved version by hand.
            migrationBuilder.DropColumn(
                name: "ContractVersion",
                schema: "PurchaseRequest",
                table: "PurchaseRequestCompletenessManifests");

            migrationBuilder.DropColumn(
                name: "SupplierFactSnapshotsJson",
                schema: "PurchaseRequest",
                table: "PurchaseRequestCompletenessManifests");
        }
    }
}
