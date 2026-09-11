using System;
#pragma warning disable CS0111, CS0115, CS0579

using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureToPay.Infrastructure.Persistence.Migrations
{
    [DbContext(typeof(ProcureToPayDbContext))]
    // pi-lens-ignore: CS0579
    [Migration("20260909152512_PolicyEngineFoundation")]
    public partial class PolicyEngineFoundationMigration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "Policy");

            migrationBuilder.CreateTable(
                name: "PolicySetVersions",
                schema: "Policy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ScopesJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ContentJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicySetVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PolicySetVersions_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "Organization",
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PolicyActivations",
                schema: "Policy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PolicySetVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EffectiveFrom = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ActorType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicyActivations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PolicyActivations_PolicySetVersions_PolicySetVersionId",
                        column: x => x.PolicySetVersionId,
                        principalSchema: "Policy",
                        principalTable: "PolicySetVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PolicyEvaluationBundles",
                schema: "Policy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EvaluationKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    WorkloadIssuer = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    WorkloadClientId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Operation = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SubjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubjectVersion = table.Column<int>(type: "int", nullable: false),
                    PolicySetVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EvaluatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PolicyContentDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    InputDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Result = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ResultDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    BundleJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IdempotencyFingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PreviousBundleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CorrelationReference = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicyEvaluationBundles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PolicyEvaluationBundles_PolicySetVersions_PolicySetVersionId",
                        column: x => x.PolicySetVersionId,
                        principalSchema: "Policy",
                        principalTable: "PolicySetVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PolicyRetirements",
                schema: "Policy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PolicyActivationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EffectiveTo = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ActorType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicyRetirements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PolicyRetirements_PolicyActivations_PolicyActivationId",
                        column: x => x.PolicyActivationId,
                        principalSchema: "Policy",
                        principalTable: "PolicyActivations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PolicyActivations_PolicySetVersionId_EffectiveFrom",
                schema: "Policy",
                table: "PolicyActivations",
                columns: new[] { "PolicySetVersionId", "EffectiveFrom" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PolicyEvaluationBundles_OrganizationId_SubjectId_SubjectVersion",
                schema: "Policy",
                table: "PolicyEvaluationBundles",
                columns: new[] { "OrganizationId", "SubjectId", "SubjectVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_PolicyEvaluationBundles_OrganizationId_WorkloadIssuer_WorkloadClientId_Operation_EvaluationKey",
                schema: "Policy",
                table: "PolicyEvaluationBundles",
                columns: new[] { "OrganizationId", "WorkloadIssuer", "WorkloadClientId", "Operation", "EvaluationKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PolicyEvaluationBundles_PolicySetVersionId",
                schema: "Policy",
                table: "PolicyEvaluationBundles",
                column: "PolicySetVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyRetirements_PolicyActivationId",
                schema: "Policy",
                table: "PolicyRetirements",
                column: "PolicyActivationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PolicySetVersions_OrganizationId_Sequence",
                schema: "Policy",
                table: "PolicySetVersions",
                columns: new[] { "OrganizationId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PolicyEvaluationBundles",
                schema: "Policy");

            migrationBuilder.DropTable(
                name: "PolicyRetirements",
                schema: "Policy");

            migrationBuilder.DropTable(
                name: "PolicyActivations",
                schema: "Policy");

            migrationBuilder.DropTable(
                name: "PolicySetVersions",
                schema: "Policy");
        }
    }
}
