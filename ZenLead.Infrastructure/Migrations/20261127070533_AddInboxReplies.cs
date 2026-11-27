using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZenLead.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddInboxReplies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ClientRequestId",
                table: "InboxMessages",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryError",
                table: "InboxMessages",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeliveryStatus",
                table: "InboxMessages",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SentByUserId",
                table: "InboxMessages",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_InboxMessages_ThreadId_ClientRequestId",
                table: "InboxMessages",
                columns: new[] { "ThreadId", "ClientRequestId" },
                unique: true,
                filter: "[ClientRequestId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InboxMessages_ThreadId_ClientRequestId",
                table: "InboxMessages");

            migrationBuilder.DropColumn(
                name: "ClientRequestId",
                table: "InboxMessages");

            migrationBuilder.DropColumn(
                name: "DeliveryError",
                table: "InboxMessages");

            migrationBuilder.DropColumn(
                name: "DeliveryStatus",
                table: "InboxMessages");

            migrationBuilder.DropColumn(
                name: "SentByUserId",
                table: "InboxMessages");
        }
    }
}
