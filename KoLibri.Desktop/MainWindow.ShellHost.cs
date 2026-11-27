
namespace KoLibri.Desktop;

/// <summary>The <see cref="IShellHost"/> side of the main window: what views and context menus may ask of the shell.</summary>
public partial class MainWindow
{
    AccountRole IShellHost.ActiveRole => _selectedRole;

    SessionLogViewModel IShellHost.SessionLog => _log;

    void IShellHost.ShowMessage(string message) => SetStatus(message);

    Task IShellHost.NavigateToAsync(string section) => OpenSectionAsync(section);

    async Task IShellHost.RefreshStatusAsync(bool forceRefresh)
    {
        await RefreshSectionAsync(forceRefresh);
        await RefreshSidebarAsync(forceRefresh);
    }

    async Task IShellHost.SearchMallAsync(string term)
    {
        await OpenSectionAsync("Mall & Shop");
        if (_mallSearchModel is null || string.IsNullOrWhiteSpace(term)) return;
        _mallSearchModel.SearchText = term;
        await _mallSearchModel.SearchAsync();
    }

    async Task IShellHost.CopyTextAsync(string text)
    {
        if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    async Task IShellHost.OpenUrlAsync(Uri uri)
    {
        await Launcher.LaunchUriAsync(uri);
    }
}
