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

    public SettingsViewModel(DesktopSettingsStore store, Func<string, string?>? env = null, string? agentRightsNote = null)
    {
        _store = store;
        _env = env ?? Environment.GetEnvironmentVariable;
        Settings = store.Load();
        AgentRightsNote = agentRightsNote ?? "Agent rights are granted on the consent page of the MCP server and shown there; see docs/mcp-access.md.";
        Save = new RelayCommand(DoSave);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public DesktopSettings Settings { get; }
    public ICommand Save { get; }
    public string AgentRightsNote { get; }
    public string[] Themes { get; } = ["Light", "Dark", "System"];

    public string Status { get => _status; private set { _status = value; Raise(); } }

    public string? DefaultAccount { get => Settings.DefaultAccount; set { Settings.DefaultAccount = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); Raise(); } }
    public bool ReloginAutomatically { get => Settings.ReloginAutomatically; set { Settings.ReloginAutomatically = value; Raise(); } }
    public bool PricegunDisplay { get => Settings.PricegunDisplay; set { Settings.PricegunDisplay = value; Raise(); } }
    public bool DailyDefaults { get => Settings.DailyDefaults; set { Settings.DailyDefaults = value; Raise(); } }
    public bool OptionalTools { get => Settings.OptionalTools; set { Settings.OptionalTools = value; Raise(); } }
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

    private void DoSave()
    {
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
