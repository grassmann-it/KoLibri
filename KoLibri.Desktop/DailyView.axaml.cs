using Avalonia.Controls;
using Avalonia.Interactivity;

namespace KoLibri.Desktop;

/// <summary>View for the DailyView tab area; its handlers forward to <see cref="IDailyViewHandlers"/> until the tab worker moves them here.</summary>
public partial class DailyView : UserControl
{
    private IShellHost? _host;
    private IDailyViewHandlers? _handlers;

    public DailyView() => InitializeComponent();

    /// <summary>The shell this view reports to (null until the shell attaches).</summary>
    public IShellHost? Host => _host;

    public void Attach(IShellHost host, IDailyViewHandlers handlers)
    {
        _host = host;
        _handlers = handlers;
    }

    private void InitializeComponent() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);

    private void DailySelectionChanged(object? sender, SelectionChangedEventArgs e) => _handlers?.DailySelectionChanged(sender, e);

    private void DailyRunClick(object? sender, RoutedEventArgs e) => _handlers?.DailyRunClick(sender, e);
}
