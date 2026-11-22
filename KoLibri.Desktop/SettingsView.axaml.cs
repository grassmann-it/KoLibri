using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Controls;

namespace KoLibri.Desktop;

public partial class SettingsView : UserControl
{
    public SettingsView() : this(new SettingsViewModel(new DesktopSettingsStore())) { }

    public SettingsView(SettingsViewModel model)
    {
        InitializeComponent();
        DataContext = model;
    }

    private void InitializeComponent() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
}

/// <summary>Edits <see cref="DesktopSettings"/>; values are clamped on set and written by <see cref="Save"/>.</summary>
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly DesktopSettingsStore _store;
    private readonly Func<string, string?> _env;
    private string _status = "";

    public SettingsViewModel(DesktopSettingsStore store, Func<string, string?>? env = null, string? agentRightsNote = null,
        Func<string, IReadOnlyList<(string Id, string Name)>>? itemLookup = null,
        KoLibri.Core.Spiel.ItemClassifier? classifier = null)
    {
        _store = store;
        _env = env ?? Environment.GetEnvironmentVariable;
        Settings = store.Load();
        AgentRightsNote = agentRightsNote ?? "Agent rights are granted on the consent page of the MCP server and shown there; see docs/mcp-access.md.";
        Save = new RelayCommand(DoSave);
        ResetDefaults = new RelayCommand(DoResetDefaults);
        Booze = new ConsumableListEditor(Settings.BoozeItems, itemLookup, classifier, KoLibri.Core.Spiel.ItemOrgan.Drink);
        Spleen = new ConsumableListEditor(Settings.SpleenItems, itemLookup, classifier, KoLibri.Core.Spiel.ItemOrgan.Spleen);
    }

    public ConsumableListEditor Booze { get; }
    public ConsumableListEditor Spleen { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public DesktopSettings Settings { get; }
    public ICommand Save { get; }
    public string AgentRightsNote { get; }
    public string[] Themes { get; } = ["Light", "Dark", "System"];

    public string Status { get => _status; private set { _status = value; Raise(); } }

    public string? DefaultAccount { get => Settings.DefaultAccount; set { Settings.DefaultAccount = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); Raise(); } }
    public bool ReloginAutomatically { get => Settings.ReloginAutomatically; set { Settings.ReloginAutomatically = value; Raise(); } }
    public bool OptionalTools { get => Settings.OptionalTools; set { Settings.OptionalTools = value; Raise(); } }
    /// <summary>"Last used" opens the role used last time; Main/Mallbot always open that role.</summary>
    public const string LastUsedChoice = "Last used";
    public string[] DefaultAccountChoices { get; } = [LastUsedChoice, "Main", "Mallbot"];
    public string DefaultAccountChoice
    {
        get => Settings.DefaultAccount is { } a && DefaultAccountChoices.Contains(a, StringComparer.OrdinalIgnoreCase)
            ? DefaultAccountChoices.First(c => string.Equals(c, a, StringComparison.OrdinalIgnoreCase))
            : LastUsedChoice;
        set { if (value is not null) { DefaultAccount = value == LastUsedChoice ? null : value; Raise(); } }
    }
    public decimal CloverThreshold { get => N("cloverThreshold"); set => Put("cloverThreshold", value); }
    public decimal CloverThresholdMin => Min("cloverThreshold"); public decimal CloverThresholdMax => Max("cloverThreshold");

    /// <summary>Puts every number back to its default (not saved until Save).</summary>
    public ICommand ResetDefaults { get; private set; } = null!;

    /// <summary>Header theme button: the shown theme and the saved theme are one value.</summary>
    public void SetTheme(string theme) { Settings.Theme = theme; Raise(nameof(Theme)); }

    public string Theme { get => Settings.Theme; set { if (value is not null) { Settings.Theme = value; Raise(); } } }

    public decimal MeatPerDay { get => N("meatPerDay"); set => Put("meatPerDay", value); }
    public decimal MeatPerPurchase { get => N("meatPerPurchase"); set => Put("meatPerPurchase", value); }
    public decimal UnitsPerPurchase { get => N("unitsPerPurchase"); set => Put("unitsPerPurchase", value); }
    public decimal ResultLimit { get => N("resultLimit"); set => Put("resultLimit", value); }
    public decimal KolIntervalSeconds { get => N("kolIntervalSeconds"); set { Put("kolIntervalSeconds", value); Raise(nameof(KolIntervalInfo)); } }
    public decimal PricegunIntervalSeconds { get => N("pricegunIntervalSeconds"); set { Put("pricegunIntervalSeconds", value); Raise(nameof(PricegunIntervalInfo)); } }
    public decimal CacheMinutes { get => N("cacheMinutes"); set => Put("cacheMinutes", value); }
    public decimal MaxStaleHours { get => N("maxStaleHours"); set => Put("maxStaleHours", value); }
    public decimal TurnBudget { get => N("turnBudget"); set => Put("turnBudget", value); }
    public decimal HpStopPercent { get => N("hpStopPercent"); set => Put("hpStopPercent", value); }
    public decimal MaxCombatRounds { get => N("maxCombatRounds"); set => Put("maxCombatRounds", value); }

    public decimal DailyKnucklebonesTarget { get => N("dailyKnucklebonesTarget"); set => Put("dailyKnucklebonesTarget", value); }
    public decimal DailyRests { get => N("dailyRests"); set => Put("dailyRests", value); }
    public decimal DailyKnucklebonesTargetMin => Min("dailyKnucklebonesTarget"); public decimal DailyKnucklebonesTargetMax => Max("dailyKnucklebonesTarget");
    public decimal DailyRestsMin => Min("dailyRests"); public decimal DailyRestsMax => Max("dailyRests");
    public string DailyKnucklebonesTargetInfo => $"Daily knucklebones target (default {DesktopSettings.Definition("dailyKnucklebonesTarget").Default:N0}, {DailyKnucklebonesTargetMin:N0}–{DailyKnucklebonesTargetMax:N0})";
    public string DailyRestsInfo => $"Rests per daily run (default {DesktopSettings.Definition("dailyRests").Default:N0}, {DailyRestsMin:N0}–{DailyRestsMax:N0})";

    public decimal MeatPerDayMin => Min("meatPerDay"); public decimal MeatPerDayMax => Max("meatPerDay");
    public decimal MeatPerPurchaseMin => Min("meatPerPurchase"); public decimal MeatPerPurchaseMax => Max("meatPerPurchase");
    public decimal UnitsPerPurchaseMin => Min("unitsPerPurchase"); public decimal UnitsPerPurchaseMax => Max("unitsPerPurchase");
    public decimal ResultLimitMin => Min("resultLimit"); public decimal ResultLimitMax => Max("resultLimit");
    public decimal IntervalMin => Min("kolIntervalSeconds"); public decimal IntervalMax => Max("kolIntervalSeconds");
    public decimal CacheMinutesMin => Min("cacheMinutes"); public decimal CacheMinutesMax => Max("cacheMinutes");
    public decimal MaxStaleHoursMin => Min("maxStaleHours"); public decimal MaxStaleHoursMax => Max("maxStaleHours");
    public decimal TurnBudgetMin => Min("turnBudget"); public decimal TurnBudgetMax => Max("turnBudget");
    public decimal HpStopPercentMin => Min("hpStopPercent"); public decimal HpStopPercentMax => Max("hpStopPercent");
    public decimal MaxCombatRoundsMin => Min("maxCombatRounds"); public decimal MaxCombatRoundsMax => Max("maxCombatRounds");

    public string KolIntervalInfo => Describe("KoL request interval (seconds)", Settings.EffectiveKolInterval(_env));
    public string PricegunIntervalInfo => Describe("Pricegun request interval (seconds)", Settings.EffectivePricegunInterval(_env));

    private static string Describe(string label, EffectiveSetting e)
        => e.OverriddenByEnvironment
            ? $"{label}: effective {e.Value:0.###} s, set by environment variable {e.EnvironmentVariable} (overrides this setting)"
            : $"{label}: effective {e.Value:0.###} s";

    private decimal N(string key) => (decimal)Settings.Get(key);
    private static decimal Min(string key) => (decimal)DesktopSettings.Definition(key).Min;
    private static decimal Max(string key) => (decimal)DesktopSettings.Definition(key).Max;
    private void Put(string key, decimal value, [CallerMemberName] string? name = null) { Settings.Set(key, (double)value); Raise(name); }

    private void DoResetDefaults()
    {
        foreach (var d in DesktopSettings.Definitions) Settings.Set(d.Key, d.Default);
        Raise(string.Empty);
        Status = "Numbers are back at their defaults. Press Save to keep them.";
    }

    private void DoSave()
    {
        // The Daily tab owns the skipped steps and saves them on its own; take the stored list so saving the
        // settings never writes this page's older copy over a skip made since it was opened.
        try { Settings.SkippedSteps = new Dictionary<string, string>(_store.Load().SkippedSteps); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the save below reports it */ }
        try { _store.Save(Settings); Status = "Saved. Request intervals apply after a restart."; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Status = "Could not save: " + e.Message; }
    }

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private sealed class RelayCommand(Action run) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => run();
    }
}

