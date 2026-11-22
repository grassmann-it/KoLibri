namespace KoLibri.Desktop.ContextMenus;

public sealed record EffectMenuTarget(string EffectId, string Name, bool Removable, int TurnsRemaining = 0);

/// <summary>Active effect rows: copy name, remaining turns, info, wiki and Uneffect when the effect can be removed.</summary>
public sealed class EffectContextMenu(IShellHost host)
{
    public IReadOnlyList<ContextMenuItem> Build(EffectMenuTarget effect) =>
    [
        ContextMenuItem.Action("Copy name", MenuActions.Copy(host, effect.Name, "effect name")),
        ContextMenuItem.Action("Show remaining turns", () => host.ShowMessage(
            effect.TurnsRemaining > 0
                ? $"{effect.Name}: {effect.TurnsRemaining} turn{(effect.TurnsRemaining == 1 ? "" : "s")} remaining."
                : $"{effect.Name}: no turn count reported (intrinsic or unknown).")),
        ContextMenuItem.Separator,
        ContextMenuItem.Action("Info", MenuActions.NotImplemented(host, $"Info {effect.Name}")),
        ContextMenuItem.Action("Wiki", MenuActions.Wiki(host, effect.Name)),
        ContextMenuItem.Action("Uneffect", MenuActions.NotImplemented(host, $"Uneffect {effect.Name}"))
            .When(effect.Removable, "This effect cannot be removed.")
    ];
}
