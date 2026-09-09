using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProcureToPay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrganizationConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RoleAssignments_UserProfileId_Role_Id",
                schema: "Organization",
                table: "RoleAssignments");

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_UserProfileId_Role_ScopeJson",
                schema: "Organization",
                table: "RoleAssignments",
                columns: new[] { "UserProfileId", "Role", "ScopeJson" },
                unique: true,
                filter: "[Status] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_LegalEntities_OrganizationId",
                schema: "Organization",
                table: "LegalEntities",
                column: "OrganizationId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RoleAssignments_UserProfileId_Role_ScopeJson",
                schema: "Organization",
                table: "RoleAssignments");

            migrationBuilder.DropIndex(
                name: "IX_LegalEntities_OrganizationId",
                schema: "Organization",
                table: "LegalEntities");

            migrationBuilder.CreateIndex(
                name: "IX_RoleAssignments_UserProfileId_Role_Id",
                schema: "Organization",
                table: "RoleAssignments",
                columns: new[] { "UserProfileId", "Role", "Id" },
                unique: true);
        }
    }
}
