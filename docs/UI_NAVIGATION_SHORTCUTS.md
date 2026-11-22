# KoLibri Desktop: Navigation, Shortcuts and Context Actions

KoLibri adopts the separation between workspace, command and presentation from
MeowChan/oChan. The sidebar or the compact top bar only switches the
workspace; the same commands can later be triggered from the app menu, the
keyboard help or a context menu.

The current shell contract provides `ShellPageContract`,
`ShellShortcutContract` and `ShellContextActionContract` for this. The defaults
are deliberately small:

| Command | Shortcut | Purpose |
|---|---|---|
| Adventures, Inventory, Mall, Daily, Chat & Mail | `Ctrl+1` … `Ctrl+5` (`Cmd` on macOS) | Switch tab |
| Settings | `Ctrl+,` (`Cmd+,` on macOS) | Open settings |
| Refresh | `F5` | Load the current read-only snapshot |
| Keyboard help | `F1` | Show all available bindings |
| Cancel the running load | `Ctrl+.` (`Cmd+.` on macOS) | Cancel the current operation (`Ctrl+Escape` opened the Start menu on Windows) |

Context actions depend on the selected object. For adventures these include
inspecting an encounter, refreshing an encounter and stopping the run. For
combat there is a manual action preview and stop. Shop rows offer a detail
price and a reprice preview. An action with `RequiresApproval` may only mutate
after the existing Preview → Approval → Commit contract.

The presentation can thus later switch between sidebar, hamburger/app menu and
context menu without implementing commands twice. Input fields, dialogs and
open overlays keep their normal keys; global bindings are only executed if the
active area does not consume them.

On wide windows, a collapsible right context bar complements the sidebar. It
shows the type of the active workspace and safe preview/refresh actions for a
selected Mall row, an encounter or a combat observation. Below desktop width
it disappears; the same actions remain reachable via the compact top
navigation and context menus.

The Avalonia shell now routes this core mapping directly at the window:
`F1`, `F5`, `Ctrl+.`, `Ctrl+,` and `Ctrl+1..5` (`Cmd` instead of `Ctrl` on macOS). Text fields keep their
normal input; the global gestures are only consumed for the explicitly defined
combinations.

## Next UI work packages

1. Hook up a central shortcut router with configurable profiles and conflict
   checking.
2. Output F1 as a keyboard-help window with search and area filter.
3. Avalonia context menus for the Mall, Adventure and Combat panels are bound
   to the shell contracts; the next stage can attach individual table rows and
   concrete encounter actions as the selection.
4. Attach global stop to the shared cancellation/checkpoint store; a stop must
   not trigger a new KoL POST.

MeowChan/oChan remains the UX reference for navigation, focus and context
actions. KoLibri adopts no image-board or download logic.
