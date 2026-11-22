namespace KoLibri.Desktop.ContextMenus;

public sealed record LocationMenuTarget(string LocationId, string Name, string? Zone = null, int RunTurns = AdventureJob.DefaultTurns,
    string? StopItemId = null);

/// <summary>
/// Adventure location rows: adventure here (once or N turns), copy name/id and the wiki page.
/// <paramref name="adventure"/> starts the run in the adventure tab (turns = 1 for "here"); without
/// a live adventure tab the entries say so instead of doing nothing.
/// </summary>
public sealed class LocationContextMenu(IShellHost host, Action<LocationMenuTarget, int>? adventure = null)
{
    public IReadOnlyList<ContextMenuItem> Build(LocationMenuTarget location) =>
    [
        ContextMenuItem.Action("Adventure here", () => Run(location, 1)),
        ContextMenuItem.Action(
            $"Run {location.RunTurns} turns" + (location.StopItemId is null ? "" : $" (until item {location.StopItemId})"),
            () => Run(location, location.RunTurns)),
        ContextMenuItem.Separator,
        ContextMenuItem.Action("Copy name", MenuActions.Copy(host, location.Name, "location name")),
        ContextMenuItem.Action("Copy id", MenuActions.Copy(host, location.LocationId, "location id")),
        ContextMenuItem.Action("Wiki page", MenuActions.Wiki(host, location.Name))
    ];

    private void Run(LocationMenuTarget location, int turns)
    {
        if (adventure is null)
            host.SessionLog.Error($"Cannot adventure in {location.Name}: the adventure tab has no live session yet.");
        else
            adventure(location, turns);
    }
}
