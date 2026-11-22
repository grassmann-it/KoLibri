using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using KoLibri.Core.Spiel;

namespace KoLibri.Desktop;

public enum SessionLogKind { Search, Purchase, Adventure, Error, Info }

/// <summary>Which entries the list, Copy and Save show.</summary>
public enum SessionLogLevelFilter { All, ErrorsOnly }

/// <param name="Role">The account role that was active when the entry was written (empty when unknown).</param>
public sealed record SessionLogEntry(DateTimeOffset At, SessionLogKind Kind, string Message, string Role = "")
{
    public string Text => string.Create(CultureInfo.InvariantCulture,
        $"{At.ToLocalTime():HH:mm:ss} {(Role.Length == 0 ? "-" : Role),-8} {Kind,-9} {Message}");
}

/// <summary>
/// One bounded log for the whole window: search, purchase, adventure and errors. View models and the shell write to
/// it. Every entry carries the role that was active, and identical consecutive entries stay separate (two real
/// purchases must read as two). Entries are scrubbed of cookies, passwords and tokens before they are stored. When a
/// directory is given, each entry is also appended to that KoL day's file, so the day can be read after a restart.
/// </summary>
public sealed class SessionLogViewModel : INotifyPropertyChanged
{
    public const int DefaultCapacity = 500;
    public const string AllRoles = "All roles";

    private readonly int _capacity;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<Action>? _post;
    private readonly string? _directory;
    private readonly object _fileGate = new();
    private SessionLogLevelFilter _level = SessionLogLevelFilter.All;
    private string _roleFilter = AllRoles;

