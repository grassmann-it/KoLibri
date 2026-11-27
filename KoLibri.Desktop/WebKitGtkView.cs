using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace KoLibri.Desktop;

/// <summary>
/// The embedded relay browser on Linux: WebKitGTK (4.1, else 4.0) in a GTK window that Avalonia reparents into the
/// app window through <see cref="NativeControlHost"/> (X11 handle "XID"; Avalonia runs on X11/XWayland).
/// GTK runs on its own thread with its own main loop (<see cref="GtkHost"/>); every GTK/WebKit call is posted there and
/// the UI thread never waits on it longer than <see cref="CreateLimit"/>.
/// Each view has an ephemeral web context: cookies live in memory only and never reach other browsers.
/// Events are raised on the GTK thread; handlers post to the UI thread themselves.
/// </summary>
public sealed class WebKitGtkView : NativeControlHost, IEmbeddedBrowser
{
    internal static readonly TimeSpan CreateLimit = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private GCHandle _self;
    private IntPtr _window;   // GtkWindow*, GTK thread only
    private IntPtr _view;     // WebKitWebView*, GTK thread only
    private string? _pendingUri;
    private readonly List<IntPtr> _popups = []; // GtkWindow* of same-origin popups (item descriptions), GTK thread only
    private volatile bool _canGoBack, _canGoForward;
    private bool _destroyed;

    /// <summary>Asks whether a navigation may load in the view (GTK thread). False cancels it.</summary>
    public Func<string, bool> AllowNavigation { get; set; } = _ => true;
#pragma warning disable CS0067 // not raised here: new windows are decided by AllowNavigation (same-origin popups open as related windows)
    public event Action<string>? NewWindowRequested;
#pragma warning restore CS0067
    public event Action<string>? NavigationCompleted;
    public event Action<string>? Failed;
    public event Action? Created;

    public Control View => this;
    public bool CanGoBack => _canGoBack;
    public bool CanGoForward => _canGoForward;

    public void Navigate(Uri uri)
    {
        var text = uri.ToString();
        lock (_gate)
        {
            if (_view == IntPtr.Zero) { _pendingUri = text; return; }
        }
        GtkHost.Post(() => { if (_view != IntPtr.Zero) WebKit.LoadUri(_view, text); });
    }

