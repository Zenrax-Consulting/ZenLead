using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZenLead.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadListIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Leads_WorkspaceId_CompanyId",
                table: "Leads",
                columns: new[] { "WorkspaceId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_WorkspaceId_CreatedAt",
                table: "Leads",
                columns: new[] { "WorkspaceId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_WorkspaceId_Status",
                table: "Leads",
                columns: new[] { "WorkspaceId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Leads_WorkspaceId_CompanyId",
                table: "Leads");

            migrationBuilder.DropIndex(
                name: "IX_Leads_WorkspaceId_CreatedAt",
                table: "Leads");

            migrationBuilder.DropIndex(
                name: "IX_Leads_WorkspaceId_Status",
                table: "Leads");
        }
    }
}
