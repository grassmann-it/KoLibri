using Avalonia.Controls;

namespace KoLibri.Desktop;

/// <summary>The character pane (left frame): name, HP/MP, resources, familiar and effects. Filled by the shell's sidebar refresh.</summary>
public partial class CharpaneView : UserControl
{
    public CharpaneView() => InitializeComponent();

    public IShellHost? Host { get; private set; }

    public void Attach(IShellHost host) => Host = host;

    private void InitializeComponent() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
}