    /// <param name="post">Marshals collection changes onto the UI thread; null runs inline (tests).</param>
    /// <param name="directory">Where the per-KoL-day files live; null keeps the log in memory only.</param>
    public SessionLogViewModel(int capacity = DefaultCapacity, Func<DateTimeOffset>? now = null, Action<Action>? post = null,
        string? directory = null)
    {
        _capacity = Math.Max(10, capacity);
        _now = now ?? (() => DateTimeOffset.Now);
        _post = post;
        _directory = directory;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Every entry in memory, newest last.</summary>
    public ObservableCollection<SessionLogEntry> Entries { get; } = new();

    /// <summary>The entries the filters let through, newest last (what the list shows).</summary>
    public ObservableCollection<SessionLogEntry> Visible { get; } = new();

    /// <summary>Set by the window: the active account role, stamped on every new entry.</summary>
    public Func<string>? RoleProvider { get; set; }

    public int ErrorCount { get; private set; }
    public string Summary => Entries.Count == 0
        ? "Session log (empty)"
        : $"Session log ({Entries.Count:N0} entries" + (ErrorCount > 0 ? $", {ErrorCount:N0} errors)" : ")");

    public string[] LevelChoices { get; } = ["All entries", "Errors only"];
    public string LevelChoice
    {
        get => _level == SessionLogLevelFilter.ErrorsOnly ? LevelChoices[1] : LevelChoices[0];
        set { Level = value == LevelChoices[1] ? SessionLogLevelFilter.ErrorsOnly : SessionLogLevelFilter.All; }
    }

    public SessionLogLevelFilter Level
    {
        get => _level;
        set { if (_level == value) return; _level = value; Refilter(); Raise(nameof(Level)); Raise(nameof(LevelChoice)); }
    }

    public string[] RoleChoices { get; } = [AllRoles, "Main", "Mallbot"];
    public string RoleFilter
    {
        get => _roleFilter;
        set { value = string.IsNullOrWhiteSpace(value) ? AllRoles : value; if (_roleFilter == value) return; _roleFilter = value; Refilter(); Raise(); }
    }

    /// <summary>Text after the filters, e.g. "3 of 12 shown"; empty when nothing is filtered out.</summary>
    public string FilterSummary => Visible.Count == Entries.Count ? "" : $"{Visible.Count:N0} of {Entries.Count:N0} shown";

    public void Add(SessionLogKind kind, string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var role = RoleProvider?.Invoke() ?? "";
        var entry = new SessionLogEntry(_now(), kind, Scrub(message.Trim()), role);
        if (_post is null) Append(entry, persist: true); else _post(() => Append(entry, persist: true));
    }

    public void Search(string? message) => Add(SessionLogKind.Search, message);
    public void Purchase(string? message) => Add(SessionLogKind.Purchase, message);
    public void Adventure(string? message) => Add(SessionLogKind.Adventure, message);
    public void Error(string? message) => Add(SessionLogKind.Error, message);
    public void Info(string? message) => Add(SessionLogKind.Info, message);

    public void Clear()
    {
        void Do()
        {
            Entries.Clear();
            Visible.Clear();
            ErrorCount = 0;
            Raise(nameof(Summary));
            Raise(nameof(FilterSummary));
        }
        if (_post is null) Do(); else _post(Do);
    }

    /// <summary>The entries the filters let through, one line each. Copy and Save both use exactly this.</summary>
    public string ToText() => string.Join(Environment.NewLine, Visible.Select(e => e.Text));

    /// <summary>The KoL day an instant belongs to; the file of that day collects its entries.</summary>
    public static string KolDay(DateTimeOffset at) => DailyDoneStore.KolDay(at);

    public string? DayFilePath(DateTimeOffset at)
        => _directory is null ? null : Path.Combine(_directory, $"session-log-{KolDay(at)}.jsonl");

    /// <summary>Loads today's KoL-day file (after a restart) into the list. Unreadable lines are skipped.</summary>
    public int LoadToday()
    {
        var path = DayFilePath(_now());
        if (path is null || !File.Exists(path)) return 0;
        var loaded = 0;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (TryParse(line) is not { } entry) continue;
                Append(entry, persist: false);
                loaded++;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return loaded;
    }

    /// <summary>Writes the current selection (same text as Copy) to a file for this KoL day; returns its path.</summary>
    public string? SaveSelection()
    {
        if (_directory is null) return null;
        try
        {
            Directory.CreateDirectory(_directory);
            var suffix = _level == SessionLogLevelFilter.ErrorsOnly ? "-errors" : "";
            suffix += _roleFilter == AllRoles ? "" : "-" + _roleFilter.ToLowerInvariant();
            var path = Path.Combine(_directory, $"session-log-{KolDay(_now())}{suffix}.txt");
            File.WriteAllText(path, ToText() + Environment.NewLine);
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private bool Passes(SessionLogEntry e)
        => (_level == SessionLogLevelFilter.All || e.Kind == SessionLogKind.Error)
           && (_roleFilter == AllRoles || string.Equals(e.Role, _roleFilter, StringComparison.OrdinalIgnoreCase));

    private void Refilter()
    {
        Visible.Clear();
        foreach (var e in Entries.Where(Passes)) Visible.Add(e);
        Raise(nameof(FilterSummary));
    }

    private void Append(SessionLogEntry entry, bool persist)
    {
        Entries.Add(entry);
        if (Passes(entry)) Visible.Add(entry);
        if (entry.Kind == SessionLogKind.Error) ErrorCount++;
        while (Entries.Count > _capacity)
        {
            var old = Entries[0];
            if (old.Kind == SessionLogKind.Error) ErrorCount--;
            Entries.RemoveAt(0);
            if (Visible.Count > 0 && ReferenceEquals(Visible[0], old)) Visible.RemoveAt(0);
        }
        if (persist) Persist(entry);
        Raise(nameof(Summary));
        Raise(nameof(FilterSummary));
    }

    private void Persist(SessionLogEntry entry)
    {
        if (_directory is null) return;
        try
        {
            lock (_fileGate)
            {
                Directory.CreateDirectory(_directory);
                var line = JsonSerializer.Serialize(new { at = entry.At, kind = entry.Kind.ToString(), role = entry.Role, message = entry.Message });
                File.AppendAllText(DayFilePath(entry.At)!, line + Environment.NewLine);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the in-memory log still works */ }
    }

    private static SessionLogEntry? TryParse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            if (!Enum.TryParse<SessionLogKind>(r.GetProperty("kind").GetString(), out var kind)) return null;
            var message = r.GetProperty("message").GetString();
            return string.IsNullOrWhiteSpace(message)
                ? null
                : new SessionLogEntry(r.GetProperty("at").GetDateTimeOffset(), kind, Scrub(message),
                    r.TryGetProperty("role", out var role) ? role.GetString() ?? "" : "");
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { return null; }
    }

    private static readonly Regex SecretPattern = new(
        @"(?i)(\b(?:password|passwd|pwd|phash|pwdhash|token|access_token|authorization|cookie|set-cookie|PHPSESSID|appserver)\b\s*[=:]\s*)(""[^""]*""|'[^']*'|[^\s;&,]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Hides values that follow secret-looking names, so credentials never reach the list, Copy, Save or the day file.</summary>
    public static string Scrub(string text) => SecretPattern.Replace(text, "$1[hidden]");

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
