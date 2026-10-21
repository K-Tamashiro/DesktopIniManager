using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopIniManager.Controls;
using DesktopIniManager.Services;
using DesktopIniManager.Views;
using NAudio.Wave;

internal static class WaveformControlTests
{
    private sealed class Samples : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(1001, 2);
        private readonly long frames;
        private long sample;
        private readonly bool oscillate;
        internal Samples(double seconds, bool oscillate = false) { frames = (long)Math.Round(seconds * 1001); this.oscillate = oscillate; }
        public int Read(float[] buffer, int offset, int count)
        {
            // Deliberately split stereo frames across reads.
            int length = (int)Math.Min(Math.Min(count, 997), frames * 2 - sample);
            for (int i = 0; i < length; i++, sample++)
                buffer[offset + i] = oscillate ? (float)(Math.Sin(sample / 2 * 0.3) * (0.45 + 0.4 * Math.Sin(sample / 2 * 0.0009))) : sample % 2 == 0 ? 0.75f : -0.25f;
            return length;
        }
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }
    internal static int Run()
    {
        foreach (double seconds in new[] { 0, 0.001, 2, 10, 60, 600, 3600 })
        {
            var data = WaveformData.Read(new Samples(seconds));
            Check(data.BinCount == (int)Math.Ceiling(data.Duration * 30), $"{seconds}s fixed-time bins");
            for (int i = 0; i < data.BinCount; i++)
                if (data.Minimum(0, i) != 0.75f || data.Maximum(0, i) != 0.75f ||
                    data.Minimum(1, i) != -0.25f || data.Maximum(1, i) != -0.25f)
                    throw new Exception("Frame boundaries or extrema were lost");
        }
        bool canceled = false, tooLong = false;
        try { WaveformData.Read(new Samples(1), new CancellationToken(true)); } catch (OperationCanceledException) { canceled = true; }
        try { WaveformData.Read(new Samples(3600.01)); } catch (ArgumentException) { tooLong = true; }
        Check(canceled && tooLong, "cancellation and one-hour limit");
        var original = WaveformData.Read(new Samples(60, true));
        var wave = new WaveformControl { Foreground = Brushes.White, Background = Brushes.Black };
        wave.SetData(original);
        wave.Measure(new Size(900, 240)); wave.Arrange(new Rect(0, 0, 900, 240)); wave.UpdateLayout();
        wave.ZoomX(6); wave.SetViewRange(17, 10); wave.ZoomY(2);
        Check(Math.Abs(wave.XToTime(wave.TimeToX(21.25)) - 21.25) < 1e-10, "time coordinates round trip after zoom and pan");
        Check(Math.Abs(wave.YToAmplitude(wave.AmplitudeToY(0.2, 1), 1) - 0.2) < 1e-10, "channel amplitude coordinates round trip");
        wave.ZoomX(100); Check(wave.ViewDuration == 10, "maximum zoom is ten seconds");
        wave.SetViewRange(999, 10); Check(wave.ViewStart == 50, "view is clamped at EOF");
        wave.Fit(); Check(wave.ViewStart == 0 && wave.ViewDuration == 60 && wave.AmplitudeScale == 1, "fit resets axes");
        var envelope = new AudioEnvelope { Data = original, Channels = 2, Duration = 60, SampleRate = 1001 };
        var timeline = new AudioTimeline(new[] { envelope, envelope }, 60, new string[2]);
        timeline.Measure(new Size(900, 480)); timeline.Arrange(new Rect(0, 0, 900, 480)); timeline.UpdateLayout();
        timeline.Waves[0].ZoomX(3);
        timeline.Waves[0].SetViewRange(12, 20);
        Check(timeline.Zoom == 3 && Math.Abs(timeline.Waves[1].ViewStart - 12) < 1e-9 && timeline.Waves[1].ViewDuration == 20,
            "control zoom and pan synchronize Source and Target");
        timeline.Measure(new Size(640, 480)); timeline.Arrange(new Rect(0, 0, 640, 480)); timeline.UpdateLayout();
        Check(Math.Abs(timeline.Waves[0].ViewStart - timeline.Waves[1].ViewStart) < 1e-9,
            "resize preserves synchronized time coordinates");
        wave.SetViewRange(15, 10); wave.SetPosition(TimeSpan.FromSeconds(19)); wave.UpdateLayout();
        var image = new RenderTargetBitmap(900, 240, 96, 96, PixelFormats.Pbgra32); image.Render(wave);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var file = File.Create(Path.Combine(AppContext.BaseDirectory, "waveform-control.png"))) encoder.Save(file);
        Check(ReferenceEquals(original, wave.Data), "view operations keep the original data instance");
        return 0;
    }
}
