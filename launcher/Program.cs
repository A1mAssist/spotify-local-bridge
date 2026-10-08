using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 1) throw new ArgumentException("Pass one Lyricify EXE path at most.");
            var hook = Path.Combine(AppContext.BaseDirectory, "lyricify-hook", "bin", "Release", "net6.0", "StartupHook.dll");
            if (!File.Exists(hook)) throw new FileNotFoundException("The Lyricify Hook DLL is missing.", hook);

            var settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpotifyLocalBridge");
            var savedPath = Path.Combine(settings, "lyricify-path.txt");
            var exe = args.Length == 1 ? Path.GetFullPath(args[0]) : File.Exists(savedPath) ? File.ReadAllText(savedPath).Trim() : "";
            if (!File.Exists(exe))
            {
                using var picker = new OpenFileDialog
                {
                    Title = "Select Lyricify for Spotify.exe",
                    Filter = "Applications (*.exe)|*.exe"
                };
                if (picker.ShowDialog() != DialogResult.OK) return 0;
                exe = picker.FileName;
            }
            if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Select a Lyricify EXE file.");
            var running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe));
            try
            {
                if (running.Length != 0) throw new InvalidOperationException("Close Lyricify completely before launching it with the Hook.");
            }
            finally
            {
                foreach (var process in running) process.Dispose();
            }

            var port = new TcpListener(IPAddress.Loopback, 48711);
            try { port.Start(); }
            catch (SocketException) { throw new InvalidOperationException("Port 48711 is already in use. Close the program using it before launching Lyricify."); }
            finally { port.Stop(); }

            Directory.CreateDirectory(settings);
            File.WriteAllText(savedPath, exe);
            var start = new ProcessStartInfo(exe)
            {
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false
            };
            start.Environment.TryGetValue("DOTNET_STARTUP_HOOKS", out var existingHooks);
            start.Environment["DOTNET_STARTUP_HOOKS"] = string.IsNullOrEmpty(existingHooks)
                ? hook : existingHooks + Path.PathSeparator + hook;
            start.Environment["LYRICIFY_HOOK_LOG"] = Path.Combine(settings, "hook.log");
            if (Process.Start(start) == null) throw new InvalidOperationException("Lyricify did not start.");
            return 0;
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "Spotify Local Bridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