/// <summary>Edits one user-selected consumable list (booze or spleen items); the list belongs to the settings object.</summary>
public sealed class ConsumableListEditor : INotifyPropertyChanged
{
    private readonly List<DailyConsumable> _list;
    private readonly Func<string, IReadOnlyList<(string Id, string Name)>>? _lookup;
    private string _item = "";
    private decimal _quantity = 1;
    private string _message = "";
    private DailyConsumable? _selected;

    private readonly KoLibri.Core.Spiel.ItemClassifier? _classifier;
    private readonly KoLibri.Core.Spiel.ItemOrgan _organ;

    public ConsumableListEditor(List<DailyConsumable> list, Func<string, IReadOnlyList<(string Id, string Name)>>? lookup = null,
        KoLibri.Core.Spiel.ItemClassifier? classifier = null, KoLibri.Core.Spiel.ItemOrgan organ = KoLibri.Core.Spiel.ItemOrgan.None)
    {
        _list = list;
        _lookup = lookup;
        _classifier = classifier;
        _organ = organ;
        Items = new System.Collections.ObjectModel.ObservableCollection<DailyConsumable>(list);
        Add = new RelayCommandImpl(DoAdd);
        Remove = new RelayCommandImpl(DoRemove);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public System.Collections.ObjectModel.ObservableCollection<DailyConsumable> Items { get; }
    public ICommand Add { get; }
    public ICommand Remove { get; }
    public decimal QuantityMin => 1;
    public decimal QuantityMax => DesktopSettings.MaxConsumableQuantity;
    /// <summary>An item id (digits) or an exact item name.</summary>
    public string Item { get => _item; set { _item = value ?? ""; Raise(); } }
    public decimal Quantity { get => _quantity; set { _quantity = Math.Clamp(value, QuantityMin, QuantityMax); Raise(); } }
    public string Message { get => _message; private set { _message = value; Raise(); } }
    public DailyConsumable? Selected { get => _selected; set { _selected = value; Raise(); } }

    private void DoAdd()
    {
        var text = Item.Trim();
        if (text.Length == 0) { Message = "Enter an item id or an exact item name."; return; }
        int id; string name = "";
        if (int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out id) && id > 0)
        {
            var found = _lookup?.Invoke(text);
            if (found is { Count: > 0 }) name = found[0].Name;
        }
        else
        {
            var found = _lookup?.Invoke(text);
            if (found is null) { Message = "Item names need the item catalog; enter the item id instead."; return; }
            if (found.Count == 0) { Message = $"No item named \"{text}\". Use the exact name or the id."; return; }
            if (found.Count > 1) { Message = $"\"{text}\" is ambiguous ({found.Count} items). Use the id."; return; }
            if (!int.TryParse(found[0].Id, out id)) { Message = "That item has no numeric id."; return; }
            name = found[0].Name;
        }
        if (Validate(id) is { } problem) { Message = problem; return; }
        if (!DesktopSettings.AddConsumable(_list, id, name, (int)Quantity))
        {
            Message = $"The list is full ({DesktopSettings.MaxConsumableEntries} entries).";
            return;
        }
        Sync();
        Item = "";
        Message = "Added. Press Save to keep the list.";
    }

