namespace KoLibri.Desktop.ContextMenus;

/// <summary>
/// Shared building blocks for the type-specific menus. Placeholders only write to the session
/// log; copy and navigation entries are real and never send a game action.
/// </summary>
public static class MenuActions
{
    public const string WikiBase = "https://kol.coldfront.net/thekolwiki/index.php/";

    /// <summary>A not-yet-built action: logs "… not implemented yet." and does nothing else.</summary>
    public static Action NotImplemented(IShellHost host, string what)
        => () => host.SessionLog.Add(SessionLogKind.Info, $"{what}: not implemented yet.");

    public static Action Copy(IShellHost host, string text, string what)
        => () => Fire(host, async () =>
        {
            await host.CopyTextAsync(text);
            host.ShowMessage($"Copied {what}.");
        });

    public static Action Wiki(IShellHost host, string name)
        => () => Fire(host, () => host.OpenUrlAsync(WikiUri(name)));

    public static Action SearchMall(IShellHost host, string term)
        => () => Fire(host, () => host.SearchMallAsync(term));

    public static Uri WikiUri(string name)
        => new(WikiBase + Uri.EscapeDataString(name.Trim().Replace(' ', '_')));

    private static async void Fire(IShellHost host, Func<Task> work)
    {
        try { await work(); }
        catch (Exception exception) { host.SessionLog.Error(exception.Message); }
    }
}
