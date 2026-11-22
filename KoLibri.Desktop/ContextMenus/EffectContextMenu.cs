namespace KoLibri.Desktop.ContextMenus;

public sealed record EffectMenuTarget(string EffectId, string Name, bool Removable);

/// <summary>Active effect rows: info, wiki, copy and Uneffect when the effect can be removed.</summary>
public sealed class EffectContextMenu(IShellHost host)
{
    public IReadOnlyList<ContextMenuItem> Build(EffectMenuTarget effect) =>
    [
        ContextMenuItem.Action("Info", MenuActions.NotImplemented(host, $"Info {effect.Name}")),
        ContextMenuItem.Action("Wiki", MenuActions.Wiki(host, effect.Name)),
        ContextMenuItem.Action("Uneffect", MenuActions.NotImplemented(host, $"Uneffect {effect.Name}"))
            .When(effect.Removable, "This effect cannot be removed."),
        ContextMenuItem.Separator,
        ContextMenuItem.Action("Copy name", MenuActions.Copy(host, effect.Name, "effect name"))
    ];
}
