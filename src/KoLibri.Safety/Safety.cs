using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KoLibri.Safety
{
    /// <summary>Kombiniert mehrere Guards; der erste Deny gewinnt.</summary>
    public sealed class CompositeGuard : IActionGuard
    {
        private readonly IActionGuard[] _guards;

        public CompositeGuard(params IActionGuard[] guards)
        {
            ArgumentNullException.ThrowIfNull(guards);
            _guards = guards.Where(g => g is not null).ToArray();
        }

        public async ValueTask<GuardDecision> CheckAsync(GuardedAction action, CancellationToken cancellationToken)
        {
            foreach (var guard in _guards)
            {
                var decision = await guard.CheckAsync(action, cancellationToken).ConfigureAwait(false);
                if (!decision.Allowed) return decision;
            }
            return GuardDecision.Allow;
        }

        public async ValueTask CompletedAsync(GuardedAction action, bool succeeded, CancellationToken cancellationToken)
        {
            List<Exception>? failures = null;
            foreach (var guard in _guards)
            {
                try { await guard.CompletedAsync(action, succeeded, cancellationToken).ConfigureAwait(false); }
                catch (Exception exception) { (failures ??= new()).Add(exception); }
            }
            if (failures is not null) throw new AggregateException("A guard failed after the action.", failures);
        }
    }

    // --- Rechte / Grants (heute LibKolibri.Access) -----------------------------------------
    [Flags]
    public enum Rights { None = 0, Read = 1, Adventure = 2, Consume = 4, MallBuy = 8, ShopChange = 16, SendMail = 32, RawPost = 64, All = 127 }

    public sealed record Grant(string Subject, AccountId Account, Rights Rights, DateTimeOffset? ExpiresAt)
    {
        public long? MeatPerDay { get; init; }
        public int? TurnsPerDay { get; init; }
    }

    public interface IGrantStore
    {
        Task<IReadOnlyList<Grant>> ListAsync(CancellationToken cancellationToken);
        Task SaveAsync(Grant grant, CancellationToken cancellationToken);
        Task RevokeAsync(string subject, AccountId account, CancellationToken cancellationToken);
    }

    public sealed class JsonGrantStore : IGrantStore
    {
        private readonly string _path;
        private readonly SemaphoreSlim _gate = new(1, 1);

        public JsonGrantStore(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            _path = Path.GetFullPath(path);
        }

        public async Task<IReadOnlyList<Grant>> ListAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { return Load(); }
            finally { _gate.Release(); }
        }

        public async Task SaveAsync(Grant grant, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(grant);
            ArgumentException.ThrowIfNullOrWhiteSpace(grant.Subject);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var grants = Load().Where(g => !Same(g, grant.Subject, grant.Account)).Append(grant).ToList();
                Store(grants);
            }
            finally { _gate.Release(); }
        }

        public async Task RevokeAsync(string subject, AccountId account, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { Store(Load().Where(g => !Same(g, subject, account)).ToList()); }
            finally { _gate.Release(); }
        }

        private static bool Same(Grant grant, string subject, AccountId account)
            => string.Equals(grant.Subject, subject, StringComparison.Ordinal)
               && string.Equals(grant.Account.Value, account.Value, StringComparison.OrdinalIgnoreCase);

        private sealed record Row(string Subject, string Account, Rights Rights, DateTimeOffset? ExpiresAt, long? MeatPerDay, int? TurnsPerDay);

        private List<Grant> Load()
        {
            if (!File.Exists(_path)) return new();
            var rows = JsonSerializer.Deserialize<List<Row>>(File.ReadAllText(_path), Json.Options) ?? new();
            return rows.Select(r => new Grant(r.Subject, new AccountId(r.Account), r.Rights, r.ExpiresAt)
            {
                MeatPerDay = r.MeatPerDay,
                TurnsPerDay = r.TurnsPerDay
            }).ToList();
        }

        private void Store(List<Grant> grants)
        {
            var rows = grants.Select(g => new Row(g.Subject, g.Account.Value, g.Rights, g.ExpiresAt, g.MeatPerDay, g.TurnsPerDay)).ToList();
            Json.WriteAtomic(_path, JsonSerializer.Serialize(rows, Json.Options));
        }
    }

    /// <summary>Lehnt Aktionen ab, für die der Subject keinen Grant hat (heute AccessGate).</summary>
    public sealed class GrantGuard : IActionGuard
    {
        private readonly string _subject;
        private readonly IGrantStore _grants;
        private readonly UsageLedger _ledger;

        public GrantGuard(string subject, IGrantStore grants, UsageLedger ledger)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(subject);
            _subject = subject;
            _grants = grants ?? throw new ArgumentNullException(nameof(grants));
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        }

        public async ValueTask<GuardDecision> CheckAsync(GuardedAction action, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(action);
            var now = DateTimeOffset.UtcNow;
            var grant = (await _grants.ListAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(g => string.Equals(g.Subject, _subject, StringComparison.Ordinal)
                                     && string.Equals(g.Account.Value, action.Account.Value, StringComparison.OrdinalIgnoreCase)
                                     && (g.ExpiresAt is null || g.ExpiresAt > now));
            if (grant is null) return GuardDecision.Deny($"'{_subject}' has no grant for account '{action.Account}'.");
            var needed = Needed(action.Kind);
            if ((grant.Rights & needed) != needed)
                return GuardDecision.Deny($"'{_subject}' may not do {action.Kind} on '{action.Account}' (needs {needed}).");
            if (grant.MeatPerDay is { } meatLimit && action.Meat > 0 && _ledger.MeatSpentToday(action.Account) + action.Meat > meatLimit)
                return GuardDecision.Deny($"The grant allows {meatLimit} Meat per day; {_ledger.MeatSpentToday(action.Account)} are spent.");
            if (grant.TurnsPerDay is { } turnLimit && UsageLedger.Turns(action) > 0
                && _ledger.TurnsSpentToday(action.Account) + UsageLedger.Turns(action) > turnLimit)
                return GuardDecision.Deny($"The grant allows {turnLimit} turns per day; {_ledger.TurnsSpentToday(action.Account)} are used.");
            return GuardDecision.Allow;
        }

        public ValueTask CompletedAsync(GuardedAction action, bool succeeded, CancellationToken cancellationToken)
        {
            if (succeeded) _ledger.RecordOnce(action);
            return ValueTask.CompletedTask;
        }

        internal static Rights Needed(GuardedActionKind kind) => kind switch
        {
            GuardedActionKind.Adventure or GuardedActionKind.Fight or GuardedActionKind.Choice => Rights.Adventure,
            GuardedActionKind.Use or GuardedActionKind.Consume or GuardedActionKind.Cast or GuardedActionKind.Rest or GuardedActionKind.Equip => Rights.Consume,
            GuardedActionKind.MallBuy => Rights.MallBuy,
            GuardedActionKind.ShopChange => Rights.ShopChange,
            GuardedActionKind.SendMail => Rights.SendMail,
            _ => Rights.RawPost
        };
    }

    // --- Budgets / Ledger ------------------------------------------------------------------
    public sealed record Budget(AccountId Account, long MeatPerDay, int? TurnsPerDay = null, long? MaxUnitPrice = null);

    /// <summary>Verbrauch je Konto und KoL-Tag, dateibasiert, über Prozesse hinweg (heute UsageLedger + SharedMallSpendLedger).</summary>
    public sealed class UsageLedger
    {
        private readonly string _path;
        private readonly object _gate = new();
        private readonly ConditionalWeakTable<GuardedAction, object> _recorded = new();
        private readonly Func<DateTimeOffset> _now;

        public UsageLedger(string path) : this(path, () => DateTimeOffset.UtcNow) { }

        internal UsageLedger(string path, Func<DateTimeOffset> now)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            _path = Path.GetFullPath(path);
            _now = now;
        }

        public long MeatSpentToday(AccountId account) => Read(account).Meat;
        public int TurnsSpentToday(AccountId account) => Read(account).Turns;

        public void Record(AccountId account, long meat, int turns)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(meat);
            ArgumentOutOfRangeException.ThrowIfNegative(turns);
            lock (_gate)
            {
                using var fileLock = Json.Lock(_path);
                var map = Load();
                var day = KolDay(_now());
                foreach (var stale in map.Keys.Where(k => !k.StartsWith(day + "|", StringComparison.Ordinal)).ToList()) map.Remove(stale);
                var key = Key(day, account);
                var current = map.TryGetValue(key, out var row) ? row : new Usage(0, 0);
                map[key] = new Usage(current.Meat + meat, current.Turns + turns);
                Json.WriteAtomic(_path, JsonSerializer.Serialize(map, Json.Options));
            }
        }

        /// <summary>Records an action once, even when several guards over the same ledger report it.</summary>
        internal void RecordOnce(GuardedAction action)
        {
            lock (_gate)
            {
                if (_recorded.TryGetValue(action, out _)) return;
                _recorded.Add(action, new object());
            }
            var meat = Math.Max(0, action.Meat);
            var turns = Turns(action);
            if (meat > 0 || turns > 0) Record(action.Account, meat, turns);
        }

        internal static int Turns(GuardedAction action) => action.Kind == GuardedActionKind.Adventure ? Math.Max(1, action.Quantity) : 0;

        private sealed record Usage(long Meat, int Turns);

        private Usage Read(AccountId account)
        {
            lock (_gate)
            {
                var map = Load();
                return map.TryGetValue(Key(KolDay(_now()), account), out var row) ? row : new Usage(0, 0);
            }
        }

        private Dictionary<string, Usage> Load()
        {
            try
            {
                return File.Exists(_path)
                    ? JsonSerializer.Deserialize<Dictionary<string, Usage>>(File.ReadAllText(_path), Json.Options) ?? new()
                    : new();
            }
            catch (JsonException exception)
            {
                // A damaged ledger must not read as "nothing spent".
                throw new InvalidOperationException($"The usage ledger '{_path}' is damaged; refusing to guess the spend.", exception);
            }
        }

        private static string Key(string day, AccountId account) => $"{day}|{account.Value.ToLowerInvariant()}";

        /// <summary>The KoL day: rollover at about 03:30 US Eastern, same as the daily markers of the core.</summary>
        internal static string KolDay(DateTimeOffset now)
        {
            TimeZoneInfo zone;
            try { zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
            catch (TimeZoneNotFoundException) { zone = TimeZoneInfo.CreateCustomTimeZone("EST", TimeSpan.FromHours(-5), "EST", "EST"); }
            return TimeZoneInfo.ConvertTime(now, zone).AddHours(-3.5).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    public sealed class BudgetGuard : IActionGuard
    {
        private readonly IReadOnlyList<Budget> _budgets;
        private readonly UsageLedger _ledger;

        public BudgetGuard(IReadOnlyList<Budget> budgets, UsageLedger ledger)
        {
            _budgets = budgets ?? throw new ArgumentNullException(nameof(budgets));
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        }

        public ValueTask<GuardDecision> CheckAsync(GuardedAction action, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(action);
            var budget = _budgets.FirstOrDefault(b => string.Equals(b.Account.Value, action.Account.Value, StringComparison.OrdinalIgnoreCase));
            if (budget is null) return ValueTask.FromResult(GuardDecision.Allow);
            if (action.Meat > 0 && _ledger.MeatSpentToday(action.Account) + action.Meat > budget.MeatPerDay)
                return ValueTask.FromResult(GuardDecision.Deny(
                    $"Budget: {budget.MeatPerDay} Meat per day for '{action.Account}', {_ledger.MeatSpentToday(action.Account)} spent, this needs {action.Meat}."));
            var turns = UsageLedger.Turns(action);
            if (budget.TurnsPerDay is { } limit && turns > 0 && _ledger.TurnsSpentToday(action.Account) + turns > limit)
                return ValueTask.FromResult(GuardDecision.Deny($"Budget: {limit} turns per day for '{action.Account}' are used up."));
            // Per line, not the average: one dear store must not hide behind cheap ones. Without line prices the
            // average is the only check left.
            if (budget.MaxUnitPrice is { } max && action.Kind == GuardedActionKind.MallBuy && action.Quantity > 0
                && (action.MaxUnitPrice is { } highest ? highest > max : action.Meat > (long)action.Quantity * max))
                return ValueTask.FromResult(GuardDecision.Deny($"Budget: at most {max} Meat per unit."));
            return ValueTask.FromResult(GuardDecision.Allow);
        }

        public ValueTask CompletedAsync(GuardedAction action, bool succeeded, CancellationToken cancellationToken)
        {
            if (succeeded) _ledger.RecordOnce(action);
            return ValueTask.CompletedTask;
        }
    }

    // --- Freigaben (Approval-Token) --------------------------------------------------------
    public sealed record ApprovalToken(string Value, GuardedAction Action, DateTimeOffset ExpiresAt);

    /// <summary>Verlangt für ausgewählte Aktionsarten ein vorher ausgestelltes Token (heute ApprovalTokenStore).</summary>
    public sealed class ApprovalGuard : IActionGuard
    {
        private readonly IReadOnlySet<GuardedActionKind> _kinds;
        private readonly TimeSpan _lifetime;
        private readonly object _gate = new();
        private readonly Dictionary<string, ApprovalToken> _issued = new(StringComparer.Ordinal);
        private readonly List<ApprovalToken> _presented = new();

        public ApprovalGuard(IReadOnlySet<GuardedActionKind> requireApprovalFor, TimeSpan tokenLifetime)
        {
            _kinds = requireApprovalFor ?? throw new ArgumentNullException(nameof(requireApprovalFor));
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(tokenLifetime, TimeSpan.Zero);
            _lifetime = tokenLifetime;
        }

        public ApprovalToken Issue(GuardedAction action)
        {
            ArgumentNullException.ThrowIfNull(action);
            var token = new ApprovalToken(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), action,
                DateTimeOffset.UtcNow + _lifetime);
            lock (_gate) _issued[token.Value] = token;
            return token;
        }

        /// <summary>Hinterlegt ein Token für die nächste passende Aktion dieses Kontos.</summary>
        public void Present(string token)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(token);
            lock (_gate)
            {
                if (!_issued.Remove(token.Trim(), out var issued))
                    throw new ArgumentException("Unknown or already used approval token.", nameof(token));
                _presented.Add(issued);
            }
        }

        public ValueTask<GuardDecision> CheckAsync(GuardedAction action, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(action);
            if (!_kinds.Contains(action.Kind)) return ValueTask.FromResult(GuardDecision.Allow);
            var now = DateTimeOffset.UtcNow;
            lock (_gate)
            {
                _presented.RemoveAll(t => t.ExpiresAt <= now);
                var match = _presented.FirstOrDefault(t => Covers(t.Action, action));
                if (match is null)
                    return ValueTask.FromResult(GuardDecision.Deny($"{action.Kind} on '{action.Account}' needs an approval token: {action.Summary}"));
                _presented.Remove(match); // one token, one action
            }
            return ValueTask.FromResult(GuardDecision.Allow);
        }

        public ValueTask CompletedAsync(GuardedAction action, bool succeeded, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        /// <summary>The approved action covers the actual one: same account, kind and item, no more units or Meat.</summary>
        private static bool Covers(GuardedAction approved, GuardedAction actual)
            => string.Equals(approved.Account.Value, actual.Account.Value, StringComparison.OrdinalIgnoreCase)
               && approved.Kind == actual.Kind
               && (approved.Item is null || approved.Item == actual.Item)
               && (approved.Quantity <= 0 || actual.Quantity <= approved.Quantity)
               && (approved.Meat <= 0 || actual.Meat <= approved.Meat);
    }

    // --- Audit -----------------------------------------------------------------------------
    public sealed record AuditEntry(long Sequence, DateTimeOffset At, AccountId Account, string Kind, string Summary, string Hash, string PreviousHash);

    /// <summary>JSONL-Journal mit Hash-Kette. Hängt sich an KolibriEvents und an den Guard.</summary>
    public sealed class AuditJournal : IActionGuard, IDisposable
    {
        private const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";
        private readonly string _path;
        private readonly object _gate = new();
        private readonly List<KolibriEvents> _attached = new();
        private long _sequence;
        private string _lastHash = Genesis;
        private bool _disposed;

        public AuditJournal(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            _path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var existing = ReadAll();
            if (existing.Count > 0)
            {
                _sequence = existing[^1].Sequence;
                _lastHash = existing[^1].Hash;
            }
        }

        public void Attach(KolibriEvents events)
        {
            ArgumentNullException.ThrowIfNull(events);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                events.FightEnded += OnFightEnded;
                events.ItemAcquired += OnItemAcquired;
                _attached.Add(events);
            }
        }

        public IReadOnlyList<AuditEntry> Read(DateTimeOffset? since = null)
        {
            lock (_gate)
            {
                var all = ReadAll();
                return since is { } from ? all.Where(e => e.At >= from).ToArray() : all;
            }
        }

        /// <summary>Prüft die Hash-Kette; liefert die erste kaputte Sequenz oder null.</summary>
        public long? Verify()
        {
            lock (_gate)
            {
                var previous = Genesis;
                long expected = 1;
                foreach (var entry in ReadAll())
                {
                    if (entry.Sequence != expected || entry.PreviousHash != previous || entry.Hash != HashOf(entry with { Hash = "" }))
                        return entry.Sequence;
                    previous = entry.Hash;
                    expected++;
                }
                return null;
            }
        }

        public ValueTask<GuardDecision> CheckAsync(GuardedAction action, CancellationToken cancellationToken)
        {
            Append(action.Account, $"{action.Kind}:requested", action.Summary);
            return ValueTask.FromResult(GuardDecision.Allow);
        }

        public ValueTask CompletedAsync(GuardedAction action, bool succeeded, CancellationToken cancellationToken)
        {
            Append(action.Account, $"{action.Kind}:{(succeeded ? "completed" : "failed")}", action.Summary);
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (var events in _attached)
                {
                    events.FightEnded -= OnFightEnded;
                    events.ItemAcquired -= OnItemAcquired;
                }
                _attached.Clear();
            }
        }

        private void OnFightEnded(object? sender, FightEndedEventArgs e)
            => Append(e.Account, "FightEnded", $"{e.Result.Outcome} against {e.Result.MonsterName} in {e.Result.Rounds.Count} rounds, {e.Result.Meat} Meat");

        private void OnItemAcquired(object? sender, ItemAcquiredEventArgs e)
            => Append(e.Account, "ItemAcquired", $"{e.Item.Quantity} x {e.Item.Name} ({e.Item.Item.Id}) from {e.Source}");

        private void Append(AccountId account, string kind, string summary)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var entry = new AuditEntry(_sequence + 1, DateTimeOffset.UtcNow, account, kind, summary, "", _lastHash);
                entry = entry with { Hash = HashOf(entry) };
                using var fileLock = Json.Lock(_path);
                File.AppendAllText(_path, JsonSerializer.Serialize(ToRow(entry), Json.Line) + "\n");
                _sequence = entry.Sequence;
                _lastHash = entry.Hash;
            }
        }

        private sealed record Row(long Seq, DateTimeOffset At, string Account, string Kind, string Summary, string Hash, string Prev);

        private static Row ToRow(AuditEntry e) => new(e.Sequence, e.At, e.Account.Value, e.Kind, e.Summary, e.Hash, e.PreviousHash);

        private List<AuditEntry> ReadAll()
        {
            if (!File.Exists(_path)) return new();
            var list = new List<AuditEntry>();
            foreach (var line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                Row? row;
                try { row = JsonSerializer.Deserialize<Row>(line, Json.Line); }
                catch (JsonException) { row = null; }
                list.Add(row is null
                    ? new AuditEntry(list.Count + 1, DateTimeOffset.MinValue, default, "unreadable", line, "", "")
                    : new AuditEntry(row.Seq, row.At, new AccountId(row.Account), row.Kind, row.Summary, row.Hash, row.Prev));
            }
            return list;
        }

        private static string HashOf(AuditEntry entry)
        {
            var canonical = string.Join('\u001f', entry.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                entry.At.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
                entry.Account.Value ?? "", entry.Kind, entry.Summary, entry.PreviousHash);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        }
    }

    internal static class Json
    {
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
        public static readonly JsonSerializerOptions Line = new(JsonSerializerDefaults.Web);

        public static void WriteAtomic(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, content);
            File.Move(temp, path, overwrite: true);
        }

        /// <summary>Exclusive lock file, so several processes do not write at the same time.</summary>
        public static FileStream Lock(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                try { return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(25); }
            }
        }
    }
}
