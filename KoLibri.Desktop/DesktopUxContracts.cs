using System.Collections.ObjectModel;

namespace KoLibri.Desktop;

/// <summary>The command groups of the shortcut catalog (kept from the 0.4 shell contract).</summary>
public enum ShellPage
{
    Cockpit,
    Inventory,
    Arbitrage,
    Shop,
    Approvals,
    Operations,
    Adventure,
    Combat
}

/// <summary>Where a shell destination is shown at the current window density.</summary>
public enum ShellNavigationPlacement
{
    Footer,
    Sidebar,
    MoreMenu
}

/// <summary>Stable, user-facing command identity. Gestures are deliberately data, not UI code.</summary>
public sealed record KoLibriShortcut(
    string Id,
    string Label,
    string Gesture,
    ShellPage? Page = null,
    string Scope = "application",
    bool IsConfigurable = true);

/// <summary>Action exposed by a row context menu or command palette.</summary>
public sealed record KoLibriContextAction(
    string Id,
    string Label,
    string Icon,
    string Scope,
    bool RequiresSelection = true,
    bool IsMutating = false,
    bool PreviewOnly = true);

/// <summary>
/// Small UX catalog inspired by MeowChan's stable command IDs, configurable
/// shortcuts, footer/More overflow and Shift+F10 context-menu convention.
/// It contains no Avalonia controls and can therefore be shared by desktop,
/// command palette and future MCP presentation layers.
/// </summary>
public sealed class KoLibriUxCatalog
{
    public static KoLibriUxCatalog Default { get; } = CreateDefault();

    public KoLibriUxCatalog(
        IEnumerable<KoLibriShortcut> shortcuts,
        IEnumerable<KoLibriContextAction> contextActions,
        IReadOnlyDictionary<ShellPage, ShellNavigationPlacement>? placements = null)
    {
        Shortcuts = new ReadOnlyCollection<KoLibriShortcut>((shortcuts ?? throw new ArgumentNullException(nameof(shortcuts))).ToArray());
        ContextActions = new ReadOnlyCollection<KoLibriContextAction>((contextActions ?? throw new ArgumentNullException(nameof(contextActions))).ToArray());
        Placements = new ReadOnlyDictionary<ShellPage, ShellNavigationPlacement>(
            placements is null
                ? Enum.GetValues<ShellPage>().ToDictionary(page => page, _ => ShellNavigationPlacement.Footer)
                : new Dictionary<ShellPage, ShellNavigationPlacement>(placements));
        Validate();
    }

    public ReadOnlyCollection<KoLibriShortcut> Shortcuts { get; }
    public ReadOnlyCollection<KoLibriContextAction> ContextActions { get; }
    public ReadOnlyDictionary<ShellPage, ShellNavigationPlacement> Placements { get; }

    public bool TryGetShortcut(string id, out KoLibriShortcut shortcut)
    {
        shortcut = Shortcuts.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))!;
        return shortcut is not null;
    }

    public IReadOnlyList<KoLibriContextAction> ActionsFor(string scope)
        => ContextActions.Where(action => string.Equals(action.Scope, scope, StringComparison.OrdinalIgnoreCase)).ToArray();

    public KoLibriUxCatalog WithGesture(string id, string gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture)) throw new ArgumentException("Gesture is required.", nameof(gesture));
        var updated = Shortcuts.Select(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)
            ? item with { Gesture = gesture.Trim() }
            : item).ToArray();
        if (!updated.Any(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)))
            throw new KeyNotFoundException($"Unknown shortcut '{id}'.");
        return new KoLibriUxCatalog(updated, ContextActions, Placements);
    }

    private void Validate()
    {
        var duplicateIds = Shortcuts.GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
        if (duplicateIds.Length > 0) throw new ArgumentException($"Duplicate shortcut ids: {string.Join(", ", duplicateIds)}.");
        var duplicateActions = ContextActions.GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
        if (duplicateActions.Length > 0) throw new ArgumentException($"Duplicate context action ids: {string.Join(", ", duplicateActions)}.");
        var duplicateGestures = Shortcuts.Where(item => !string.IsNullOrWhiteSpace(item.Gesture)).GroupBy(item => item.Gesture.Trim(), StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
        if (duplicateGestures.Length > 0) throw new ArgumentException($"Duplicate shortcut gestures: {string.Join(", ", duplicateGestures)}.");
    }

    private static KoLibriUxCatalog CreateDefault()
    {
        // One catalog: the gestures come from DesktopShortcuts (Cmd on macOS, Ctrl elsewhere).
        var mac = OperatingSystem.IsMacOS();
        var shortcuts = DesktopShortcuts.All
            .Select(item => new KoLibriShortcut(item.Id, item.Label, item.Display(mac),
                Scope: item.Command == DesktopCommand.ContextMenu ? "selection" : "application",
                IsConfigurable: false))
            .Append(new KoLibriShortcut("shell.dismiss-overlay", "Dismiss overlay", "Escape", Scope: "application", IsConfigurable: false))
            .ToArray();
        var actions = new[]
        {
            new KoLibriContextAction("adventure.inspect", "Inspect encounter", "search", "adventure"),
            new KoLibriContextAction("adventure.refresh", "Refresh encounter", "refresh-cw", "adventure", IsMutating: false),
            new KoLibriContextAction("adventure.preview-start", "Preview start", "play", "adventure", IsMutating: false),
            new KoLibriContextAction("combat.inspect", "Inspect combat", "search", "combat"),
            new KoLibriContextAction("combat.manual-preview", "Preview manual action", "hand", "combat"),
            new KoLibriContextAction("combat.autocombat-preview", "Preview AutoCombat", "list-checks", "combat"),
            new KoLibriContextAction("shop.market-preview", "Compare market", "chart-no-axes-combined", "shop"),
            new KoLibriContextAction("shop.reprice-preview", "Preview reprice", "tag", "shop")
        };
        var placements = Enum.GetValues<ShellPage>().ToDictionary(page => page, _ => ShellNavigationPlacement.Footer);
        placements[ShellPage.Approvals] = ShellNavigationPlacement.MoreMenu;
        placements[ShellPage.Operations] = ShellNavigationPlacement.MoreMenu;
        placements[ShellPage.Combat] = ShellNavigationPlacement.Sidebar;
        return new KoLibriUxCatalog(shortcuts, actions, placements);
    }
}
