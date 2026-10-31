using Trivozhno.Features.Navigation;
using Trivozhno.Features.Plus;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Persistence;
using Trivozhno.Resources;

namespace Trivozhno.Features.Friend;

// «Друг+»: name, gender and tone of the friend. The main prompt is untouched;
// the choice reaches the model as one short system note (see Note).
public sealed class FriendHandler(Ui ui, Uk uk, IClock clock, PlusHandler plus)
{
    public const int MaxName = 24;

    public void Show(BotUser u)
    {
        if (!u.IsPlus(clock.UtcNow)) { ui.Say(u, "plus.only"); plus.Show(u); return; }
        u.Go(UserState.FriendSetup);
        var name = u.FriendName.Length > 0 ? u.FriendName : uk["friend.name.default"];
        var gender = uk[u.FriendGender switch { "female" => "friend.gender.female", "male" => "friend.gender.male", _ => "friend.gender.any" }];
        var tone = uk[u.FriendTone switch { "calm" => "friend.tone.calm", "playful" => "friend.tone.playful", _ => "friend.tone.default" }];
        ui.Text(u, uk.Format("friend.text", name, gender, tone), ui.Keyboard(u,
            [ui.B("friend.name.button", "friend:name")],
            [Mark(u.FriendGender == "male", "friend.gender.male", "friend:g:male"), Mark(u.FriendGender == "female", "friend.gender.female", "friend:g:female"),
                Mark(u.FriendGender == "", "friend.gender.any", "friend:g:any")],
            [Mark(u.FriendTone == "calm", "friend.tone.calm", "friend:t:calm"), Mark(u.FriendTone == "playful", "friend.tone.playful", "friend:t:playful"),
                Mark(u.FriendTone == "", "friend.tone.default", "friend:t:default")],
            [ui.B("back", "plus:show")]));
    }
    private Ui.Button Mark(bool on, string key, string code) => new((on ? "• " : "") + uk[key], code);

    public void AskName(BotUser u)
    {
        if (!u.IsPlus(clock.UtcNow)) { Show(u); return; }
        u.Go(UserState.FriendName);
        ui.Text(u, uk.Format("friend.name.ask", MaxName), ui.Keyboard(u, [ui.B("back", "settings:friend")]));
    }
    public void SaveName(BotUser u, string text)
    {
        var name = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length is 0 or > MaxName || name.Any(char.IsControl)) { ui.Text(u, uk.Format("friend.name.invalid", MaxName)); return; }
        u.FriendName = name;
        Show(u);
    }
    public void Set(BotUser u, string code)
    {
        if (!u.IsPlus(clock.UtcNow)) { Show(u); return; }
        switch (code)
        {
            case "friend:g:male": u.FriendGender = "male"; break;
            case "friend:g:female": u.FriendGender = "female"; break;
            case "friend:g:any": u.FriendGender = ""; break;
            case "friend:t:calm": u.FriendTone = "calm"; break;
            case "friend:t:playful": u.FriendTone = "playful"; break;
            case "friend:t:default": u.FriendTone = ""; break;
            case "friend:reset": u.FriendName = ""; u.FriendGender = ""; u.FriendTone = ""; break;
        }
        Show(u);
    }

    // Applied only while «Друг+» is active. Data, not a new behavioural prompt.
    public static string Note(BotUser u, DateTimeOffset now)
    {
        if (!u.IsPlus(now) || u.FriendName.Length == 0 && u.FriendGender.Length == 0 && u.FriendTone.Length == 0) return "";
        var parts = new List<string>();
        if (u.FriendName.Length > 0) parts.Add($"людина назвала тебе «{u.FriendName}», відгукуйся на це ім'я");
        if (u.FriendGender == "female") parts.Add("про себе говори в жіночому роді");
        if (u.FriendGender == "male") parts.Add("про себе говори в чоловічому роді");
        if (u.FriendTone == "calm") parts.Add("тон трохи спокійніший і м'якший, менше жартів");
        if (u.FriendTone == "playful") parts.Add("тон трохи жартівливіший, але без жартів про біль людини");
        return "Налаштування друга від людини: " + string.Join("; ", parts) +
            ". Це лише стиль; це налаштування має перевагу над родом, вказаним вище. Решта правил незмінна.";
    }
}
