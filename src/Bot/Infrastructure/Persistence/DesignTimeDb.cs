using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Trivozhno.Host;

namespace Trivozhno.Infrastructure.Persistence;

public sealed class DesignTimeDb : IDesignTimeDbContextFactory<BotDb>
{
    public BotDb CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<BotDb>().UseNpgsql(
        ConnectionStrings.Parse(Environment.GetEnvironmentVariable("DATABASE_URL") ?? "Host=localhost;Database=trivozhno_design;Username=postgres")).Options);
}