    public void GoBack() => GtkHost.Post(() => { if (_view != IntPtr.Zero) WebKit.GoBack(_view); });
    public void GoForward() => GtkHost.Post(() => { if (_view != IntPtr.Zero) WebKit.GoForward(_view); });
    public void Reload() => GtkHost.Post(() => { if (_view != IntPtr.Zero) WebKit.Reload(_view); });

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (parent.HandleDescriptor != "XID") return Fail(parent, $"window system handle {parent.HandleDescriptor} is not X11");
        if (!GtkHost.EnsureStarted(out var why)) return Fail(parent, why);
        var task = GtkHost.InvokeAsync(CreateOnGtk);
        try
        {
            if (!task.Wait(CreateLimit)) return Fail(parent, $"the GTK thread did not create the view within {CreateLimit.TotalSeconds:0} s");
        }
        catch (AggregateException exception)
        {
            return Fail(parent, $"creating the view failed ({exception.InnerException?.GetType().Name}: {exception.InnerException?.Message})");
        }
        if (task.Result == 0) return Fail(parent, "WebKitGTK returned no window");
        // Map the GTK window only after Avalonia reparented it, so it never shows up as a top-level window.
        Dispatcher.UIThread.Post(() => GtkHost.Post(ShowOnGtk), DispatcherPriority.Background);
        return new PlatformHandle((IntPtr)task.Result, "XID");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (control.HandleDescriptor == "XID" && _window != IntPtr.Zero) GtkHost.Post(DestroyOnGtk);
        else base.DestroyNativeControlCore(control);
    }

    private IPlatformHandle Fail(IPlatformHandle parent, string reason)
    {
        Failed?.Invoke(reason);
        return base.CreateNativeControlCore(parent);
    }

    private ulong CreateOnGtk()
    {
        _destroyed = false;
        _window = Gtk.gtk_window_new(0);
        Gtk.gtk_window_set_decorated(_window, 0);
        var context = WebKit.NewEphemeralContext();
        var view = WebKit.NewViewWithContext(context);
        GObject.g_object_unref(context); // the view holds its own reference
        Gtk.gtk_container_add(_window, view);
        _self = GCHandle.Alloc(this);
        ConnectPolicy(view);
        unsafe
        {
            GObject.Connect(view, "load-changed", (IntPtr)(delegate* unmanaged<IntPtr, int, IntPtr, void>)&OnLoadChanged, GCHandle.ToIntPtr(_self));
        }
        Gtk.gtk_widget_realize(_window);
        var xid = Gdk.gdk_x11_window_get_xid(Gtk.gtk_widget_get_window(_window));
        string? pending;
        lock (_gate) { _view = view; pending = _pendingUri; _pendingUri = null; }
        if (pending is not null) WebKit.LoadUri(view, pending);
        Created?.Invoke();
        return xid;
    }

    /// <summary>Navigation policy and popup handling, for the main view and for every popup view.</summary>
    private unsafe void ConnectPolicy(IntPtr view)
    {
        GObject.Connect(view, "decide-policy", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int, IntPtr, int>)&OnDecidePolicy, GCHandle.ToIntPtr(_self));
        GObject.Connect(view, "create", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr>)&OnCreate, GCHandle.ToIntPtr(_self));
    }

    /// <summary>
    /// A same-origin window.open (KoL's item and effect descriptions): a small separate GTK window whose view is related
    /// to the main one, so it shares the relay cookie, and that obeys the same navigation policy.
    /// </summary>
    private unsafe IntPtr CreatePopup(IntPtr parentView)
    {
        if (_destroyed) return IntPtr.Zero;
        var popup = WebKit.NewRelatedView(parentView);
        var window = Gtk.gtk_window_new(0);
        Gtk.gtk_window_set_default_size(window, 640, 560);
        Gtk.gtk_container_add(window, popup);
        ConnectPolicy(popup);
        GObject.Connect(popup, "ready-to-show", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&OnPopupReady, window);
        GObject.Connect(popup, "close", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&OnPopupClose, window);
        GObject.Connect(window, "destroy", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&OnPopupDestroyed, GCHandle.ToIntPtr(_self));
        _popups.Add(window);
        return popup;
    }

    [UnmanagedCallersOnly]
    private static void OnPopupReady(IntPtr view, IntPtr window) { try { Gtk.gtk_widget_show_all(window); } catch { } }

    [UnmanagedCallersOnly]
    private static void OnPopupClose(IntPtr view, IntPtr window) { try { Gtk.gtk_widget_destroy(window); } catch { } }

    /// <summary>The popup window is gone (closed by the page, the user or with the main view): forget it.</summary>
    [UnmanagedCallersOnly]
    private static void OnPopupDestroyed(IntPtr window, IntPtr data) { try { From(data)?._popups.Remove(window); } catch { } }

    private void ShowOnGtk()
    {
        if (_window != IntPtr.Zero && !_destroyed) Gtk.gtk_widget_show_all(_window);
    }

    private void DestroyOnGtk()
    {
        _destroyed = true;
        IntPtr view;
        lock (_gate) view = _view;
        if (view != IntPtr.Zero && _self.IsAllocated) GObject.DisconnectByData(view, GCHandle.ToIntPtr(_self));
        foreach (var popup in _popups.ToArray()) Gtk.gtk_widget_destroy(popup);
        _popups.Clear();
        if (_window != IntPtr.Zero) Gtk.gtk_widget_destroy(_window);
        lock (_gate) { _window = IntPtr.Zero; _view = IntPtr.Zero; }
        if (_self.IsAllocated) _self.Free();
    }

    private static WebKitGtkView? From(IntPtr data)
        => data == IntPtr.Zero ? null : GCHandle.FromIntPtr(data).Target as WebKitGtkView;

    // WebKitPolicyDecisionType: 0 navigation, 1 new window, 2 response.
    [UnmanagedCallersOnly]
    private static int OnDecidePolicy(IntPtr view, IntPtr decision, int type, IntPtr data)
    {
        try
        {
            var self = From(data);
            if (self is null || type == 2) return 0; // responses: WebKit's default handling
            var uri = WebKit.DecisionUri(decision) ?? "";
            // New windows (type 1) go through the same check: same-origin ones open as a related popup ("create" below),
            // anything else is cancelled and AllowNavigation's owner asks about the system browser.
            if (self.AllowNavigation(uri)) WebKit.Use(decision);
            else WebKit.Ignore(decision);
            return 1;
        }
        catch { WebKit.Ignore(decision); return 1; }
    }

    // WebKitLoadEvent: 0 started, 1 redirected, 2 committed, 3 finished.
    [UnmanagedCallersOnly]
    private static void OnLoadChanged(IntPtr view, int loadEvent, IntPtr data)
    {
        try
        {
            if (From(data) is not { } self) return;
            self._canGoBack = WebKit.CanGoBack(view);
            self._canGoForward = WebKit.CanGoForward(view);
            if (loadEvent == 3) self.NavigationCompleted?.Invoke(WebKit.CurrentUri(view) ?? "");
        }
        catch { /* never throw into GTK */ }
    }

    /// <summary>"create" (window.open, target=_blank): only reached for same-origin targets the policy allowed.</summary>
    [UnmanagedCallersOnly]
    private static IntPtr OnCreate(IntPtr view, IntPtr action, IntPtr data)
    {
        try
        {
            var uri = WebKit.ActionUri(action);
            if (From(data) is not { } self || uri is null || !self.AllowNavigation(uri)) return IntPtr.Zero;
            return self.CreatePopup(view);
        }
        catch { return IntPtr.Zero; }
    }
}

