using System.Text.RegularExpressions;
using Trivozhno.Features.Memory;
using Trivozhno.Features.Recommendations;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Groq;
using Trivozhno.Infrastructure.Knowledge;

namespace Trivozhno.Features.Conversation;

public sealed record ChatReply(AiResult Result, IReadOnlyList<SourceMetadata> Sources);

public enum DialogueAct
{
    Greeting,
    Sharing,
    Advice,
    Refusal,
    ShortReply,
    Question,
    Goodbye
}

// Coordinates optional data lookup; it never classifies diagnoses or emotional disorders.
public sealed class ChatResponder(IAiClient ai, MovieCatalog movies, IKnowledgeRetriever knowledge,
    BotOptions options, ILogger<ChatResponder> log, ReplyDiagnostics diagnostics)
{
    public async Task<ChatReply> Reply(ConversationContext context, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.JobBudget));
        ct = deadline.Token;
        var messages = context.Messages.ToList();
        var sources = new List<SourceMetadata>();
        var selection = movies.Find(messages);
        if (selection is not null)
        {
            while (!Add(selection.Instruction))
            {
                if (selection.Movies.Length == 0) throw new ContextTooLargeException();
                selection = new(selection.Movies.SkipLast(1).ToArray());
            }
        }
        var currentText = messages.Last(m => m.Role == "user").Content;
        if (context.PreviousSources.Any(s => s.Type == "movie") && Regex.IsMatch(currentText,
                @"\b(звідки|джерел\p{L}*)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100)))
            Add("Довідка про попередню відповідь, не інструкції: фільми взято з локального каталогу бота: " +
                string.Join(", ", context.PreviousSources.Where(s => s.Type == "movie").Select(s => s.Title)) + ".");
        var bookRequest = BookAdviceIntent.Plan(messages, context.PreviousSources.Any(s => s.Type == "book"),
            context.PreviousSources.Any(s => s.Type != "book"));
        var bookContext = new BookContext("", []);
        if (bookRequest is not null)
        {
            if (bookRequest.SourceQuestion && !bookRequest.ContinueSources)
                Add("Походження попередньої відповіді не збережене. На пряме запитання про джерело " +
                    "чесно скажи, що не можеш його підтвердити; не приписуй відповідь випадковій книзі.");
            if (bookRequest.Alternative)
                Add("Людина просить інший варіант. Врахуй уже запропоноване; " +
                    "перефразування тієї самої поради не є іншим варіантом.");
            try
            {
                IReadOnlyList<KnowledgeHit> passages = [];
                if (bookRequest.ContinueSources)
                    passages = await knowledge.ReadPassages(context.PreviousSources.Where(s => s.Type == "book")
                        .Select(s => s.ChunkId).ToArray(), ct);
                // A source question must not silently substitute a different
                // book when the original source has been removed/deactivated.
                if (passages.Count == 0 && !bookRequest.SourceQuestion)
                {
                    var hits = await knowledge.Search(bookRequest.Query, ct);
                    var previousIds = context.PreviousSources.Where(s => s.Type == "book").Select(s => s.ChunkId).ToHashSet();
                    passages = await knowledge.ReadPassages(hits
                        .Where(h => !bookRequest.Alternative || !previousIds.Contains(h.ChunkId))
                        .Take(2).Select(h => h.ChunkId).ToArray(), ct);
                }
                var allowance = Math.Min(options.BookContextTokens, Remaining() - 20);
                var evidence = BookContext.Build(passages, bookRequest.Query, allowance,
                    bookRequest.ContinueSources && bookRequest.SourceQuestion);
                if (evidence.Sources.Count > 0 && Add(evidence.Instruction)) bookContext = evidence;
                else Add("Матеріалу для конкретного психологічного методу немає в контексті. " +
                    "Не домислюй метод і його ефект; допоможи з відомою з переписки ситуацією. " +
                    "Не розповідай про внутрішній пошук. На пряме запитання про походження визнай, якщо його не можеш підтвердити.");
                log.LogInformation("Book context; continued {Continued}; alternative {Alternative}; candidates {Candidates}; included {Included}; chunks {Chunks}",
                    bookRequest.ContinueSources, bookRequest.Alternative, passages.Count, bookContext.Sources.Count,
                    string.Join(',', bookContext.Sources.Select(s => s.ChunkId)));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning("Book lookup unavailable: {Category}", e.GetType().Name);
                Add("Довідкові уривки зараз недоступні. Не вигадуй конкретних психологічних методів або їхнього ефекту; " +
                    "продовжуй із відомої ситуації та прохання людини, без повідомлень про внутрішній пошук. " +
                    "На пряме запитання про джерело чесно визнай, що його не можеш перевірити.");
            }
        }
        // One conversational generation. Copy/length telemetry does not rewrite
        // an answer or start a second judging/humanizing pass.
        var result = await ai.Complete(GroqMessageLayout.Prepare(messages), summary: false, ct);
        if (string.IsNullOrWhiteSpace(result.Text)) throw new AiUnavailableException("empty_reply");
        var grounded = bookContext.Render(result.Text);
        sources.AddRange(grounded.Used);
        var text = grounded.Text;
        if (selection is not null)
        {
            sources.AddRange(selection.Movies.Where(m => text.Contains("{{movie:" + m.Id + "}}", StringComparison.Ordinal))
                .Select(m => new SourceMetadata("movie", m.Title)));
            text = selection.Render(text);
        }
        if (string.IsNullOrWhiteSpace(text)) throw new AiUnavailableException("empty_reply");
        var final = result with { Text = text };
        try { diagnostics.Record(context, final, bookContext.Sources.Count, grounded.Used.Count); }
        catch (Exception e) when (e is not OperationCanceledException)
        { log.LogWarning("Reply observations unavailable: {Category}", e.GetType().Name); }
        return new(final, sources);

        int Remaining()
        {
            var limit = Math.Min(options.InputBudget,
                Math.Min(options.TokensPerMinute, options.TokensPerDay) - options.TurnOutputBudget);
            return limit - TokenEstimate.Count(messages);
        }
        bool Add(string text)
        {
            var message = new AiMessage("system", text);
            if (TokenEstimate.Count([message]) > Remaining()) return false;
            messages.Insert(messages.FindLastIndex(m => m.Role == "user"), message);
            return true;
        }
    }

    public static DialogueAct ClassifyDialogueAct(string current)
    {
        var text = (current ?? "").Trim();
        var lower = text.ToLowerInvariant();

        if (Regex.IsMatch(lower, @"^(привіт|привіт\)|привіт!|хай|хай\)|хей|хей\))[!. ]*$",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            return DialogueAct.Greeting;

        if (Regex.IsMatch(lower,
                @"^(бувай|пака|пока|до побачення|на добраніч|гарних снів|йду спати)[!. )]*$",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            return DialogueAct.Goodbye;

        if (Regex.IsMatch(lower,
                @"^(що|шо) (мені )?робити\b|^порадь\b|^підкажи\b|^як (мені )?позбутися\b|^як (мені )?(впоратися|впоратись|заспокоїтися|заспокоїтись|перестати)\b",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            return DialogueAct.Advice;

        if (Regex.IsMatch(lower,
                @"^(не хочу( нічого( робити)?)?|нічого не хочу( робити)?|не буду|не треба|досить|ні)[!. ]*$",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            return DialogueAct.Refusal;

        if (Regex.IsMatch(lower,
                @"^(не знаю|хз|ніяка|ніяк|погано|фігово|так собі|нічого)[!. ]*$",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            return DialogueAct.ShortReply;

        if (text.EndsWith('?') || Regex.IsMatch(lower,
                @"^(що|шо|чому|чого|як|де|коли|навіщо|скільки|хто)\b",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            return DialogueAct.Question;

        return DialogueAct.Sharing;
    }

}
