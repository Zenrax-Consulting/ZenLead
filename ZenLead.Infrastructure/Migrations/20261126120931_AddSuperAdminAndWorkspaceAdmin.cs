using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZenLead.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSuperAdminAndWorkspaceAdmin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AdminEmail",
                table: "Workspaces",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "WorkspaceId",
                table: "AspNetUsers",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<int>(
                name: "PlatformRole",
                table: "AspNetUsers",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "AuditLogEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Action = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TargetType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DetailJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogEntries", x => x.Id);
                });

            // hand-added: fail with a clear message instead of a confusing unique-index error (dev databases can simply be reset)
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM Workspaces GROUP BY Name HAVING COUNT(*) > 1)
                    THROW 50002, 'Duplicate workspace names exist. Rename them before applying AddSuperAdminAndWorkspaceAdmin.', 1;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Workspaces_Name",
                table: "Workspaces",
                column: "Name",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_AspNetUsers_WorkspaceOrSuperAdmin",
                table: "AspNetUsers",
                sql: "[WorkspaceId] IS NOT NULL OR [PlatformRole] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogEntries_Timestamp",
                table: "AuditLogEntries",
                column: "Timestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditLogEntries");

            migrationBuilder.DropIndex(
                name: "IX_Workspaces_Name",
                table: "Workspaces");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AspNetUsers_WorkspaceOrSuperAdmin",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "AdminEmail",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "PlatformRole",
                table: "AspNetUsers");

            migrationBuilder.AlterColumn<Guid>(
                name: "WorkspaceId",
                table: "AspNetUsers",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);
        }
    }
}
