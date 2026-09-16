using Microsoft.EntityFrameworkCore;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Persistence;

try
{
    var builder = WebApplication.CreateBuilder(args);
    var options = BotOptions.Load(builder.Configuration); var cli = args.Length > 0 && args[0] != "serve";
    options.Validate(!cli); builder.Services.AddBot(options);
    builder.Logging.ClearProviders(); builder.Logging.AddSimpleConsole(c => c.SingleLine = true);
    builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(45));
    if (!cli) builder.Services.AddHostedService<BotRuntime>();
    builder.WebHost.UseUrls($"http://0.0.0.0:{builder.Configuration["PORT"] ?? "8080"}");
    var app = builder.Build();
    if (cli) { Environment.ExitCode = await OperatorCommands.Run(app.Services, args); return; }
    app.MapGet("/api/healthz", () => Results.Ok(new { status = "alive" }));
    app.MapGet("/api/readyz", async (BotDb db, WorkerStatus status, IClock clock, CancellationToken ct) =>
    {
        try
        {
            return status.Running && clock.UtcNow - status.Heartbeat < TimeSpan.FromSeconds(40) && await db.Database.CanConnectAsync(ct)
                ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503);
        }
        catch { return Results.StatusCode(503); }
    });
    await app.RunAsync();
}
catch (Exception e)
{
    // Messages from network/SQL exceptions may contain secrets; only our startup validation is safe.
    Console.Error.WriteLine(e is InvalidOperationException && (e.Message.StartsWith("Set ") || e.Message.StartsWith("DATABASE_URL") || e.Message.StartsWith("Invalid "))
        ? e.Message : $"Startup failed ({e.GetType().Name}). Check README troubleshooting. Credentials and content are hidden.");
            for (Exception? error = e; error != null; error = error.InnerException)
{
    Console.Error.WriteLine($"Error type: {error.GetType().Name}");

    if (error is Npgsql.PostgresException pg)
    {
        Console.Error.WriteLine(
            $"SQLSTATE: {pg.SqlState}\n" +
            $"Table: {pg.TableName}\n" +
            $"Column: {pg.ColumnName}\n" +
            $"Constraint: {pg.ConstraintName}");
    }
}
    Environment.ExitCode = 1;
}

public partial class Program { }
