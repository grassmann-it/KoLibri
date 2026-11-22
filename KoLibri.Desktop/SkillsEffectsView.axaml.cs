using Avalonia.Controls;
using Avalonia.Interactivity;

namespace KoLibri.Desktop;

/// <summary>View for the SkillsEffectsView tab area; its handlers forward to <see cref="ISkillsEffectsViewHandlers"/> until the tab worker moves them here.</summary>
public partial class SkillsEffectsView : UserControl
{
    private IShellHost? _host;
    private ISkillsEffectsViewHandlers? _handlers;

    public SkillsEffectsView() => InitializeComponent();

    /// <summary>The shell this view reports to (null until the shell attaches).</summary>
    public IShellHost? Host => _host;

    public void Attach(IShellHost host, ISkillsEffectsViewHandlers handlers)
    {
        _host = host;
        _handlers = handlers;
    }

    private void InitializeComponent() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);

    private void CastCharacterSkillClick(object? sender, RoutedEventArgs e) => _handlers?.CastCharacterSkillClick(sender, e);

    private void RestCharacterClick(object? sender, RoutedEventArgs e) => _handlers?.RestCharacterClick(sender, e);

    private void ApplyCharacterMoodClick(object? sender, RoutedEventArgs e) => _handlers?.ApplyCharacterMoodClick(sender, e);
}