    /// <summary>Null when the id may be saved in this list: the catalog must know it with the right organ
    /// (the Lucky Surprise Egg is food and can never be saved as booze or a spleen item).</summary>
    public string? Validate(int id)
    {
        if (_organ == KoLibri.Core.Spiel.ItemOrgan.None) return null;
        var label = _organ == KoLibri.Core.Spiel.ItemOrgan.Drink ? "booze" : "spleen item";
        if (id == DailyStepBoard.LuckySurpriseEggItemId)
            return "The Lucky Surprise Egg is food; it is eaten by its own daily step and cannot be saved as " + label + ".";
        if (_classifier is null) return null;
        var c = _classifier.Classify(id);
        if (!c.Known) return $"Item {id} is not in the item catalog, so it cannot be saved as {label}.";
        if (c.Organ != _organ) return $"{c.Name} is not a {label} in the catalog.";
        return null;
    }

    private void DoRemove()
    {
        if (_selected is null) { Message = "Select an entry to remove."; return; }
        _list.RemoveAll(x => x.ItemId == _selected.ItemId);
        Sync();
        Message = "Removed. Press Save to keep the list.";
    }

    private void Sync()
    {
        Items.Clear();
        foreach (var x in _list) Items.Add(x);
    }

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private sealed class RelayCommandImpl(Action run) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => run();
    }
}
