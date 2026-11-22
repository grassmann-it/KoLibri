# In-game browser for the desktop app

Goal: let people play KoL in the normal web pages inside the KoLibri desktop
app, using the same account session as the rest of the app.

**Status: not implemented.** The desktop app has a "Browser" page that is a
placeholder. This note records why, and what would be needed.

> The options below are written from general knowledge of the Avalonia
> ecosystem and were **not** re-checked against current package releases or
> run on all three systems in this pass. Verify versions before deciding.

## Options

| Option | Windows | Linux | macOS | Packaging / licence |
| --- | --- | --- | --- | --- |
| Avalonia's own `NativeWebView` (Avalonia 12 / Accelerate) | WebView2 | WebKitGTK | WKWebView | Needs a newer Avalonia major than the 11.3 used here; some parts may be commercial. Smallest native footprint if available. |
| Community wrappers (for example WebView.Avalonia, Avalonia.WebView) | WebView2 | WebKitGTK (GTK3 required) | WKWebView | Small packages, but maintenance is uneven and Linux needs GTK/WebKit system libraries. |
| CefGlue / CefNet (Chromium) | yes | yes | yes | Same engine everywhere and full cookie API, but ~100+ MiB native payload per platform, per-RID packaging, and a sandbox/helper-process setup. |
| Embedding via the system browser only | n/a | n/a | n/a | No embedding. Open the KoL URL in the user's browser. Zero packaging, but the session is not shared. |

## Sessions and cookies

KoLibri authenticates with its own HTTP client and session cookie. A web view
would need that cookie injected: WebView2 `CookieManager`, WebKitGTK
`WebKitCookieManager`, WKWebView `WKHTTPCookieStore`, CEF `CefCookieManager`.
All four can do it, with different APIs. The reverse direction (reading the
cookie back after the user logs in inside the view) works the same way. The
password must never be stored, so the safe flow is to share the existing
session cookie from the app into the view, not to log in again.

## Recommendation

1. Keep the placeholder for now.
2. When Avalonia is moved to a version that ships a first-party native web
   view, re-evaluate: it is the only option with small native packaging on all
   three systems.
3. Until then, do not add CEF just for this. If a browser is wanted sooner,
   ship it as an optional plugin project so the main app stays light.
4. Whatever is chosen, route page loads through a small abstraction
   (`IGameBrowser`: navigate, set session cookie, read cookie) so the engine
   can be swapped.
