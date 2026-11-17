# KoLibri Desktop UX contract

KoLibri borrows the useful parts of MeowChan/oChan's desktop model: stable command IDs, configurable keyboard gestures, a shortcut help surface, and context actions that remain available when a page is moved into an overflow menu. Layout density changes where a destination is shown; it does not remove the command.

The first contract lives in `KoLibri.Desktop/DesktopUxContracts.cs`:

- `KoLibriShortcut` describes a stable command ID, localized label, default gesture, scope and whether the user may rebind it.
- `KoLibriContextAction` describes a row or page context action. Actions are preview-only by default and carry an explicit mutating flag.
- `KoLibriUxCatalog` validates duplicate command IDs and gestures and supports creating a user override with `WithGesture`.
- `ShellNavigationPlacement` gives the shell a density-independent placement of `Footer`, `Sidebar` or `MoreMenu`.

The initial defaults use `F1` for shortcut help, `Escape` to dismiss overlays, `Shift+F10` for context menus, `Ctrl+1..7` for the main pages, `F5` to refresh an Adventure encounter and `Ctrl+Escape` to stop a local Adventure run. Context actions cover Adventure inspection/refresh/preview, manual and AutoCombat previews, and shop market/reprice previews. No default context action submits a KoL request.

The UI can later bind the catalog to Avalonia key routing and `ContextMenu` controls. MCP and CLI can reuse the same IDs so a keyboard action, menu item and automation tool stay aligned.
