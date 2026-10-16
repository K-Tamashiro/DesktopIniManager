using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopIniManager.Services;
using DesktopIniManager.Views;
using NAudio.Wave;

internal static class AudioDiffTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private static void Await(Task task)
    {
        var frame = new DispatcherFrame();
        task.GetAwaiter().OnCompleted(() => frame.Continue = false);
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
    internal static int Run()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "audio-fixtures");
        Directory.CreateDirectory(root);
        string a = Path.Combine(root, "source.wav"), b = Path.Combine(root, "target.wav");
        Write(a, 48000, 1, 1, 0.25f); Write(b, 24000, 2, 2, 0.5f);
        Check(DiffMedia.IsAudio(a) && DiffMedia.IsAudio("A.MP3") && DiffMedia.IsAudio("a.wave") && !DiffMedia.IsBinary(a), "audio routing");
        var one = AudioEnvelope.Load(a, CancellationToken.None);
        var two = AudioEnvelope.Load(b, CancellationToken.None);
        Check(one.Channels == 1 && one.Duration == 1 && two.Channels == 2 && two.Duration == 2, "mono/stereo and unequal durations");
        Check(one.Max[0].Max() > 0.24 && one.Min[0].Min() < -0.24, "waveform signed peaks");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel(); bool rejected = false;
            try { AudioEnvelope.Load(a, canceled.Token); } catch (OperationCanceledException) { rejected = true; }
            Check(rejected, "decoding cancellation");
        }
        var samples = new float[9600];
        using (var player = new AudioDiffPlayer(a, b, 2))
        {
            player.SetVolume(0, 0); player.SetVolume(1, 0);
            Check(ReadSamples(player, samples) == samples.Length && samples.All(x => x == 0), "independent mute produces silence");
            player.Seek(0.2); player.SetVolume(0, 1);
            ReadSamples(player, samples);
            Check(samples.Max() > 0.24 && samples.Max() < 0.26 && samples[100] == samples[101], "source-only gain and mono stereo mapping");
            player.Seek(1.5); ReadSamples(player, samples);
            Check(samples.All(x => x == 0), "shorter source becomes silence after seek");
            player.SetVolume(1, 1); ReadSamples(player, samples);
            Check(samples.Max() > 0.45 && samples.Max() < 0.55, "longer target continues with resampling");
            player.Seek(2); Check(ReadSamples(player, samples) == 0, "end of timeline");
            player.Seek(0.25); player.SetVolume(1, 0); ReadSamples(player, samples);
            Check(samples.Max() > 0.24, "backwards seek resets decoder");
        }
        using (var player = new AudioDiffPlayer(null, b, 2))
        { Check(ReadSamples(player, samples) > 0, "one-sided audio"); }
        string mp3 = Path.Combine(root, "sample.mp3");
        using (var reader = new AudioFileReader(a)) MediaFoundationEncoder.EncodeToMp3(reader, mp3);
        Check(AudioEnvelope.Load(mp3, CancellationToken.None).Duration > 0.9, "MP3 decoding");
        using (var player = new AudioDiffPlayer(mp3, b, 2))
        { player.Seek(0.4); ReadSamples(player, samples); Check(samples.Any(x => Math.Abs(x) > 0.1), "MP3/WAV mixed decoding and seeking"); }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var xml = new System.Xml.XmlDocument();
        xml.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "App.xaml"));
        var ns = new System.Xml.XmlNamespaceManager(xml.NameTable);
        ns.AddNamespace("p", "http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        var context = new ParserContext(); context.XmlnsDictionary.Add("x", "http://schemas.microsoft.com/winfx/2006/xaml");
        app.Resources = (ResourceDictionary)XamlReader.Parse(xml.SelectSingleNode("p:Application/p:Application.Resources/p:ResourceDictionary", ns).OuterXml
            .Replace("Source=\"Themes/", "Source=\"/DesktopIniManager;component/Themes/"), context);
        var snapshot = ExternalDiffLaunch.Parse(new[] { "-diff", a, b }).CreateSnapshot();
        var viewer = new DiffViewWindow(snapshot, snapshot.Files[0], a, b);
        try
        {
            Await(viewer.ViewModel.LoadContentAsync());
            // A caller may have no WPF synchronization context. The view must dispatch explicitly.
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                Await((Task)typeof(DiffViewWindow).GetMethod("RenderAudio", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(viewer, null));
            }
            finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
            Check(((DispatcherTimer)typeof(DiffViewWindow).GetField("audioTimer", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(viewer)).Dispatcher == viewer.Dispatcher,
                "audio controls and timer belong to the window dispatcher without a synchronization context");
            var body = (Grid)viewer.FindName("body");
            var audioRoot = (Grid)body.Parent;
            foreach (string theme in new[] { "Dark", "Light" })
            {
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/DesktopIniManager;component/Themes/{theme}Theme.xaml", UriKind.Relative) });
                audioRoot.Measure(new Size(1200, 650)); audioRoot.Arrange(new Rect(0, 0, 1200, 650)); audioRoot.UpdateLayout();
                var png = new RenderTargetBitmap(1200, 650, 96, 96, PixelFormats.Pbgra32); png.Render(audioRoot);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(png));
                using var file = File.Create(Path.Combine(root, theme + ".png")); encoder.Save(file);
                var wave = Find<AudioWaveform>(audioRoot);
                Check(((SolidColorBrush)wave.Foreground).Color == (theme == "Dark" ? Colors.White : Color.FromRgb(46, 32, 48)), theme + " waveform theme");
            }
            typeof(DiffViewWindow).GetMethod("ResetMediaController", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(viewer, null);
            Check(typeof(DiffViewWindow).GetField("audioPlayer", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(viewer) == null, "reload releases audio resources");
        }
        finally { viewer.Close(); app.Shutdown(); }
        using (File.Open(a, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        Console.WriteLine("PASS audio handles released; previews: " + root);
        return 0;
    }
    private static T Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T value) return value;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var found = Find<T>(VisualTreeHelper.GetChild(root, i)); if (found != null) return found; }
        return null;
    }
    private static int ReadSamples(AudioDiffPlayer player, float[] samples)
    {
        var provider = (ISampleProvider)typeof(AudioDiffPlayer).GetField("mixer", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(player);
        return provider.Read(samples, 0, samples.Length);
    }
    private static void Write(string path, int rate, int channels, double duration, float amplitude)
    {
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(rate, channels));
        for (int i = 0; i < rate * duration; i++)
            for (int c = 0; c < channels; c++) writer.WriteSample(amplitude * (float)Math.Sin(2 * Math.PI * (c == 0 ? 440 : 660) * i / rate));
    }
}
