using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace KoLibri.Desktop;

/// <summary>A skill row: id, name, MP cost.</summary>
public sealed record SkillRow(int Id, string Name, int MpCost, string Type)
{
    public string Display => MpCost > 0 ? $"{Name} · {MpCost} MP" : Name;
}

/// <summary>Result of one cast as shown in the window.</summary>
public sealed record SkillCastReport(bool Confirmed, string Message, int? HpChange, int? MpChange)
{
    /// <summary>From the library's cast result: <see cref="ActionResult.Completed"/> is the number of confirmed casts.</summary>
    public static SkillCastReport From(SkillRow skill, int times, ActionResult outcome)
    {
        var fresh = outcome.Before is not null && outcome.After is not null;
        int? hp = fresh ? outcome.After!.Hp - outcome.Before!.Hp : null;
        int? mp = fresh ? outcome.After!.Mp - outcome.Before!.Mp : null;
        var confirmed = outcome.Status == ActionStatus.Done;
        var problem = confirmed ? null : outcome.Rejection;
        if (!confirmed)
            return new(false, outcome.Completed > 0
                ? $"{skill.Name}: {outcome.Completed} of {times} cast(s) confirmed. {problem ?? "The rest is not confirmed."} {Delta("HP", hp)}, {Delta("MP", mp)}."
                : $"{skill.Name} ×{times}: {problem ?? "not confirmed."}", hp, mp);
        return new(true, $"Cast {skill.Name} ×{outcome.Completed} (each confirmed). {Delta("HP", hp)}, {Delta("MP", mp)}.", hp, mp);
    }

    private static string Delta(string label, int? value)
        => value is { } v ? $"{label} {(v >= 0 ? "+" : "")}{v:N0}" : $"{label} unknown";
}

/// <summary>
/// The skills and effects page. Casting is gated: <see cref="RequestCast"/> only prepares a confirmation
/// ("Cast X ×N for M MP?"); nothing is sent until <see cref="ConfirmCastAsync"/> runs. The request itself is
/// injected so the model is testable and the shell owns the account session.
/// </summary>
public sealed class SkillsEffectsViewModel : INotifyPropertyChanged
{
    public const int MaxQuantity = 999;

    private readonly IReadOnlyList<SkillRow> _all;
    private string _filter = "";
    private SkillRow? _selected;
    private int _quantity = 1;
    private string? _pendingText;
    private string _resultText = "";
    private bool _busy;
    private (SkillRow Skill, int Times, string Role)? _pending;
    private string _role = "";
    private int _currentMp = -1;

    public SkillsEffectsViewModel(IEnumerable<SkillRow> skills)
    {
        _all = skills.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Skills = new ObservableCollection<SkillRow>(_all);
        Effects = new ObservableCollection<EffectRow>();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<SkillRow> Skills { get; }
    public ObservableCollection<EffectRow> Effects { get; }

    /// <summary>Skills castable outside a fight, from the catalog tags (passive and combat-only skills are left out).</summary>
    public static IEnumerable<SkillRow> FromCatalog(IEnumerable<MafiaSkillRecord> records)
        => records.Where(r => r.Name.Length > 0 && r.UsableOutsideCombat)
            .Select(r => new SkillRow(r.Id, r.Name, r.MpCost, r.Type));

    public string Filter
    {
        get => _filter;
        set
        {
            if (!Set(ref _filter, value ?? "")) return;
            Skills.Clear();
            foreach (var skill in _all.Where(s => s.Name.Contains(_filter.Trim(), StringComparison.OrdinalIgnoreCase)))
                Skills.Add(skill);
        }
    }

    public SkillRow? SelectedSkill
    {
        get => _selected;
        set { if (Set(ref _selected, value)) { CancelCast(); Notify(nameof(CanRequestCast)); Notify(nameof(TotalMp)); } }
    }

    public int Quantity
    {
        get => _quantity;
        set { if (Set(ref _quantity, Math.Clamp(value, 1, MaxQuantity))) { CancelCast(); Notify(nameof(TotalMp)); } }
    }

    /// <summary>Label of the account the cast would run as. Changing it discards a pending confirmation.</summary>
    public string Role
    {
        get => _role;
        set { if (_role == (value ?? "")) return; _role = value ?? ""; CancelCast(); }
    }

    public int TotalMp => (_selected?.MpCost ?? 0) * _quantity;
    public bool CanRequestCast => _selected is not null && !_busy;
    public bool IsBusy => _busy;
    public string? PendingText { get => _pendingText; private set { if (Set(ref _pendingText, value)) Notify(nameof(HasPending)); } }
    public bool HasPending => _pendingText is not null;
    public string ResultText { get => _resultText; private set => Set(ref _resultText, value); }

    /// <summary>Takes the latest status: effect rows and the MP used for the "not enough MP" pre-check.</summary>
    public void ApplyStatus(Status? status)
    {
        Effects.Clear();
        if (status is null) { _currentMp = -1; return; }
        _currentMp = status.Mp;
        foreach (var row in CharpaneState.FromStatus(status, "").Effects) Effects.Add(row);
    }

    /// <summary>Prepares the confirmation. Returns false (with a result text) when nothing can be cast.</summary>
    public bool RequestCast()
    {
        if (_selected is not { } skill) { ResultText = "Select a skill first."; return false; }
        if (_busy) return false;
        var total = skill.MpCost * _quantity;
        if (_currentMp >= 0 && total > _currentMp)
        {
            ResultText = $"Not enough MP for {skill.Name} ×{_quantity}: needs {total:N0}, you have {_currentMp:N0}. Lower the quantity or refresh.";
            return false;
        }
        _pending = (skill, _quantity, _role);
        var who = _role.Length > 0 ? $" as {_role}" : "";
        PendingText = total > 0
            ? $"Cast {skill.Name} ×{_quantity}{who} for {total:N0} MP?"
            : $"Cast {skill.Name} ×{_quantity}{who}? (no MP cost; it may use another resource such as soulsauce)";
        return true;
    }

    public void CancelCast()
    {
        _pending = null;
        PendingText = null;
    }

    /// <summary>Sends the confirmed cast. Errors become result text; never throws.</summary>
    public async Task<SkillCastReport?> ConfirmCastAsync(
        Func<int, int, CancellationToken, Task<ActionResult>> cast, CancellationToken cancellationToken = default)
    {
        if (_pending is not { } pending) return null;
        CancelCast();
        if (!string.Equals(pending.Role, _role, StringComparison.Ordinal))
        {
            ResultText = "The account changed since the confirmation; nothing was cast. Request the cast again.";
            return new SkillCastReport(false, ResultText, null, null);
        }
        _busy = true;
        Notify(nameof(IsBusy)); Notify(nameof(CanRequestCast));
        try
        {
            var outcome = await cast(pending.Skill.Id, pending.Times, cancellationToken).ConfigureAwait(true);
            var report = SkillCastReport.From(pending.Skill, pending.Times, outcome);
            ResultText = report.Message;
            // Without a fresh status after the cast the MP pre-check is unknown until the next refresh.
            ApplyStatus(outcome.After);
            return report;
        }
        catch (Exception exception)
        {
            ResultText = $"Cast failed: {CharpaneState.DescribeError(exception)}";
            return new SkillCastReport(false, ResultText, null, null);
        }
        finally
        {
            _busy = false;
            Notify(nameof(IsBusy)); Notify(nameof(CanRequestCast));
        }
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name!);
        return true;
    }

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
