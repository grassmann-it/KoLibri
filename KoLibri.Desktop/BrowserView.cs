using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using KoLibri.Core.Basis;

namespace KoLibri.Desktop;

/// <summary>
/// KoLmafia-style relay browser inside the window: starts <see cref="RelayHost"/> in-process (127.0.0.1, free port, secret
/// token, redacted log) and shows it in a native web view (WebView2 / WKWebView / WebKitGTK through WebView.Avalonia).
/// The web view keeps its own cookie jar, so the per-run relay cookie never reaches other browsers. Without a usable
/// web view runtime the page explains that and offers the relay URL in the system browser instead.
/// </summary>
public sealed class BrowserView : UserControl, IDisposable
{
    private static readonly TimeSpan EngineWait = TimeSpan.FromSeconds(8);

    private AccountSessionManager? _accounts;
    private Func<AccountRole> _role = () => AccountRole.Main;
    private Action<string> _status = _ => { };
    private Action<string> _log = _ => { };
    private RelayHost? _host;
    private AccountRole? _hostRole;
    private Control? _web;
    private bool _engineSeen;
    private bool _disposed;
    private int _generation;

    private readonly Button _back = Tool("◀", "Back");
    private readonly Button _forward = Tool("▶", "Forward");
    private readonly Button _reload = Tool("⟳", "Reload");
    private readonly Button _adventure = Tool("Adventure", "Open adventure.php");
    private readonly Button _main = Tool("Main page", "Open main.php");
    private readonly Button _external = Tool("Open in system browser", "Open the relay URL in your default browser");
    private readonly TextBox _address = new() { MinWidth = 220, Watermark = "/main.php", VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ContentControl _body = new();
    private readonly TextBlock _message = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 640 };
    private readonly Border _messagePanel = new() { Padding = new Thickness(24), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };

    public BrowserView()
    {
        _address.SetValue(AutomationProperties_NameProperty(), "Relay address (paths on the relay only)");
        _address.KeyDown += (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Enter) return;
            e.Handled = true;
            if (_host is null) return;
            if (BrowserNavigationPolicy.ResolveTyped(_host.Origin, _address.Text) is { } path) NavigateToPath(path);
            else _status("The address bar only opens pages of the relay, for example /main.php.");
        };
        _back.Click += (_, _) => { if (_web is AvaloniaWebView.WebView w) w.GoBack(); };
        _forward.Click += (_, _) => { if (_web is AvaloniaWebView.WebView w) w.GoForward(); };
        _reload.Click += (_, _) => { if (_web is AvaloniaWebView.WebView w) w.Reload(); };
        _adventure.Click += (_, _) => NavigateToPath("/adventure.php");
        _main.Click += (_, _) => NavigateToPath("/main.php");
        _external.Click += (_, _) => OpenInSystemBrowser();

