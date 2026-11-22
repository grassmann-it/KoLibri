using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace KoLibri.Desktop;

public enum SessionLogKind { Search, Purchase, Adventure, Error, Info }

public sealed record SessionLogEntry(DateTimeOffset At, SessionLogKind Kind, string Message)
{
    public string Text => string.Create(CultureInfo.InvariantCulture,
        $"{At.ToLocalTime():HH:mm:ss} {Kind,-9} {Message}");
}

/// <summary>
/// One bounded, in-memory log for the whole window: search, purchase, adventure and
/// errors. View models and the shell write to it; nothing is persisted and entries
/// never carry cookies, passwords or request bodies. Consecutive identical lines are
/// collapsed so a polling status does not flood the list.
/// </summary>
public sealed class SessionLogViewModel : INotifyPropertyChanged
{
    public const int DefaultCapacity = 500;

    private readonly int _capacity;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<Action>? _post;

    /// <param name="post">Marshals collection changes onto the UI thread; null runs inline (tests).</param>
    public SessionLogViewModel(int capacity = DefaultCapacity, Func<DateTimeOffset>? now = null, Action<Action>? post = null)
    {
        _capacity = Math.Max(10, capacity);
        _now = now ?? (() => DateTimeOffset.Now);
        _post = post;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Newest entry last.</summary>
    public ObservableCollection<SessionLogEntry> Entries { get; } = new();

    public int ErrorCount { get; private set; }
    public string Summary => Entries.Count == 0
        ? "Session log (empty)"
        : $"Session log ({Entries.Count:N0} entries" + (ErrorCount > 0 ? $", {ErrorCount:N0} errors)" : ")");

    public void Add(SessionLogKind kind, string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var entry = new SessionLogEntry(_now(), kind, message.Trim());
        if (_post is null) Append(entry); else _post(() => Append(entry));
    }

    public void Search(string? message) => Add(SessionLogKind.Search, message);
    public void Purchase(string? message) => Add(SessionLogKind.Purchase, message);
    public void Adventure(string? message) => Add(SessionLogKind.Adventure, message);
    public void Error(string? message) => Add(SessionLogKind.Error, message);

    public void Clear()
    {
        void Do()
        {
            Entries.Clear();
            ErrorCount = 0;
            Raise(nameof(Summary));
        }
        if (_post is null) Do(); else _post(Do);
    }

    public string ToText() => string.Join(Environment.NewLine, Entries.Select(e => e.Text));

    private void Append(SessionLogEntry entry)
    {
        if (Entries.Count > 0 && Entries[^1] is { } last && last.Kind == entry.Kind && last.Message == entry.Message)
            return;
        Entries.Add(entry);
        if (entry.Kind == SessionLogKind.Error) ErrorCount++;
        while (Entries.Count > _capacity)
        {
            if (Entries[0].Kind == SessionLogKind.Error) ErrorCount--;
            Entries.RemoveAt(0);
        }
        Raise(nameof(Summary));
    }

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
