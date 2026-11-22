using Avalonia.Controls;
using Avalonia.Threading;
using KoLibri.Core.Spiel;

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

    private T? Side<T>(string name) where T : Control => this.FindControl<T>(name);

    private void SetSide(string name, string text)
    {
        if (Side<TextBlock>(name) is { } block) block.Text = text;
    }

    private void SetSideBar(string name, int current, int maximum)
    {
        if (Side<ProgressBar>(name) is { } bar)
            bar.Value = maximum > 0 ? Math.Clamp((double)current / maximum, 0, 1) : 0;
    }

    private void ShowSidebarOffline(string reason)
    {
        SetSide("SidebarName", "Not signed in");
        SetSide("SidebarRole", $"{_selectedRole} · {reason}");
        SetSide("SidebarLevel", "Level —");
        SetSide("SidebarHp", "HP —");
        SetSide("SidebarMp", "MP —");
        SetSideBar("SidebarHpBar", 0, 0);
        SetSideBar("SidebarMpBar", 0, 0);
        SetSide("SidebarAdventures", "Adventures —");
        SetSide("SidebarMeat", "Meat —");
        SetSide("SidebarFullness", "Fullness —");
        SetSide("SidebarDrunk", "Drunkenness —");
        SetSide("SidebarSpleen", "Spleen —");
        SetSide("SidebarFamiliar", "None");
        SetSide("SidebarEffects", "None");
    }

    private void ShowSidebarStatus(KoLStatusSnapshot status)
    {
        SetSide("SidebarName", status.PlayerName ?? _selectedRole.ToString());
        SetSide("SidebarRole", status.IsStale ? $"{_selectedRole} · stale" : _selectedRole.ToString());
        SetSide("SidebarLevel", $"Level {status.Level}");
        SetSide("SidebarHp", status.MaximumHp > 0 ? $"HP {status.CurrentHp:N0} / {status.MaximumHp:N0}" : "HP —");
        SetSide("SidebarMp", status.MaximumMp > 0 ? $"MP {status.CurrentMp:N0} / {status.MaximumMp:N0}" : "MP —");
        SetSideBar("SidebarHpBar", status.CurrentHp, status.MaximumHp);
        SetSideBar("SidebarMpBar", status.CurrentMp, status.MaximumMp);
        SetSide("SidebarAdventures", $"Adventures {status.Adventures:N0}");
        SetSide("SidebarMeat", $"Meat {status.Meat:N0}");
        SetSide("SidebarFullness", $"Fullness {status.Fullness?.ToString("N0") ?? "—"}");
        SetSide("SidebarDrunk", $"Drunkenness {status.Inebriety?.ToString("N0") ?? "—"}");
        SetSide("SidebarSpleen", $"Spleen {status.Spleen?.ToString("N0") ?? "—"}");
        SetSide("SidebarFamiliar", string.IsNullOrWhiteSpace(status.FamiliarId) || status.FamiliarId == "0"
            ? "None" : $"Familiar #{status.FamiliarId}");
        var effects = status.TimedEffects.Count > 0
            ? status.TimedEffects.Select(e => e.TurnsRemaining > 0 ? $"{e.Name} · {e.TurnsRemaining} turns" : e.Name).ToList()
            : status.Effects.ToList();
        SetSide("SidebarEffects", effects.Count == 0 ? "None" : string.Join(Environment.NewLine, effects));
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
                if (status is null) ShowSidebarOffline("signed out");
                else ShowSidebarStatus(status);
            });
        }
        catch (Exception exception)
        {
            if (version == _sidebarVersion)
                await Dispatcher.UIThread.InvokeAsync(() => ShowSidebarOffline("unavailable"));
            _ = exception;
        }
    }
}
