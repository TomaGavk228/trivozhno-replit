namespace Trivozhno.Host;

// Registration and cancellation happen under UserLocks. A removed registration
// may finish later, but cannot remove or cancel the newer user's generation.
public sealed class ActiveGenerations(BotOptions options)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Registration> active = new();
    public Registration Begin(Guid userId, CancellationToken stoppingToken)
    {
        Cancel(userId);
        var registration = new Registration(this, userId, stoppingToken, options.JobBudget);
        active[userId] = registration;
        return registration;
    }
    public void Cancel(Guid userId)
    {
        if (active.TryRemove(userId, out var registration)) registration.Cancel();
    }
    public sealed class Registration : IDisposable
    {
        private readonly ActiveGenerations owner;
        private readonly Guid userId;
        private readonly CancellationTokenSource source;
        private readonly object sync = new();
        private bool disposed;
        internal Registration(ActiveGenerations owner, Guid userId, CancellationToken token, int budget)
        {
            this.owner = owner; this.userId = userId; source = CancellationTokenSource.CreateLinkedTokenSource(token);
            source.CancelAfter(TimeSpan.FromSeconds(budget));
        }
        public bool Superseded { get; private set; }
        public CancellationToken Token => source.Token;
        internal void Cancel() { lock (sync) { if (!disposed) { Superseded = true; source.Cancel(); } } }
        public void Dispose()
        {
            ((ICollection<KeyValuePair<Guid, Registration>>)owner.active).Remove(new(userId, this));
            lock (sync) { disposed = true; source.Dispose(); }
        }
    }
}
