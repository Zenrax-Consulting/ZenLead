using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZenLead.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserStatusAndRegistrationApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "AspNetUsers",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));
            // hand-edited: existing users get "now" rather than 0001-01-01
            migrationBuilder.Sql("UPDATE AspNetUsers SET CreatedAt = SYSUTCDATETIME();");

            // hand-edited: existing Phase 1 users (and the super admin) stay usable → Active (1)
            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "AspNetUsers",
                type: "int",
                nullable: false,
                defaultValue: 1);
            // …then drop the DB default so new rows must set the value in code (AppUser defaults to PendingApproval)
            migrationBuilder.Sql("DECLARE @c sysname = (SELECT dc.name FROM sys.default_constraints dc JOIN sys.columns col ON col.default_object_id = dc.object_id AND col.object_id = dc.parent_object_id WHERE dc.parent_object_id = OBJECT_ID('AspNetUsers') AND col.name = 'Status'); IF @c IS NOT NULL EXEC('ALTER TABLE AspNetUsers DROP CONSTRAINT ' + @c);");

            migrationBuilder.CreateTable(
                name: "RegistrationApprovals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UsedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Decision = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistrationApprovals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RegistrationApprovals_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_WorkspaceId_Status",
                table: "AspNetUsers",
                columns: new[] { "WorkspaceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_RegistrationApprovals_TokenHash",
                table: "RegistrationApprovals",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RegistrationApprovals_UserId_CreatedAt",
                table: "RegistrationApprovals",
                columns: new[] { "UserId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RegistrationApprovals");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_WorkspaceId_Status",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "AspNetUsers");
        }
    }
}
