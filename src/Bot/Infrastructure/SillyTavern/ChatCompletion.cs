// SPDX-License-Identifier: AGPL-3.0-only
// Adapted from SillyTavern 1.19.0, public/scripts/openai.js:
// MessageCollection and ChatCompletion (commit 06bde939fb1e9c4c8d8641d810f0a916b5bce127).
// Copyright SillyTavern contributors. C# text-only adaptation, 2026-10-04.
using Trivozhno.Infrastructure.Groq;

namespace Trivozhno.Infrastructure.SillyTavern;

internal sealed record TavernMessage(string Identifier, AiMessage Message)
{
    public int Tokens => string.IsNullOrEmpty(Message.Content) ? 0 : TokenEstimate.Count([Message]);
}

internal sealed class MessageCollection(string identifier)
{
    public string Identifier { get; } = identifier;
    public List<TavernMessage> Collection { get; } = [];
    public int Tokens => Collection.Sum(x => x.Tokens);
    public IReadOnlyList<AiMessage> GetChat() => Collection
        .Where(x => !string.IsNullOrEmpty(x.Message.Content)).Select(x => x.Message).ToArray();
}

// The upstream collection/order/budget operations, with our conservative
// token estimate in place of its browser tokenHandler. No browser or HTTP here.
internal sealed class ChatCompletion
{
    private readonly SortedDictionary<int, MessageCollection> messages = [];
    private int tokenBudget;

    public void SetTokenBudget(int context, int response) => tokenBudget = context - response;
    public void ReserveBudget(int tokens) => tokenBudget -= tokens;
    public void FreeBudget(TavernMessage message) => tokenBudget += message.Tokens;
    public bool CanAfford(TavernMessage message) => tokenBudget >= message.Tokens;
    public bool CanAffordAll(IEnumerable<TavernMessage> collection) => tokenBudget >= collection.Sum(x => x.Tokens);

    public void Add(MessageCollection collection, int position)
    {
        if (tokenBudget < collection.Tokens) throw new ContextTooLargeException();
        if (messages.ContainsKey(position)) throw new InvalidOperationException("Duplicate Tavern prompt position.");
        messages.Add(position, collection);
        tokenBudget -= collection.Tokens;
    }

    public void Insert(TavernMessage message, string identifier, bool atStart = false)
    {
        if (!CanAfford(message)) throw new ContextTooLargeException();
        var collection = messages.Values.Single(x => x.Identifier == identifier);
        if (string.IsNullOrEmpty(message.Message.Content)) return;
        if (atStart) collection.Collection.Insert(0, message);
        else collection.Collection.Add(message);
        tokenBudget -= message.Tokens;
    }

    public IReadOnlyList<AiMessage> GetChat() => messages.Values.SelectMany(x => x.GetChat()).ToArray();
}
