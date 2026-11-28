# Desktop relay browser

The **Browser** button opens KoL for the selected account through a relay bound to
`127.0.0.1`. The relay keeps the KoL session server-side and gives the browser its own
relay session cookie. The address bar and relay log redact sensitive URL values.

| OS | Embedded backend | Requirements |
| --- | --- | --- |
| Linux | KoLibri's WebKitGTK host. GTK runs on a dedicated thread and Avalonia embeds the X11 window through `NativeControlHost`. | GTK 3 and `libwebkit2gtk-4.1.so.0` (e.g. `libwebkit2gtk-4.1-0` on Debian/Ubuntu). The host also accepts `libwebkit2gtk-4.0.so.37`. Requires X11 or XWayland. |
| Windows | WebView2 via WebView.Avalonia | Microsoft Edge WebView2 Runtime. |
| macOS | WKWebView via WebView.Avalonia | macOS system WebKit. |

The embedded view allows navigation within the relay. External HTTP(S) links open in the
system browser after confirmation. On Linux, same-origin popups such as item descriptions
open in separate GTK windows and share the main view's in-memory relay cookie. On Windows
and macOS, same-origin new-window requests load in the existing view.

If the WebKitGTK/WebView2 runtime is missing or the first navigation times out after 15
seconds, use **Open in system browser** on the Browser page. (On Linux the desktop app itself
needs X11 or XWayland; pure Wayland is not supported.) Set
`KOLIBRI_NO_WEBVIEW=1` or `KOLIBRI_EMBED_WEBVIEW=0` to select this mode at startup.
The system browser shares loopback cookies across ports within a profile.
