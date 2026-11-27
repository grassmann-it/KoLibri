using Avalonia;

namespace KoLibri.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        ShutdownGuard.Install();
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            ShutdownGuard.MarkDispatcherStopped();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
        // The in-app relay browser needs a native web view; the backend and its start-up result go to stderr
        // (and into the session log when the Browser tab opens). When it is missing the Browser page says so.
        return EmbeddedBrowserBackend.Initialize(builder);
    }
}

/// <summary>
/// Exit crash on Linux: Avalonia's session-bus connection (IME, portal settings; Tmds.DBus) delivers signals through the
/// Avalonia synchronization context. A signal that arrives after the dispatcher stopped makes <c>Dispatcher.Send</c>
/// throw <see cref="TaskCanceledException"/> on the DBus reader thread; the connection then disconnects and throws the
/// same again from <c>Observer.Emit</c>, unhandled, so the process dies with a stack trace while exiting.
/// The connection is internal to Avalonia, so it cannot be closed earlier from here. This guard ends the process
/// normally in exactly that case (cancellation, thrown from Tmds.DBus, after the dispatcher stopped) and lets every
/// other unhandled exception crash as before.
/// </summary>
internal static class ShutdownGuard
{
    private static volatile bool _dispatcherStopped;
    private static int _installed;

    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1) return;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is not Exception exception || !IsLateDBusCancellation(exception, _dispatcherStopped)) return;
            Console.Error.WriteLine("Exit: ignored a DBus signal that arrived after the UI dispatcher stopped (Avalonia/Tmds.DBus).");
            Environment.Exit(Environment.ExitCode);
        };
    }

    public static void MarkDispatcherStopped() => _dispatcherStopped = true;

    internal static bool IsLateDBusCancellation(Exception exception, bool dispatcherStopped)
        => dispatcherStopped
           && exception is OperationCanceledException
           && exception.StackTrace is { } trace
           && trace.Contains("Tmds.DBus", StringComparison.Ordinal)
           && trace.Contains("Avalonia.Threading.Dispatcher", StringComparison.Ordinal);
}
