using System.Collections.ObjectModel;
using KoLibri.Core.Spiel;

namespace KoLibri.Desktop;

/// <summary>Reads and submits the open choice of the selected account; faked in tests.</summary>
public interface IOpenChoicePort
{
    Task<OpenChoiceDetection> DetectAsync(CancellationToken cancellationToken);
    Task<OpenChoiceSubmitResult> SubmitAsync(string choiceId, int option, CancellationToken cancellationToken);
}

/// <summary>One button of the decision panel.</summary>
public sealed record DecisionOptionRow(int Number, string Text)
{
    public string Label => $"{Number} · {Text}";
}

/// <summary>
/// Holds the open choice/fight shown in the adventure tab: the options as buttons, the choice
/// policy ("apply policy") and the readable result of each action. It never submits on its own.
/// </summary>
public sealed class AdventureDecisionController : DesktopViewModel
{
    private const int MaxPolicySteps = 5;
    private readonly IOpenChoicePort? _port;
    private readonly Func<IChoiceResolver> _resolver;
    private string? _choiceId;
    private string? _title;
    private bool _fightOpen;
    private bool _choiceUnreadable;
    private string _message = string.Empty;

    public AdventureDecisionController(IOpenChoicePort? port, Func<IChoiceResolver>? resolver = null)
    {
        _port = port;
        _resolver = resolver ?? (() => new ChoiceResolver(new ChoiceResolutionConfigStore().Load()));
        Options = new ObservableCollection<DecisionOptionRow>();
    }

    /// <summary>
    /// Set by the session: answers the choice through the active run's checkpoint. Returns null when
    /// no run owns the choice (then the choice is answered directly on KoL).
    /// </summary>
    public Func<string, int, CancellationToken, Task<string?>>? ActiveRunSubmit { get; set; }

    public ObservableCollection<DecisionOptionRow> Options { get; }
    public string? ChoiceId { get => _choiceId; private set => SetProperty(ref _choiceId, value); }
    public string? Title { get => _title; private set => SetProperty(ref _title, value); }
    public bool IsFightOpen { get => _fightOpen; private set { if (SetProperty(ref _fightOpen, value)) OnPropertyChanged(nameof(IsVisible)); } }
    public bool IsChoiceOpen => ChoiceId is not null || _choiceUnreadable;
    public bool IsVisible => IsChoiceOpen || IsFightOpen;
    public string Message { get => _message; private set => SetProperty(ref _message, value); }

    public void Clear()
    {
        Options.Clear();
        ChoiceId = null; Title = null; IsFightOpen = false; _choiceUnreadable = false;
        OnPropertyChanged(nameof(IsChoiceOpen)); OnPropertyChanged(nameof(IsVisible));
    }

    /// <summary>Shows the choice or fight of an encounter the gateway returned; clears the panel otherwise.</summary>
    public void Show(AdventureEncounterState? encounter)
    {
        if (encounter is { Kind: AdventureEncounterKind.Choice, ChoiceId: { } id })
        {
            SetChoice(id, encounter.ChoiceTitle, encounter.AvailableChoices
                .Where(x => int.TryParse(x, out _))
                .Select(x => new ChoiceOptionInfo(int.Parse(x), encounter.ChoiceLabels.GetValueOrDefault(x, $"Option {x}"))));
            Message = "A choice is open. Pick an option or apply your policy.";
        }
        else if (encounter is { Kind: AdventureEncounterKind.Fight })
        {
            Clear();
            IsFightOpen = true;
            Message = "A fight is open. Use Fight encounter or pick an action.";
        }
        else Clear();
    }

