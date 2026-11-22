using Avalonia;
using Avalonia.WebView.Desktop;

namespace KoLibri.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
        // The in-app relay browser needs a native web view. When the runtime is missing the Browser page says so.
        if (Environment.GetEnvironmentVariable("KOLIBRI_NO_WEBVIEW") != "1")
        {
            try { builder = builder.UseDesktopWebView(); }
            catch (Exception exception) { Console.Error.WriteLine($"Web view unavailable: {exception.GetType().Name}"); }
        }
        return builder;
    }
}
