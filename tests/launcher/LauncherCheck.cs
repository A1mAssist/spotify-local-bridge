using System.Net;

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
