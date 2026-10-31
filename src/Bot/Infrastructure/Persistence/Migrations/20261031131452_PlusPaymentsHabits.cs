using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Trivozhno.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PlusPaymentsHabits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ChatCount",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ChatCountDay",
                table: "Users",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FriendGender",
                table: "Users",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "FriendName",
                table: "Users",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "FriendTone",
                table: "Users",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PlusEndNoticeFor",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PlusUntil",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PlusWarnedFor",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Personal",
                table: "Reminders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "Habits",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Title = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Habits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Habits_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Payments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    TelegramId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Amount = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    TelegramChargeId = table.Column<string>(type: "text", nullable: false),
                    ProviderChargeId = table.Column<string>(type: "text", nullable: false),
                    PaidAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PeriodStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PeriodEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RefundedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Payments_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "HabitMarks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    HabitId = table.Column<long>(type: "bigint", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HabitMarks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HabitMarks_Habits_HabitId",
                        column: x => x.HabitId,
                        principalTable: "Habits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_PlusUntil",
                table: "Users",
                column: "PlusUntil");

            migrationBuilder.CreateIndex(
                name: "IX_HabitMarks_HabitId_Day",
                table: "HabitMarks",
                columns: new[] { "HabitId", "Day" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Habits_UserId_Id",
                table: "Habits",
                columns: new[] { "UserId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_TelegramChargeId",
                table: "Payments",
                column: "TelegramChargeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_TelegramId_PaidAt",
                table: "Payments",
                columns: new[] { "TelegramId", "PaidAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_UserId",
                table: "Payments",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HabitMarks");

            migrationBuilder.DropTable(
                name: "Payments");

            migrationBuilder.DropTable(
                name: "Habits");

            migrationBuilder.DropIndex(
                name: "IX_Users_PlusUntil",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ChatCount",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ChatCountDay",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "FriendGender",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "FriendName",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "FriendTone",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PlusEndNoticeFor",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PlusUntil",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PlusWarnedFor",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Personal",
                table: "Reminders");
        }
    }
}
