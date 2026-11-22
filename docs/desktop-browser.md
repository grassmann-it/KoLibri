# In-app relay browser (Browser page)

The desktop app has a **Browser** button in the top bar (not a fifth main tab). It starts the KoLibri relay inside
the app and shows it in an embedded web view, like KoLmafia's built-in browser. You play KoL's normal pages with the
stored session of the selected account; no password and no KoL cookie ever reach the web view.

## How it works

1. `RelayHost` (`src/KoLibri.Core/Basis/Relay`) wraps `RelayServer`: bound to `127.0.0.1` on a free port, upstream
   through the global KoL rate limiter, a per-run secret token, redacted JSONL log (`relay-log.jsonl`), exact
   `Host: 127.0.0.1:PORT` check, 403 for foreign origins / missing cookie. `kolibri relay` (CLI) uses the same class.
2. `BrowserView` starts a host for the selected role, creates the web view and navigates to
   `http://127.0.0.1:PORT/__kolibri/enter?t=<token>&next=/main.php`. The relay answers with an
   `HttpOnly; SameSite=Strict` session cookie that lands in the **web view's own cookie jar**, not in the user's
   normal browser. Later page loads carry only that cookie.
3. The token is only kept in memory. The address bar shows paths only (the enter URL shows as `/`), the log and
   status line never contain it. Changing the account or closing the window stops the relay; a new run has a new token.
4. The view is pinned to the relay origin. Typed addresses must be relay paths (`/adventure.php`, `main.php`).
   A link to another site is cancelled; the user is asked "Open in system browser?" and only a yes opens it.
   Non-http(s) targets (`javascript:`, `file:`...) are cancelled silently. New windows are never opened.
   (`BrowserNavigationPolicy`, unit-tested.)
5. If the web view cannot run (runtime missing, or `KOLIBRI_NO_WEBVIEW=1`), the page shows a readable message and
   the button **Open in system browser** opens the relay enter URL in the default browser. That path shares cookies
   with the user's other tabs on other `127.0.0.1` ports; do not browse other local servers with that profile while
   the relay runs (see `RelayServer` remarks).

## Decision: which web view

Avalonia in this repo is **11.3.11**.

| Option | Verdict |
| --- | --- |
| Avalonia `NativeWebView` / `Avalonia.Controls.WebView` | Needs Avalonia 12 (the package targets 12.x). Moving the whole app from 11 to 12 is out of scope for this package. Re-evaluate on the 12 upgrade: it would replace the adapter below. |
| **`WebView.Avalonia` + `WebView.Avalonia.Desktop` 11.0.0.1 (MIT)** | **Chosen.** Built for Avalonia 11, no commercial license, tiny: WebView2 on Windows, WebKitGTK on Linux (via GtkSharp), WKWebView on macOS. `UseDesktopWebView()` is called at startup in `Program.cs`; the control is created only when the Browser page is opened. |
| CefGlue / `WebViewControl-Avalonia` (Chromium) | Same engine everywhere, but 100+ MiB native payload per platform and helper processes. Not worth it for a relay page. |
| System browser only | Kept as the fallback button. |

Why the adapter stays small: only `BrowserView.cs` touches the package (navigate, back/forward/reload, navigation
and new-window events), so swapping the engine means replacing one file.

## Platform caveats (not verified on all systems)

* **Windows:** needs the Microsoft Edge WebView2 Runtime (preinstalled on current Windows 10/11). Not tested yet.
* **macOS:** WKWebView; the 11.0.0.1 package ships a "MacCatalyst" assembly name, so plain macOS support has to be
  checked by running it. Not tested yet.
* **Linux:** WebKitGTK 4.x and GTK 3 must be installed (`webkit2gtk` / `gtk3`); embedding a GTK widget in an
  Avalonia X11 window can fail on Wayland-only sessions. If nothing answers within 8 seconds the page falls back to
  the message plus the system-browser button. The package's Linux native bindings were not run in this pass; only
  start-up without GTK and the fallback page were checked (headless Xvfb).
* **Cookie lifetime:** the relay cookie is a session cookie (no expiry), so engines normally drop it on exit; the
  package does not expose a reliable private-mode switch for every engine. Even if it were kept, the next run uses a
  new secret and answers 403 until the enter URL is used.
* The package is old (2023 era API) and maintained by one person; keep the abstraction thin.

## Auto-login

"Log in again automatically" is now **on by default** (existing settings files that saved `false` keep it off). At
desktop start the selected role, then the other role, is checked once: a valid session is reused; a stale session or
a stored password without cookie is renewed with one login request through the global rate limiter (no retries); with
the setting off nothing is sent. The top-bar status line says what happened ("Main: signed in again", or a plain
instruction to use Sign in). The Sign in button stays visible. Logic: `StartupLogin`.
