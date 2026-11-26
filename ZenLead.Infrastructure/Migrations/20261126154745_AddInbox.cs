using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZenLead.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InboundQuarantine",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    FromAddress = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ToAddress = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    HeadersSnippet = table.Column<string>(type: "nvarchar(max)", maxLength: 4096, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboundQuarantine", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InboxThreads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LeadId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EnrollmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    LastMessageAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsRead = table.Column<bool>(type: "bit", nullable: false),
                    Classification = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxThreads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InboxThreads_Leads_LeadId",
                        column: x => x.LeadId,
                        principalTable: "Leads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InboxThreads_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InboxMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ThreadId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Direction = table.Column<int>(type: "int", nullable: false),
                    FromAddress = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    FromName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ToAddress = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    TextBody = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FullTextBody = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    HtmlBody = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Classification = table.Column<int>(type: "int", nullable: false),
                    ClassificationConfidence = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: true),
                    RawMessageId = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    EmailMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SenderMismatch = table.Column<bool>(type: "bit", nullable: false),
                    IsAutoReply = table.Column<bool>(type: "bit", nullable: false),
                    AuthSummary = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InboxMessages_InboxThreads_ThreadId",
                        column: x => x.ThreadId,
                        principalTable: "InboxThreads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InboxMessages_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InboundQuarantine_ReceivedAt",
                table: "InboundQuarantine",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_InboxMessages_EmailMessageId",
                table: "InboxMessages",
                column: "EmailMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_InboxMessages_ThreadId_OccurredAt",
                table: "InboxMessages",
                columns: new[] { "ThreadId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_InboxMessages_WorkspaceId_RawMessageId",
                table: "InboxMessages",
                columns: new[] { "WorkspaceId", "RawMessageId" },
                unique: true,
                filter: "[RawMessageId] IS NOT NULL AND [Direction] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_InboxThreads_EnrollmentId",
                table: "InboxThreads",
                column: "EnrollmentId",
                unique: true,
                filter: "[EnrollmentId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_InboxThreads_LeadId",
                table: "InboxThreads",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_InboxThreads_WorkspaceId_Classification",
                table: "InboxThreads",
                columns: new[] { "WorkspaceId", "Classification" });

            migrationBuilder.CreateIndex(
                name: "IX_InboxThreads_WorkspaceId_IsRead",
                table: "InboxThreads",
                columns: new[] { "WorkspaceId", "IsRead" });

            migrationBuilder.CreateIndex(
                name: "IX_InboxThreads_WorkspaceId_LastMessageAt",
                table: "InboxThreads",
                columns: new[] { "WorkspaceId", "LastMessageAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InboundQuarantine");

            migrationBuilder.DropTable(
                name: "InboxMessages");

            migrationBuilder.DropTable(
                name: "InboxThreads");
        }
    }
}
