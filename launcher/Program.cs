using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

internal static class Program
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpotifyLocalBridge");
    private static readonly string SavedPath = Path.Combine(SettingsDirectory, "lyricify-path.txt");
    private static readonly string LogPath = Path.Combine(SettingsDirectory, "hook.log");
    // shortcut: old versioned payloads remain on disk while an older hooked Lyricify may still use them.
    private static readonly string RuntimeDirectory = Path.Combine(SettingsDirectory, "runtime",
        typeof(Program).Assembly.ManifestModule.ModuleVersionId.ToString("N"));
    private static readonly string HookPath = Path.Combine(RuntimeDirectory, "StartupHook.dll");
    private static readonly string LauncherPath = Environment.ProcessPath
        ?? Path.Combine(AppContext.BaseDirectory, "LyricifyLauncher.exe");
    private static readonly string[] PayloadFiles =
    [
        "StartupHook.dll", "StartupHook.deps.json", "0Harmony.dll", "System.Text.Json.dll",
        "spotify-local-bridge.js"
    ];
    private static readonly string SpotifyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spotify", "Spotify.exe");
    private static readonly string InstallManifest = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spotify", "Apps", "xpui", "spotify-local-bridge-install.json");
    private static readonly string ShortcutPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Start Menu", "Programs", "Lyricify", "Lyricify 4.lnk");
    private static readonly string ShortcutBackupPath = Path.Combine(SettingsDirectory, "Lyricify 4.original.lnk");
    private const string StartingMessage = "Starting Lyricify. The hook may take a few seconds to connect.";
    private static readonly int[] MenuRows = [5, 7, 9, 11, 18];
    internal sealed record PanelPrompt(string Title, string Detail, string Value, string Footer,
        bool Editing = false, bool Confirmation = false);

    internal static string[] MenuLabels(bool installed, string shortcut) =>
    [
        installed ? "Remove Spotify patch" : "Install Spotify patch",
        shortcut == "Patched" ? "Restore Lyricify shortcut"
            : shortcut == "Patched (old location)" ? "Repair Lyricify shortcut"
            : "Enable Lyricify shortcut",
        "Set Lyricify path",
        "View hook log",
        "Exit"
    ];

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint window, string text, string caption, uint type);

    private static async Task<int> Main(string[] args)
    {
        var launchOnly = args.Length > 0 && args[0] == "--launch-only";
        if (!launchOnly) Console.OutputEncoding = Encoding.UTF8;
        var interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;
        if (interactive)
        {
            try { _ = Console.KeyAvailable; }
            catch (InvalidOperationException) { interactive = false; }
        }
        try
        {
            EnsurePayloadFiles();
            if (launchOnly)
            {
                if (args.Length != 2) throw new ArgumentException("The shortcut needs one Lyricify EXE path.");
                var target = ValidatePath(args[1]);
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
                var connected = (await ReadBridgeAsync(client)).Connected;
                if (IsRunning(Path.GetFileNameWithoutExtension(target), target) && !connected)
                    throw new InvalidOperationException("Lyricify is already running without the hook. Exit it from the tray and try again.");
                Launch(target, connected);
                return 0;
            }
            if (args.Length > 1) throw new ArgumentException("Pass at most one Lyricify EXE path.");
            var exe = args.Length == 1 ? ValidatePath(args[0]) : FindLyricify();
            if (args.Length == 1) SavePath(exe);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1), MaxResponseContentBufferSize = 64 * 1024 };
            var message = "";
            DateTime? pendingSince = null;
            if (args.Length == 1)
            {
                message = Launch(exe, (await ReadBridgeAsync(http)).Connected);
                if (message == StartingMessage) pendingSince = DateTime.UtcNow;
            }
            Console.Title = "Spotify Local Bridge";
            if (interactive) { Console.Clear(); Console.CursorVisible = false; }
            string[] previous = [];
            var previousWidth = interactive ? Math.Max(1, Console.WindowWidth - 1) : 0;
            var selection = 0;
            var refreshAt = DateTime.MinValue;
            var bridge = new BridgeView(false);
            if (!interactive)
            {
                bridge = await ReadBridgeAsync(http);
                Render(exe, bridge, message, pendingSince != null, selection, false, ref previous, ref previousWidth);
                return 0;
            }
            Task<BridgeView>? pendingRead = null;
            void ShowPrompt(PanelPrompt prompt) => Render(exe, bridge, message, pendingSince != null,
                selection, interactive, ref previous, ref previousWidth, prompt);
            while (true)
            {
                if (pendingRead == null && DateTime.UtcNow >= refreshAt)
                    pendingRead = ReadBridgeAsync(http);
                if (pendingRead?.IsCompleted == true)
                {
                    bridge = await pendingRead;
                    pendingRead = null;
                    if (pendingSince != null)
                    {
                        if (bridge.Connected)
                        {
                            message = "Hook connected. Lyricify is ready.";
                            pendingSince = null;
                        }
                        else if (DateTime.UtcNow - pendingSince.Value > TimeSpan.FromSeconds(15))
                        {
                            message = "Hook not connected. Check the log or relaunch Lyricify from its shortcut.";
                            pendingSince = null;
                        }
                    }
                    Render(exe, bridge, message, pendingSince != null, selection, interactive, ref previous, ref previousWidth);
                    if (!interactive) return 0;
                    refreshAt = DateTime.UtcNow.AddMilliseconds(250);
                }
                bool keyAvailable;
                try { keyAvailable = Console.KeyAvailable; }
                catch (InvalidOperationException) { interactive = false; return 0; }
                if (keyAvailable)
                {
                    var key = Console.ReadKey(true).Key;
                    if (key is ConsoleKey.UpArrow or ConsoleKey.DownArrow)
                    {
                        selection = (selection + (key == ConsoleKey.DownArrow ? 1 : MenuRows.Length - 1)) % MenuRows.Length;
                        Render(exe, bridge, message, pendingSince != null, selection, interactive, ref previous, ref previousWidth);
                        continue;
                    }
                    if (key is ConsoleKey.Q or ConsoleKey.Escape) return 0;
                    if (key != ConsoleKey.Enter) continue;
                    if (selection == MenuRows.Length - 1) return 0;
                    try
                    {
                        switch (selection)
                        {
                            case 0:
                                var install = !File.Exists(InstallManifest);
                                if (install && (!File.Exists(HookPath) || !File.Exists(Path.Combine(RuntimeDirectory, "spotify-local-bridge.js"))))
                                    throw new InvalidOperationException("Bridge files are incomplete. Keep the full package beside the launcher.");
                                var source = install ? FindXpuiSource(Path.Combine(Path.GetDirectoryName(SpotifyPath)!, "Apps"),
                                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "spicetify", "Backup")) : null;
                                ShowPrompt(install
                                    ? new("CONFIRM INSTALL", "Close Spotify and Lyricify", "Install Spotify patch automatically", "Y Confirm    Esc Cancel", Confirmation: true)
                                    : new("CONFIRM REMOVAL", "Close Spotify and Lyricify", "Remove Spotify patch", "Y Confirm    Esc Cancel", Confirmation: true));
                                if (Console.ReadKey(true).Key != ConsoleKey.Y)
                                {
                                    message = install ? "Installation cancelled." : "Uninstallation cancelled.";
                                    break;
                                }
                                message = await ChangeInstallationAsync(install, exe, source,
                                    stage => ShowPrompt(new("UPDATING SPOTIFY", stage, "Please wait", "Working...")));
                                pendingSince = null;
                                break;
                            case 1:
                                var launcher = LauncherPath;
                                if (ShortcutState(ShortcutPath, ShortcutBackupPath, launcher) == "Patched")
                                {
                                    RestoreShortcut(ShortcutPath, ShortcutBackupPath, launcher);
                                    message = "Original Lyricify shortcut restored.";
                                }
                                else
                                {
                                    if (string.IsNullOrEmpty(exe)) throw new InvalidOperationException("Set the Lyricify path first.");
                                    if (!File.Exists(InstallManifest)) throw new InvalidOperationException("Install the Spotify patch first.");
                                    PatchShortcut(ShortcutPath, ShortcutBackupPath, launcher, exe);
                                    message = "Lyricify shortcut now loads the hook.";
                                }
                                break;
                            case 2:
                                var pathInput = ReadPanelInput(new("SET LYRICIFY PATH", $"Current: {Path.GetFileName(exe)}", "", "Enter Keep/Save    Esc Cancel", true), ShowPrompt);
                                if (pathInput == null) { message = "Path change cancelled."; break; }
                                if (string.IsNullOrWhiteSpace(pathInput)) { message = "Lyricify path unchanged."; break; }
                                exe = ValidatePath(pathInput);
                                SavePath(exe);
                                message = "Lyricify path saved.";
                                break;
                            case 3:
                                ShowLog();
                                previous = [];
                                break;
                        }
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        message = error.Message;
                        previous = [];
                    }
                    Render(exe, bridge, message, pendingSince != null, selection, interactive, ref previous, ref previousWidth);
                    refreshAt = DateTime.MinValue;
                }
                await Task.Delay(25);
            }
        }
        catch (Exception error)
        {
            if (launchOnly)
            {
                MessageBox(0, error.Message, "Lyricify Local Bridge", 0x10);
                return 1;
            }
            Console.WriteLine($"Launcher failed: {error.Message}");
            if (interactive) { Console.WriteLine("Press any key to exit."); Console.ReadKey(true); }
            return 1;
        }
        finally
        {
            if (!launchOnly) Console.ResetColor();
            if (interactive && !launchOnly) { Console.CursorVisible = true; Console.WriteLine(); }
        }
    }

    private static void EnsurePayloadFiles()
    {
        Directory.CreateDirectory(RuntimeDirectory);
        var assembly = typeof(Program).Assembly;
        foreach (var name in PayloadFiles)
        {
            using var resource = assembly.GetManifestResourceStream("SpotifyLocalBridge.Payload." + name)
                ?? throw new InvalidOperationException($"Launcher payload is missing {name}. Rebuild the single EXE package.");
            using var content = new MemoryStream();
            resource.CopyTo(content);
            var bytes = content.ToArray();
            var destination = Path.Combine(RuntimeDirectory, name);
            if (File.Exists(destination) && File.ReadAllBytes(destination).AsSpan().SequenceEqual(bytes)) continue;
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, bytes);
                if (!File.Exists(destination) || !File.ReadAllBytes(destination).AsSpan().SequenceEqual(bytes))
                    File.Move(temporary, destination, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private static string FindLyricify()
    {
        if (File.Exists(SavedPath))
        {
            var saved = File.ReadAllText(SavedPath).Trim();
            if (File.Exists(saved)) return saved;
        }
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Lyricify 4", "Lyricify for Spotify.exe");
        return File.Exists(installed) ? installed : "";
    }

    internal static string ValidatePath(string value)
    {
        var path = Path.GetFullPath(value.Trim().Trim('"'));
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new ArgumentException("Lyricify EXE not found. Paste its full path.");
        return path;
    }

    private static T WithShortcut<T>(string path, Func<dynamic, T> action)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows shortcut support is unavailable.");
        var shell = Activator.CreateInstance(shellType)!;
        try
        {
            var link = ((dynamic)shell).CreateShortcut(path);
            try { return action(link); }
            finally { Marshal.FinalReleaseComObject(link); }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    internal static (string Target, string Arguments) ReadShortcut(string path) =>
        WithShortcut(path, link => ((string)link.TargetPath, (string)link.Arguments));

    internal static string ShortcutState(string path, string backup, string launcher)
    {
        if (!File.Exists(path)) return "Not found";
        try
        {
            var (target, arguments) = ReadShortcut(path);
            if (File.Exists(backup))
                return string.Equals(Path.GetFileName(target), Path.GetFileName(launcher), StringComparison.OrdinalIgnoreCase)
                    && arguments.StartsWith("--launch-only ", StringComparison.Ordinal)
                    ? MatchesExecutable(target, launcher) ? "Patched" : "Patched (old location)"
                    : "Changed externally";
            return MatchesExecutable(target, launcher) ? "Unmanaged launcher link" : "Original";
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException) { return "Unavailable"; }
    }

    internal static void PatchShortcut(string path, string backup, string launcher, string exe)
    {
        exe = ValidatePath(exe);
        if (!File.Exists(path)) throw new FileNotFoundException("Lyricify Start Menu shortcut was not found.", path);
        if (!File.Exists(launcher)) throw new FileNotFoundException("Launcher EXE was not found.", launcher);
        var managed = File.Exists(backup);
        if (managed && !ShortcutState(path, backup, launcher).StartsWith("Patched", StringComparison.Ordinal))
            throw new InvalidOperationException("The shortcut changed externally; its original backup was left intact.");
        if (!managed && MatchesExecutable(ReadShortcut(path).Target, launcher))
            throw new InvalidOperationException("This shortcut already points to the launcher, but has no restorable backup.");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        var previousLink = File.ReadAllBytes(path);
        if (!managed) File.Copy(path, backup);
        try
        {
            WithShortcut(path, link =>
            {
                link.TargetPath = launcher;
                link.Arguments = $"--launch-only \"{exe}\"";
                link.WorkingDirectory = Path.GetDirectoryName(launcher)!;
                link.IconLocation = exe + ",0";
                link.WindowStyle = 7;
                link.Description = "Lyricify with Spotify Local Bridge";
                link.Save();
                return 0;
            });
        }
        catch
        {
            File.WriteAllBytes(path, previousLink);
            if (!managed) File.Delete(backup);
            throw;
        }
    }

    internal static void RestoreShortcut(string path, string backup, string launcher)
    {
        if (!File.Exists(backup)) throw new InvalidOperationException("No original Lyricify shortcut backup was found.");
        if (!File.Exists(path) || !ShortcutState(path, backup, launcher).StartsWith("Patched", StringComparison.Ordinal))
            throw new InvalidOperationException("The shortcut changed since patching; the original backup was left intact.");
        File.Copy(backup, path, overwrite: true);
        File.Delete(backup);
    }

    private static void SavePath(string exe)
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(SavedPath, exe);
    }

    private static string? ReadPanelInput(PanelPrompt prompt, Action<PanelPrompt> show)
    {
        var input = new StringBuilder();
        while (true)
        {
            show(prompt with { Value = input.ToString() });
            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Escape) return null;
            if (key.Key == ConsoleKey.Enter) return input.ToString();
            if (key.Key == ConsoleKey.Backspace && input.Length > 0) input.Length--;
            else if (!char.IsControl(key.KeyChar)) input.Append(key.KeyChar);
        }
    }

    private static void ShowLog()
    {
        var rawLines = ReadLogTail(LogPath);
        string[] lines = [];
        var showAll = false;
        var preparedWidth = 0;
        var start = 0;
        string[] previous = [];
        var previousWidth = 0;
        Console.Clear();
        try
        {
            while (true)
            {
                var width = Math.Max(1, Console.WindowWidth - 1);
                var height = Math.Max(1, Console.WindowHeight - 1);
                var pageSize = Math.Max(1, height - 2);
                if (width != preparedWidth)
                {
                    lines = PrepareLogLines(rawLines, width, showAll);
                    preparedWidth = width;
                    start = lines.Length;
                }
                start = Math.Clamp(start, 0, Math.Max(0, lines.Length - pageSize));
                var frame = BuildLogPanel(lines, width, height, start, showAll);
                try
                {
                    if (previousWidth != width || previous.Length != frame.Length)
                    {
                        Console.Clear();
                        previous = [];
                        previousWidth = width;
                    }
                    foreach (var row in ChangedRows(previous, frame))
                    {
                        Console.SetCursorPosition(0, row);
                        if (row == 0 || row == frame.Length - 1)
                        {
                            Console.ForegroundColor = row == 0 ? ConsoleColor.Cyan : ConsoleColor.DarkGray;
                            Console.Write(frame[row]);
                        }
                        else if (frame[row].Length >= 16 && frame[row][2] == '-' && frame[row][5] == ' '
                            && frame[row][8] == ':' && frame[row][11] == ':')
                        {
                            Console.ForegroundColor = ConsoleColor.DarkGray;
                            Console.Write(frame[row][..16]);
                            Console.ForegroundColor = LogTone(frame[row][16..]);
                            Console.Write(frame[row][16..]);
                        }
                        else
                        {
                            Console.ForegroundColor = LogTone(frame[row]);
                            Console.Write(frame[row]);
                        }
                    }
                    previous = frame;
                }
                catch (ArgumentOutOfRangeException) { previous = []; }

                switch (Console.ReadKey(true).Key)
                {
                    case ConsoleKey.Q:
                    case ConsoleKey.Escape:
                    case ConsoleKey.Enter:
                        return;
                    case ConsoleKey.UpArrow: start--; break;
                    case ConsoleKey.DownArrow: start++; break;
                    case ConsoleKey.PageUp: start -= pageSize; break;
                    case ConsoleKey.PageDown: start += pageSize; break;
                    case ConsoleKey.Home: start = 0; break;
                    case ConsoleKey.End: start = lines.Length; break;
                    case ConsoleKey.R:
                        var atEnd = start == Math.Max(0, lines.Length - pageSize);
                        rawLines = ReadLogTail(LogPath);
                        lines = PrepareLogLines(rawLines, width, showAll);
                        if (atEnd) start = lines.Length;
                        break;
                    case ConsoleKey.F:
                        showAll = !showAll;
                        lines = PrepareLogLines(rawLines, width, showAll);
                        start = lines.Length;
                        break;
                }
            }
        }
        finally { Console.ResetColor(); Console.Clear(); Console.CursorVisible = false; }
    }

    internal static string[] ReadLogTail(string path)
    {
        if (!File.Exists(path)) return [];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new Queue<string>();
        while (reader.ReadLine() is { } line)
        {
            if (lines.Count == 10000) lines.Dequeue();
            lines.Enqueue(line);
        }
        return lines.ToArray();
    }

    internal static string[] PrepareLogLines(string[] rawLines, int width, bool showAll)
    {
        var output = new List<string>();
        var indent = width >= 32 ? new string(' ', 16) : width >= 8 ? "  " : "";
        foreach (var raw in rawLines)
        {
            var separator = raw.IndexOf(' ');
            DateTimeOffset timestamp = default;
            var hasTime = separator > 0 && DateTimeOffset.TryParseExact(raw[..separator], "O",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp);
            var message = hasTime ? raw[(separator + 1)..] : raw;
            if (!showAll && IsRoutineLog(message)) continue;
            var remaining = Clip(hasTime ? $"{timestamp:MM-dd HH:mm:ss}  {message}" : message, int.MaxValue);
            if (remaining.Length == 0) { output.Add(""); continue; }
            var first = true;
            while (remaining.Length > 0)
            {
                var prefix = first ? "" : indent;
                var segment = Clip(remaining, Math.Max(1, width - prefix.Length));
                if (segment.Length == 0)
                {
                    segment = "?";
                    remaining = remaining[StringInfo.GetNextTextElement(remaining).Length..];
                }
                else remaining = remaining[segment.Length..];
                output.Add(prefix + segment);
                first = false;
            }
        }
        return output.ToArray();
    }

    private static bool IsRoutineLog(string message) =>
        message.StartsWith("ISRC CACHE ", StringComparison.Ordinal)
        || message.StartsWith("COMMAND POLL ", StringComparison.Ordinal)
        || message is "INGEST POST 204" or "INGEST OPTIONS 204"
        || (message.StartsWith("CONTROL RESULT library-check ", StringComparison.Ordinal)
            && message.EndsWith(" success=True", StringComparison.Ordinal));

    private static ConsoleColor LogTone(string message) =>
        message.Contains("FAILED", StringComparison.OrdinalIgnoreCase)
            || message.Contains("success=False", StringComparison.OrdinalIgnoreCase)
            || message.Contains("error=", StringComparison.OrdinalIgnoreCase) ? ConsoleColor.Red
        : message.Contains("WARN", StringComparison.OrdinalIgnoreCase)
            || message.Contains("TIMEOUT", StringComparison.OrdinalIgnoreCase)
            || message.Contains("RETRY", StringComparison.OrdinalIgnoreCase) ? ConsoleColor.Yellow
        : message.Contains("PATCHED", StringComparison.Ordinal)
            || message.Contains("BRIDGE LISTENING", StringComparison.Ordinal) ? ConsoleColor.Green
        : ConsoleColor.Gray;

    internal static string[] BuildLogPanel(string[] lines, int width, int height, int start, bool showAll)
    {
        if (height < 3)
        {
            var current = lines.Length == 0 ? " No log entries yet." : lines[Math.Clamp(start, 0, lines.Length - 1)];
            return height == 1 ? [Fit(current, width)] : [Fit(current, width), Fit(" Q Back  Up/Down Scroll", width)];
        }
        var pageSize = Math.Max(0, height - 2);
        var frame = new string[height];
        frame[0] = Fit($" HOOK LOG  {(showAll ? "ALL" : "EVENTS")}  {Math.Min(start + 1, lines.Length)}-{Math.Min(start + pageSize, lines.Length)} / {lines.Length}", width);
        for (var row = 0; row < pageSize; row++)
            frame[row + 1] = Fit(start + row < lines.Length ? lines[start + row]
                : lines.Length == 0 && row == 0 ? " No entries. Press F for all, or R to refresh." : "", width);
        if (height > 1) frame[^1] = Fit(width < 60
            ? $" Q Back  ↑↓ Scroll  F {(showAll ? "Events" : "All")}  R Reload"
            : $" Q Back  ↑↓ Scroll  PgUp/Dn Page  Home/End  F {(showAll ? "Events" : "All")}  R Refresh", width);
        return frame;
    }

    private static bool IsRunning(string name, string exe)
    {
        var processes = Process.GetProcessesByName(name);
        try
        {
            foreach (var process in processes)
            {
                try { if (MatchesExecutable(process.MainModule?.FileName, exe)) return true; }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
            return false;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static string Launch(string exe, bool connected)
    {
        exe = ValidatePath(exe);
        if (!File.Exists(InstallManifest))
            throw new InvalidOperationException("Spotify bridge is not installed. Press I to install it.");
        if (IsRunning(Path.GetFileNameWithoutExtension(exe), exe))
            return connected ? "Hook connected. Lyricify is ready."
                : "Exit Lyricify from the tray, then reopen it from the patched shortcut.";
        if (!File.Exists(HookPath))
            throw new InvalidOperationException("Hook file is missing. Keep the launcher beside lyricify-hook.");

        var port = new TcpListener(IPAddress.Loopback, 48711);
        try { port.Start(); }
        catch (SocketException) { throw new InvalidOperationException("Local port 48711 is in use. Close the old bridge first."); }
        finally { port.Stop(); }

        StartSpotify();
        SavePath(exe);
        using var process = Process.Start(CreateStartInfo(exe, HookPath, LogPath));
        if (process == null) throw new InvalidOperationException("Lyricify did not start. Check its EXE path.");
        return StartingMessage;
    }

    private static string StartSpotify()
    {
        if (IsRunning("Spotify", SpotifyPath)) return "Spotify is already running.";
        if (!File.Exists(SpotifyPath)) throw new InvalidOperationException("Spotify EXE not found. Install desktop Spotify first.");
        using var process = Process.Start(new ProcessStartInfo(SpotifyPath) { UseShellExecute = true });
        if (process == null) throw new InvalidOperationException("Spotify did not start.");
        return "Spotify started.";
    }

    internal static bool MatchesExecutable(string? actual, string expected) =>
        !string.IsNullOrEmpty(actual) && !string.IsNullOrEmpty(expected)
        && string.Equals(Path.GetFullPath(actual), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);

    internal static bool StopApp(string name, string exe)
    {
        if (string.IsNullOrEmpty(exe)) return false;
        var candidates = Process.GetProcessesByName(name);
        var targets = new List<Process>();
        try
        {
            foreach (var process in candidates)
            {
                try
                {
                    if (MatchesExecutable(process.MainModule?.FileName, exe)) targets.Add(process);
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
            foreach (var process in targets)
                if (!process.HasExited) process.CloseMainWindow();
            // shortcut: Tray apps can ignore window close; force stop after two seconds until they expose an exit command.
            if (targets.Count > 0) Thread.Sleep(2000);
            foreach (var process in targets)
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            foreach (var process in targets)
                if (!process.HasExited && !process.WaitForExit(5000)) throw new InvalidOperationException($"{name} did not stop.");
            return targets.Count > 0;
        }
        finally { foreach (var process in candidates) process.Dispose(); }
    }

    private static Task<string> ChangeInstallationAsync(bool install, string lyricifyExe,
        string? source, Action<string> report)
    {
        if (install == File.Exists(InstallManifest))
            return Task.FromResult(install ? "Spotify bridge is already installed." : "Spotify bridge is not installed.");
        report("Closing Spotify and Lyricify");
        StopApp(string.IsNullOrEmpty(lyricifyExe) ? "Lyricify for Spotify" : Path.GetFileNameWithoutExtension(lyricifyExe), lyricifyExe);
        StopApp("Spotify", SpotifyPath);
        report(install ? "Patching Spotify XPUI" : "Restoring original Spotify XPUI");
        var apps = Path.Combine(Path.GetDirectoryName(SpotifyPath)!, "Apps");
        var backupRoot = Path.Combine(SettingsDirectory, "backups");
        return Task.FromResult(install
            ? InstallPatch(source ?? throw new InvalidOperationException("Spotify XPUI source not found."), apps, backupRoot,
                Path.Combine(RuntimeDirectory, "spotify-local-bridge.js"))
            : UninstallPatch(apps, backupRoot));
    }

    internal static string FindXpuiSource(string apps, string spicetifyBackup)
    {
        foreach (var source in new[] { Path.Combine(apps, "xpui.spa"), Path.Combine(apps, "xpui"),
            Path.Combine(spicetifyBackup, "xpui.spa") })
        {
            if (!File.Exists(source) && !Directory.Exists(source)) continue;
            try
            {
                var (index, bundle) = ReadXpuiSource(source);
                _ = PatchXpui(index, bundle);
                return source;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException) { }
        }
        throw new InvalidOperationException("No compatible original Spotify XPUI found. Repair or reinstall desktop Spotify; newer clients may need a bridge update.");
    }

    private static (string Index, string Bundle) ReadXpuiSource(string source)
    {
        if (Directory.Exists(source))
            return (File.ReadAllText(Path.Combine(source, "index.html")), File.ReadAllText(Path.Combine(source, "xpui.js")));
        using var archive = ZipFile.OpenRead(source);
        string Read(string name)
        {
            var entry = archive.GetEntry(name) ?? throw new InvalidOperationException($"Spotify XPUI is missing {name}.");
            using var reader = new StreamReader(entry.Open());
            return reader.ReadToEnd();
        }
        return (Read("index.html"), Read("xpui.js"));
    }

    private static (string Index, string Bundle) PatchXpui(string index, string bundle)
    {
        if (index.Contains("spicetifyWrapper.js", StringComparison.Ordinal) || bundle.Contains("Spicetify._platform", StringComparison.Ordinal))
            throw new InvalidOperationException("Spotify XPUI is already modified by Spicetify.");
        const string scriptTag = "<script defer=\"defer\" src=\"/xpui.js\"></script>";
        if (Count(index, scriptTag) != 1) throw new InvalidOperationException("Spotify XPUI script tag changed; no files were installed.");
        foreach (var (from, to) in new[]
        {
            ("i.registerFactory(nq.J,e=>new n$(new Z.XO(e.resolve(ff.VI)),e.resolve(uo.z))),{version:n,container:e,operatingSystem:",
             "i.registerFactory(nq.J,e=>new n$(new Z.XO(e.resolve(ff.VI)),e.resolve(uo.z))),window.__spotifyLocalBridgeStart?.(i),{version:n,container:e,operatingSystem:"),
            ("u.registerFactory(ed.J,()=>new ep.Q),{version:n,container:e,operatingSystem:",
             "u.registerFactory(ed.J,()=>new ep.Q),window.__spotifyLocalBridgeStart?.(u),{version:n,container:e,operatingSystem:")
        })
        {
            if (Count(bundle, from) != 1) throw new InvalidOperationException("Spotify XPUI bootstrap changed; no files were installed.");
            bundle = bundle.Replace(from, to, StringComparison.Ordinal);
        }
        return (index.Replace(scriptTag, "<script defer=\"defer\" src=\"/spotify-local-bridge.js\"></script>" + scriptTag, StringComparison.Ordinal), bundle);
    }

    internal static string InstallPatch(string source, string apps, string backupRoot, string scriptPath)
    {
        if (!Directory.Exists(apps)) throw new InvalidOperationException($"Spotify Apps directory not found: {apps}");
        var live = Path.Combine(apps, "xpui");
        if (File.Exists(Path.Combine(live, "spotify-local-bridge-install.json")))
            throw new InvalidOperationException("The standalone bridge is already installed. Remove it before installing again.");
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture);
        var stage = Path.Combine(apps, "xpui.local-bridge-stage-" + stamp);
        var previous = Directory.Exists(live) ? Path.Combine(backupRoot, "xpui-before-" + stamp) : null;
        var archive = Path.Combine(apps, "xpui.spa");
        var previousArchive = File.Exists(archive) ? Path.Combine(backupRoot, "xpui-before-" + stamp + ".spa") : null;
        var utf8 = new UTF8Encoding(false);
        try
        {
            if (Directory.Exists(source))
            {
                Directory.CreateDirectory(stage);
                foreach (var file in Directory.EnumerateFiles(source, "*", new EnumerationOptions
                    { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
                {
                    var destination = Path.Combine(stage, Path.GetRelativePath(source, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(file, destination);
                }
            }
            else ZipFile.ExtractToDirectory(source, stage);
            var original = ReadXpuiSource(stage);
            var (index, bundle) = PatchXpui(original.Index, original.Bundle);
            var indexPath = Path.Combine(stage, "index.html");
            var bundlePath = Path.Combine(stage, "xpui.js");
            File.WriteAllText(indexPath, index, utf8);
            File.WriteAllText(bundlePath, bundle, utf8);
            File.Copy(scriptPath, Path.Combine(stage, "spotify-local-bridge.js"), overwrite: true);
            File.WriteAllText(Path.Combine(stage, "spotify-local-bridge-install.json"), JsonSerializer.Serialize(
                new { PreviousDirectory = previous, PreviousArchive = previousArchive }, new JsonSerializerOptions { WriteIndented = true }), utf8);
            Directory.CreateDirectory(backupRoot);
            if (previous != null) Directory.Move(live, previous);
            if (previousArchive != null) File.Move(archive, previousArchive);
            Directory.Move(stage, live);
            return "Bridge installed. Start Spotify and Lyricify normally.";
        }
        catch
        {
            if (!Directory.Exists(live) && previous != null && Directory.Exists(previous)) Directory.Move(previous, live);
            if (!File.Exists(archive) && previousArchive != null && File.Exists(previousArchive)) File.Move(previousArchive, archive);
            DeleteStage(stage, apps);
            throw;
        }
    }

    internal static string UninstallPatch(string apps, string backupRoot)
    {
        var live = Path.Combine(apps, "xpui");
        var manifestPath = Path.Combine(live, "spotify-local-bridge-install.json");
        if (!File.Exists(manifestPath)) throw new InvalidOperationException("Standalone bridge installation not found.");
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;
        var previous = OptionalManifestPath(root, "PreviousDirectory");
        var previousArchive = OptionalManifestPath(root, "PreviousArchive");
        var legacyRoot = Path.Combine(apps, ".spotify-local-bridge-backups");
        foreach (var path in new[] { previous, previousArchive }.Where(path => path != null))
            if (!IsInside(path!, backupRoot) && !IsInside(path!, legacyRoot))
                throw new InvalidOperationException($"Unexpected backup path in manifest: {path}");
        if (previous != null && !Directory.Exists(previous))
            throw new InvalidOperationException($"Previous XPUI backup is missing; leaving the installation intact. Backup: {previous}");
        if (previousArchive != null && !File.Exists(previousArchive))
            throw new InvalidOperationException($"Previous xpui.spa backup is missing; leaving the installation intact. Backup: {previousArchive}");
        var archive = Path.Combine(apps, "xpui.spa");
        if (previousArchive != null && File.Exists(archive))
            throw new InvalidOperationException("Spotify created a new xpui.spa; leaving both it and the installation intact.");
        var retired = Path.Combine(backupRoot, "xpui-standalone-retired-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(backupRoot);
        Directory.Move(live, retired);
        try
        {
            if (previous != null) Directory.Move(previous, live);
            if (previousArchive != null) File.Move(previousArchive, archive);
        }
        catch
        {
            if (Directory.Exists(live) && previous != null && !Directory.Exists(previous)) Directory.Move(live, previous);
            if (File.Exists(archive) && previousArchive != null && !File.Exists(previousArchive)) File.Move(archive, previousArchive);
            if (Directory.Exists(retired) && !Directory.Exists(live)) Directory.Move(retired, live);
            throw;
        }
        return "Bridge uninstalled. Start Spotify normally.";
    }

    private static string? OptionalManifestPath(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length) count++;
        return count;
    }

    private static bool IsInside(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteStage(string stage, string apps)
    {
        if (!Directory.Exists(stage) || !IsInside(stage, apps)) return;
        Directory.Delete(stage, recursive: true);
    }

    internal static ProcessStartInfo CreateStartInfo(string exe, string hook, string log)
    {
        var start = new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false
        };
        start.Environment.TryGetValue("DOTNET_STARTUP_HOOKS", out var existingHooks);
        start.Environment["DOTNET_STARTUP_HOOKS"] = string.IsNullOrEmpty(existingHooks)
            ? hook : existingHooks + Path.PathSeparator + hook;
        start.Environment["LYRICIFY_HOOK_LOG"] = log;
        return start;
    }

    internal sealed record BridgeView(bool Connected, string? Title = null, bool Playing = false, int Progress = 0, int Duration = 0);

    internal static async Task<BridgeView> ReadBridgeAsync(HttpClient http)
    {
        try
        {
            using var response = await http.GetAsync("http://127.0.0.1:48711/state");
            if (response.StatusCode == HttpStatusCode.NoContent) return new(true);
            if (!response.IsSuccessStatusCode) return new(false);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("progress_ms", out var progress) || !progress.TryGetInt32(out var position)
                || !root.TryGetProperty("duration_ms", out var duration) || !duration.TryGetInt32(out var length)
                || !root.TryGetProperty("is_playing", out var playing) || playing.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return new(false);
            var title = root.TryGetProperty("title", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null;
            return new(true, title, playing.GetBoolean(), Math.Max(0, position), Math.Max(0, length));
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return new(false);
        }
    }

    private static void Render(string exe, BridgeView bridge, string message, bool pending, int selection,
        bool interactive, ref string[] previous, ref int previousWidth, PanelPrompt? prompt = null)
    {
        var spotify = IsRunning("Spotify", SpotifyPath);
        var lyricify = IsRunning(string.IsNullOrEmpty(exe) ? "Lyricify for Spotify" : Path.GetFileNameWithoutExtension(exe), exe);
        var width = interactive ? Math.Max(1, Console.WindowWidth - 1) : 79;
        var height = interactive ? Console.WindowHeight : 25;
        var shortcut = ShortcutState(ShortcutPath, ShortcutBackupPath, LauncherPath);
        var lines = BuildPanel(width, height, spotify, lyricify, File.Exists(InstallManifest), shortcut,
            bridge, exe, message, pending, selection, prompt);
        if (!interactive) { foreach (var line in lines) Console.WriteLine(line.TrimEnd()); return; }
        try
        {
            if (previousWidth != width || previous.Length > 0 && previous.Length != lines.Length)
            {
                Console.Clear(); previous = []; previousWidth = width;
            }
            var visible = lines.Take(Math.Max(0, height - 1)).ToArray();
            foreach (var row in ChangedRows(previous, visible))
            {
                if (width >= 76 && height >= 23)
                {
                    var leftWidth = PanelLeftWidth(width);
                    var leftOnly = row is >= 3 and <= 19 && row < previous.Length
                        && RightPanelUnchanged(previous[row], visible[row], leftWidth);
                    var rightOnly = row is >= 3 and <= 19 && row < previous.Length
                        && LeftPanelUnchanged(previous[row], visible[row], leftWidth);
                    Console.SetCursorPosition(leftOnly ? 1 : rightOnly ? leftWidth + 1 : 0, row);
                    WritePanelRow(visible[row], row, leftWidth, row == MenuRows[selection], leftOnly, rightOnly,
                        prompt?.Confirmation == true);
                }
                else
                {
                    Console.SetCursorPosition(0, row);
                    var confirmRow = prompt?.Confirmation == true && (height < 13 ? row == 0 : row == 5);
                    if (confirmRow) Console.BackgroundColor = ConsoleColor.Yellow;
                    Console.ForegroundColor = confirmRow ? ConsoleColor.Black
                        : prompt?.Confirmation == true && row == visible.Length - 1 ? ConsoleColor.Yellow
                        : row == 1 ? ConsoleColor.Green
                        : row is 0 or 2 or 20 or 21 ? ConsoleColor.DarkGray
                        : row is 3 or 9 or 11 or 14 or 16 or 17 ? ConsoleColor.Cyan : ConsoleColor.Gray;
                    Console.Write(visible[row]);
                    Console.ResetColor();
                }
            }
            previous = visible;
        }
        // The window can shrink between measuring it and placing the cursor; redraw on the next refresh.
        catch (ArgumentOutOfRangeException) { }
        finally { Console.ResetColor(); }
    }

    private static void WritePanelRow(string line, int row, int leftWidth, bool selected, bool leftOnly,
        bool rightOnly, bool confirmation)
    {
        if (row is 0 or 2 or 20)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write(line);
            return;
        }
        if (row == 21)
        {
            Console.ForegroundColor = confirmation ? ConsoleColor.Yellow : ConsoleColor.DarkGray;
            Console.Write(line);
            return;
        }
        if (!leftOnly && !rightOnly)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write(line[0]);
        }
        if (!rightOnly)
        {
            if (selected) Console.BackgroundColor = ConsoleColor.DarkGreen;
            Console.ForegroundColor = selected ? ConsoleColor.White
                : row == 1 ? ConsoleColor.Green : row == 3 ? ConsoleColor.Cyan : ConsoleColor.Gray;
            Console.Write(line.Substring(1, leftWidth));
            Console.ResetColor();
        }
        if (leftOnly) return;
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write(line[leftWidth + 1]);
        if (confirmation && row == 14) Console.BackgroundColor = ConsoleColor.Yellow;
        Console.ForegroundColor = confirmation && row == 14 ? ConsoleColor.Black
            : confirmation && row == 8 ? ConsoleColor.Yellow
            : row == 1 ? ConsoleColor.Green
            : row is 3 or 8 or 10 or 13 or 16 ? ConsoleColor.Cyan : ConsoleColor.Gray;
        Console.Write(line.Substring(leftWidth + 2, line.Length - leftWidth - 3));
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write(line[^1]);
    }

    internal static bool RightPanelUnchanged(string previous, string current, int leftWidth) =>
        previous.Length == current.Length && previous.AsSpan(leftWidth + 1).SequenceEqual(current.AsSpan(leftWidth + 1));

    internal static bool LeftPanelUnchanged(string previous, string current, int leftWidth) =>
        previous.Length == current.Length && previous.AsSpan(0, leftWidth + 1).SequenceEqual(current.AsSpan(0, leftWidth + 1));

    internal static string[] BuildPanel(int width, int height, bool spotify, bool lyricify, bool installed,
        string shortcut, BridgeView bridge, string exe, string message, bool pending, int selection, PanelPrompt? prompt = null)
    {
        var menu = MenuLabels(installed, shortcut);
        var connected = lyricify && bridge.Connected;
        var title = connected && spotify && !string.IsNullOrWhiteSpace(bridge.Title) ? bridge.Title : "No track available";
        var lyricifyStatus = !lyricify ? "Stopped" : connected ? "Hook active" : pending ? "Hook starting" : "Disconnected";
        if (height < 5) return [Fit(prompt?.Confirmation == true ? $"! {prompt.Title}" : prompt?.Title ?? $"> {menu[selection]}", width)];
        if (height < 13)
        {
            if (prompt != null) return
            [
                Fit($" {(prompt.Confirmation ? "ACTION REQUIRED: " : "")}{prompt.Title}", width),
                Fit($" {prompt.Detail}", width),
                Fit($" {(prompt.Editing ? "> " + Tail(prompt.Value, width - 4) : Tail(prompt.Value, width - 2))}", width),
                Fit($" {prompt.Footer}", width)
            ];
            return
            [
                Fit(" SPOTIFY LOCAL BRIDGE", width),
                Fit($" Spotify {(spotify ? "On" : "Off")}   Lyricify {lyricifyStatus}", width),
                Fit($" > {menu[selection]}", width),
                Fit(" Up/Down  Move   Enter  Select   Q  Exit", width)
            ];
        }
        if (width < 76 || height < 23)
        {
            if (prompt != null) return
            [
                Fit(" SPOTIFY LOCAL BRIDGE", width),
                Fit($" Spotify: {(spotify ? "Running" : "Stopped")}  Lyricify: {lyricifyStatus}", width),
                Fit($" XPUI: {(installed ? "Installed" : "Missing")}  Shortcut: {shortcut}", width),
                Fit($" {title}", width),
                Fit($" {(bridge.Playing ? "Playing" : "Paused")}  {Clock(bridge.Progress)} / {Clock(bridge.Duration)}", width),
                Fit($" {(prompt.Confirmation ? "ACTION REQUIRED: " : "")}{prompt.Title}", width),
                Fit($"   {menu[(selection + menu.Length - 1) % menu.Length]}", width),
                Fit($" > {menu[selection]}", width),
                Fit($"   {menu[(selection + 1) % menu.Length]}", width),
                Fit($" {prompt.Detail}", width),
                Fit($" {(prompt.Editing ? "> " + Tail(prompt.Value, width - 4) : Tail(prompt.Value, width - 2))}", width),
                Fit($" {prompt.Footer}", width)
            ];
            return
            [
                Fit(" SPOTIFY LOCAL BRIDGE", width),
                Fit($" Spotify: {(spotify ? "Running" : "Stopped")}  Lyricify: {lyricifyStatus}", width),
                Fit($" XPUI: {(installed ? "Installed" : "Missing")}  Shortcut: {shortcut}", width),
                Fit($" {title}", width),
                Fit($" {(bridge.Playing ? "Playing" : "Paused")}  {Clock(bridge.Progress)} / {Clock(bridge.Duration)}", width),
                Fit("", width),
                Fit($"   {menu[(selection + menu.Length - 1) % menu.Length]}", width),
                Fit($" > {menu[selection]}", width),
                Fit($"   {menu[(selection + 1) % menu.Length]}", width),
                Fit("", width),
                Fit($" {message}", width),
                Fit(" Up/Down  Move     Enter  Select     Q  Exit", width)
            ];
        }

        var leftWidth = PanelLeftWidth(width);
        var rightWidth = width - leftWidth - 3;
        var left = new string[18];
        var right = new string[18];
        left[0] = "  LOCAL BRIDGE"; right[0] = "  SPOTIFY  /  LYRICIFY";
        left[1] = "  MANAGE";
        right[1] = "  CONNECTION";
        right[2] = $"  Spotify       {(spotify ? "Running" : "Stopped")}";
        right[3] = $"  Lyricify      {lyricifyStatus}";
        right[4] = $"  XPUI patch    {(installed ? "Installed" : "Not installed")}";
        if (prompt?.Confirmation == true)
        {
            right[6] = "  ACTION REQUIRED";
            right[7] = "  " + prompt.Title;
            right[9] = "  " + prompt.Detail;
            right[10] = "  " + Tail(prompt.Value, rightWidth - 2);
            right[12] = "  Y   CONFIRM";
            right[13] = "  Esc CANCEL";
        }
        else
        {
            right[6] = "  NOW PLAYING";
            right[7] = "  " + title;
            right[8] = connected && spotify ? $"  {(bridge.Playing ? "Playing" : "Paused")}   {Clock(bridge.Progress)} / {Clock(bridge.Duration)}" : "";
            if (connected && spotify && bridge.Duration > 0)
            {
                var barWidth = rightWidth - 4;
                var filled = (int)Math.Clamp((long)bridge.Progress * barWidth / bridge.Duration, 0, barWidth);
                right[9] = "  " + new string('█', filled) + new string('░', barWidth - filled);
            }
            right[11] = "  START MENU SHORTCUT";
            right[12] = "  " + (shortcut switch
            {
                "Patched" => "Hook enabled",
                "Patched (old location)" => "Needs repair",
                "Original" => "Hook not enabled",
                _ => shortcut
            });
            if (prompt != null)
            {
                right[14] = "  " + prompt.Title;
                right[15] = "  " + prompt.Detail;
                right[16] = "  " + (prompt.Editing ? "> " + Tail(prompt.Value, rightWidth - 4) : Tail(prompt.Value, rightWidth - 2));
            }
            else if (!string.IsNullOrWhiteSpace(message))
            {
                right[14] = "  ACTIVITY";
                var activity = Clip(message.Replace("\r\n", " | ", StringComparison.Ordinal).Replace("\n", " | ", StringComparison.Ordinal), int.MaxValue);
                var firstLine = Clip(activity, rightWidth - 4);
                right[15] = "  " + firstLine;
                right[16] = "  " + Tail(activity.Length > firstLine.Length ? activity[firstLine.Length..].TrimStart() : "", rightWidth - 2);
            }
        }
        for (var index = 0; index < menu.Length; index++)
            left[MenuRows[index] - 2] = $" {(selection == index ? ">" : " ")} " + menu[index];
        var top = "╭" + new string('─', leftWidth) + "┬" + new string('─', rightWidth) + "╮";
        var divider = "├" + new string('─', leftWidth) + "┼" + new string('─', rightWidth) + "┤";
        var bottom = "╰" + new string('─', leftWidth) + "┴" + new string('─', rightWidth) + "╯";
        var lines = new List<string> { top, Frame(left[0], right[0], leftWidth, rightWidth), divider };
        for (var row = 1; row < left.Length; row++) lines.Add(Frame(left[row] ?? "", right[row] ?? "", leftWidth, rightWidth));
        lines.Add(bottom);
        lines.Add(Fit("  " + (prompt?.Footer ?? "↑↓ Move    Enter Select    Q Exit"), width));
        return lines.ToArray();
    }

    private static string Frame(string left, string right, int leftWidth, int rightWidth) =>
        "│" + Fit(left, leftWidth) + "│" + Fit(right, rightWidth) + "│";

    private static int PanelLeftWidth(int width) => Math.Clamp(width / 2 - 1, 34, 42);

    internal static string Tail(string text, int width)
    {
        if (width <= 0) return "";
        text = Clip(text, int.MaxValue);
        if (CellWidth(text) <= width) return text;
        var elements = new List<string>();
        var iterator = StringInfo.GetTextElementEnumerator(text);
        while (iterator.MoveNext()) elements.Add((string)iterator.Current!);
        var start = elements.Count;
        var remaining = Math.Max(0, width - 1);
        while (start > 0 && ElementWidth(elements[start - 1]) <= remaining)
            remaining -= ElementWidth(elements[--start]);
        return "…" + string.Concat(elements.Skip(start));
    }

    private static string Fit(string text, int width)
    {
        var fitted = CellWidth(text) <= width ? Clip(text, width) : Clip(text, width - 1) + "…";
        return fitted + new string(' ', Math.Max(0, width - CellWidth(fitted)));
    }

    private static string Clock(int milliseconds) => $"{milliseconds / 60000:00}:{milliseconds / 1000 % 60:00}";

    internal static IEnumerable<int> ChangedRows(string[] previous, string[] current)
    {
        for (var row = 0; row < current.Length; row++)
            if (row >= previous.Length || previous[row] != current[row]) yield return row;
    }

    internal static int CellWidth(string text)
    {
        var width = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext()) width += ElementWidth((string)elements.Current!);
        return width;
    }

    private static int ElementWidth(string element)
    {
        var rune = element.EnumerateRunes().First();
        if (Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.Format) return 0;
        return rune.Value >= 0x2E80 || element.Contains('\uFE0F') ? 2 : 1;
    }

    internal static string Clip(string text, int width)
    {
        var output = new StringBuilder();
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = (string)elements.Current!;
            var cells = ElementWidth(element);
            if (cells == 0) continue;
            if (width < cells) break;
            output.Append(element);
            width -= cells;
        }
        return output.ToString();
    }
}
