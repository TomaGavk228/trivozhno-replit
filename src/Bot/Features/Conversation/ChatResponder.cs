using System.Text.RegularExpressions;
using Trivozhno.Features.Memory;
using Trivozhno.Features.Recommendations;
using Trivozhno.Host;
using Trivozhno.Infrastructure.Groq;
using Trivozhno.Infrastructure.Knowledge;

namespace Trivozhno.Features.Conversation;

public sealed record ChatReply(AiResult Result, IReadOnlyList<SourceMetadata> Sources);

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
        var signals = FriendTurn.Analyze(messages);
        var hint = FriendTurn.Hint(signals);
        if (hint.Length > 0) Add(hint);
        var bookRequest = signals.Crisis ? null : BookAdviceIntent.Plan(messages, context.PreviousSources.Any(s => s.Type == "book"),
            context.PreviousSources.Any(s => s.Type != "book"));
        var bookContext = new BookContext("", []);
        if (bookRequest is not null)
        {
            if (bookRequest.SourceQuestion && !bookRequest.ContinueSources)
                Add("Для попередньої відповіді книжкове посилання не збережене. " +
                    "Якщо уривок знайдеться зараз, це нова перевірка, а не підтвердження того, звідки була попередня відповідь.");
            try
            {
                IReadOnlyList<KnowledgeHit> passages = [];
                if (bookRequest.ContinueSources)
                    passages = await knowledge.ReadPassages(context.PreviousSources.Where(s => s.Type == "book")
                        .Select(s => s.ChunkId).ToArray(), ct);
                // A source question must not silently substitute a different
                // book when the original source has been removed/deactivated.
                if (passages.Count == 0 && !(bookRequest.ContinueSources && bookRequest.SourceQuestion))
                {
                    var hits = await knowledge.Search(bookRequest.Query, ct);
                    passages = await knowledge.ReadPassages(hits.Take(2).Select(h => h.ChunkId).ToArray(), ct);
                }
                var allowance = Math.Min(options.BookContextTokens, Remaining() - 20);
                var evidence = BookContext.Build(passages, bookRequest.Query, allowance,
                    bookRequest.ContinueSources && bookRequest.SourceQuestion);
                if (evidence.Sources.Count > 0 && Add(evidence.Instruction)) bookContext = evidence;
                else Add("Доречного книжкового уривка для цього прохання зараз немає в контексті. " +
                    "Скажи про це, якщо потрібен метод або джерело; допоможи розібрати відому з розмови ситуацію.");
                log.LogInformation("Book context; continued {Continued}; candidates {Candidates}; included {Included}; chunks {Chunks}",
                    bookRequest.ContinueSources, passages.Count, bookContext.Sources.Count,
                    string.Join(',', bookContext.Sources.Select(s => s.ChunkId)));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning("Book lookup unavailable: {Category}", e.GetType().Name);
                Add("Пошук у книжках зараз недоступний. Чесно познач цю межу, якщо потрібна конкретна техніка; " +
                    "можна допомогти розібрати ситуацію з переписки.");
            }
        }
        // One generation; a second one only when the draft is structurally not chat
        // (list, essay, canned opener, interrogation, repeat of the previous reply).
        var detail = ChatReplyBudget.WantsDetail(messages);
        var recentUser = messages.Where(m => m.Role == "user").Select(m => m.Content).TakeLast(4).ToArray();
        var recentAssistant = messages.Where(m => m.Role == "assistant").Select(m => m.Content).TakeLast(3).ToArray();
        var hasReferences = bookContext.Sources.Count > 0 || selection is not null;
        var prepared = GroqMessageLayout.WithExamples(messages, context.Examples);
        var result = await ai.Complete(prepared, summary: false, ct);
        if (string.IsNullOrWhiteSpace(result.Text)) throw new AiUnavailableException("empty_reply");
        result = result with { Text = ReplyQualityGate.Polish(result.Text) };
        var quality = ReplyQualityGate.Check(signals.Act, currentText, result.Text, recentUser,
            recentAssistant, detail || hasReferences, signals.Crisis);
        if (!quality.Accept)
        {
            log.LogInformation("Reply rejected by structure check: {Feedback}", quality.Feedback);
            try
            {
                var retryMessages = prepared.ToList();
                retryMessages.Insert(retryMessages.FindLastIndex(m => m.Role == "user"), new AiMessage("system",
                    ReplyQualityGate.RetryInstruction(quality, result.Text, signals.Act, currentText, recentUser)));
                var retry = await ai.Complete(retryMessages, summary: false, ct);
                var polished = ReplyQualityGate.Polish(retry.Text ?? "");
                if (polished.Length > 0 && ReplyQualityGate.Check(signals.Act, currentText, polished, recentUser,
                        recentAssistant, detail || hasReferences, signals.Crisis).Accept)
                    result = retry with { Text = polished, Tokens = result.Tokens + retry.Tokens,
                        PromptTokens = result.PromptTokens + retry.PromptTokens,
                        CompletionTokens = result.CompletionTokens + retry.CompletionTokens };
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning("Reply retry unavailable: {Category}", e.GetType().Name);
            }
        }
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
            return limit - TokenEstimate.Count(messages) - TokenEstimate.Count(context.Examples);
        }
        bool Add(string text)
        {
            var message = new AiMessage("system", text);
            if (TokenEstimate.Count([message]) > Remaining()) return false;
            messages.Insert(messages.FindLastIndex(m => m.Role == "user"), message);
            return true;
        }
    }

    public static DialogueAct ClassifyDialogueAct(string current) => DialogueClassifier.Classify(current);
}
