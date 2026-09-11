using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureToPay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PolicyEvaluationReservation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PolicyEvaluationReservations",
                schema: "Policy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkloadIssuer = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    WorkloadClientId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Operation = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EvaluationKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SubjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubjectVersion = table.Column<int>(type: "int", nullable: false),
                    IdempotencyFingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ReservedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicyEvaluationReservations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PolicyEvaluationReservations_OrganizationId_WorkloadIssuer_WorkloadClientId_Operation_EvaluationKey",
                schema: "Policy",
                table: "PolicyEvaluationReservations",
                columns: new[] { "OrganizationId", "WorkloadIssuer", "WorkloadClientId", "Operation", "EvaluationKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PolicyEvaluationReservations",
                schema: "Policy");
        }
    }
}
