using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using DesktopIniManager.Services;
using DesktopIniManager.Views;
using NAudio.Wave;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }
    private static object Call(object obj, string name, params object[] args) => obj.GetType().GetMethod(name, Private).Invoke(obj, args);
    private static T Field<T>(object obj, string name) => (T)obj.GetType().GetField(name, Private).GetValue(obj);

    [STAThread]
    private static int Main()
    {
        try
        {
            TestAudio();
            TestControls();
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void TestAudio()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "delay.wav");
        using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)))
            for (int frame = 0; frame < 48000; frame++)
            {
                float value = frame < 24000 ? 0.25f : 0.5f;
                writer.WriteSample(value); writer.WriteSample(value);
            }
        using var player = new AudioDiffPlayer(path, path, 1);
        player.SetVolume(1, 0);
        var mixer = Field<ISampleProvider>(player, "mixer");
        float[] block = new float[9600];
        player.SetDelay(0, 0.2);
        Check(Math.Abs(player.Duration - 1.2) < 1e-6, "positive delay extends the timeline through the full tail");
        Check(mixer.Read(block, 0, block.Length) == block.Length && block.All(x => x == 0), "delayed source starts with silence");
        player.Seek(0.2);
        mixer.Read(block, 0, block.Length);
        Check(block.All(x => x == 0.25f), "seek at delay boundary starts source at zero");
        player.Seek(1.1);
        mixer.Read(block, 0, block.Length);
        Check(block.All(x => x == 0.5f), "delayed tail remains audible after original duration");
        player.Seek(0);
        player.SetDelay(0, -0.6);
        mixer.Read(block, 0, block.Length);
        Check(block.All(x => x == 0.5f), "negative offset skips source samples");
        player.SetDelay(0, 0);
        player.SetDelay(1, 0.3);
        player.SetVolume(0, 0); player.SetVolume(1, 1); player.Seek(0);
        mixer.Read(block, 0, block.Length);
        Check(block.All(x => x == 0), "target delay is independent of source delay");
        player.SetRepeat(0.3, 0.4);
        player.Seek(0.3);
        var repeat = Field<ISampleProvider>(player, "repeat");
        float[] repeated = new float[28800];
        Check(repeat.Read(repeated, 0, repeated.Length) == repeated.Length && repeated.All(x => x == 0.25f), "repeat restores delayed inputs at each loop");
        foreach (double speed in new[] { 0.5, 0.75, 1, 1.5, 1.75, 2 }) player.SetSpeed(speed);
        Check(Math.Abs(player.GetDelay(1) - 0.3) < 1e-6, "speed changes preserve delay");
        player.SetDelay(0, -2); player.SetDelay(1, -2);
        Check(player.Duration == 0 && repeat.Read(block, 0, block.Length) == 0, "fully skipped inputs disable out-of-range repeat and end cleanly");
    }

    private static void TestControls()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var xml = new System.Xml.XmlDocument();
        xml.Load(Path.Combine(AppContext.BaseDirectory, "App.xaml"));
        var ns = new System.Xml.XmlNamespaceManager(xml.NameTable);
        ns.AddNamespace("p", "http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        var context = new ParserContext();
        context.XmlnsDictionary.Add("x", "http://schemas.microsoft.com/winfx/2006/xaml");
        app.Resources = (ResourceDictionary)XamlReader.Parse(xml.SelectSingleNode("p:Application/p:Application.Resources/p:ResourceDictionary", ns).OuterXml
            .Replace("Source=\"Themes/", "Source=\"/DesktopIniManager;component/Themes/"), context);
        var snapshot = ExternalDiffLaunch.Parse(new[] { "-diff" }).CreateSnapshot();
        var viewer = new DiffViewWindow(snapshot, snapshot.Files[0], "", "");
        try
        {
            typeof(DiffViewWindow).GetField("leftMedia", Private).SetValue(viewer, new MediaElement());
            typeof(DiffViewWindow).GetField("rightMedia", Private).SetValue(viewer, new MediaElement());
            Call(viewer, "BuildVideoController");
            var slider = Field<Slider>(viewer, "videoSeek");
            slider.Maximum = 10800; slider.IsEnabled = true;
            slider.Measure(new Size(150, 40)); slider.Arrange(new Rect(0, 0, 150, 40)); slider.UpdateLayout();
            Check(slider.ActualWidth <= 150, "video slider fits narrow transport instead of forcing 400px");
            slider.Value = 10799;
            var track = (Track)slider.Template.FindName("PART_Track", slider);
            var right = track.Thumb.TransformToAncestor(slider).Transform(new Point(track.Thumb.ActualWidth, 0)).X;
            Check(right <= 150, "thumb stays within track near long-video end");
            slider.Value = 7201;
            Check((double)typeof(DiffViewWindow).GetProperty("VideoPosition", Private).GetValue(viewer) == 7201, "requested long seek owns timeline even before decoder reports new position");
            Check(Field<TextBlock>(viewer, "videoTimeLabel").Text == "02:00:01 / 03:00:00", "long video label retains hours");
            slider.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            slider.Value = 8000;
            Check((double)typeof(DiffViewWindow).GetProperty("VideoPosition", Private).GetValue(viewer) == 7201, "drag previews do not repeatedly seek decoders");
            slider.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            Check((double)typeof(DiffViewWindow).GetProperty("VideoPosition", Private).GetValue(viewer) == 8000, "drag release commits final seek");
            var silentPanel = (Grid)Call(viewer, "CreateVideoVolumePanel", new MediaElement(), true);
            Check(!silentPanel.IsEnabled, "unopened or silent media disables volume controls");
            double selected = 1;
            var speeds = (StackPanel)Call(viewer, "CreatePlaybackSpeeds", "TestSpeed", (Action<double>)(v => selected = v));
            Check(speeds.Children.Count == 6, "all six playback rates available");
            ((RadioButton)speeds.Children[5]).IsChecked = true;
            Check(selected == 2, "speed button applies selected rate");
            double delay = 0;
            var pickerFrame = (Border)DiffViewWindow.CreateDelayPicker(true, () => delay, v => delay = v);
            var picker = (StackPanel)pickerFrame.Child;
            var arrows = (StackPanel)picker.Children[3];
            ((RepeatButton)arrows.Children[0]).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(delay == 0.1, "spinner increments by 0.1 seconds");
            var input = (TextBox)picker.Children[1]; input.Text = "invalid";
            ((RepeatButton)arrows.Children[1]).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(delay == 0, "invalid input recovers without corrupting offset");
            Call(viewer, "UpdateVideoSeekRange");
            Check(slider.Maximum == 0 && !slider.IsEnabled, "empty video timeline disables seeking");
            string path = Path.Combine(AppContext.BaseDirectory, "delay.wav");
            var envelope = AudioEnvelope.Load(path, System.Threading.CancellationToken.None);
            var timeline = new AudioTimeline(new[] { envelope, envelope }, 1, new[] { path, path });
            timeline.SetDelay(0, 0.2); timeline.SetDelay(1, -0.1); timeline.UpdatePosition(0.5, false);
            Check(Math.Abs(Field<double>(timeline.Waves[0], "cursorPosition") - 0.3) < 1e-6 &&
                Math.Abs(Field<double>(timeline.Waves[1], "cursorPosition") - 0.6) < 1e-6,
                "source and target red cursors reflect independent offsets");
            var preview = new StackPanel { Background = (Brush)app.FindResource("WindowBackground"), Width = 720 };
            var videoHeader = new TextBlock { Foreground = (Brush)app.FindResource("Ink"), Margin = new Thickness(16), FontSize = 12 };
            videoHeader.Inlines.Add("Source  ");
            videoHeader.Inlines.Add(new System.Windows.Documents.InlineUIContainer(new Border { CornerRadius = new CornerRadius(10),
                BorderBrush = Brushes.Gold, BorderThickness = new Thickness(1), Padding = new Thickness(8, 2, 8, 2),
                Child = new TextBlock { Text = "MP4", Foreground = Brushes.Gold } }) { BaselineAlignment = BaselineAlignment.Center });
            videoHeader.Inlines.Add(new System.Windows.Documents.InlineUIContainer(pickerFrame) { BaselineAlignment = BaselineAlignment.Center });
            preview.Children.Add(videoHeader);
            preview.Children.Add(timeline);
            preview.Measure(new Size(720, 300)); preview.Arrange(new Rect(0, 0, 720, 300)); preview.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(720, 300, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(preview);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(AppContext.BaseDirectory, "delay-layout.png"));
            encoder.Save(file);
        }
        finally { viewer.Close(); app.Shutdown(); }
    }
}
