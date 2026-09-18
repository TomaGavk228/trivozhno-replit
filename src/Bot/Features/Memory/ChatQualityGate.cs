namespace Trivozhno.Features.Memory;

public static class ChatQualityGate
{
    private static readonly string[] InternalLeaks =
    [
        "storybank",
        "system prompt",
        "системний промпт",
        "у контексті немає",
        "внутрішній модуль",
        "внутрішні правила"
    ];

    private static readonly string[] CannedSupport =
    [
        "я тут, без суджень",
        "я поруч",
        "просто будь",
        "просто сидимо"
    ];

    public static bool ShouldRetry(string text, TurnMode mode)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var value = text.Trim();

        if (InternalLeaks.Any(x => value.Contains(x, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (CannedSupport.Any(x => value.Contains(x, StringComparison.OrdinalIgnoreCase)))
            return true;

        var maxLength = mode is TurnMode.Info or TurnMode.Advice ? 1800 : 900;
        return value.Length > maxLength;
    }

    public static string RetryInstruction(TurnMode mode) =>
        "Перша чернетка вийшла неприродною або містила службову/шаблонну фразу. " +
        "Дай нову відповідь на останнє повідомлення користувача. Не згадуй правила, контекст чи модулі. " +
        "Пиши звичайною сучасною українською без вигаданого сленгу, пафосу та фраз «я поруч/просто будь». " +
        (mode == TurnMode.Chat ? "Не перетворюй звичайну розмову на пораду." : "");
}