        var bar = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 8) };
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        left.Children.Add(_back); left.Children.Add(_forward); left.Children.Add(_reload);
        left.Children.Add(_adventure); left.Children.Add(_main);
        DockPanel.SetDock(left, Dock.Left);
        DockPanel.SetDock(_external, Dock.Right);
        bar.Children.Add(left);
        bar.Children.Add(_external);
        bar.Children.Add(new Border { Margin = new Thickness(8, 0), Child = _address });

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        _messagePanel.Child = _message;
        _body.Content = _messagePanel;
        root.Children.Add(new Border { BorderThickness = new Thickness(1), BorderBrush = Avalonia.Media.Brushes.Gray, Child = _body, MinHeight = 320 });
        Content = root;
        SetControlsEnabled(false);
        _message.Text = "The browser opens when you choose Browser in the top bar.";
    }

    private static AvaloniaProperty<string?> AutomationProperties_NameProperty()
        => Avalonia.Automation.AutomationProperties.NameProperty;

    private static Button Tool(string text, string tip)
    {
        var b = new Button { Content = text };
        ToolTip.SetTip(b, tip);
        b.SetValue(Avalonia.Automation.AutomationProperties.NameProperty, tip);
        return b;
    }

    public void Attach(AccountSessionManager accounts, Func<AccountRole> role, Action<string> status, Action<string> log)
    {
        _accounts = accounts; _role = role; _status = status; _log = log;
    }

    /// <summary>Starts (or restarts for another role) the relay and loads the main page.</summary>
    public async Task OpenAsync()
    {
        if (_accounts is null || _disposed) return;
        var role = _role();
        if (_host is not null && _hostRole == role) return; // already running for this role
        StopHost();
        var generation = ++_generation;
        ShowMessage($"Starting the relay for {role}…");
        try
        {
            var host = await RelayHost.StartAsync(_accounts, role);
            if (generation != _generation || _disposed) { host.Dispose(); return; }
            _host = host; _hostRole = role;
            _log($"Relay browser started for {role} on {host.Origin} (token not logged).");
        }
        catch (InvalidOperationException exception)
        {
            ShowMessage($"{exception.Message} Use the Sign in button, then open the browser again.");
            return;
        }
        catch (Exception exception)
        {
            ShowMessage($"The relay could not start ({exception.GetType().Name}). Use Sign in, then try again.");
            return;
        }
        ShowWebView();
        NavigateToPath("/main.php");
    }

    private void ShowWebView()
    {
        _engineSeen = false;
        if (Environment.GetEnvironmentVariable("KOLIBRI_NO_WEBVIEW") == "1")
        {
            ShowMessage(EngineMissingText("the web view is turned off (KOLIBRI_NO_WEBVIEW=1)"));
            return;
        }
        try
        {
            var web = new AvaloniaWebView.WebView();
            web.WebViewCreated += (_, _) => Dispatcher.UIThread.Post(() => { _engineSeen = true; });
            web.NavigationStarting += (_, e) =>
            {
                var origin = _host?.Origin;
                if (origin is null) { e.Cancel = true; return; }
                var url = e.Url?.ToString();
                switch (BrowserNavigationPolicy.Decide(origin, url))
                {
                    case BrowserNavigationDecision.Allow:
                        var shown = BrowserNavigationPolicy.DisplayPath(origin, url);
                        Dispatcher.UIThread.Post(() => { _engineSeen = true; _address.Text = shown; UpdateNavButtons(); });
                        break;
                    case BrowserNavigationDecision.AskExternal:
                        e.Cancel = true;
                        if (url is not null) Dispatcher.UIThread.Post(async () => await AskOpenExternalAsync(url));
                        break;
                    default:
                        e.Cancel = true;
                        break;
                }
            };
            web.NavigationCompleted += (_, _) => Dispatcher.UIThread.Post(() => { _engineSeen = true; UpdateNavButtons(); });
            web.WebViewNewWindowRequested += (_, e) =>
            {
                // A new window is never opened here; same-origin targets load in this view, others ask first.
                e.UrlLoadingStrategy = WebViewCore.Enums.UrlRequestStrategy.CancelLoad;
                var origin = _host?.Origin; var url = e.Url?.ToString();
                if (origin is null || url is null) return;
                switch (BrowserNavigationPolicy.Decide(origin, url))
                {
                    case BrowserNavigationDecision.Allow:
                        Dispatcher.UIThread.Post(() => { if (BrowserNavigationPolicy.ResolveTyped(origin, url) is { } p) NavigateToPath(p); });
                        break;
                    case BrowserNavigationDecision.AskExternal:
                        Dispatcher.UIThread.Post(async () => await AskOpenExternalAsync(url));
                        break;
                }
            };
            _web = web;
            _body.Content = web;
            SetControlsEnabled(true);
            _ = WatchEngineAsync(_generation);
        }
        catch (Exception exception)
        {
            _web = null;
            ShowMessage(EngineMissingText($"the web view could not be created ({exception.GetType().Name})"));
        }
    }

    private async Task WatchEngineAsync(int generation)
    {
        await Task.Delay(EngineWait);
        if (generation != _generation || _disposed || _engineSeen || _web is null) return;
        _web = null;
        ShowMessage(EngineMissingText("no web view runtime answered"));
    }

    private static string EngineMissingText(string reason) =>
        $"The in-app browser is not available here: {reason}. " +
        "It needs WebView2 (Windows), WKWebView (macOS) or WebKitGTK 4.1 / GTK 3 (Linux). " +
        "You can still use the relay: “Open in system browser” opens it in your default browser. " +
        "Note that browsers share cookies between local ports, so do not browse other local servers with that profile while the relay runs.";

    private void NavigateToPath(string path)
    {
        if (_host is null) return;
        if (_web is AvaloniaWebView.WebView web)
        {
            // The first load goes through the secret enter URL; after that the cookie is in the view's own jar.
            web.Url = new Uri(_host.EnterUrl(path));
            _status($"Browser: {path}");
        }
    }

    private void UpdateNavButtons()
    {
        if (_web is AvaloniaWebView.WebView w)
        {
            _back.IsEnabled = w.IsCanGoBack;
            _forward.IsEnabled = w.IsCanGoForward;
        }
    }

    private void SetControlsEnabled(bool on)
    {
        foreach (var b in new[] { _back, _forward, _reload, _adventure, _main }) b.IsEnabled = on;
        _address.IsEnabled = on;
        _external.IsEnabled = true;
    }

    private void ShowMessage(string text)
    {
        _message.Text = text;
        _body.Content = _messagePanel;
        SetControlsEnabled(false);
    }

    private void OpenInSystemBrowser()
    {
        _ = OpenInSystemBrowserAsync();
    }

    private async Task OpenInSystemBrowserAsync()
    {
        try
        {
            if (_host is null) await OpenAsync();
            if (_host is null) return;
            // The URL carries this run's secret; it goes to the default browser only, never into a log.
            Process.Start(new ProcessStartInfo(_host.EnterUrl("/main.php")) { UseShellExecute = true });
            _status("The relay was opened in your system browser.");
        }
        catch (Exception exception)
        {
            _status($"Could not open the system browser ({exception.GetType().Name}).");
        }
    }

    private async Task AskOpenExternalAsync(string url)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "another site";
        var open = false;
        var dialog = new Window
        {
            Title = "Open link in your browser?", Width = 460, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false
        };
        var yes = new Button { Content = "Open in system browser" };
        var no = new Button { Content = "Stay here", IsDefault = true };
        yes.Click += (_, _) => { open = true; dialog.Close(); };
        no.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 14,
            Children =
            {
                new TextBlock { Text = $"The page wants to open {host}. The in-app browser only shows the relay. Open that site in your normal browser instead?", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { no, yes } }
            }
        };
        await dialog.ShowDialog(owner);
        if (!open) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception exception) { _status($"Could not open the system browser ({exception.GetType().Name})."); }
    }

    private void StopHost()
    {
        _generation++;
        _web = null;
        _host?.Dispose();
        _host = null; _hostRole = null;
        _address.Text = "";
    }

    /// <summary>Called when the selected account changes: the next open starts a fresh relay for the new role.</summary>
    public void ResetForRoleChange()
    {
        if (_host is null || _hostRole == _role()) return;
        StopHost();
        ShowMessage("The account changed. Open the browser again to start a relay for it.");
    }

    public void Dispose()
    {
        _disposed = true;
        StopHost();
    }
}
