using System.Text.RegularExpressions;
using Trivozhno.Infrastructure.Groq;

namespace Trivozhno.Features.Conversation;

// What the dialogue needs RIGHT NOW. Read locally from the visible chat only;
// the result is a short working hint for the model, never a label on the person.
public sealed record FriendSignals(
    DialogueAct Act,
    bool Crisis,
    bool DeclinedRecently,
    int ShortRepliesInRow,
    int UserTurns,
    bool WantsDistraction);

public static class FriendTurn
{
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(100);

    private const string CrisisPattern =
        @"(не\s+хочу\s+(більше\s+)?жити|хочу\s+померти|краще\s+б\s+(я\s+)?(не\s+)?(жив|помер|існував)|покінчити\s+(з\s+собою|з\s+усім|з\s+цим)|" +
        @"вбити\s+себе|вбʼю\s+себе|вб'ю\s+себе|накласти\s+на\s+себе\s+руки|суїцид\p{L}*|самогуб\p{L}*|" +
        @"не\s+бачу\s+сенсу\s+(жити|далі\s+жити|існувати)|набридло\s+жити|втомився\s+жити|втомилась\s+жити|" +
        @"(поріз|різати|ріжу|шкодити|нашкодити)\p{L}*\s+(себе|собі)|хочу\s+зникнути\s+назавжди)";

    public static FriendSignals Analyze(IReadOnlyList<AiMessage> conversation)
    {
        var users = conversation.Where(m => m.Role == "user").Select(m => m.Content.Trim()).ToArray();
        if (users.Length == 0) return new(DialogueAct.Sharing, false, false, 0, 0, false);
        var current = users[^1];
        var act = DialogueClassifier.Classify(current);

        // Only the latest two user turns: an old remark must not keep the bot in crisis mode forever.
        var crisis = users.TakeLast(2).Any(t => Matches(t, CrisisPattern));

        var recent = users.TakeLast(4).ToArray();
        var declined = recent.Any(t => DialogueClassifier.Classify(t) == DialogueAct.Refusal ||
                                       Matches(t, @"\bне\s+(хочу|треба|буду)\b.*\b(вправ|технік|порад|це)\p{L}*|\bне\s+радь\b|\bбез\s+порад\b"));

        var shortRun = 0;
        for (var i = users.Length - 1; i >= 0; i--)
        {
            var a = DialogueClassifier.Classify(users[i]);
            if (a is DialogueAct.ShortReply or DialogueAct.Refusal) shortRun++; else break;
        }

        var distraction = Matches(current,
            @"\b(відверн\p{L}*|відволік\p{L}*|переключ\p{L}*|розкажи\s+(щось|історію|анекдот)|розваж\p{L}*|поговорім\s+про\s+щось)\b");

        return new(act, crisis, declined, shortRun, users.Length, distraction);
    }

    // A compact instruction block for this one turn. Reference data, not a command from the person.
    public static string Hint(FriendSignals s)
    {
        var lines = new List<string>();
        if (s.Crisis)
        {
            lines.Add("У повідомленнях є ознаки думок про самогубство чи самоушкодження. Не ігноруй і не відволікай на інше. " +
                      "Спокійно, без паніки й лекцій: покажи, що почув, і прямо, по-людськи запитай, чи йдеться про бажання позбавити себе життя або нашкодити собі (якщо ще не питали). " +
                      "Дізнайся, чи людина зараз у безпеці, і залишайся поруч. Підштовхни звернутися до живої людини поруч; " +
                      "при безпосередній небезпеці - 112, безкоштовна лінія підтримки в Україні - 7333. " +
                      "Не кажи, що бажання померти звучить логічно чи зрозуміло, не сперечайся з ним, не пропонуй вправ і технік. Без списків, 2–4 короткі речення.");
            return Wrap(lines);
        }

        switch (s.Act)
        {
            case DialogueAct.Greeting:
                lines.Add("Це привітання. Привітайся коротко й по-живому, одним реченням, без анкети «як справи, чим займаєшся».");
                break;
            case DialogueAct.Goodbye:
                lines.Add("Людина прощається чи йде спати. Тепло й коротко попрощайся, без нових питань і порад.");
                break;
            case DialogueAct.ShortReply:
                lines.Add(s.ShortRepliesInRow >= 2
                    ? "Людина вже кілька разів відповідає «не знаю»/коротко. Не допитуй, не питай про тіло чи причини, не пропонуй вправ. " +
                      "Коротко прийми це й дай легкий вибір: безладно виговоритись, перемкнутись на щось інше або просто побути тут. Один-два короткі речення."
                    : "Відповідь коротка. Прочитай її разом з попередніми репліками. Не вимагай пояснень: відреагуй по-людськи й залиш місце, щоб людина підхопила, коли захоче.");
                break;
            case DialogueAct.Refusal:
                lines.Add("Людина відмовляється від пропозиції чи не має сил. Прийми це одним коротким реченням і не повертайся до відхиленого. Нових варіантів не підсовуй.");
                break;
            case DialogueAct.Advice:
                lines.Add(s.DeclinedRecently
                    ? "Людина щойно відмовлялась від порад, а тепер питає про допомогу. Коротко перепитай, чи справді хоче, або дай одну малу конкретну річ своїми словами."
                    : "Людина просить допомоги. Дай одну конкретну річ своїми словами, не набір пунктів, і запитай, чи це зайшло.");
                break;
            case DialogueAct.Question:
                lines.Add("Це питання. Відповідай по суті, прямо, а потім, якщо природно, додай свою думку.");
                break;
        }

        if (s.DeclinedRecently && s.Act != DialogueAct.Advice)
            lines.Add("Нещодавно людина відмовилась від поради чи вправи. Не пропонуй їх знову, поки сама не попросить.");
        if (s.WantsDistraction)
            lines.Add("Людина просить відволікти. Розкажи завершену, короткою розповіддю, цікаву річ чи жарт, а не питай «про що поговорити».");
        if (s.UserTurns <= 2 && s.Act is not DialogueAct.Greeting)
            lines.Add("Розмова щойно почалась: не ставай психологом одразу, будь звичайним співрозмовником.");

        return lines.Count == 0 ? "" : Wrap(lines);

        static string Wrap(List<string> items) =>
            "Підказка до цього ходу (довідка для тебе, людині не показуй і не згадуй):\n- " + string.Join("\n- ", items);
    }

    private static bool Matches(string text, string pattern) => Regex.IsMatch(text, pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Limit);
}
