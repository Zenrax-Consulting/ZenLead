using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZenLead.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWebhookEventsAndAnalyticsIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProcessedWebhookEvents",
                columns: table => new
                {
                    SgEventId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessedWebhookEvents", x => x.SgEventId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InboxMessages_WorkspaceId_Direction_IsAutoReply_OccurredAt",
                table: "InboxMessages",
                columns: new[] { "WorkspaceId", "Direction", "IsAutoReply", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailMessages_WorkspaceId_CampaignId_StepId_SentAt",
                table: "EmailMessages",
                columns: new[] { "WorkspaceId", "CampaignId", "StepId", "SentAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProcessedWebhookEvents_ReceivedAt",
                table: "ProcessedWebhookEvents",
                column: "ReceivedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProcessedWebhookEvents");

            migrationBuilder.DropIndex(
                name: "IX_InboxMessages_WorkspaceId_Direction_IsAutoReply_OccurredAt",
                table: "InboxMessages");

            migrationBuilder.DropIndex(
                name: "IX_EmailMessages_WorkspaceId_CampaignId_StepId_SentAt",
                table: "EmailMessages");
        }
    }
}
