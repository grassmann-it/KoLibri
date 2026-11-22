using Avalonia.Controls;
using KoLibri.Desktop.ContextMenus;

namespace KoLibri.Desktop;

/// <summary>The character pane (left frame): name, HP/MP, resources, familiar and effects. Filled by the shell's sidebar refresh.</summary>
public partial class CharpaneView : UserControl
{
    public CharpaneView() => InitializeComponent();

    public IShellHost? Host { get; private set; }

    public RowContextMenuController<EffectRow>? EffectMenu { get; private set; }

    public void Attach(IShellHost host)
    {
        Host = host;
        if (this.FindControl<ListBox>("SidebarEffectList") is { } list)
        {
            var menu = new EffectContextMenu(host);
            EffectMenu = RowContextMenuController<EffectRow>.Attach(list,
                row => menu.Build(new EffectMenuTarget(row.Name, row.Name, false, row.TurnsRemaining)));
        }
    }

    /// <summary>Shows a computed state; the only place that writes the pane's controls.</summary>
    public void Show(CharpaneState state)
    {
        SetText("SidebarName", state.Name);
        SetText("SidebarRole", state.Role);
        SetText("SidebarLevel", state.Level);
        SetText("SidebarHp", state.Hp);
        SetText("SidebarMp", state.Mp);
        SetBar("SidebarHpBar", state.HpFraction);
        SetBar("SidebarMpBar", state.MpFraction);
        SetText("SidebarAdventures", state.Adventures);
        SetText("SidebarMeat", state.Meat);
        SetText("SidebarFullness", state.Fullness);
        SetText("SidebarDrunk", state.Drunk);
        SetText("SidebarSpleen", state.Spleen);
        SetText("SidebarFamiliar", state.Familiar);
        SetText("SidebarEffects", state.EffectsText);
        if (this.FindControl<TextBlock>("SidebarMessage") is { } message)
        {
            message.Text = state.Message ?? "";
            message.IsVisible = state.Message is not null;
            message.Classes.Set("error", state.IsError);
            message.Foreground = state.IsError ? Avalonia.Media.Brushes.IndianRed : null;
        }
        if (this.FindControl<ListBox>("SidebarEffectList") is { } list)
        {
            list.ItemsSource = state.Effects;
            list.IsVisible = state.Effects.Count > 0;
        }
        if (this.FindControl<TextBlock>("SidebarEffects") is { } effects) effects.IsVisible = state.Effects.Count == 0;
    }

    private void SetText(string name, string text)
    {
        if (this.FindControl<TextBlock>(name) is { } block) block.Text = text;
    }

    private void SetBar(string name, double value)
    {
        if (this.FindControl<ProgressBar>(name) is { } bar) bar.Value = value;
    }

    private void InitializeComponent() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
}
