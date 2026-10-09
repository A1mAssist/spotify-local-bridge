using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SpotifyAPI.Web;

internal static class IsrcCheck
{
    [STAThread]
    private static void Main()
    {
        var update = typeof(StartupHook).Assembly.GetType("PlaybackPatch")!
            .GetMethod("UpdateTrackInfoPage", BindingFlags.NonPublic | BindingFlags.Static)!;
        bool Refresh(object? page, string id, string isrc) => (bool)update.Invoke(null, new[] { page, id, isrc })!;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var page = new Lyricify_for_Spotify.Pages.PageAudioFeatures();
        page.TrackId.Text = "0BxE4FqsDD1Ot4YuBXwAPp";
        page.Isrc.Text = "暂无";
        Check(Refresh(page, page.TrackId.Text, "USUM71703861"), "Late ISRC must update an open page.");
        Check(page.Isrc.Text == "USUM71703861", "The visible WPF text did not update.");
        Check(!Refresh(page, page.TrackId.Text, "USUM71703861"), "Repeated state must not update the page again.");
        page.TrackId.Text = "6rqhFgbbKwnb9MLmUQDhG6";
        page.Isrc.Text = "另一首歌";
        Check(!Refresh(page, "0BxE4FqsDD1Ot4YuBXwAPp", "USUM71703861"), "A previous track must not replace another track's ISRC.");
        Check(page.Isrc.Text == "另一首歌", "Stale track metadata changed the new page.");
        Check(!Refresh(null, page.TrackId.Text, "USUM71703861"), "Closed pages must be ignored.");
        Check(!Refresh(new TextBlock(), page.TrackId.Text, "USUM71703861"), "Unrelated pages must be ignored.");
        var app = new Application();
        var main = new Lyricify_for_Spotify.MainWindow();
        app.MainWindow = main;
        main.FrameTrackInfo.Content = page;
        var schedule = typeof(StartupHook).Assembly.GetType("PlaybackPatch")!
            .GetMethod("RefreshTrackInfo", BindingFlags.NonPublic | BindingFlags.Static)!;
        var track = new FullTrack { Id = page.TrackId.Text, Uri = $"spotify:track:{page.TrackId.Text}",
            ExternalIds = new Dictionary<string, string> { ["isrc"] = "GBUM71703861" } };
        schedule.Invoke(null, new object[] { track });
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Check(page.Isrc.Text == "GBUM71703861", "The refresh was not dispatched to the actual WPF UI thread.");
        main.Close();
        app.Shutdown();
        Console.WriteLine("ISRC page checks passed");
    }
}

namespace Lyricify_for_Spotify
{
    internal sealed class MainWindow : Window
    {
        private static MainWindow? instance;
        internal Frame FrameTrackInfo = new();
        internal MainWindow() { instance = this; }
        public static MainWindow? GetMainWindow() => instance;
    }
}

namespace Lyricify_for_Spotify.Pages
{
    // Same public page shape as Lyricify 4.4.0, with real WPF text controls.
    internal sealed class PageAudioFeatures : Page
    {
        internal TextBlock TextBlock_TrackID = new();
        internal TextBlock TextBlock_TrackISRC = new();
        internal TextBlock TrackId => TextBlock_TrackID;
        internal TextBlock Isrc => TextBlock_TrackISRC;
    }
}
