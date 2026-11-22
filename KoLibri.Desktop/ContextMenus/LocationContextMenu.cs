namespace KoLibri.Desktop.ContextMenus;

public sealed record LocationMenuTarget(string LocationId, string Name, string? Zone = null, int RunTurns = 10);

/// <summary>Adventure location rows: adventure once, run N turns, info and copy.</summary>
public sealed class LocationContextMenu(IShellHost host)
{
    public IReadOnlyList<ContextMenuItem> Build(LocationMenuTarget location) =>
    [
        ContextMenuItem.Action("Adventure once", MenuActions.NotImplemented(host, $"Adventure once in {location.Name}")),
        ContextMenuItem.Action($"Run {location.RunTurns} turns", MenuActions.NotImplemented(host, $"Run {location.RunTurns} turns in {location.Name}")),
        ContextMenuItem.Separator,
        ContextMenuItem.Action("Info", MenuActions.NotImplemented(host, $"Info {location.Name}")),
        ContextMenuItem.Action("Copy name", MenuActions.Copy(host, location.Name, "location name"))
    ];
}
