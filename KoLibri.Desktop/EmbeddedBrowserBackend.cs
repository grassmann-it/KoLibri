using Avalonia;
using Avalonia.Controls;
using Avalonia.WebView.Desktop;

namespace KoLibri.Desktop;

/// <summary>
/// Picks and starts the native web view behind the Browser tab and keeps one diagnostic line about it
/// (<see cref="Report"/>): which backend, whether it initialized, and the error if not.
/// Linux: own WebKitGTK host (<see cref="WebKitGtkView"/>; WebView.Avalonia's Linux part blocks the UI thread forever
/// when its control is created). Windows / macOS: WebView.Avalonia (WebView2 / WKWebView).
/// </summary>
public static class EmbeddedBrowserBackend
{
    private static volatile string _report = "Embedded browser: not initialized yet.";

    public static string Report => _report;

    /// <summary>Raised once when the backend's start-up result is known (any thread).</summary>
    public static event Action<string>? Reported;

    internal static string BackendName(bool isLinux) => isLinux ? "WebKitGTK (own GTK thread)" : "WebView.Avalonia (WebView2 / WKWebView)";

    internal static string Describe(string backend, bool ok, string? detail) =>
        ok ? $"Embedded browser: {backend}, initialized{(string.IsNullOrEmpty(detail) ? "" : $" ({detail})")}."
           : $"Embedded browser: {backend}, NOT available: {detail}. The Browser tab offers the system browser instead.";

    /// <summary>Called from <c>Program.BuildAvaloniaApp</c>: registers / starts the backend and logs the result to stderr.</summary>
    public static AppBuilder Initialize(AppBuilder builder)
    {
        if (BrowserModePolicy.Current() == BrowserMode.SystemBrowser)
        {
            Publish("Embedded browser: off (KOLIBRI_NO_WEBVIEW=1 or KOLIBRI_EMBED_WEBVIEW=0); the Browser tab uses the system browser.");
            return builder;
        }
        var backend = BackendName(OperatingSystem.IsLinux());
        if (OperatingSystem.IsLinux())
        {
            // GTK starts on its own thread; the UI thread never waits for it here.
            _ = Task.Run(() =>
            {
                var ok = GtkHost.EnsureStarted(out var error);
                Publish(Describe(backend, ok, ok ? $"{GtkHost.Library}, X11" : error));
            });
            return builder;
        }
        try
        {
            builder = builder.UseDesktopWebView();
            Publish(Describe(backend, true, null));
        }
        catch (Exception exception)
        {
            Publish(Describe(backend, false, $"{exception.GetType().Name}: {exception.Message}"));
        }
        return builder;
    }

    public static IEmbeddedBrowser Create() => OperatingSystem.IsLinux() ? new WebKitGtkView() : new WebViewAvaloniaBrowser();

    private static void Publish(string line)
    {
        _report = line;
        Console.Error.WriteLine(line);
        Reported?.Invoke(line);
    }
}

/// <summary>Windows / macOS: WebView.Avalonia behind <see cref="IEmbeddedBrowser"/>.</summary>
internal sealed class WebViewAvaloniaBrowser : IEmbeddedBrowser
{
    private readonly AvaloniaWebView.WebView _web = new();

    public WebViewAvaloniaBrowser()
    {
        _web.WebViewCreated += (_, _) => Created?.Invoke();
        _web.NavigationStarting += (_, e) =>
        {
            if (!AllowNavigation(e.Url?.ToString() ?? "")) e.Cancel = true;
        };
        _web.NavigationCompleted += (_, _) => NavigationCompleted?.Invoke(_web.Url?.ToString() ?? "");
        _web.WebViewNewWindowRequested += (_, e) =>
        {
            e.UrlLoadingStrategy = WebViewCore.Enums.UrlRequestStrategy.CancelLoad;
            if (e.Url?.ToString() is { } url) NewWindowRequested?.Invoke(url);
        };
    }

    public Control View => _web;
    public Func<string, bool> AllowNavigation { get; set; } = _ => true;
    public event Action<string>? NewWindowRequested;
    public event Action<string>? NavigationCompleted;
#pragma warning disable CS0067 // WebView.Avalonia reports no creation failure; the Browser tab's watchdog covers it
    public event Action<string>? Failed;
#pragma warning restore CS0067
    public event Action? Created;
    public bool CanGoBack => _web.IsCanGoBack;
    public bool CanGoForward => _web.IsCanGoForward;
    public void Navigate(Uri uri) => _web.Url = uri;
    public void GoBack() => _web.GoBack();
    public void GoForward() => _web.GoForward();
    public void Reload() => _web.Reload();
}
