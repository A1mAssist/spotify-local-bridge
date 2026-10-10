using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

internal static class LauncherCheck
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static async Task Main()
    {
        using var handler = new StubHandler();
        using var http = new HttpClient(handler);
        handler.Body = """{"title":"测试歌曲","progress_ms":12345,"duration_ms":180000,"is_playing":true}""";
        var live = await Program.ReadBridgeAsync(http);
        Check(live.Connected && live.Playing && live.Progress == 12345 && live.Title == "测试歌曲", "Live bridge state was not recognized.");
        handler.Status = HttpStatusCode.NoContent;
        Check((await Program.ReadBridgeAsync(http)).Connected, "An idle hooked client must remain connected.");
        handler.Status = HttpStatusCode.OK;
        foreach (var invalid in new[] { "<html>other local service</html>", "{}", "[]", "{\"progress_ms\":\"bad\"}", "{\"progress_ms\":1,\"duration_ms\":2,\"is_playing\":null}" })
        {
            handler.Body = invalid;
            Check(!(await Program.ReadBridgeAsync(http)).Connected, "An unrelated or malformed service was shown as connected.");
        }
        handler.Status = HttpStatusCode.InternalServerError;
        Check(!(await Program.ReadBridgeAsync(http)).Connected, "An HTTP error was shown as connected.");
        handler.Error = new HttpRequestException("Connection refused");
        Check(!(await Program.ReadBridgeAsync(http)).Connected, "A closed client must show disconnected.");
        handler.Error = new TaskCanceledException("Timeout");
        Check(!(await Program.ReadBridgeAsync(http)).Connected, "An unresponsive client must show disconnected.");

        var previousHooks = Environment.GetEnvironmentVariable("DOTNET_STARTUP_HOOKS");
        try
        {
            Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", "C:\\existing hook.dll");
            var start = Program.CreateStartInfo("C:\\Lyricify 4\\Lyricify for Spotify.exe", "D:\\local bridge\\StartupHook.dll", "D:\\logs\\hook.log");
            Check(!start.UseShellExecute && start.WorkingDirectory == "C:\\Lyricify 4", "The hooked process must retain its working directory.");
            Check(start.Environment["DOTNET_STARTUP_HOOKS"] == "C:\\existing hook.dll;D:\\local bridge\\StartupHook.dll", "Existing startup hooks must be preserved.");
            Check(start.Environment["LYRICIFY_HOOK_LOG"] == "D:\\logs\\hook.log", "Hook log path was not propagated.");
            Check(Environment.GetEnvironmentVariable("DOTNET_STARTUP_HOOKS") == "C:\\existing hook.dll", "Launcher changed global startup hooks.");
        }
        finally { Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", previousHooks); }
        Check(Program.Clip("歌曲abcdef", 6) == "歌曲ab", "Chinese text must fit narrow terminals.");
        Check(Program.Clip("A\u001b\nB", 10) == "AB", "Track metadata must not inject terminal controls.");
        Check(Program.CellWidth("A👩‍🎤曲") == 5, "Grapheme width must keep mixed emoji and Chinese titles aligned.");
        Check(Program.ChangedRows(["Spotify Running", "01:02"], ["Spotify Running", "01:04"]).SequenceEqual([1]),
            "Only the changed timeline row should be redrawn.");
        Check(!Program.ChangedRows(["Stable"], ["Stable"]).Any(), "Unchanged frames must not redraw.");
        Check(Program.MatchesExecutable("C:\\Spotify\\Spotify.exe", "c:\\spotify\\SPOTIFY.EXE"),
            "Process matching should ignore Windows path case.");
        Check(!Program.MatchesExecutable("C:\\Other\\Spotify.exe", "C:\\Spotify\\Spotify.exe"),
            "Process management must not target a different executable.");
        var panel = Program.BuildPanel(79, 25, true, true, true, "Patched",
            new Program.BridgeView(true, "测试歌曲", true, 12345, 180000), "C:\\Lyricify\\Lyricify.exe", "", false, 0);
        Check(panel.Length == 22 && panel.All(line => Program.CellWidth(line) == 79),
            "The two-column panel must fit a standard 80-column terminal.");
        Check(panel[0][0] == '╭' && panel[0][^1] == '╮' && panel[20][0] == '╰' && panel[20][^1] == '╯',
            "The outer border should use rounded corners.");
        var nextSelection = Program.BuildPanel(79, 25, true, true, true, "Patched",
            new Program.BridgeView(true, "测试歌曲", true, 12345, 180000), "C:\\Lyricify\\Lyricify.exe", "Ready", false, 1);
        var leftWidth = panel[0].IndexOf('┬') - 1;
        Check(Program.RightPanelUnchanged(panel[5], nextSelection[5], leftWidth)
            && Program.RightPanelUnchanged(panel[7], nextSelection[7], leftWidth)
            && !Program.RightPanelUnchanged(panel[5], Program.BuildPanel(79, 25, true, false, true, "Patched",
                new Program.BridgeView(false), "C:\\Lyricify\\Lyricify.exe", "Ready", false, 1)[5], leftWidth),
            "Menu navigation must redraw only the left pane while real status changes redraw the right pane.");
        var laterProgress = Program.BuildPanel(79, 25, true, true, true, "Patched",
            new Program.BridgeView(true, "测试歌曲", true, 30000, 180000), "C:\\Lyricify\\Lyricify.exe", "", false, 0);
        Check(Program.LeftPanelUnchanged(panel[11], laterProgress[11], leftWidth)
            && !Program.RightPanelUnchanged(panel[11], laterProgress[11], leftWidth),
            "Playback updates must redraw only the right pane without flashing the menu.");
        Check(panel[5].Contains("> Remove Spotify patch") && panel[7].Contains("Restore Lyricify shortcut")
            && panel[9].Contains("Set Lyricify path") && panel[11].Contains("View hook log")
            && panel[18].Contains("Exit") && panel[9].Contains("测试歌曲")
            && panel[11].Count(cell => "█░".Contains(cell)) == 34
            && panel[11].Contains('█') && panel[11].Contains('░')
            && panel[14].Contains("Hook enabled") && !panel.Any(line => line.Contains("App       Lyricify.exe"))
            && panel[3].Contains("CONNECTION") && panel[8].Contains("NOW PLAYING")
            && panel[13].Contains("START MENU SHORTCUT") && !panel.Any(line => line.Contains("ACTIVITY") || line.Contains("Ready."))
            && !panel.Any(line => line.Contains("Overview") || line.Contains("Start both") || line.Contains("Restart Spotify")),
            "The wider management menu should show the current actions without app launch controls.");
        var resultPanel = Program.BuildPanel(79, 25, true, true, true, "Patched",
            new Program.BridgeView(true), "", "Lyricify path saved.", false, 0);
        Check(resultPanel[16].Contains("ACTIVITY") && resultPanel[17].Contains("Lyricify path saved."),
            "Actual operation results must remain visible in the right pane.");
        var confirmation = new Program.PanelPrompt("CONFIRM REMOVAL", "Close Spotify and Lyricify",
            "Remove Spotify patch", "Y Confirm    Esc Cancel", Confirmation: true);
        var confirmPanel = Program.BuildPanel(79, 25, true, true, true, "Patched", new(true), "", "", false, 0, confirmation);
        Check(confirmPanel.Length == 22 && confirmPanel.All(line => Program.CellWidth(line) == 79)
            && confirmPanel[8].Contains("ACTION REQUIRED") && confirmPanel[9].Contains("CONFIRM REMOVAL")
            && confirmPanel[11].Contains("Close Spotify and Lyricify") && confirmPanel[12].Contains("Remove Spotify patch")
            && confirmPanel[14].Contains("Y   CONFIRM") && confirmPanel[15].Contains("Esc CANCEL")
            && confirmPanel[^1].Contains("Y Confirm    Esc Cancel")
            && !confirmPanel.Any(line => line.Contains("ACTIVITY") || line.Contains("NOW PLAYING")),
            "Destructive confirmation must take the central right pane with clear key choices.");
        var compactConfirm = Program.BuildPanel(48, 18, true, true, true, "Patched", new(true), "", "", false, 0, confirmation);
        Check(compactConfirm.Length == 12 && compactConfirm[5].Contains("ACTION REQUIRED: CONFIRM REMOVAL")
            && compactConfirm[9].Contains("Close Spotify and Lyricify") && compactConfirm[^1].Contains("Esc Cancel"),
            "A narrow terminal must keep the confirmation and cancel action visible.");
        var archiveConfirm = Program.BuildPanel(79, 25, true, true, false, "Original", new(false), "", "", false, 0,
            new("CONFIRM INSTALL", "Close Spotify and Lyricify", "Archive: C:\\Users\\Someone\\spicetify\\Backup\\xpui.spa", "Y Confirm    Esc Cancel", Confirmation: true));
        Check(archiveConfirm[12].Contains("xpui.spa"), "The install confirmation must show the selected archive filename.");
        Check(Program.Tail("C:\\音乐\\👩‍🎤歌曲\\Lyricify.exe", 15).EndsWith("Lyricify.exe")
            && Program.CellWidth(Program.Tail("C:\\音乐\\👩‍🎤歌曲\\Lyricify.exe", 15)) <= 15,
            "Long pasted paths must show their filename without overflowing the panel.");
        var widePanel = Program.BuildPanel(120, 25, true, true, true, "Patched",
            new Program.BridgeView(true, "测试歌曲", true, 12345, 180000), "C:\\Lyricify\\Lyricify.exe", "Ready", false, 0);
        Check(widePanel.All(line => Program.CellWidth(line) == 120)
            && widePanel[11].Count(cell => "█░".Contains(cell)) == 71,
            "A wide panel should use the full block progress track.");
        var startBar = Program.BuildPanel(79, 25, true, true, true, "Patched",
            new Program.BridgeView(true, "Track", true, 0, 180000), "", "", false, 0)[11];
        var endBar = Program.BuildPanel(79, 25, true, true, true, "Patched",
            new Program.BridgeView(true, "Track", true, 180000, 180000), "", "", false, 0)[11];
        Check(startBar.Contains("  " + new string('░', 33))
            && endBar.Contains("  " + new string('█', 33)),
            "The progress bar must fill cleanly at both endpoints.");
        Check(Program.BuildPanel(76, 25, true, true, true, "Patched", new(true), "", "Ready", false, 0).Length == 22
            && Program.BuildPanel(75, 25, true, true, true, "Patched", new(true), "", "Ready", false, 0).Length == 12,
            "The two-column layout should switch to compact mode before its status rows become cramped.");
        Check(Program.MenuLabels(false, "Original")[0] == "Install Spotify patch"
            && Program.MenuLabels(false, "Original")[1] == "Enable Lyricify shortcut"
            && Program.MenuLabels(true, "Patched (old location)")[1] == "Repair Lyricify shortcut",
            "Unavailable integrations should show their setup actions.");
        Check(Program.BuildPanel(79, 25, true, true, true, "Patched (old location)", new(true), "", "Ready", false, 0)[14].Contains("Needs repair")
            && Program.BuildPanel(79, 25, true, true, true, "Original", new(true), "", "Ready", false, 0)[14].Contains("Hook not enabled"),
            "The shortcut summary should describe what its state means.");
        Check(!Program.BuildPanel(79, 25, true, true, true, "Original",
            new Program.BridgeView(true, "A\u001b[31m👩‍🎤曲", true, 1000, 2000), "", "Ready", false, 0)
            .Any(line => line.Contains('\u001b')), "Track metadata must not inject terminal escape sequences into the panel.");
        var compact = Program.BuildPanel(48, 18, false, false, false, "Original", new(false), "", "Ready", false, 0);
        Check(compact.Length <= 17 && compact.All(line => Program.Clip(line, 48) == line) && compact.Any(line => line.Contains("> Install Spotify patch")),
            "Narrow terminals must keep the selected action visible.");
        Check(Program.BuildPanel(48, 7, false, false, false, "Original", new(false), "", "Ready", false, 0)
            .Any(line => line.Contains("> Install Spotify patch")), "Short terminals must keep the selected action visible.");
        var testDirectory = Path.Combine(Path.GetTempPath(), "spotify-bridge-launcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        var logPath = Path.Combine(testDirectory, "hook.log");
        Check(Program.ReadLogTail(logPath).Length == 0, "A missing hook log should show an empty viewer.");
        File.WriteAllLines(logPath, Enumerable.Range(0, 10001).Select(index => $"line {index}"));
        using (File.Open(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            var tail = Program.ReadLogTail(logPath);
            Check(tail.Length == 10000 && tail[0] == "line 1" && tail[^1] == "line 10000",
                "The viewer must read the newest entries while Lyricify has the file open.");
            var logPanel = Program.BuildLogPanel(tail, 48, 8, tail.Length - 6, false);
            Check(logPanel.Length == 8 && logPanel.All(line => Program.CellWidth(line) == 48)
                && logPanel[1].Contains("line 9995") && logPanel[6].Contains("line 10000")
                && logPanel[7].Contains("Q Back"), "Log navigation must fit a small terminal and end at the newest line.");
            Check(Program.BuildLogPanel(tail, 30, 2, tail.Length - 1, false)[0].Contains("line 10000"),
                "A very short terminal must still show the log content.");
            var raw = new[]
            {
                "2026-10-09T21:24:26.9387597+08:00 ISRC CACHE spotify:track:1234567890123456789012 USUG12505774",
                "2026-10-09T21:24:27.0000000+08:00 PATCH FAILED: This long error must remain visible after wrapping.",
                "2026-10-09T21:24:27.5000000+08:00 CONTROL RESULT library-check id=y success=True",
                "2026-10-09T21:24:28.0000000+08:00 CONTROL RESULT library-check id=x success=False"
            };
            var events = Program.PrepareLogLines(raw, 48, false);
            var completeEvents = string.Concat(events.Select(line => line.StartsWith(new string(' ', 16)) ? line[16..] : line));
            Check(events.All(line => Program.CellWidth(line) <= 48)
                && events[0].StartsWith("10-09 21:24:27  PATCH FAILED")
                && completeEvents.Contains("visible after wrapping.")
                && completeEvents.Contains("success=False")
                && !completeEvents.Contains("success=True")
                && !completeEvents.Contains("ISRC CACHE"),
                "Events mode should shorten timestamps, wrap long messages, and retain failures.");
            Check(Program.PrepareLogLines(raw, 48, true).Any(line => line.Contains("ISRC CACHE"))
                && Program.PrepareLogLines(["A👩‍🎤曲BCD"], 4, true).SequenceEqual(["A👩‍🎤", "曲BC", "D"]),
                "All mode must retain raw events and wrapping must preserve Unicode text.");
        }
        var testExe = Path.Combine(testDirectory, "BridgeStopCheck.exe");
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "PING.EXE"), testExe);
        try
        {
            var shortcutPath = Path.Combine(testDirectory, "Lyricify 4.lnk");
            var backupPath = Path.Combine(testDirectory, "original.lnk");
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = testExe;
            shortcut.Arguments = "-n 1";
            shortcut.Save();
            Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
            var originalShortcut = File.ReadAllBytes(shortcutPath);
            Program.PatchShortcut(shortcutPath, backupPath, Environment.ProcessPath!, testExe);
            Check(Program.ShortcutState(shortcutPath, backupPath, Environment.ProcessPath!) == "Patched"
                && Program.ReadShortcut(shortcutPath).Arguments.Contains("--launch-only") && File.Exists(backupPath),
                "Patching must route the shortcut through the launcher and preserve a backup.");
            Program.PatchShortcut(shortcutPath, backupPath, Environment.ProcessPath!, testExe);
            Check(File.ReadAllBytes(backupPath).SequenceEqual(originalShortcut),
                "Patching an already managed shortcut must keep its original backup.");
            var movedLauncher = Path.Combine(testDirectory, "moved", Path.GetFileName(Environment.ProcessPath!));
            Directory.CreateDirectory(Path.GetDirectoryName(movedLauncher)!);
            File.Copy(testExe, movedLauncher);
            Check(Program.ShortcutState(shortcutPath, backupPath, movedLauncher) == "Patched (old location)",
                "Moving the launcher must leave the backup available for repair.");
            Program.PatchShortcut(shortcutPath, backupPath, movedLauncher, testExe);
            Check(Program.ShortcutState(shortcutPath, backupPath, movedLauncher) == "Patched"
                && File.ReadAllBytes(backupPath).SequenceEqual(originalShortcut),
                "Repairing a moved launcher must preserve the original shortcut backup.");
            Program.RestoreShortcut(shortcutPath, backupPath, movedLauncher);
            Check(Program.ReadShortcut(shortcutPath).Target == testExe && Program.ReadShortcut(shortcutPath).Arguments == "-n 1"
                && !File.Exists(backupPath) && File.ReadAllBytes(shortcutPath).SequenceEqual(originalShortcut),
                "Restoring must recover the exact original shortcut.");
            using var probe = Process.Start(new ProcessStartInfo(testExe)
            {
                ArgumentList = { "-t", "127.0.0.1" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            }) ?? throw new Exception("Could not start isolated process check.");
            try
            {
                Check(Program.StopApp("BridgeStopCheck", testExe), "StopApp did not find the selected executable.");
                Check(probe.WaitForExit(5000), "StopApp left the selected executable running.");
            }
            finally { if (!probe.HasExited) probe.Kill(); }
        }
        finally { Directory.Delete(testDirectory, recursive: true); }
        Console.WriteLine("Launcher checks passed");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        internal HttpStatusCode Status = HttpStatusCode.OK;
        internal string Body = "";
        internal Exception? Error;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Check(request.Method == HttpMethod.Get && request.RequestUri?.AbsoluteUri == "http://127.0.0.1:48711/state", "Panel must only read the existing local state endpoint.");
            if (Error != null) return Task.FromException<HttpResponseMessage>(Error);
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body) });
        }
    }
}