/// <summary>The GTK thread: one per process, started on first use, runs gtk_main until the process exits.</summary>
internal static class GtkHost
{
    private static readonly object Gate = new();
    private static Thread? _thread;
    private static string? _error;
    private static readonly ManualResetEventSlim Ready = new();
    private static readonly ConcurrentQueue<Action> Work = new();

    /// <summary>Which WebKitGTK library is in use ("libwebkit2gtk-4.1.so.0"), once started.</summary>
    public static string? Library => WebKit.LibraryName;

    public static bool EnsureStarted(out string error)
    {
        lock (Gate)
        {
            if (_thread is null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "KoLibri GTK" };
                _thread.Start();
            }
        }
        if (!Ready.Wait(WebKitGtkView.CreateLimit)) { error = "GTK did not start in time"; return false; }
        error = _error ?? "";
        return _error is null;
    }

    private static void Run()
    {
        try
        {
            WebKit.Load();
            // Avalonia draws through X11 (also under Wayland, via XWayland); the reparented GTK window must be X11 too.
            Gdk.gdk_set_allowed_backends("x11");
            if (!Gtk.gtk_init_check(IntPtr.Zero, IntPtr.Zero)) throw new InvalidOperationException("gtk_init_check failed (no X11 display?)");
        }
        catch (Exception exception)
        {
            _error = exception is DllNotFoundException or EntryPointNotFoundException
                ? $"native library missing ({exception.Message})" : exception.Message;
            Ready.Set();
            return;
        }
        Ready.Set();
        Gtk.gtk_main();
    }

    public static void Post(Action action)
    {
        if (_thread is null || _error is not null) return;
        Work.Enqueue(action);
        unsafe { GLib.g_idle_add((IntPtr)(delegate* unmanaged<IntPtr, int>)&Drain, IntPtr.Zero); }
    }

    public static Task<T> InvokeAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => { try { tcs.SetResult(func()); } catch (Exception e) { tcs.SetException(e); } });
        return tcs.Task;
    }

    [UnmanagedCallersOnly]
    private static int Drain(IntPtr _)
    {
        while (Work.TryDequeue(out var action))
        {
            try { action(); } catch { /* one failed call never stops the GTK loop */ }
        }
        return 0; // G_SOURCE_REMOVE
    }
}

internal static class Gtk
{
    private const string Lib = "libgtk-3.so.0";
    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool gtk_init_check(IntPtr argc, IntPtr argv);
    [DllImport(Lib)] public static extern void gtk_main();
    [DllImport(Lib)] public static extern IntPtr gtk_window_new(int type);
    [DllImport(Lib)] public static extern void gtk_window_set_decorated(IntPtr window, int on);
    [DllImport(Lib)] public static extern void gtk_window_set_default_size(IntPtr window, int width, int height);
    [DllImport(Lib)] public static extern void gtk_container_add(IntPtr container, IntPtr widget);
    [DllImport(Lib)] public static extern void gtk_widget_realize(IntPtr widget);
    [DllImport(Lib)] public static extern void gtk_widget_show_all(IntPtr widget);
    [DllImport(Lib)] public static extern void gtk_widget_destroy(IntPtr widget);
    [DllImport(Lib)] public static extern IntPtr gtk_widget_get_window(IntPtr widget);
}

internal static class Gdk
{
    private const string Lib = "libgdk-3.so.0";
    [DllImport(Lib)] public static extern void gdk_set_allowed_backends([MarshalAs(UnmanagedType.LPUTF8Str)] string backends);
    [DllImport(Lib)] public static extern ulong gdk_x11_window_get_xid(IntPtr window);
}

internal static class GLib
{
    [DllImport("libglib-2.0.so.0")] public static extern uint g_idle_add(IntPtr function, IntPtr data);
}

internal static class GObject
{
    private const string Lib = "libgobject-2.0.so.0";
    [DllImport(Lib)] public static extern void g_object_unref(IntPtr obj);
    [DllImport(Lib)] private static extern ulong g_signal_connect_data(IntPtr instance, [MarshalAs(UnmanagedType.LPUTF8Str)] string signal,
        IntPtr handler, IntPtr data, IntPtr destroy, int flags);
    public static void Connect(IntPtr instance, string signal, IntPtr handler, IntPtr data)
        => g_signal_connect_data(instance, signal, handler, data, IntPtr.Zero, 0);
    [DllImport(Lib)] private static extern uint g_signal_handlers_disconnect_matched(IntPtr instance, int mask, uint signalId, uint detail,
        IntPtr closure, IntPtr func, IntPtr data);
    /// <summary>Drops every handler that carries <paramref name="data"/> (G_SIGNAL_MATCH_DATA).</summary>
    public static void DisconnectByData(IntPtr instance, IntPtr data) => g_signal_handlers_disconnect_matched(instance, 16, 0, 0, IntPtr.Zero, IntPtr.Zero, data);
}

