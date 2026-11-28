namespace KoLibri
{
    /// <summary>
    /// Events werden nach dem Parsen synchron auf dem aufrufenden Thread ausgelöst. Handler dürfen keine
    /// Requests derselben Sitzung starten (Action-Lane ist belegt) und sollten schnell sein. Ausnahmen in
    /// Handlern werden gefangen und als HandlerFailed gemeldet; sie brechen die Aktion nie ab.
    /// </summary>
    public sealed class KolibriEvents
    {
        internal KolibriEvents() { }
        public event EventHandler<RequestEventArgs>? Request;
        public event EventHandler<EncounterEventArgs>? Encounter;
        public event EventHandler<FightEndedEventArgs>? FightEnded;
        public event EventHandler<ItemAcquiredEventArgs>? ItemAcquired;
        public event EventHandler<StatusChangedEventArgs>? StatusChanged;
        public event EventHandler<HandlerFailedEventArgs>? HandlerFailed;

        internal List<Exception> OnRequest(RequestEventArgs args) => Fire(Request, args);
        internal List<Exception> OnEncounter(EncounterEventArgs args) => Fire(Encounter, args);
        internal List<Exception> OnFightEnded(FightEndedEventArgs args) => Fire(FightEnded, args);
        internal List<Exception> OnItemAcquired(ItemAcquiredEventArgs args) => Fire(ItemAcquired, args);
        internal List<Exception> OnStatusChanged(StatusChangedEventArgs args) => Fire(StatusChanged, args);

        /// <summary>A failing HandlerFailed handler is swallowed: there is nobody left to tell.</summary>
        internal void OnHandlerFailed(HandlerFailedEventArgs args) => Fire(HandlerFailed, args);

        private List<Exception> Fire<T>(EventHandler<T>? handler, T args)
        {
            var failures = new List<Exception>();
            if (handler is null) return failures;
            foreach (var single in handler.GetInvocationList().Cast<EventHandler<T>>())
            {
                try { single(this, args); }
                catch (Exception exception) { failures.Add(exception); }
            }
            return failures;
        }
    }

    public abstract class KolibriEventArgs : EventArgs
    {
        protected KolibriEventArgs(AccountId account, DateTimeOffset at) { Account = account; At = at; }
        public AccountId Account { get; }
        public DateTimeOffset At { get; }
    }

    public enum RequestMethod { Get, Post }

    /// <summary>Ein HTTP-Request an KoL. Path ist redigiert (kein pwd, kein Passwort, keine Cookies).</summary>
    public sealed class RequestEventArgs : KolibriEventArgs
    {
        public RequestEventArgs(AccountId account, DateTimeOffset at, RequestMethod method, string path, RequestLane lane, int statusCode, TimeSpan duration, TimeSpan waited, PageKind kind)
            : base(account, at) { Method = method; Path = path; Lane = lane; StatusCode = statusCode; Duration = duration; Waited = waited; Kind = kind; }
        public RequestMethod Method { get; }
        public string Path { get; }
        public RequestLane Lane { get; }
        public int StatusCode { get; }
        public TimeSpan Duration { get; }
        /// <summary>Zeit, die der Limiter vor dem Senden gewartet hat.</summary>
        public TimeSpan Waited { get; }
        public PageKind Kind { get; }
    }

    public sealed class EncounterEventArgs : KolibriEventArgs
    {
        public EncounterEventArgs(AccountId account, DateTimeOffset at, Encounter encounter) : base(account, at) => Encounter = encounter;
        public Encounter Encounter { get; }
    }

    public sealed class FightEndedEventArgs : KolibriEventArgs
    {
        public FightEndedEventArgs(AccountId account, DateTimeOffset at, FightResult result) : base(account, at) => Result = result;
        public FightResult Result { get; }
    }

    public enum ItemSource { Fight, Choice, NonCombat, Mall, Mail, Use, Other }

    public sealed class ItemAcquiredEventArgs : KolibriEventArgs
    {
        public ItemAcquiredEventArgs(AccountId account, DateTimeOffset at, InventoryItem item, ItemSource source) : base(account, at) { Item = item; Source = source; }
        public InventoryItem Item { get; }
        public ItemSource Source { get; }
    }

    public sealed class StatusChangedEventArgs : KolibriEventArgs
    {
        public StatusChangedEventArgs(AccountId account, DateTimeOffset at, Status? before, Status after) : base(account, at) { Before = before; After = after; }
        public Status? Before { get; }
        public Status After { get; }
    }

    public sealed class HandlerFailedEventArgs : KolibriEventArgs
    {
        public HandlerFailedEventArgs(AccountId account, DateTimeOffset at, string eventName, Exception exception) : base(account, at) { EventName = eventName; Exception = exception; }
        public string EventName { get; }
        public Exception Exception { get; }
    }

    public enum GuardedActionKind { Adventure, Fight, Choice, Use, Consume, Cast, Rest, Equip, MallBuy, ShopChange, SendMail, RawPost, Other }

    /// <summary>Beschreibung einer verändernden Aktion, bevor der Request rausgeht.</summary>
    public sealed record GuardedAction(AccountId Account, GuardedActionKind Kind, string Summary)
    {
        public ItemRef? Item { get; init; }
        public int Quantity { get; init; }
        public long Meat { get; init; }
        /// <summary>Höchster Stückpreis einer Zeile der Aktion (MallBuy); null, wenn unbekannt.</summary>
        public long? MaxUnitPrice { get; init; }
        public string? Path { get; init; }
        internal string? Binding { get; init; }
    }

    public sealed record GuardDecision(bool Allowed, string? Reason)
    {
        public static GuardDecision Allow { get; } = new(true, null);
        public static GuardDecision Deny(string reason) => new(false, reason);
    }

    /// <summary>Einziger Policy-Hook im Kern. Freigaben, Budgets und Audit hängen sich hier an (KoLibri.Safety).</summary>
    public interface IActionGuard
    {
        ValueTask<GuardDecision> CheckAsync(GuardedAction action, CancellationToken cancellationToken);
        /// <summary>Nach dem Request, auch bei Fehler. Für Ledger und Audit.</summary>
        ValueTask CompletedAsync(GuardedAction action, bool succeeded, CancellationToken cancellationToken);
    }
}
