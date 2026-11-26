using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZenLead.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSendingEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SendAttempts",
                table: "CampaignEnrollments",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "CampaignId",
                table: "AiUsageLogs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EnrollmentId",
                table: "AiUsageLogs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Purpose",
                table: "AiUsageLogs",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "Succeeded",
                table: "AiUsageLogs",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "EmailMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EnrollmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StepId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LeadId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProviderMessageId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SmtpMessageId = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UsedAi = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    QueuedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DeliveredAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    OpenedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    BouncedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmailMessages_CampaignEnrollments_EnrollmentId",
                        column: x => x.EnrollmentId,
                        principalTable: "CampaignEnrollments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EmailMessages_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SuppressedEmails",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Reason = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SourceLeadId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SuppressedEmails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SuppressedEmails_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageLogs_WorkspaceId_CreatedAt",
                table: "AiUsageLogs",
                columns: new[] { "WorkspaceId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailMessages_EnrollmentId_StepId",
                table: "EmailMessages",
                columns: new[] { "EnrollmentId", "StepId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailMessages_ProviderMessageId",
                table: "EmailMessages",
                column: "ProviderMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_EmailMessages_WorkspaceId_CampaignId_SentAt",
                table: "EmailMessages",
                columns: new[] { "WorkspaceId", "CampaignId", "SentAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailMessages_WorkspaceId_QueuedAt",
                table: "EmailMessages",
                columns: new[] { "WorkspaceId", "QueuedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SuppressedEmails_WorkspaceId_Email",
                table: "SuppressedEmails",
                columns: new[] { "WorkspaceId", "Email" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailMessages");

            migrationBuilder.DropTable(
                name: "SuppressedEmails");

            migrationBuilder.DropIndex(
                name: "IX_AiUsageLogs_WorkspaceId_CreatedAt",
                table: "AiUsageLogs");

            migrationBuilder.DropColumn(
                name: "SendAttempts",
                table: "CampaignEnrollments");

            migrationBuilder.DropColumn(
                name: "CampaignId",
                table: "AiUsageLogs");

            migrationBuilder.DropColumn(
                name: "EnrollmentId",
                table: "AiUsageLogs");

            migrationBuilder.DropColumn(
                name: "Purpose",
                table: "AiUsageLogs");

            migrationBuilder.DropColumn(
                name: "Succeeded",
                table: "AiUsageLogs");
        }
    }
}
