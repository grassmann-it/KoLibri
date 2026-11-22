namespace KoLibri.Desktop.ContextMenus;

public sealed record SkillMenuTarget(string SkillId, string Name, int MpCost, bool Castable = true);

/// <summary>Skill rows: cast (showing the MP cost), info, wiki and copy.</summary>
public sealed class SkillContextMenu(IShellHost host)
{
    public IReadOnlyList<ContextMenuItem> Build(SkillMenuTarget skill) =>
    [
        ContextMenuItem.Action($"Cast ({skill.MpCost} MP)", MenuActions.NotImplemented(host, $"Cast {skill.Name}"))
            .When(skill.Castable, "This skill cannot be cast here."),
        ContextMenuItem.Action("Info", MenuActions.NotImplemented(host, $"Info {skill.Name}")),
        ContextMenuItem.Action("Wiki", MenuActions.Wiki(host, skill.Name)),
        ContextMenuItem.Separator,
        ContextMenuItem.Action("Copy name", MenuActions.Copy(host, skill.Name, "skill name")),
        ContextMenuItem.Action("Copy ID", MenuActions.Copy(host, skill.SkillId, "skill ID"))
    ];
}
