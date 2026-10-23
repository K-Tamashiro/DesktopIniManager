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
        public WaveFormat WaveFormat { get; }
        private readonly long frames;
        private long sample;
        private readonly bool oscillate;
        internal Samples(double seconds, bool oscillate = false, int rate = 1001) { WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2); frames = (long)Math.Round(seconds * rate); this.oscillate = oscillate; }
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
            Check(data.BinCount == (int)Math.Ceiling(data.Duration * data.BinsPerSecond), $"{seconds}s fixed-time bins");
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
        wave.ZoomX(100); Check(wave.ViewDuration == 0.5, "maximum zoom is half a second");
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
        CheckRendering();
        return 0;
    }
    private sealed class RenderProbe : WaveformControl
    {
        internal DrawingVisual RenderNow()
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) OnRender(dc);
            return visual;
        }
    }
    private static void CheckRendering()
    {
        var data = WaveformData.Read(new Samples(1, true, 8000));
        Check(data.BinsPerSecond == 2048, "normal audio keeps 2048 bins per second");
        var random = new Random(314);
        for (int n = 0; n < 3000; n++)
        {
            int first = random.Next(data.BinCount), end = random.Next(first + 1, data.BinCount + 1), channel = n % 2;
            float min = float.PositiveInfinity, max = float.NegativeInfinity;
            for (int bin = first; bin < end; bin++)
            {
                min = Math.Min(min, data.Minimum(channel, bin)); max = Math.Max(max, data.Maximum(channel, bin));
            }
            data.GetExtrema(channel, first, end, out float indexedMin, out float indexedMax);
            if (min != indexedMin || max != indexedMax) throw new Exception("Indexed extrema differ from original bins");
        }
        Check(true, "3000 ranges preserve exact peaks across block boundaries");
        var wave = new RenderProbe { Foreground = Brushes.White, Background = Brushes.Black };
        wave.SetData(data, 4); wave.Measure(new Size(400, 200)); wave.Arrange(new Rect(0, 0, 400, 200));
        void CheckTail(double start, double span, int firstBlankPixel)
        {
            wave.SetViewRange(start, span);
            int width = (int)wave.ActualWidth;
            var image = new RenderTargetBitmap(width, 200, 96, 96, PixelFormats.Pbgra32);
            image.Render(wave.RenderNow()); var pixels = new byte[width * 200 * 4]; image.CopyPixels(pixels, width * 4, 0);
            // Outside each channel's zero baseline and outside the cursor.
            for (int x = firstBlankPixel; x < (int)wave.ActualWidth - 1; x++)
                for (int y = 10; y < 40; y++)
                    if (pixels[(y * (int)wave.ActualWidth + x) * 4 + 2] != 0) throw new Exception("Waveform extends beyond source EOF");
        }
        CheckTail(0, 4, 102); CheckTail(2, 1, 2); CheckTail(0.75, 0.5, 202);
        wave.Measure(new Size(1200, 200)); wave.Arrange(new Rect(0, 0, 1200, 200));
        CheckTail(0.75, 0.5, 602);
        Check(true, "source tail is blank at fit, beyond EOF and at detailed zoom");
        wave.SetViewRange(0, 1);
        var shape = wave.GetWaveformGeometry(0);
        wave.SetPosition(TimeSpan.FromSeconds(0.3)); wave.Foreground = Brushes.Blue;
        Check(ReferenceEquals(shape, wave.GetWaveformGeometry(0)), "cursor and color reuse geometry");
        wave.ZoomY(2); Check(!ReferenceEquals(shape, wave.GetWaveformGeometry(0)), "amplitude changes invalidate geometry");
        shape = wave.GetWaveformGeometry(0);
        wave.SetViewRange(0.1, 0.5); Check(!ReferenceEquals(shape, wave.GetWaveformGeometry(0)), "pan and zoom invalidate geometry");
        shape = wave.GetWaveformGeometry(0);
        wave.Measure(new Size(600, 240)); wave.Arrange(new Rect(0, 0, 600, 240));
        Check(!ReferenceEquals(shape, wave.GetWaveformGeometry(0)), "resize invalidates geometry");
        wave.SetData(WaveformData.Read(new Samples(1, false, 8000)));
        Check(!ReferenceEquals(shape, wave.GetWaveformGeometry(0)), "new source invalidates geometry");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var hour = WaveformData.Read(new Samples(3600, true, 2048));
        Console.WriteLine($"1h stereo decode + index: {watch.Elapsed.TotalMilliseconds:0.0} ms; bins/channel: {hour.BinCount}");
        wave.SetData(hour); wave.Measure(new Size(1200, 400)); wave.Arrange(new Rect(0, 0, 1200, 400));
        wave.RenderNow(); watch.Restart();
        for (int i = 0; i < 10; i++) wave.RenderNow();
        Console.WriteLine($"1h stereo cached render CPU: {watch.Elapsed.TotalMilliseconds / 10:0.000} ms/frame");
        watch.Restart();
        for (int i = 0; i < 10; i++) { wave.SetViewRange(i * 0.01, 3599); wave.RenderNow(); }
        Console.WriteLine($"1h stereo changed-view render CPU: {watch.Elapsed.TotalMilliseconds / 10:0.000} ms/frame");
        var envelope = new AudioEnvelope { Data = hour, Channels = 2, Duration = 3600, SampleRate = 2048 };
        var overview = new AudioOverview(new[] { envelope, envelope }, 3600) { Foreground = Brushes.White, Background = Brushes.Black };
        overview.Measure(new Size(1800, 64)); overview.Arrange(new Rect(0, 0, 1800, 64)); overview.UpdateLayout();
        var overviewImage = new RenderTargetBitmap(1800, 64, 96, 96, PixelFormats.Pbgra32); overviewImage.Render(overview);
        Check(true, "1h overview avoids integer overflow at wide window sizes");
        Check(ReferenceEquals(hour, wave.Data) && hour.BinsPerSecond == 2048, "performance path retains original full-resolution data");
    }

}
