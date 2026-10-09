using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

internal static class Program
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpotifyLocalBridge");
    private static readonly string SavedPath = Path.Combine(SettingsDirectory, "lyricify-path.txt");
    private static readonly string LogPath = Path.Combine(SettingsDirectory, "hook.log");
    private static readonly string HookPath = Path.Combine(AppContext.BaseDirectory,
        "lyricify-hook", "bin", "Release", "net6.0", "StartupHook.dll");
    private const string StartingMessage = "正在启动 Lyricify，插件连接通常需要几秒。";

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;
        try
        {
            if (args.Length > 1) throw new ArgumentException("最多传入一个 Lyricify 程序路径。");
            var exe = args.Length == 1 ? ValidatePath(args[0]) : FindLyricify();
            if (args.Length == 1) SavePath(exe);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1), MaxResponseContentBufferSize = 64 * 1024 };
            var message = "按 Enter 启动；已连接时可以直接使用 Lyricify。";
            DateTime? pendingSince = null;
            if (args.Length == 1)
            {
                message = Launch(exe, (await ReadBridgeAsync(http)).Connected);
                if (message == StartingMessage) pendingSince = DateTime.UtcNow;
            }
            Console.Title = "Spotify Local Bridge";
            if (interactive) { Console.Clear(); Console.CursorVisible = false; }
            var refreshAt = DateTime.MinValue;
            while (true)
            {
                if (DateTime.UtcNow >= refreshAt)
                {
                    var bridge = await ReadBridgeAsync(http);
                    if (pendingSince != null)
                    {
                        if (bridge.Connected)
                        {
                            message = "插件已连接，可以直接使用 Lyricify。";
                            pendingSince = null;
                        }
                        else if (DateTime.UtcNow - pendingSince.Value > TimeSpan.FromSeconds(15))
                        {
                            message = "插件尚未连接，按 G 查看日志；若 Lyricify 已退出，可按 Enter 重试。";
                            pendingSince = null;
                        }
                    }
                    Render(exe, bridge, message, pendingSince != null, interactive);
                    if (!interactive) return 0;
                    refreshAt = DateTime.UtcNow.AddSeconds(2);
                }
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true).Key;
                    if (key is ConsoleKey.Q or ConsoleKey.Escape) return 0;
                    try
                    {
                        switch (key)
                        {
                            case ConsoleKey.Enter:
                                if (string.IsNullOrEmpty(exe)) exe = PromptPath(exe);
                                message = string.IsNullOrEmpty(exe) ? "尚未选择 Lyricify，按 L 设置路径。"
                                    : Launch(exe, (await ReadBridgeAsync(http)).Connected);
                                pendingSince = message == StartingMessage ? DateTime.UtcNow : null;
                                break;
                            case ConsoleKey.L:
                                exe = PromptPath(exe);
                                message = string.IsNullOrEmpty(exe) ? "尚未选择 Lyricify，按 L 设置路径。" : "当前路径已就绪，按 Enter 启动。";
                                break;
                            case ConsoleKey.G:
                                if (!File.Exists(LogPath)) message = "还没有运行日志，启动 Lyricify 后再查看。";
                                else
                                {
                                    using var notepad = Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { LogPath } });
                                    message = "已打开插件日志。";
                                }
                                break;
                        }
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        message = error.Message;
                    }
                    refreshAt = DateTime.MinValue;
                }
                await Task.Delay(100);
            }
        }
        catch (Exception error)
        {
            Console.WriteLine($"启动器无法运行：{error.Message}");
            if (interactive) { Console.WriteLine("按任意键退出。"); Console.ReadKey(true); }
            return 1;
        }
        finally
        {
            Console.ResetColor();
            if (interactive) { Console.CursorVisible = true; Console.WriteLine(); }
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
            throw new ArgumentException("没有找到这个程序，请粘贴 Lyricify 的完整 EXE 路径。");
        return path;
    }

    private static void SavePath(string exe)
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(SavedPath, exe);
    }

    private static string PromptPath(string current)
    {
        Console.Clear();
        Console.CursorVisible = true;
        Console.WriteLine("设置 Lyricify 路径");
        Console.WriteLine("粘贴完整 EXE 路径，或把程序拖进窗口。直接回车保留当前选择。");
        if (!string.IsNullOrEmpty(current)) Console.WriteLine($"当前：{current}");
        Console.Write("路径 > ");
        try
        {
            var input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input)) return current;
            var path = ValidatePath(input);
            SavePath(path);
            return path;
        }
        finally { Console.Clear(); Console.CursorVisible = false; }
    }

    private static bool IsRunning(string name)
    {
        var processes = Process.GetProcessesByName(name);
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static string Launch(string exe, bool connected)
    {
        exe = ValidatePath(exe);
        if (IsRunning(Path.GetFileNameWithoutExtension(exe)))
            return connected ? "插件已连接，可以直接使用 Lyricify。"
                : "请先从托盘完全退出 Lyricify，再按 Enter 加载插件。";
        if (!File.Exists(HookPath))
            throw new InvalidOperationException("插件文件不完整，请把启动器和 lyricify-hook 文件夹放在一起。");

        var port = new TcpListener(IPAddress.Loopback, 48711);
        try { port.Start(); }
        catch (SocketException) { throw new InvalidOperationException("本地连接被其他程序占用，请先关闭旧桥接程序再启动。"); }
        finally { port.Stop(); }

        if (!IsRunning("Spotify"))
        {
            var spotify = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spotify", "Spotify.exe");
            if (!File.Exists(spotify))
                throw new InvalidOperationException("没有找到 Spotify，请手动打开 Spotify 后再按 Enter。");
            using var spotifyProcess = Process.Start(new ProcessStartInfo(spotify) { UseShellExecute = true });
            if (spotifyProcess == null) throw new InvalidOperationException("Spotify 启动失败，请手动打开后重试。");
        }
        SavePath(exe);
        using var process = Process.Start(CreateStartInfo(exe, HookPath, LogPath));
        if (process == null) throw new InvalidOperationException("Lyricify 启动失败，请查看程序路径。");
        return StartingMessage;
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

    private static void Render(string exe, BridgeView bridge, string message, bool pending, bool interactive)
    {
        var spotify = IsRunning("Spotify");
        var lyricify = IsRunning(string.IsNullOrEmpty(exe) ? "Lyricify for Spotify" : Path.GetFileNameWithoutExtension(exe));
        var connected = lyricify && bridge.Connected;
        var lyricifyStatus = "未打开";
        if (lyricify) lyricifyStatus = connected ? "插件已接入" : pending ? "正在接入插件" : "已打开，插件未连接";
        var localStatus = "等待启动";
        if (connected) localStatus = !spotify ? "等待 Spotify" : string.IsNullOrWhiteSpace(bridge.Title) ? "等待播放状态" : "已连接";
        var showTrack = connected && spotify && !string.IsNullOrWhiteSpace(bridge.Title);
        var lines = new[]
        {
            "  SPOTIFY × LYRICIFY",
            "  本地连接 · 歌词照常使用 Lyricify",
            "",
            $"  Spotify     {(spotify ? "已打开" : "未打开")}",
            $"  Lyricify    {lyricifyStatus}",
            $"  本地状态    {localStatus}",
            "",
            showTrack ? $"  {bridge.Title}" : "  在 Spotify 播放歌曲后，这里会显示当前状态。",
            showTrack ? $"  {(bridge.Playing ? "播放中" : "已暂停")}  {Clock(bridge.Progress)} / {Clock(bridge.Duration)}" : "",
            "",
            "  Enter 启动   L 设置路径   G 查看日志   Q 退出",
            "  退出面板后，Spotify 和 Lyricify 继续运行。",
            "",
            $"  {message}",
            string.IsNullOrEmpty(exe) ? "  尚未找到 Lyricify，按 L 设置路径。" : $"  程序：{exe}"
        };
        if (!interactive) { foreach (var line in lines) Console.WriteLine(Clip(line, int.MaxValue)); return; }
        var width = Math.Max(1, Console.WindowWidth - 1);
        try
        {
            for (var row = 0; row < Math.Min(lines.Length, Console.WindowHeight - 1); row++)
            {
                Console.SetCursorPosition(0, row);
                Console.Write(new string(' ', width));
                Console.SetCursorPosition(0, row);
                Console.ForegroundColor = row == 0 ? ConsoleColor.Green : row is 1 or 11 or 14 ? ConsoleColor.DarkGray : ConsoleColor.Gray;
                Console.Write(Clip(lines[row], width));
            }
            Console.SetCursorPosition(0, Math.Min(lines.Length, Console.WindowHeight - 1));
        }
        // The window can shrink between measuring it and placing the cursor; redraw on the next refresh.
        catch (ArgumentOutOfRangeException) { }
        finally { Console.ResetColor(); }
    }

    private static string Clock(int milliseconds) => $"{milliseconds / 60000:00}:{milliseconds / 1000 % 60:00}";

    internal static string Clip(string text, int width)
    {
        var output = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsControl(rune)) continue;
            var cells = Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.NonSpacingMark ? 0
                : rune.Value >= 0x2E80 ? 2 : 1;
            if (width < cells) break;
            output.Append(rune.ToString());
            width -= cells;
        }
        return output.ToString();
    }
}
