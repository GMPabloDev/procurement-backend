using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureToPay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Spec07ReferenceCatalogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ReferenceCatalog");

            migrationBuilder.CreateTable(
                name: "CostCenters",
                schema: "ReferenceCatalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CurrentVersion = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CostCenters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CostCenters_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "Organization",
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SpendCategories",
                schema: "ReferenceCatalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CurrentVersion = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpendCategories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SpendCategories_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "Organization",
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CostCenterVersions",
                schema: "ReferenceCatalog",
                columns: table => new
                {
                    CostCenterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    PredecessorVersion = table.Column<int>(type: "int", nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CostCenterVersions", x => new { x.CostCenterId, x.Version });
                    table.ForeignKey(
                        name: "FK_CostCenterVersions_CostCenters_CostCenterId",
                        column: x => x.CostCenterId,
                        principalSchema: "ReferenceCatalog",
                        principalTable: "CostCenters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CostCenterVersions_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalSchema: "Organization",
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SpendCategoryVersions",
                schema: "ReferenceCatalog",
                columns: table => new
                {
                    SpendCategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Digest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    PredecessorVersion = table.Column<int>(type: "int", nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpendCategoryVersions", x => new { x.SpendCategoryId, x.Version });
                    table.ForeignKey(
                        name: "FK_SpendCategoryVersions_SpendCategories_SpendCategoryId",
                        column: x => x.SpendCategoryId,
                        principalSchema: "ReferenceCatalog",
                        principalTable: "SpendCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CostCenters_OrganizationId_Code",
                schema: "ReferenceCatalog",
                table: "CostCenters",
                columns: new[] { "OrganizationId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CostCenterVersions_CostCenterId_Version",
                schema: "ReferenceCatalog",
                table: "CostCenterVersions",
                columns: new[] { "CostCenterId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CostCenterVersions_DepartmentId",
                schema: "ReferenceCatalog",
                table: "CostCenterVersions",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_SpendCategories_OrganizationId_Code",
                schema: "ReferenceCatalog",
                table: "SpendCategories",
                columns: new[] { "OrganizationId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SpendCategoryVersions_OrganizationId_Code_Version",
                schema: "ReferenceCatalog",
                table: "SpendCategoryVersions",
                columns: new[] { "OrganizationId", "Code", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CostCenterVersions",
                schema: "ReferenceCatalog");

            migrationBuilder.DropTable(
                name: "SpendCategoryVersions",
                schema: "ReferenceCatalog");

            migrationBuilder.DropTable(
                name: "CostCenters",
                schema: "ReferenceCatalog");

            migrationBuilder.DropTable(
                name: "SpendCategories",
                schema: "ReferenceCatalog");
        }
    }
}
