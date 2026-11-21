using System.Text.Json;

/// <summary>
/// Agent mode. When the CLI runs under an AI harness, commands that change the game
/// need a confirmation typed at the real terminal (/dev/tty or the Windows console), which an AI that only
/// has a command channel cannot give. This guards against mistakes and against an AI
/// talking itself around the MCP limits through the CLI; it is not a security boundary
/// against a hostile program with the same operating-system user (see docs/safety).
/// </summary>
internal static class AgentGuard
{
    private static readonly string[] WriteFlags = ["--execute", "--commit", "--sell"];

    /// <summary>Environment variables set by known AI harnesses.</summary>
    private static readonly string[] AgentVariables =
    [
        "KOLIBRI_AGENT", "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CODEX_SANDBOX", "CODEX_SANDBOX_NETWORK_DISABLED",
        "GEMINI_CLI", "CURSOR_AGENT", "OPENCODE", "AIDER_CHAT_HISTORY_FILE",
    ];

    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KoLibri", "agent-mode.json");

    public static bool IsWrite(string[] args)
        => args.Any(a => WriteFlags.Contains(a, StringComparer.OrdinalIgnoreCase));

    public static bool LooksLikeAgent(Func<string, string?> env, bool stdinRedirected, bool stdoutRedirected)
        => AgentVariables.Any(v => !string.IsNullOrEmpty(env(v))) || (stdinRedirected && stdoutRedirected);

    /// <summary>Set by the human with <c>kolibri agent-mode off</c> for their own scripts.</summary>
    public static bool UnattendedAllowed(string? path = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path ?? ConfigPath));
            return doc.RootElement.TryGetProperty("allowUnattended", out var v) && v.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return false; }
    }

    /// <summary>
    /// Returns null when the command may run, otherwise the exit code to stop with.
    /// <paramref name="confirm"/> asks at the real terminal; null when none is available.
    /// </summary>
    public static int? Check(
        string[] args, Func<string, string?> env, bool stdinRedirected, bool stdoutRedirected,
        bool unattendedAllowed, Func<string, bool?> confirm, TextWriter error)
    {
        if (!IsWrite(args) || unattendedAllowed) return null;
        if (!LooksLikeAgent(env, stdinRedirected, stdoutRedirected)) return null;

        var answer = confirm("An agent/script wants to change something in the game: kolibri " + string.Join(' ', args.Select(Shorten))
            + "\nAllow? (yes/no): ");
        if (answer is true) return null;
        error.WriteLine(answer is null
            ? "Agent mode: write actions require confirmation at the terminal, but none is reachable here. Nothing was sent.\n"
              + "Own scripts: run 'kolibri agent-mode allow' once in a real terminal."
            : "Agent mode: not confirmed. Nothing was sent.");
        return 3;
    }

    /// <summary>
    /// Reads yes/no from the real terminal (/dev/tty on Linux and macOS, the console device on Windows),
    /// not from stdin, which an agent controls. Null when no terminal exists.
    /// </summary>
    public static bool? ConfirmOnTerminal(string question)
    {
        try
        {
            using var input = OpenTerminal(write: false);
            using var output = OpenTerminal(write: true);
            if (input is null || output is null) return null;
            using var writer = new StreamWriter(output) { AutoFlush = true };
            using var reader = new StreamReader(input);
            writer.Write(question);
            var line = reader.ReadLine()?.Trim().ToLowerInvariant();
            return line is "ja" or "j" or "yes" or "y";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static Stream? OpenTerminal(bool write)
    {
        if (!OperatingSystem.IsWindows())
            return new FileStream("/dev/tty", FileMode.Open, write ? FileAccess.Write : FileAccess.Read);

        // CONIN$ / CONOUT$ are the console of this process even when stdin/stdout are redirected.
        var handle = NativeMethods.CreateFileW(write ? "CONOUT$" : "CONIN$", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); return null; }
        return new FileStream(handle, write ? FileAccess.Write : FileAccess.Read, 1, false);
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        internal static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
            string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
    }

    /// <summary><c>kolibri agent-mode status|allow|guard</c>; changing it always needs the real terminal.</summary>
    public static int Command(string[] args)
    {
        var verb = args.FirstOrDefault()?.ToLowerInvariant() ?? "status";
        if (verb == "status")
        {
            Console.WriteLine(UnattendedAllowed()
                ? "Agent mode off: scripts may write (agent-mode guard turns it back on)."
                : "Agent mode on: write actions by AI/scripts require confirmation at the terminal.");
            return 0;
        }
        if (verb is not ("allow" or "guard")) { Console.Error.WriteLine("agent-mode status|allow|guard"); return 2; }
        var ok = ConfirmOnTerminal(verb == "allow"
            ? "Scripts and agents may write without asking. This removes the safeguard. Are you sure? (yes/no): "
            : "Turn agent mode back on? (yes/no): ");
        if (ok is not true) { Console.Error.WriteLine(ok is null ? "No terminal reachable." : "Cancelled."); return 3; }
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new { allowUnattended = verb == "allow" }));
        Console.WriteLine("ok");
        return 0;
    }

    private static string Shorten(string a) => a.Length > 60 ? a[..60] + "…" : a;
}