/// <summary>WebKitGTK, bound at run time: 4.1 (GTK 3 + libsoup 3) first, then 4.0 (libsoup 2). Never both in one process.</summary>
internal static unsafe class WebKit
{
    internal static readonly string[] Candidates = ["libwebkit2gtk-4.1.so.0", "libwebkit2gtk-4.0.so.37"];
    public static string? LibraryName { get; private set; }

    private static delegate* unmanaged<IntPtr> _newEphemeral;
    private static delegate* unmanaged<IntPtr, IntPtr> _newWithContext, _newRelated;
    private static delegate* unmanaged<IntPtr, byte*, void> _loadUri;
    private static delegate* unmanaged<IntPtr, void> _goBack, _goForward, _reload, _use, _ignore;
    private static delegate* unmanaged<IntPtr, int> _canGoBack, _canGoForward;
    private static delegate* unmanaged<IntPtr, IntPtr> _getUri, _navAction, _actionRequest, _requestUri;

    public static void Load()
    {
        IntPtr lib = IntPtr.Zero;
        foreach (var name in Candidates)
            if (NativeLibrary.TryLoad(name, out lib)) { LibraryName = name; break; }
        if (lib == IntPtr.Zero) throw new DllNotFoundException($"none of {string.Join(", ", Candidates)} found");
        IntPtr F(string n) => NativeLibrary.GetExport(lib, n);
        _newEphemeral = (delegate* unmanaged<IntPtr>)F("webkit_web_context_new_ephemeral");
        _newWithContext = (delegate* unmanaged<IntPtr, IntPtr>)F("webkit_web_view_new_with_context");
        _newRelated = (delegate* unmanaged<IntPtr, IntPtr>)F("webkit_web_view_new_with_related_view");
        _loadUri = (delegate* unmanaged<IntPtr, byte*, void>)F("webkit_web_view_load_uri");
        _goBack = (delegate* unmanaged<IntPtr, void>)F("webkit_web_view_go_back");
        _goForward = (delegate* unmanaged<IntPtr, void>)F("webkit_web_view_go_forward");
        _reload = (delegate* unmanaged<IntPtr, void>)F("webkit_web_view_reload");
        _canGoBack = (delegate* unmanaged<IntPtr, int>)F("webkit_web_view_can_go_back");
        _canGoForward = (delegate* unmanaged<IntPtr, int>)F("webkit_web_view_can_go_forward");
        _getUri = (delegate* unmanaged<IntPtr, IntPtr>)F("webkit_web_view_get_uri");
        _use = (delegate* unmanaged<IntPtr, void>)F("webkit_policy_decision_use");
        _ignore = (delegate* unmanaged<IntPtr, void>)F("webkit_policy_decision_ignore");
        _navAction = (delegate* unmanaged<IntPtr, IntPtr>)F("webkit_navigation_policy_decision_get_navigation_action");
        _actionRequest = (delegate* unmanaged<IntPtr, IntPtr>)F("webkit_navigation_action_get_request");
        _requestUri = (delegate* unmanaged<IntPtr, IntPtr>)F("webkit_uri_request_get_uri");
    }

    public static IntPtr NewEphemeralContext() => _newEphemeral();
    public static IntPtr NewViewWithContext(IntPtr context) => _newWithContext(context);
    public static IntPtr NewRelatedView(IntPtr view) => _newRelated(view);
    public static void LoadUri(IntPtr view, string uri)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(uri + "\0");
        fixed (byte* p = bytes) _loadUri(view, p);
    }
    public static void GoBack(IntPtr view) => _goBack(view);
    public static void GoForward(IntPtr view) => _goForward(view);
    public static void Reload(IntPtr view) => _reload(view);
    public static bool CanGoBack(IntPtr view) => _canGoBack(view) != 0;
    public static bool CanGoForward(IntPtr view) => _canGoForward(view) != 0;
    public static string? CurrentUri(IntPtr view) => Marshal.PtrToStringUTF8(_getUri(view));
    public static void Use(IntPtr decision) => _use(decision);
    public static void Ignore(IntPtr decision) => _ignore(decision);
    public static string? DecisionUri(IntPtr decision)
    {
        var action = _navAction(decision);
        return action == IntPtr.Zero ? null : ActionUri(action);
    }
    public static string? ActionUri(IntPtr action)
    {
        var request = _actionRequest(action);
        return request == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(_requestUri(request));
    }
}
