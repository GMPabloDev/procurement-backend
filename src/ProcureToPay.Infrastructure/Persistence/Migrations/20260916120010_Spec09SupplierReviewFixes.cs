using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureToPay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Spec09SupplierReviewFixes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ApprovedSupplierCatalogEntries_OrganizationId_SupplierId_SpendCategoryCode_ProductId",
                schema: "Supplier",
                table: "ApprovedSupplierCatalogEntries");

            migrationBuilder.DropColumn(
                name: "AccountHolder",
                schema: "Supplier",
                table: "SupplierBankingVersions");

            migrationBuilder.AddColumn<bool>(
                name: "IsDefault",
                schema: "Supplier",
                table: "SupplierBankingVersions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "SupplierBankingCommands",
                schema: "Supplier",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SupplierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChangeKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    BankingDetailId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierBankingCommands", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovedSupplierCatalogEntries_OrganizationId_SupplierId_SpendCategoryCode_ProductId",
                schema: "Supplier",
                table: "ApprovedSupplierCatalogEntries",
                columns: new[] { "OrganizationId", "SupplierId", "SpendCategoryCode", "ProductId" },
                unique: true,
                filter: "[OrganizationId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierBankingCommands_OrganizationId_SupplierId_ChangeKey",
                schema: "Supplier",
                table: "SupplierBankingCommands",
                columns: new[] { "OrganizationId", "SupplierId", "ChangeKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SupplierBankingCommands",
                schema: "Supplier");

            migrationBuilder.DropIndex(
                name: "IX_ApprovedSupplierCatalogEntries_OrganizationId_SupplierId_SpendCategoryCode_ProductId",
                schema: "Supplier",
                table: "ApprovedSupplierCatalogEntries");

            migrationBuilder.DropColumn(
                name: "IsDefault",
                schema: "Supplier",
                table: "SupplierBankingVersions");

            migrationBuilder.AddColumn<string>(
                name: "AccountHolder",
                schema: "Supplier",
                table: "SupplierBankingVersions",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovedSupplierCatalogEntries_OrganizationId_SupplierId_SpendCategoryCode_ProductId",
                schema: "Supplier",
                table: "ApprovedSupplierCatalogEntries",
                columns: new[] { "OrganizationId", "SupplierId", "SpendCategoryCode", "ProductId" },
                unique: true,
                filter: "[ProductId] IS NOT NULL");
        }
    }
}
