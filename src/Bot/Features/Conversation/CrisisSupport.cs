using System.Text.RegularExpressions;

namespace Trivozhno.Features.Conversation;

// A narrow failure fallback, not a diagnosis or a general sentiment detector.
// Ordinary sadness and tiredness must not trigger emergency messaging.
public static class CrisisSupport
{
    public static string? IfGenerationFailed(string text)
    {
        // Relocation wording alone is not a wish to die.
        if (Regex.IsMatch(text, @"\bне\s+хочу\s+(більше\s+)?жити\s+(тут|там|у\b|в\b|з\b|із\b|поруч|вдома|дома)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) &&
            !Regex.IsMatch(text, @"\b(померти|вмерти|самогубство|нашкодити\s+собі|вбити\s+себе)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) return null;
        if (Regex.IsMatch(text, @"\b(не\s+хочу\s+(більше\s+)?жити|хочу\s+(померти|вмерти|вбити\s+себе|нашкодити\s+собі)|думаю\s+про\s+самогубство|збираюс[яь]\s+(померти|вбити\s+себе))\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            return "Не хочу пропустити те, що ти написав про небезпеку\nТи зараз у безпеці? Є намір щось зробити просто зараз?\nЯкщо можеш, відійди від небезпечних речей і напиши чи подзвони людині, яка може побути поруч\nЯкщо ризик негайний, в Україні телефонуй 112, в іншій країні — до місцевої екстреної служби";
        if (Regex.IsMatch(text, @"\b(не\s+витримаю|не\s+хочу\s+більше\s+так|можна\s+просто\s+не\s+бути)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            return "Не хочу пройти повз це\nТи про що саме зараз? Ти в безпеці?";
        return null;
    }
}
