using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Lyricify_for_Spotify.Helpers.MediaSession;

var helper = new MediaSessionHelper();
if (!MediaSessionHelper.ShouldReact) throw new Exception("Fixture must start with Media Session enabled.");
var patch = typeof(StartupHook).Assembly.GetType("PlaybackPatch", true)!;
patch.GetMethod("PatchMediaSessionControls", BindingFlags.NonPublic | BindingFlags.Static)!
    .Invoke(null, new object[] { new Harmony("spotify-local-bridge.media-session.check") });
helper.InitMediaSessionManager();
if (MediaSessionHelper.InitCalls != 0) throw new Exception("Windows session initialization was not suppressed.");
if (await helper.MediaSessionTimerEvent()) throw new Exception("Windows session timer still supplied playback state.");
foreach (var method in new[] { "PausePlayback", "ResumePlayback", "SkipPrevious", "SkipNext", "SeekTo" })
{
    var arguments = method == "SeekTo" ? new object[] { 12000 } : Array.Empty<object>();
    var result = (Task<bool>)typeof(MediaSessionHelper).GetMethod(method)!.Invoke(null, arguments)!;
    if (await result) throw new Exception($"Media Session {method} did not fall back to the hooked SDK.");
}
if (MediaSessionHelper.ControlCalls != 0) throw new Exception("Windows controls were invoked.");
if (!MediaSessionHelper.EnableMediaSession) throw new Exception("Saved Media Session preference was changed.");
Console.WriteLine("Media Session bypass checks passed (.NET 6)");

// Recovered public entry points, tested under Lyricify's runtime without its protected WPF initializer.
namespace Lyricify_for_Spotify.Helpers.MediaSession
{
    public class MediaSessionHelper
    {
        public static bool EnableMediaSession = true;
        public static int InitCalls;
        public static int ControlCalls;
        public static bool ShouldReact
        {
            [MethodImpl(MethodImplOptions.NoInlining)] get => EnableMediaSession;
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void InitMediaSessionManager() => InitCalls++;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public Task<bool> MediaSessionTimerEvent() { ControlCalls++; return Task.FromResult(true); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Task<bool> PausePlayback() { ControlCalls++; return Task.FromResult(true); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Task<bool> ResumePlayback() { ControlCalls++; return Task.FromResult(true); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Task<bool> SkipPrevious() { ControlCalls++; return Task.FromResult(true); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Task<bool> SkipNext() { ControlCalls++; return Task.FromResult(true); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Task<bool> SeekTo(int position) { ControlCalls++; return Task.FromResult(true); }
    }
}
