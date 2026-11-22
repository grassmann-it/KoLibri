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
    private Action? _signIn;
    private Func<bool> _autoLogin = () => true;
    private RelayHost? _host;
    private AccountRole? _hostRole;
    private string? _hostBinding;
    private Control? _web;
    private bool _engineSeen;
    private bool _navigated;
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
    private readonly Button _signInButton = new() { Content = "Sign in", IsVisible = false };
    private readonly Button _retryButton = new() { Content = "Try again", IsVisible = false };
    private readonly Button _systemButton = new() { Content = "Open in system browser", IsVisible = false };

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
        _signInButton.SetValue(Avalonia.Automation.AutomationProperties.NameProperty, "Sign in to use the browser");
        _signInButton.Click += (_, _) => _signIn?.Invoke();
        _systemButton.Click += (_, _) => OpenInSystemBrowser();
        _retryButton.Click += async (_, _) =>
        {
            try { StopHost(); await OpenAsync(); }
            catch (Exception exception) { ShowMessage($"The browser could not open ({exception.GetType().Name}).", offerSignIn: _signIn is not null, retry: true); }
        };
        _messagePanel.Child = new StackPanel
        {
            Spacing = 12,
            Children = { _message, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _signInButton, _retryButton, _systemButton } } }
        };
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

    public void Attach(AccountSessionManager accounts, Func<AccountRole> role, Action<string> status, Action<string> log,
        Action? signIn = null, Func<bool>? autoLogin = null)
    {
        _accounts = accounts; _role = role; _status = status; _log = log;
        _signIn = signIn;
        if (autoLogin is not null) _autoLogin = autoLogin;
    }

    /// <summary>Starts (or restarts for another role) the relay and loads the main page.</summary>
    public async Task OpenAsync()
    {
        if (_accounts is null || _disposed) return;
        var role = _role();
        // Reuse only while the role is still bound to the same player and login cookie (BindingKey).
        var generation = _generation;
        try
        {
            var accounts = _accounts;
            var (sessionTimedOut, current) = await BrowserTimeouts.RunAsync(_ => accounts.GetSessionAsync(role, includeExpired: true), BrowserTimeouts.Gate);
            if (sessionTimedOut) { ShowTimeout("Reading the stored session"); return; }
            if (_host is not null && _hostRole == role && _hostBinding is not null && _hostBinding == current?.BindingKey()) return;
            StopHost();
            generation = ++_generation;
            ShowMessage($"Checking the {role} session…");
            // Not signed in: renew from the stored password when automatic log-in is on, else offer Sign in.
            var auto = _autoLogin();
            var (gateTimedOut, gate) = await BrowserTimeouts.RunAsync(ct => BrowserGate.CheckAsync(accounts, role, auto, null, ct), BrowserTimeouts.Gate);
            if (generation != _generation || _disposed) return;
            if (gateTimedOut || gate is null) { ShowTimeout("Checking or renewing the session"); return; }
            if (generation != _generation || _disposed) return;
            if (!gate.CanStart)
            {
                ShowMessage(gate.Message, offerSignIn: _signIn is not null);
                _status(gate.Message);
                return;
            }
            ShowMessage($"Starting the relay for {role}…");
            var (startTimedOut, started) = await BrowserTimeouts.RunAsync(_ => accounts.GetSessionAsync(role), BrowserTimeouts.Gate);
            if (startTimedOut) { ShowTimeout("Reading the session"); return; }
            if (started is null) throw new InvalidOperationException($"{role} is not logged in. Log in first.");
            var binding = started.BindingKey();
            var startedSession = started;
            var host = await Task.Run(() => RelayHost.Start(startedSession, sessionCheck: RelayHost.SessionBindingCheck(accounts, role, binding)));
            if (generation != _generation || _disposed) { host.Dispose(); return; }
            _host = host; _hostRole = role; _hostBinding = started.BindingKey();
            _log($"Relay browser started for {role} on {host.Origin} (token not logged).");
        }
        catch (InvalidOperationException exception)
        {
            ShowMessage($"{exception.Message}", offerSignIn: _signIn is not null, retry: true);
            return;
        }
        catch (Exception exception)
        {
            ShowMessage($"The relay could not start ({exception.GetType().Name}).", offerSignIn: _signIn is not null, retry: true);
            return;
        }
        if (BrowserModePolicy.Current() == BrowserMode.SystemBrowser)
        {
            ShowMessage("The browser opens in your system browser on Linux, because the embedded WebKitGTK view can hang the app. " +
                "Use “Open in system browser” below. Set KOLIBRI_EMBED_WEBVIEW=1 to try the embedded view anyway. " +
                "Note that browsers share cookies between local ports, so do not browse other local servers with that profile while the relay runs.", systemBrowser: true);
            return;
        }
        ShowWebView();
        NavigateToPath("/main.php");
    }

    /// <summary>UiQa only: renders one of the tab states without a relay.</summary>
    public void ShowStateForQa(string state)
    {
        switch (state)
        {
            case "opening": ShowMessage("Starting the relay for Main…"); break;
            case "error": ShowTimeout("Checking or renewing the session"); break;
            case "system": ShowMessage("The browser opens in your system browser on Linux, because the embedded WebKitGTK view can hang the app. Use “Open in system browser” below. Set KOLIBRI_EMBED_WEBVIEW=1 to try the embedded view anyway.", systemBrowser: true); break;
        }
    }

    private void ShowTimeout(string what) =>
        ShowMessage($"{what} took longer than {BrowserTimeouts.Gate.TotalSeconds:0} s, so the browser was not opened. KoL may be slow or unreachable.",
            offerSignIn: _signIn is not null, retry: true, systemBrowser: true);

    private void ShowWebView()
    {
        _engineSeen = false;
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
            web.NavigationCompleted += (_, _) => Dispatcher.UIThread.Post(() => { _engineSeen = true; _navigated = true; UpdateNavButtons(); });
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
            _back.IsEnabled = false; _forward.IsEnabled = false; // nothing to go back to before the first page
            _ = WatchEngineAsync(_generation);
            ShowOpeningOverlay();
        }
        catch (Exception exception)
        {
            _web = null;
            ShowMessage(EngineMissingText($"the web view could not be created ({exception.GetType().Name})"), retry: true);
        }
    }

    private void ShowOpeningOverlay() { _status("Opening the browser…"); }

    /// <summary>Watchdog: a view that never reports created/navigated within the first-navigation limit is dropped for an error.</summary>
    private async Task WatchEngineAsync(int generation)
    {
        _navigated = false;
        var never = await BrowserTimeouts.NeverSeenAsync(() => _navigated || generation != _generation || _disposed,
            BrowserTimeouts.FirstNavigation, TimeSpan.FromMilliseconds(250));
        if (!never || generation != _generation || _disposed) return;
        _web = null;
        ShowMessage(EngineMissingText(_engineSeen ? "the page did not finish loading in time" : "no web view runtime answered"),
            retry: true, systemBrowser: true);
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

    private void ShowMessage(string text, bool offerSignIn = false, bool retry = false, bool systemBrowser = false)
    {
        _message.Text = text;
        _signInButton.IsVisible = offerSignIn;
        _retryButton.IsVisible = retry;
        _systemButton.IsVisible = systemBrowser;
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
            if (_host is null) { _status("Sign in first: the relay needs a KoL session."); return; }
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
        _host = null; _hostRole = null; _hostBinding = null;
        _address.Text = "";
    }

    /// <summary>Called when the selected account changes: the next open starts a fresh relay for the new role.</summary>
    public void ResetForRoleChange()
    {
        if (_host is null || _hostRole == _role()) return;
        StopHost();
        ShowMessage("The account changed. Open the browser again to start a relay for it.");
    }

    /// <summary>Stops the running relay (its cookie belongs to the previous login); the next open starts a fresh one.</summary>
    public void StopForLogin() => StopHost();

    public void Dispose()
    {
        _disposed = true;
        StopHost();
    }
}
