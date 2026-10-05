using System.Text.RegularExpressions;
using Trivozhno.Features.Memory;
using Trivozhno.Features.Recommendations;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Groq;
using Trivozhno.Infrastructure.Knowledge;
using Trivozhno.Infrastructure.SillyTavern;
using Trivozhno.Resources;

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
    TavernPromptBuilder? tavern = null)
{
    public async Task<ChatReply> Reply(ConversationContext context, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.JobBudget));
        ct = deadline.Token;
        var messages = context.Messages.ToList();
        var sources = new List<SourceMetadata>();
        var currentText = messages.Last(m => m.Role == "user").Content;
        var builder = tavern ?? new(new Uk(), options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TavernPromptBuilder>.Instance);
        var history = messages.Where(m => m.Role is "user" or "assistant").ToArray();
        var references = new List<string>();
        if (history.Length == 1 && ClassifyDialogueAct(currentText) == DialogueAct.Greeting)
        {
            // ST's first_mes is an actual character message, not an instruction
            // asking a model to imitate a greeting. Persist this delivered turn.
            log.LogInformation("SillyTavern first message; model calls 0");
            return new(new(builder.FirstMessage, "sillytavern:first_mes", 0), [])
            {
                ConversationState = ConversationThread.FromExchange(currentText, builder.FirstMessage,
                    context.ConversationState, explicitPreferences: context.ExplicitStyleDelta)
            };
        }
        var bookRequest = context.BookRequest;
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
        var bookContext = new BookContext("", [], bookRequest?.SourceQuestion == true);
        if (bookRequest is not null)
        {
            if (!Add(BookContext.ReplyInstruction)) throw new ContextTooLargeException();
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
                log.LogInformation("Book context; continued {Continued}; alternative {Alternative}; candidates {Candidates}; included {Included}; chunks {Chunks}; practical {Practical}",
                    bookRequest.ContinueSources, bookRequest.Alternative, passages.Count, bookContext.Sources.Count,
                    string.Join(',', bookContext.Sources.Select(s => s.ChunkId)), PassageRelevance.WantsPracticalHelp(bookRequest.Query));
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
        var memory = messages.Where(m => m.Role == "system").Skip(1).ToArray();
        var prepared = builder.Build(history, memory, references);
        // Ordinary chat returns text. Books add kind/source metadata in the SAME
        // generation. Continuity is recorded locally from the delivered reply.
        var generated = bookRequest is null ? await ai.Complete(prepared, summary: false, ct) :
            await ai.CompleteBook(prepared, ct);
        if (string.IsNullOrWhiteSpace(generated.Text)) throw new AiUnavailableException("empty_reply");
        if (bookRequest is null && options.ChatRepair)
        {
            // One structural retry, only when the draft is a lecture, a menu, a
            // closing formula, a list or far too long for a chat turn. Wording is
            // never rewritten by code; a failed retry keeps the first draft.
            var act = ClassifyDialogueAct(currentText);
            var recentUser = history.Where(m => m.Role == "user").Select(m => m.Content).ToArray();
            var quality = ReplyQualityGate.Check(act, currentText, generated.Text, recentUser);
            if (!quality.Accept)
            {
                log.LogWarning("Chat reply failed structural check; reason {Reason}", quality.Feedback);
                if (Add(ReplyQualityGate.RetryInstruction(quality, generated.Text, act, currentText, recentUser)))
                {
                    try
                    {
                        var revised = await ai.Complete(builder.Build(history, memory, references), summary: false, ct);
                        var accepted = !string.IsNullOrWhiteSpace(revised.Text) &&
                            ReplyQualityGate.Check(act, currentText, revised.Text, recentUser).Accept;
                        log.LogInformation("Chat reply retry; accepted {Accepted}", accepted);
                        generated = (accepted ? revised : generated) with
                        {
                            Tokens = generated.Tokens + revised.Tokens,
                            PromptTokens = generated.PromptTokens + revised.PromptTokens,
                            CompletionTokens = generated.CompletionTokens + revised.CompletionTokens,
                            ReasoningTokens = generated.ReasoningTokens + revised.ReasoningTokens,
                            CachedTokens = generated.CachedTokens + revised.CachedTokens
                        };
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        log.LogWarning("Chat reply retry unavailable: {Category}", e.GetType().Name);
                    }
                }
            }
        }
        var grounded = (Text: generated.Text, Used: (IReadOnlyList<SourceMetadata>)Array.Empty<SourceMetadata>());
        var awaitsClarification = false;
        var continuedBookQuery = bookRequest?.Query;
        if (bookRequest is not null)
        {
            var userContext = string.Join('\n', history.Where(m => m.Role == "user").Select(m => m.Content));
            if (!BookReply.TryRead(generated.Text, bookContext, out var bookReply, out var reason, userContext))
            {
                log.LogWarning("Book reply needs revision; reason {Reason}; sources {Sources}", reason, bookContext.Sources.Count);
                if (!Add("Попередня генерація не пройшла перевірку: " + reason + ". " +
                    "Сформуй book_reply заново. Використай наявні ID; поясни своїми словами. " +
                    "Числові параметри дозволені тільки з використаного матеріалу. " +
                    "Коли відповідь не підкріплена уривками, доречні clarify або insufficient."))
                    throw new ContextTooLargeException();
                var revised = await ai.CompleteBook(builder.Build(history, memory, references), ct);
                generated = revised with { Tokens = generated.Tokens + revised.Tokens,
                    PromptTokens = generated.PromptTokens + revised.PromptTokens,
                    CompletionTokens = generated.CompletionTokens + revised.CompletionTokens,
                    ReasoningTokens = generated.ReasoningTokens + revised.ReasoningTokens,
                    CachedTokens = generated.CachedTokens + revised.CachedTokens };
                if (!BookReply.TryRead(generated.Text, bookContext, out bookReply, out reason, userContext))
                {
                    log.LogWarning("Book reply revision rejected; reason {Reason}", reason);
                    throw new AiUnavailableException("invalid_book_reply");
                }
            }
            grounded = (bookReply!.Text, bookReply.Sources);
            awaitsClarification = bookReply.Kind == "clarify" || bookReply.Kind == "insufficient" && bookReply.Text.Contains('?');
            if (bookReply.Kind == "conversation") continuedBookQuery = null;
            log.LogInformation("Book reply; kind {Kind}; available {Available}; used {Used}; copied fallback False",
                bookReply.Kind, bookContext.Sources.Count, bookReply.Sources.Count);
        }
        sources.AddRange(grounded.Used.Where(x => !sources.Any(s => s.Id == x.Id)));
        var text = grounded.Text;
        if (facts is not null)
        {
            sources.AddRange(facts.Items.Where(x => text.Contains("{{fact:" + x.Id + "}}", StringComparison.Ordinal))
                .Select(x => x.Source));
            text = facts.Render(text);
        }
        if (selection is not null)
        {
            sources.AddRange(selection.Movies.Where(m => text.Contains("{{movie:" + m.Id + "}}", StringComparison.Ordinal))
                .Select(m => new SourceMetadata("movie", m.Title, ReferenceId: m.Id, Url: m.Source)));
            text = selection.Render(text);
        }
        if (string.IsNullOrWhiteSpace(text)) throw new AiUnavailableException("empty_reply");
        var final = generated with { Text = text };
        try { diagnostics.Record(context, final, bookContext.Sources.Count, sources.Count(x => x.Type == "book")); }
        catch (Exception e) when (e is not OperationCanceledException)
        { log.LogWarning("Reply observations unavailable: {Category}", e.GetType().Name); }
        var state = ConversationThread.FromExchange(currentText, text, context.ConversationState,
            continuedBookQuery, context.ExplicitStyleDelta, awaitsClarification);
        log.LogInformation("Friend exchange; version {Version}; format {Format}; thread prepared {Thread}; facts available {Facts}; references used {Sources}",
            TavernConfiguration.EngineVersion, bookRequest is null ? "text" : "book_reply", state.Length > 0, facts?.Items.Length ?? 0, sources.Count);
        return new(final, sources) { ConversationState = state };

        int Remaining()
        {
            return builder.InputLimit - TokenEstimate.Count(messages) - builder.ExampleReserve - builder.InstructionReserve -
                (bookRequest is null ? 0 : GroqClient.BookSchemaReserve) -
                references.Sum(x => TokenEstimate.Count([new AiMessage("system", x)]));
        }
        bool Add(string text)
        {
            var message = new AiMessage("system", text);
            if (TokenEstimate.Count([message]) > Remaining()) return false;
            references.Add(text);
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
