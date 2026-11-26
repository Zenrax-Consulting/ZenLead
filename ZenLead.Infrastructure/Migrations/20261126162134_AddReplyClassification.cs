using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZenLead.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReplyClassification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClassificationNote",
                table: "InboxMessages",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ClassifiedBy",
                table: "InboxMessages",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "NeedsReview",
                table: "InboxMessages",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // F25 rows labelled from headers (auto-replies) were classified by the header heuristics: ClassifiedBy = Header (1)
            migrationBuilder.Sql("UPDATE [InboxMessages] SET [ClassifiedBy] = 1, [ClassificationNote] = N'auto_header' WHERE [IsAutoReply] = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClassificationNote",
                table: "InboxMessages");

            migrationBuilder.DropColumn(
                name: "ClassifiedBy",
                table: "InboxMessages");

            migrationBuilder.DropColumn(
                name: "NeedsReview",
                table: "InboxMessages");
        }
    }
}
