using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trivozhno.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReliableChatMemoryAndDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Removed UI states must remain readable for existing users.
            migrationBuilder.Sql("""
                UPDATE "Users" SET "State" = 'Settings' WHERE "State" = 'ConfirmMoodContext';
                UPDATE "Users" SET "ReturnState" = 'Settings' WHERE "ReturnState" = 'ConfirmMoodContext';
                UPDATE "Messages" m SET "Status" = 'interrupted'
                WHERE m."Role" = 'assistant' AND m."Status" = 'done'
                  AND EXISTS (SELECT 1 FROM "Outbox" o WHERE o."Kind" = 'ai'
                    AND o."ReplyToId" = m."ReplyToId" AND o."Status" != 'sent');
                """);
            migrationBuilder.DropColumn(
                name: "MoodConsentShown",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "MoodContextEnabled",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "SummaryNextAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "QueueNoticeShown",
                table: "Messages");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeaseUntil",
                table: "Outbox",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LeaseUntil",
                table: "Outbox");

            migrationBuilder.AddColumn<bool>(
                name: "MoodConsentShown",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "MoodContextEnabled",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SummaryNextAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<bool>(
                name: "QueueNoticeShown",
                table: "Messages",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
