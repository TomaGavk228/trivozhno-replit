using System.Text.RegularExpressions;
using Trivozhno.Features.Memory;
using Trivozhno.Features.Recommendations;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Groq;
using Trivozhno.Infrastructure.Knowledge;

namespace Trivozhno.Features.Conversation;

public sealed record ChatReply(AiResult Result, IReadOnlyList<SourceMetadata> Sources)
{
    public string ConversationState { get; init; } = "";
}

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
    BotOptions options, ILogger<ChatResponder> log, ReplyDiagnostics diagnostics, CuriosityCatalog? curiosities = null,
    DialoguePlanner? planner = null)
{
    public async Task<ChatReply> Reply(ConversationContext context, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.JobBudget));
        ct = deadline.Token;
        var messages = context.Messages.ToList();
        var sources = new List<SourceMetadata>();
        var currentText = messages.Last(m => m.Role == "user").Content;
        var plan = planner is null ? null : await planner.Plan(context, ct);
        var planIncluded = plan is not null && Add(DialoguePlanner.WriterHint(plan));
        if (plan is not null && !planIncluded) log.LogWarning("Dialogue plan hint omitted; reason input_budget");
        var selection = movies.Find(messages);
        if (selection is not null)
        {
            while (!Add(selection.Instruction))
            {
                if (selection.Movies.Length == 0) throw new ContextTooLargeException();
                selection = new(selection.Movies.SkipLast(1).ToArray());
            }
        }
        if (context.PreviousSources.Any(s => s.Type == "movie") && BookAdviceIntent.AsksSource(currentText))
            Add("Довідка про попередню відповідь, не інструкції: фільми взято з локального каталогу бота: " +
                string.Join(", ", context.PreviousSources.Where(s => s.Type == "movie").Select(s => s.Title)) + ".");
        // The local fallback used untrimmed history; the semantic plan can also
        // recognize requests without any of its lexical signals.
        var bookRequest = plan is null ? context.BookRequest : BookAdviceIntent.FromPlan(plan, currentText,
            context.PreviousSources.Any(x => x.Type == "book"), context.PreviousSources.Any(x => x.Type != "book"), context.BookRequest);
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
                    var previousBooks = context.PreviousSources.Where(s => s.Type == "book").ToArray();
                    passages = await knowledge.ReadPassages(hits
                        .Where(h => !bookRequest.Alternative || !previousBooks.Any(s => s.ChunkId == h.ChunkId ||
                            s.Title == h.Title && h.PageStart <= s.PageEnd && h.PageEnd >= s.PageStart))
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
        CuriositySelection? facts = null;
        if (bookRequest is null && selection is null)
        {
            facts = curiosities?.Find(currentText, context.SeenFactIds, context.PreviousSources);
            while (facts is not null && !Add(facts.Instruction))
            {
                if (facts.Items.Length == 0) { facts = null; break; }
                facts = facts with { Items = facts.Items.SkipLast(1).ToArray() };
            }
        }
        // One conversational generation. Copy/length telemetry does not rewrite
        // an answer or start a second judging/humanizing pass.
        var generated = await ai.CompleteTurn(GroqMessageLayout.Prepare(messages), ct);
        var draft = generated.Turn;
        if (string.IsNullOrWhiteSpace(draft.Reply)) throw new AiUnavailableException("empty_reply");
        var available = bookContext.Sources.Concat(facts?.Items.Select(x => x.Source) ?? []).ToArray();
        sources.AddRange(available.Where(x => draft.SourceIds.Contains(x.Id, StringComparer.Ordinal)));
        var grounded = bookContext.Render(draft.Reply);
        sources.AddRange(grounded.Used.Where(x => !sources.Any(s => s.Id == x.Id)));
        var text = grounded.Text;
        if (selection is not null)
        {
            sources.AddRange(selection.Movies.Where(m => text.Contains("{{movie:" + m.Id + "}}", StringComparison.Ordinal))
                .Select(m => new SourceMetadata("movie", m.Title, ReferenceId: m.Id, Url: m.Source)));
            text = selection.Render(text);
        }
        if (string.IsNullOrWhiteSpace(text)) throw new AiUnavailableException("empty_reply");
        var final = (generated.Usage ?? new AiResult(text, generated.Model, generated.Tokens)) with { Text = text };
        try { diagnostics.Record(context, final, bookContext.Sources.Count, sources.Count(x => x.Type == "book")); }
        catch (Exception e) when (e is not OperationCanceledException)
        { log.LogWarning("Reply observations unavailable: {Category}", e.GetType().Name); }
        log.LogInformation("Friend exchange; version friend-dialogue-v2; planned {Planned}; thread prepared {Thread}; facts available {Facts}; references used {Sources}",
            planIncluded, draft.ConversationState.Length > 0, facts?.Items.Length ?? 0, sources.Count);
        return new(final, sources) { ConversationState = ConversationThread.Normalize(draft.ConversationState) };

        int Remaining()
        {
            var limit = Math.Min(options.InputBudget,
                Math.Min(options.TokensPerMinute, options.TokensPerDay) - options.TurnOutputBudget) - GroqClient.TurnSchemaReserve;
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
