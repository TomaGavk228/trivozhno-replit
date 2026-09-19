using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Trivozhno.Infrastructure.Persistence;

#nullable disable

namespace Trivozhno.Infrastructure.Persistence.Migrations;

[DbContext(typeof(BotDb))]
[Migration("20260918221500_ChatPersonalizationTelemetry")]
public sealed class ChatPersonalizationTelemetry : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ChatStyleProfile",
            table: "Users",
            type: "text",
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "Model",
            table: "ApiUsage",
            type: "text",
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<int>(
            name: "PromptTokens",
            table: "ApiUsage",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "CompletionTokens",
            table: "ApiUsage",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "ReasoningTokens",
            table: "ApiUsage",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "CachedTokens",
            table: "ApiUsage",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.CreateIndex(
            name: "IX_ApiUsage_Model_At",
            table: "ApiUsage",
            columns: new[] { "Model", "At" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_ApiUsage_Model_At",
            table: "ApiUsage");

        migrationBuilder.DropColumn(name: "ChatStyleProfile", table: "Users");
        migrationBuilder.DropColumn(name: "Model", table: "ApiUsage");
        migrationBuilder.DropColumn(name: "PromptTokens", table: "ApiUsage");
        migrationBuilder.DropColumn(name: "CompletionTokens", table: "ApiUsage");
        migrationBuilder.DropColumn(name: "ReasoningTokens", table: "ApiUsage");
        migrationBuilder.DropColumn(name: "CachedTokens", table: "ApiUsage");
    }
}
