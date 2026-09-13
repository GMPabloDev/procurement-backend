using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureToPay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Spec05PolicyExceptionIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PolicyExceptionRequests",
                schema: "Approval",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequirementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequirementKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SubjectType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SubjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubjectVersion = table.Column<int>(type: "int", nullable: false),
                    BaseBundleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BaseResultDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PolicyVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PolicyContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ManifestDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TargetRequirementKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CoveredLinesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    From = table.Column<int>(type: "int", nullable: false),
                    To = table.Column<int>(type: "int", nullable: false),
                    Floor = table.Column<int>(type: "int", nullable: false),
                    ReferenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequesterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OriginatorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkloadSubjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RequestedValidTo = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Nonce = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Binding = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicyExceptionRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PolicyExceptionRequests_ApprovalCases_CaseId",
                        column: x => x.CaseId,
                        principalSchema: "Approval",
                        principalTable: "ApprovalCases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PolicyExceptionVerifications",
                schema: "Approval",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequirementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VerifierType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    WorkflowDecisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowDecisionVersion = table.Column<int>(type: "int", nullable: false),
                    WorkflowDecisionDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EvidenceDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Binding = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Nonce = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Verified = table.Column<bool>(type: "bit", nullable: false),
                    FailureCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RevocationReference = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicyExceptionVerifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PolicyExceptionVerifications_PolicyExceptionRequests_RequestId",
                        column: x => x.RequestId,
                        principalSchema: "Approval",
                        principalTable: "PolicyExceptionRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PolicyExceptionRequests_CaseId_RequirementKey",
                schema: "Approval",
                table: "PolicyExceptionRequests",
                columns: new[] { "CaseId", "RequirementKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PolicyExceptionRequests_OrganizationId_BaseBundleId_TargetRequirementKey_Binding",
                schema: "Approval",
                table: "PolicyExceptionRequests",
                columns: new[] { "OrganizationId", "BaseBundleId", "TargetRequirementKey", "Binding" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PolicyExceptionRequests_RequirementId",
                schema: "Approval",
                table: "PolicyExceptionRequests",
                column: "RequirementId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PolicyExceptionVerifications_OrganizationId_VerifierType_Nonce",
                schema: "Approval",
                table: "PolicyExceptionVerifications",
                columns: new[] { "OrganizationId", "VerifierType", "Nonce" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PolicyExceptionVerifications_OrganizationId_WorkflowDecisionId_WorkflowDecisionVersion",
                schema: "Approval",
                table: "PolicyExceptionVerifications",
                columns: new[] { "OrganizationId", "WorkflowDecisionId", "WorkflowDecisionVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PolicyExceptionVerifications_RequestId",
                schema: "Approval",
                table: "PolicyExceptionVerifications",
                column: "RequestId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // SPEC 05 forbids destructive down migrations: reverting the application keeps the
            // Approval schema and its policy exception history.
            throw new NotSupportedException(
                "The Approval schema has no destructive downgrade; revert the application and keep the schema.");
        }
    }
}
