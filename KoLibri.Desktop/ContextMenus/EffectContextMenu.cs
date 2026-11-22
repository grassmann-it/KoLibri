namespace KoLibri.Desktop.ContextMenus;

public sealed record EffectMenuTarget(string EffectId, string Name, bool Removable, int TurnsRemaining = 0);

/// <summary>Active effect rows: copy name, remaining turns and wiki. Uneffect is not offered until it is wired to a confirmed action.</summary>
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
        ContextMenuItem.Action("Wiki", MenuActions.Wiki(host, effect.Name))
    ];
}
