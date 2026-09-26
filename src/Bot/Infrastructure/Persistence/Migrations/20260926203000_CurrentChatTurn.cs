using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Trivozhno.Infrastructure.Persistence;

#nullable disable

namespace Trivozhno.Infrastructure.Persistence.Migrations;

[DbContext(typeof(BotDb))]
[Migration("20260926203000_CurrentChatTurn")]
public sealed class CurrentChatTurn : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>("Revision", "Sessions", type: "bigint", nullable: false, defaultValue: 0L);
        migrationBuilder.AddColumn<string>("TurnText", "Messages", type: "text", nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>("TurnStartedAt", "Messages", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>("ReadyAt", "Messages", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<long>("TurnRevision", "Messages", type: "bigint", nullable: false, defaultValue: 0L);
        migrationBuilder.AddColumn<long>("TurnRevision", "Outbox", type: "bigint", nullable: true);
        migrationBuilder.AddColumn<long>("ReplyToId", "Outbox", type: "bigint", nullable: true);
        migrationBuilder.AddColumn<bool>("Burst", "Outbox", type: "boolean", nullable: false, defaultValue: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("Revision", "Sessions");
        migrationBuilder.DropColumn("TurnText", "Messages");
        migrationBuilder.DropColumn("TurnStartedAt", "Messages");
        migrationBuilder.DropColumn("ReadyAt", "Messages");
        migrationBuilder.DropColumn("TurnRevision", "Messages");
        migrationBuilder.DropColumn("TurnRevision", "Outbox");
        migrationBuilder.DropColumn("ReplyToId", "Outbox");
        migrationBuilder.DropColumn("Burst", "Outbox");
    }
}
