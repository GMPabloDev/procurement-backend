using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

#nullable disable

namespace ProcureToPay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PolicyExceptionVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PolicyExceptionVerifications",
                schema: "Policy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EvaluationBundleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BaseBundleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowDecisionId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    TargetRequirementKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Binding = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Nonce = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    EvidenceDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ApproverId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    VerifierReference = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicyExceptionVerifications", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PolicyExceptionVerifications_EvaluationBundleId",
                schema: "Policy",
                table: "PolicyExceptionVerifications",
                column: "EvaluationBundleId");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyExceptionVerifications_WorkflowDecisionId_BaseBundleId_TargetRequirementKey",
                schema: "Policy",
                table: "PolicyExceptionVerifications",
                columns: new[] { "WorkflowDecisionId", "BaseBundleId", "TargetRequirementKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PolicyExceptionVerifications",
                schema: "Policy");
        }

    }
}
