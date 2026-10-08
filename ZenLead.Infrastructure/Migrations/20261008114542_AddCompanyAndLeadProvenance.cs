using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZenLead.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyAndLeadProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "Leads",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                table: "Leads",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EmailVerificationStatus",
                table: "Leads",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "Leads",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceRunId",
                table: "Leads",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "Leads",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Companies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Domain = table.Column<string>(type: "nvarchar(253)", maxLength: 253, nullable: true),
                    Industry = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Country = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Size = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Companies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Companies_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_CompanyId",
                table: "Leads",
                column: "CompanyId");

            // normalise first, then fail loudly if lowercasing produced collisions — never silently merge or drop leads
            migrationBuilder.Sql("UPDATE Leads SET Email = LOWER(LTRIM(RTRIM(Email)))");
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM Leads GROUP BY WorkspaceId, Email HAVING COUNT(*) > 1)
                    THROW 50001, 'Duplicate (WorkspaceId, Email) leads exist. Merge or delete them before applying AddCompanyAndLeadProvenance.', 1;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Leads_WorkspaceId_Email",
                table: "Leads",
                columns: new[] { "WorkspaceId", "Email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Leads_WorkspaceId_SourceRunId",
                table: "Leads",
                columns: new[] { "WorkspaceId", "SourceRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_Companies_WorkspaceId_Domain",
                table: "Companies",
                columns: new[] { "WorkspaceId", "Domain" },
                unique: true,
                filter: "[Domain] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Companies_WorkspaceId_Name",
                table: "Companies",
                columns: new[] { "WorkspaceId", "Name" });

            migrationBuilder.AddForeignKey(
                name: "FK_Leads_Companies_CompanyId",
                table: "Leads",
                column: "CompanyId",
                principalTable: "Companies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Leads_Companies_CompanyId",
                table: "Leads");

            migrationBuilder.DropTable(
                name: "Companies");

            migrationBuilder.DropIndex(
                name: "IX_Leads_CompanyId",
                table: "Leads");

            migrationBuilder.DropIndex(
                name: "IX_Leads_WorkspaceId_Email",
                table: "Leads");

            migrationBuilder.DropIndex(
                name: "IX_Leads_WorkspaceId_SourceRunId",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "EmailVerificationStatus",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "SourceRunId",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "Leads");
        }
    }
}
