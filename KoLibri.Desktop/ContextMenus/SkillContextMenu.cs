namespace KoLibri.Desktop.ContextMenus;

public sealed record SkillMenuTarget(string SkillId, string Name, int MpCost, bool Castable = true);

/// <summary>
/// Skill rows: cast (showing the MP cost), info, wiki and copy. <paramref name="cast"/> only prepares the
/// confirmation in the skills page; choosing "Cast" from the menu never sends a cast by itself.
/// </summary>
public sealed class SkillContextMenu(IShellHost host, Action<SkillMenuTarget>? cast = null)
{
    public IReadOnlyList<ContextMenuItem> Build(SkillMenuTarget skill) =>
    [
        ContextMenuItem.Action($"Cast ({skill.MpCost} MP)",
                cast is null ? MenuActions.NotImplemented(host, $"Cast {skill.Name}") : () => cast(skill))
            .When(skill.Castable, "This skill cannot be cast here."),
        ContextMenuItem.Action("Wiki", MenuActions.Wiki(host, skill.Name)),
        ContextMenuItem.Separator,
        ContextMenuItem.Action("Copy name", MenuActions.Copy(host, skill.Name, "skill name")),
        ContextMenuItem.Action("Copy ID", MenuActions.Copy(host, skill.SkillId, "skill ID"))
    ];
}
