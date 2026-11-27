using Avalonia.Controls;

namespace KoLibri.Desktop;

/// <summary>
/// The native web view behind the Browser tab, whatever the platform backend. Events may arrive on a non-UI thread;
/// <see cref="AllowNavigation"/> is asked synchronously for every navigation and must not block.
/// </summary>
public interface IEmbeddedBrowser
{
    Control View { get; }
    Func<string, bool> AllowNavigation { get; set; }
    /// <summary>A new window the backend did not already route through <see cref="AllowNavigation"/> (WebView.Avalonia only).</summary>
    event Action<string>? NewWindowRequested;
    event Action<string>? NavigationCompleted;
    event Action<string>? Failed;
    event Action? Created;
    bool CanGoBack { get; }
    bool CanGoForward { get; }
    void Navigate(Uri uri);
    void GoBack();
    void GoForward();
    void Reload();
}
