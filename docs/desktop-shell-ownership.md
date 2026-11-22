# Desktop shell split and context menus

`MainWindow` is now only the shell (menu bar, account selector, status bar, session log, Chat & Mail,
Settings). Each tab is a `UserControl` with its own XAML and code-behind in `KoLibri.Desktop`.

## Hand-over points

- `IShellHost` (`IShellHost.cs`): active account, session log, status message, navigate to a tab,
  refresh, mall search, copy text, open URL. Views and menus use only this.
- Per-view handler interfaces (`IAdventuresViewHandlers`, `IInventoryViewHandlers`, ...) in the same file.
  The view forwards its Click/SelectionChanged events to them; `MainWindow` implements them for now.
  A tab worker moves an implementation into its own handler file and removes it from the shell.
- `MainWindow.FindControl<T>(name)` also searches the tab views, so `FindControl` calls in the
  shell and in tests keep working for moved controls.

## Context menus (`ContextMenus/`)

- `ContextMenuItem`: label, enabled state and reason, handler, submenu, separator, shortcut text.
  `BindTo(button)` makes a button run the same handler as the menu entry.
- `RowContextMenuController<T>.Attach(listBox, build)`: right-click selects the row under the pointer, then
  opens the menu for that row. Menu key and Shift+F10 address the focused row (else the selected one).
  Building a menu never runs a handler, so opening a menu sends no game action.
- Menus: `ItemContextMenu`, `GearSlotContextMenu`, `MallOfferContextMenu`, `LocationContextMenu`,
  `EffectContextMenu`, `SkillContextMenu`. Copy, Wiki and Mallsearch are real; everything else is
  `MenuActions.NotImplemented(...)`, which writes "... not implemented yet." to the session log.
  Wired today: item list (`InventoryView`), location list (`AdventuresView`), offer list (`MallSearchView`).

## Ownership map

| Area | Files |
| --- | --- |
| Shell (nobody else edits) | `MainWindow.axaml(.cs)`, `MainWindow.Shell.cs`, `MainWindow.ShellHost.cs`, `IShellHost.cs`, `ContextMenus/RowContextMenuController.cs`, `ContextMenus/ContextMenuItem.cs`, `ContextMenus/ContextMenuFactory.cs`, `ContextMenus/MenuActions.cs` |
| Adventures | `AdventuresView.*`, `MainWindow.Encounter.cs`, `MainWindow.Combat.cs`, `ContextMenus/LocationContextMenu.cs` |
| Inventory (items) | `InventoryView.*`, `InventoryViewModel.cs`, `ItemManagerViewModel.cs`, `ContextMenus/ItemContextMenu.cs` |
| Gear | `GearView.*`, equipment handlers in `MainWindow.CharacterActions.cs`, `ContextMenus/GearSlotContextMenu.cs` |
| Skills and effects | `SkillsEffectsView.*`, skill/mood handlers in `MainWindow.CharacterActions.cs`, `ContextMenus/SkillContextMenu.cs`, `ContextMenus/EffectContextMenu.cs` |
| Charpane | `CharpaneView.*`, `MainWindow.Sidebar.cs` |
| Daily | `DailyView.*`, `MainWindow.Daily.cs` |
| Mall | `MallSearchView.*`, `MallSearchViewModel.cs`, `ContextMenus/MallOfferContextMenu.cs` |
| Settings | `SettingsView.*` |

`MainWindow.CharacterActions.cs` is shared by Inventory, Gear and Skills; split it by moving each
worker's handlers into a new `<View>.Handlers.cs` first, one worker at a time.
