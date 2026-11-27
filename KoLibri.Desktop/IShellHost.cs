using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace KoLibri.Desktop;

/// <summary>
/// The hand-over point between the shell (<see cref="MainWindow"/>) and the tab views.
/// A view or a context menu talks to the shell only through this interface, so a tab
/// worker edits the view, its view model and its handler file without touching the
/// shell. Opening a menu must never call anything here that sends a game action.
/// </summary>
public interface IShellHost
{
    /// <summary>The account (Main or Mallbot) the tabs currently show.</summary>
    AccountRole ActiveRole { get; }

    /// <summary>The shared session log (also the place for "not implemented yet" notices).</summary>
    SessionLogViewModel SessionLog { get; }

    /// <summary>Shows a short message in the status bar.</summary>
    void ShowMessage(string message);

    /// <summary>Opens a tab. Sections: Adventure, Inventory, "Mall &amp; Shop", Daily, Communication, Settings.</summary>
    Task NavigateToAsync(string section);

    /// <summary>Reloads the active tab and the character pane.</summary>
    Task RefreshStatusAsync(bool forceRefresh);

    /// <summary>Opens the mall tab and searches for a term (a read, never a purchase).</summary>
    Task SearchMallAsync(string term);

    /// <summary>Puts text on the clipboard.</summary>
    Task CopyTextAsync(string text);

    /// <summary>Opens a web address in the system browser.</summary>
    Task OpenUrlAsync(Uri uri);
}

// Click handlers a view forwards to the shell. Today MainWindow implements them
// (MainWindow.CharacterActions.cs and friends); a tab worker moves an implementation
// into the view's own handler file and drops the member from the shell.

public interface IAdventuresViewHandlers
{
    void RefreshClick(object? sender, RoutedEventArgs e);
    void OpenFriarsClick(object? sender, RoutedEventArgs e);
    void FriarRitualClick(object? sender, RoutedEventArgs e);
    void FriarBlessingClick(object? sender, RoutedEventArgs e);
    void CombatRunClick(object? sender, RoutedEventArgs e);
}

public interface IInventoryViewHandlers
{
    void InventorySearchChanged(object? sender, TextChangedEventArgs e);
    void InventorySelectionChanged(object? sender, SelectionChangedEventArgs e);
    void ReadCharacterStorageClick(object? sender, RoutedEventArgs e);
    void TakeCharacterStoredItemClick(object? sender, RoutedEventArgs e);
    void PreviewCharacterAcquisitionClick(object? sender, RoutedEventArgs e);
    void CombineCharacterItemClick(object? sender, RoutedEventArgs e);
}

public interface IGearViewHandlers
{
    void ReadCharacterEquipmentClick(object? sender, RoutedEventArgs e);
    void UnequipCharacterSlotClick(object? sender, RoutedEventArgs e);
    void WearCharacterOutfitClick(object? sender, RoutedEventArgs e);
    void SwitchCharacterFamiliarClick(object? sender, RoutedEventArgs e);
    void UnequipCharacterFamiliarClick(object? sender, RoutedEventArgs e);
    void CompareCharacterEquipmentClick(object? sender, RoutedEventArgs e);
}

public interface ISkillsEffectsViewHandlers
{
    void CastCharacterSkillClick(object? sender, RoutedEventArgs e);
    void RestCharacterClick(object? sender, RoutedEventArgs e);
    void ApplyCharacterMoodClick(object? sender, RoutedEventArgs e);
}

public interface IDailyViewHandlers
{
    void DailySelectionChanged(object? sender, SelectionChangedEventArgs e);
    void DailyRunClick(object? sender, RoutedEventArgs e);
}
