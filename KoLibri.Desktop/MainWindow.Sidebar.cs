using Avalonia.Controls;
using Avalonia.Threading;

namespace KoLibri.Desktop;

/// <summary>
/// The character pane on the left, modelled on the KoL browser's frame.
/// It reads the cached status snapshot through the shared provider (the
/// Adventures page uses the same one, so a refresh costs one fetch) and is
/// refreshed after every operation. Signed out, it shows placeholders.
/// </summary>
public sealed partial class MainWindow
{
    private int _sidebarVersion;

    private void ShowCharpane(CharpaneState state, Status? status = null)
    {
        _charpaneView?.Show(state);
        _skillsEffectsModel?.ApplyStatus(status);
    }

    /// <summary>Reloads the pane for the selected account; never throws.</summary>
    private async Task RefreshSidebarAsync(bool forceRefresh = false)
    {
        var version = ++_sidebarVersion;
        try
        {
            var status = await _live.LoadStatusAsync(_selectedRole, forceRefresh, CancellationToken.None);
            if (version != _sidebarVersion) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (status is null) ShowCharpane(CharpaneState.SignedOut(_selectedRole.ToString()));
                else ShowCharpane(CharpaneState.FromStatus(status, _selectedRole.ToString()), status);
            });
        }
        catch (Exception exception)
        {
            if (version == _sidebarVersion)
            {
                var role = _selectedRole;
                await Dispatcher.UIThread.InvokeAsync(() => ShowCharpane(CharpaneState.Failed(role.ToString(), exception)));
                _log.Error($"Character pane: {CharpaneState.DescribeError(exception)}");
            }
        }
    }
}
