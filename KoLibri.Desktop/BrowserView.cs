using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace KoLibri.Desktop;

/// <summary>
/// KoLmafia-style relay browser inside the window: starts the library relay (<see cref="KolibriSession.StartRelayAsync"/>) in-process (127.0.0.1, free port, secret
/// token, redacted log) and shows it in a native web view (<see cref="EmbeddedBrowserBackend"/>: own WebKitGTK host on Linux,
/// WebView2 / WKWebView through WebView.Avalonia on Windows / macOS).
/// The web view keeps its own cookie jar, so the per-run relay cookie never reaches other browsers. Without a usable
/// web view runtime the page explains that and offers the relay URL in the system browser instead.
/// </summary>
public sealed class BrowserView : UserControl, IDisposable
{

    private DesktopAccounts? _accounts;
    private Func<AccountRole> _role = () => AccountRole.Main;
    private Action<string> _status = _ => { };
    private Action<string> _log = _ => { };
    private Action? _signIn;
    private Func<bool> _autoLogin = () => true;
    private RelayServer? _host;
    private string _hostOrigin = "";
    private AccountRole? _hostRole;
    private KolibriSession? _hostBinding;
    private IEmbeddedBrowser? _web;
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
            if (BrowserNavigationPolicy.ResolveTyped(_hostOrigin, _address.Text) is { } path) NavigateToPath(path);
            else _status("The address bar only opens pages of the relay, for example /main.php.");
        };
        _back.Click += (_, _) => _web?.GoBack();
        _forward.Click += (_, _) => _web?.GoForward();
        _reload.Click += (_, _) => _web?.Reload();
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

    public void Attach(DesktopAccounts accounts, Func<AccountRole> role, Action<string> status, Action<string> log,
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
            // The binding is the session object: a new login replaces it in DesktopAccounts (no request here).
            var (sessionTimedOut, current) = await BrowserTimeouts.RunAsync(_ => CurrentSessionAsync(accounts, role), BrowserTimeouts.Gate);
            if (sessionTimedOut) { ShowTimeout("Reading the stored session"); return; }
            if (_host is not null && _hostRole == role && _hostBinding is not null && ReferenceEquals(_hostBinding, current)) return;
            StopHost();
            generation = ++_generation;
            ShowMessage($"Checking the {role} session…");
            // Not signed in: renew from the stored password when automatic log-in is on, else offer Sign in.
            var auto = _autoLogin();
            var (gateTimedOut, gate) = await BrowserTimeouts.RunAsync(ct => BrowserGate.CheckAsync(accounts, role, auto, ct), BrowserTimeouts.Gate);
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
            var (startTimedOut, started) = await BrowserTimeouts.RunAsync(ct => accounts.TryOpenAsync(role, ct), BrowserTimeouts.Gate);
            if (startTimedOut) { ShowTimeout("Reading the session"); return; }
            if (started is null) throw new InvalidOperationException($"{role} is not logged in. Log in first.");
            var startedSession = started;
            var host = await Task.Run(() => startedSession.StartRelayAsync(new RelayOptions
            {
                SessionCheck = SessionBindingCheck(accounts, role, startedSession)
            }));
            if (generation != _generation || _disposed) { DisposeInBackground(host); return; }
            _host = host; _hostRole = role; _hostBinding = startedSession;
            _hostOrigin = host.BaseUri.GetLeftPart(UriPartial.Authority);
            _log($"Relay browser started for {role} on {_hostOrigin} (token not logged).");
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
            ShowMessage(SystemBrowserText + " " +
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
            case "system": ShowMessage(SystemBrowserText, systemBrowser: true); break;
            case "unavailable": ShowMessage(EngineMissingText("libwebkit2gtk-4.1.so.0 not found"), retry: true, systemBrowser: true); break;
        }
    }

    private const string SystemBrowserText =
        "The embedded browser is switched off (KOLIBRI_NO_WEBVIEW=1 or KOLIBRI_EMBED_WEBVIEW=0), so the relay opens in your system browser. " +
        "Use “Open in system browser” below.";

    private void ShowTimeout(string what) =>
        ShowMessage($"{what} took longer than {BrowserTimeouts.Gate.TotalSeconds:0} s, so the browser was not opened. KoL may be slow or unreachable.",
            offerSignIn: _signIn is not null, retry: true, systemBrowser: true);

    private void ShowWebView()
    {
        _engineSeen = false;
        _log(EmbeddedBrowserBackend.Report);
        try
        {
            var web = EmbeddedBrowserBackend.Create();
            var generation = _generation;
            web.Created += () => Dispatcher.UIThread.Post(() => { _engineSeen = true; });
            // Asked for every navigation, on the engine's thread: pure policy check, never blocks.
            var origin = _hostOrigin;
            web.AllowNavigation = url =>
            {
                switch (BrowserNavigationPolicy.Decide(origin, url))
                {
                    case BrowserNavigationDecision.Allow:
                        Dispatcher.UIThread.Post(() => { if (generation == _generation) _engineSeen = true; });
                        return true;
                    case BrowserNavigationDecision.AskExternal:
                        Dispatcher.UIThread.Post(async () => { if (generation == _generation) await AskOpenExternalAsync(url); });
                        return false;
                    default:
                        return false;
                }
            };
            // The address bar follows the top-level page (KoL's frames load through the policy above, too).
            web.NavigationCompleted += url => Dispatcher.UIThread.Post(() =>
            {
                if (generation != _generation) return;
                _engineSeen = true; _navigated = true; UpdateNavButtons();
                var shown = BrowserNavigationPolicy.DisplayPath(origin, url);
                if (shown.Length > 0) _address.Text = shown;
            });
            // A new window is never opened here; same-origin targets load in this view, others ask first.
            web.NewWindowRequested += url => Dispatcher.UIThread.Post(async () =>
            {
                if (generation != _generation) return;
                switch (BrowserNavigationPolicy.Decide(origin, url))
                {
                    case BrowserNavigationDecision.Allow:
                        if (BrowserNavigationPolicy.ResolveTyped(origin, url) is { } p) NavigateToPath(p);
                        break;
                    case BrowserNavigationDecision.AskExternal:
                        await AskOpenExternalAsync(url);
                        break;
                }
            });
            web.Failed += reason => Dispatcher.UIThread.Post(() =>
            {
                if (generation != _generation) return;
                _log($"Embedded browser failed: {reason}");
                _web = null;
                ShowMessage(EngineMissingText(reason), retry: true, systemBrowser: true);
            });
            _web = web;
            _body.Content = web.View;
            SetControlsEnabled(true);
            _back.IsEnabled = false; _forward.IsEnabled = false; // nothing to go back to before the first page
            _ = WatchEngineAsync(_generation);
            ShowOpeningOverlay();
        }
        catch (Exception exception)
        {
            _web = null;
            _log($"Embedded browser could not be created: {exception.GetType().Name}: {exception.Message}");
            ShowMessage(EngineMissingText($"the web view could not be created ({exception.GetType().Name})"), retry: true, systemBrowser: true);
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
        "It needs WebView2 (Windows), WKWebView (macOS) or WebKitGTK 4.1 (or 4.0) with GTK 3 on X11/XWayland (Linux, package webkit2gtk-4.1). " +
        "You can still use the relay: “Open in system browser” opens it in your default browser. " +
        "Note that browsers share cookies between local ports, so do not browse other local servers with that profile while the relay runs.";

    private void NavigateToPath(string path)
    {
        if (_host is null) return;
        if (_web is { } web)
        {
            // The first load goes through the secret enter URL; after that the cookie is in the view's own jar.
            web.Navigate(_host.EntryUri(path));
            _status($"Browser: {path}");
        }
    }

    private void UpdateNavButtons()
    {
        if (_web is { } w)
        {
            _back.IsEnabled = w.CanGoBack;
            _forward.IsEnabled = w.CanGoForward;
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
            Process.Start(new ProcessStartInfo(_host.EntryUri("/main.php").ToString()) { UseShellExecute = true });
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
        if (_host is { } host) DisposeInBackground(host);
        _host = null; _hostRole = null; _hostBinding = null; _hostOrigin = "";
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

    /// <summary>The cached session of the role without sending anything (null when signed out or expired).</summary>
    private static async Task<KolibriSession?> CurrentSessionAsync(DesktopAccounts accounts, AccountRole role)
        => await accounts.HasExpiredSessionAsync(role).ConfigureAwait(false) ? null
            : await accounts.FindAsync(role).ConfigureAwait(false) is { HasSession: true } ? await accounts.TryOpenAsync(role).ConfigureAwait(false) : null;

    /// <summary>
    /// Per relayed request: the role must still hold the session the relay started with (a new sign-in replaces it).
    /// Checked at most once a second; null while it matches, else a clear message.
    /// </summary>
    internal static Func<Task<string?>> SessionBindingCheck(DesktopAccounts accounts, AccountRole role, KolibriSession binding)
    {
        var gate = new object(); long checkedAt = 0; string? last = null;
        return async () =>
        {
            var now = Environment.TickCount64;
            lock (gate) { if (checkedAt != 0 && now - checkedAt < 1000) return last; }
            var account = await accounts.FindAsync(role).ConfigureAwait(false);
            var current = account is { HasSession: true } ? await accounts.TryOpenAsync(role).ConfigureAwait(false) : null;
            var msg = current is not null && ReferenceEquals(current, binding) ? null
                : $"The {role} login changed since this relay started. Close this tab and open the browser again.";
            lock (gate) { checkedAt = Math.Max(1, now); last = msg; }
            return msg;
        };
    }

    /// <summary>Stopping the relay never blocks the UI thread (the old freeze): it is released on the thread pool.</summary>
    private static void DisposeInBackground(RelayServer host)
        => _ = Task.Run(async () => { try { await host.DisposeAsync().ConfigureAwait(false); } catch { /* already gone */ } });
}
