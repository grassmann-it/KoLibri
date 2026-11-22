namespace KoLibri.Desktop;

public enum BrowserMode { Embedded, SystemBrowser }

/// <summary>
/// Chooses how the Browser tab shows the relay. WebKitGTK inside Avalonia runs its own GTK loop and is known to hang on
/// Linux, so Linux defaults to the system browser; Windows/macOS embed. KOLIBRI_EMBED_WEBVIEW=1 opts Linux in,
/// KOLIBRI_NO_WEBVIEW=1 always forces the system browser (and wins over the opt-in).
/// </summary>
public static class BrowserModePolicy
{
    public static BrowserMode Decide(bool isLinux, string? embedEnv, string? noWebViewEnv)
    {
        if (noWebViewEnv == "1") return BrowserMode.SystemBrowser;
        if (embedEnv == "1") return BrowserMode.Embedded;
        if (embedEnv == "0") return BrowserMode.SystemBrowser;
        return isLinux ? BrowserMode.SystemBrowser : BrowserMode.Embedded;
    }

    public static BrowserMode Current() => Decide(OperatingSystem.IsLinux(),
        Environment.GetEnvironmentVariable("KOLIBRI_EMBED_WEBVIEW"), Environment.GetEnvironmentVariable("KOLIBRI_NO_WEBVIEW"));
}

/// <summary>Time limits of the Browser tab; nothing the tab awaits may run longer.</summary>
public static class BrowserTimeouts
{
    public static readonly TimeSpan Gate = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan FirstNavigation = TimeSpan.FromSeconds(15);

    /// <summary>Runs <paramref name="work"/> off the caller's context with a hard limit; null means it timed out.</summary>
    public static async Task<(bool TimedOut, T? Value)> RunAsync<T>(Func<CancellationToken, Task<T>> work, TimeSpan limit,
        CancellationToken outer = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(limit);
        var task = Task.Run(() => work(cts.Token));
        var done = await Task.WhenAny(task, Task.Delay(limit, outer)).ConfigureAwait(false);
        if (done != task) { cts.Cancel(); _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); return (true, default); }
        return (false, await task.ConfigureAwait(false));
    }

    /// <summary>Watchdog: true when <paramref name="seen"/> never became true within <paramref name="limit"/>.</summary>
    public static async Task<bool> NeverSeenAsync(Func<bool> seen, TimeSpan limit, TimeSpan poll, CancellationToken ct = default)
    {
        var until = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < until)
        {
            if (seen()) return false;
            try { await Task.Delay(poll, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return false; }
        }
        return !seen();
    }
}
