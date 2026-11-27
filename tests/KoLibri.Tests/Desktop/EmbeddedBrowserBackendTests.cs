using KoLibri.Desktop;
using Xunit;

namespace KoLibri.Tests.Desktop;

public sealed class EmbeddedBrowserBackendTests
{
    [Fact]
    public void Linux_uses_the_own_WebKitGtk_host_and_others_WebView_Avalonia()
    {
        Assert.Contains("WebKitGTK", EmbeddedBrowserBackend.BackendName(isLinux: true));
        Assert.Contains("WebView.Avalonia", EmbeddedBrowserBackend.BackendName(isLinux: false));
    }

    [Fact]
    public void Report_names_backend_state_and_error()
    {
        Assert.Equal("Embedded browser: WebKitGTK, initialized (libwebkit2gtk-4.1.so.0, X11).",
            EmbeddedBrowserBackend.Describe("WebKitGTK", true, "libwebkit2gtk-4.1.so.0, X11"));
        var failed = EmbeddedBrowserBackend.Describe("WebKitGTK", false, "native library missing");
        Assert.Contains("NOT available: native library missing", failed);
        Assert.Contains("system browser", failed);
    }

    [Fact]
    public void WebKitGtk_prefers_4_1_and_falls_back_to_4_0()
        => Assert.Equal(["libwebkit2gtk-4.1.so.0", "libwebkit2gtk-4.0.so.37"], KoLibri.Desktop.WebKit.Candidates);

    [Fact]
    public void Shutdown_guard_only_swallows_late_DBus_cancellation()
    {
        var late = new TracedCancellation("   at Avalonia.Threading.Dispatcher.Send(...)\n   at Tmds.DBus.Protocol.DBusConnection.Observer.Emit(Exception exception)");
        Assert.True(ShutdownGuard.IsLateDBusCancellation(late, dispatcherStopped: true));
        // While the app runs the same exception is a real error.
        Assert.False(ShutdownGuard.IsLateDBusCancellation(late, dispatcherStopped: false));
        // Other exceptions, or cancellations from elsewhere, still crash.
        Assert.False(ShutdownGuard.IsLateDBusCancellation(new InvalidOperationException("boom"), dispatcherStopped: true));
        Assert.False(ShutdownGuard.IsLateDBusCancellation(new TracedCancellation("   at KoLibri.Desktop.MainWindow.Load()"), dispatcherStopped: true));
        Assert.False(ShutdownGuard.IsLateDBusCancellation(new TaskCanceledException(), dispatcherStopped: true));
    }

    private sealed class TracedCancellation(string trace) : TaskCanceledException
    {
        public override string StackTrace => trace;
    }
}
