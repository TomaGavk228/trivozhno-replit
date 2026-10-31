using Microsoft.EntityFrameworkCore;
using Trivozhno.Features.Navigation;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Persistence;
using Trivozhno.Resources;

namespace Trivozhno.Features.Support;

// «Мені зараз погано» and short anxiety exercises. No AI involved, so this
// works with an exhausted daily limit and while Gemini is unavailable.
public sealed class SupportHandler(BotDb db, Ui ui, Uk uk, IClock clock)
{
    public const string Kind = "exercise";

    public void Sos(BotUser u) => ui.Say(u, "sos.text", ui.Keyboard(u,
        [ui.B("ex.breath.button", "ex:breath")], [ui.B("ex.ground.button", "ex:ground")], [ui.B("sos.talk", "sos:talk")]));

    // Queued steps wait in the ordered outbox; any later action removes the rest.
    public Task<int> StopExercise(BotUser u, CancellationToken ct) =>
        db.Outbox.Where(x => x.UserId == u.Id && x.Kind == Kind && x.Status == "queued").ExecuteDeleteAsync(ct);

    public void Stopped(BotUser u) => ui.Say(u, "ex.stopped", ui.Keyboard(u,
        [ui.B("ex.breath.button", "ex:breath"), ui.B("ex.ground.button", "ex:ground")], [ui.B("sos.talk", "sos:talk")]));

    public async Task Breath(BotUser u, CancellationToken ct)
    {
        await StopExercise(u, ct);
        const int cycles = 5;
        var steps = new List<(double At, string Text)> { (0, uk["ex.breath.intro"]) };
        for (var i = 0; i < cycles; i++)
        {
            var start = 6 + i * 11;
            steps.Add((start, uk.Format("ex.breath.in", i + 1, cycles)));
            steps.Add((start + 4, uk["ex.breath.out"]));
        }
        steps.Add((6 + cycles * 11 + 1, uk["ex.breath.end"]));
        Enqueue(u, steps);
    }

    public async Task Ground(BotUser u, CancellationToken ct)
    {
        await StopExercise(u, ct);
        Enqueue(u, [(0, uk["ex.ground.intro"]), (6, uk["ex.ground.5"]), (36, uk["ex.ground.4"]), (61, uk["ex.ground.3"]),
            (81, uk["ex.ground.2"]), (99, uk["ex.ground.1"]), (114, uk["ex.ground.end"])]);
    }

    private void Enqueue(BotUser u, IReadOnlyList<(double At, string Text)> steps)
    {
        var operation = Guid.NewGuid();
        var now = clock.UtcNow;
        var stop = ui.Keyboard(u, [ui.B("ex.stop", "ex:stop")]);
        var end = ui.Keyboard(u, [ui.B("ex.again", "sos")], [ui.B("sos.talk", "sos:talk")]);
        for (var i = 0; i < steps.Count; i++)
            db.Outbox.Add(new()
            {
                UserId = u.Id, Destination = u.TelegramId, Kind = Kind, OperationId = operation, PartIndex = i,
                Text = steps[i].Text, Markup = i == steps.Count - 1 ? end : stop,
                CreatedAt = now, AvailableAt = now.AddSeconds(steps[i].At)
            });
    }
}