    /// <summary>Shows the choice a bounded run stopped on.</summary>
    public void Show(NeedsDecisionInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.ChoiceId is null) { Clear(); return; }
        SetChoice(info.ChoiceId, info.Title, info.Options);
        Message = string.IsNullOrWhiteSpace(info.Reason)
            ? "No choice policy answers this choice. Pick an option." : info.Reason!;
    }

    public void Show(OpenChoiceDetection detection)
    {
        ArgumentNullException.ThrowIfNull(detection);
        switch (detection.Kind)
        {
            case OpenChoiceKind.Fight:
                Clear(); IsFightOpen = true; Message = "A fight is open. Use Fight encounter or pick an action."; break;
            case OpenChoiceKind.Choice when detection.Choice is { } state:
                SetChoice(state.ChoiceId, state.Title, state.Options);
                Message = "A choice is open. Pick an option or apply your policy."; break;
            case OpenChoiceKind.Choice:
                Clear(); _choiceUnreadable = true; OnPropertyChanged(nameof(IsChoiceOpen)); OnPropertyChanged(nameof(IsVisible));
                Message = "A choice is open but its options could not be read. Continue it in KoL (choice.php)."; break;
            case OpenChoiceKind.Unavailable:
                // Keep what is shown: "could not ask" must never look like "nothing is open".
                Message = detection.Message; break;
            default:
                Clear(); Message = "No choice or fight is open."; break;
        }
    }

    public async Task<string> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_port is null) return Message = "No live session; the open choice cannot be read.";
        try { Show(await _port.DetectAsync(cancellationToken).ConfigureAwait(false)); }
        catch (Exception e) when (e is not OperationCanceledException) { Message = AdventureFailureMapper.Describe(e).Text; }
        return Message;
    }

    /// <summary>Submits the option the user picked (one POST).</summary>
    public async Task<string> SubmitAsync(int option, CancellationToken cancellationToken = default)
    {
        if (_port is null) return Message = "No live session; nothing was sent.";
        if (ChoiceId is not { } id) return Message = "No readable choice is open; nothing was sent.";
        if (Options.All(o => o.Number != option)) return Message = $"Option {option} is not offered; nothing was sent.";
        try
        {
            if (ActiveRunSubmit is not null
                && await ActiveRunSubmit(id, option, cancellationToken).ConfigureAwait(false) is { } handled)
                return Message = handled;
            var result = await _port.SubmitAsync(id, option, cancellationToken).ConfigureAwait(false);
            Show(result.Next);
            Message = result.Submitted
                ? $"Chose option {option}. {result.Message}"
                : $"Not sent: {AdventureFailureMapper.Describe(result.Message).Text}";
        }
        catch (Exception e) when (e is not OperationCanceledException) { Message = AdventureFailureMapper.Describe(e).Text; }
        return Message;
    }

    /// <summary>
    /// Lets the choice policy answer the open choice (at most a few chained choices). When it has
    /// no answer the options stay on screen for the user.
    /// </summary>
    public async Task<string> ApplyPolicyAsync(CancellationToken cancellationToken = default)
    {
        if (_port is null) return Message = "No live session; nothing was sent.";
        var done = new List<string>();
        for (var i = 0; i < MaxPolicySteps; i++)
        {
            if (ChoiceId is not { } id)
                return Message = done.Count == 0 ? "No readable choice is open." : $"Policy applied: {string.Join("; ", done)}.";
            var resolution = _resolver().Resolve(new ChoiceResolutionRequest("manual", id, Title, Options
                .Select(o => new ChoiceOptionInfo(o.Number, o.Text)).ToArray()));
            if (resolution.Option is not { } option)
                return Message = (done.Count == 0 ? "" : $"Policy applied: {string.Join("; ", done)}. ")
                    + $"The policy has no answer for choice {id}: {resolution.Reason} Pick an option yourself.";
            await SubmitAsync(option, cancellationToken).ConfigureAwait(false);
            if (Message.StartsWith("Not sent", StringComparison.Ordinal) || !Message.StartsWith("Chose", StringComparison.Ordinal))
                return Message;
            done.Add($"choice {id} → option {option} ({resolution.Reason.TrimEnd('.')})");
        }
        return Message = $"Policy applied {done.Count} steps ({string.Join("; ", done)}); stopped to stay bounded.";
    }

    private void SetChoice(string id, string? title, IEnumerable<ChoiceOptionInfo> options)
    {
        Options.Clear();
        foreach (var option in options.OrderBy(o => o.Number)) Options.Add(new DecisionOptionRow(option.Number, option.Text));
        _choiceUnreadable = false;
        IsFightOpen = false;
        ChoiceId = id; Title = title;
        OnPropertyChanged(nameof(IsChoiceOpen)); OnPropertyChanged(nameof(IsVisible));
    }
}
