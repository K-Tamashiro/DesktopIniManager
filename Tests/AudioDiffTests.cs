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
    internal static int RunProgress()
    {
        foreach (double duration in new[] { 2.425, 30.0, 300.0 })
        {
            Check(Math.Abs(AudioDiffPlayer.PlaybackPosition(0, duration / 2, 0, 0, duration) - duration / 2) < 1e-9,
                "elapsed playback seconds advance the cursor at real-time speed");
            Check(AudioDiffPlayer.PlaybackPosition(0, duration, 0, 0, duration) == duration,
                "cursor reaches the end after the full audio duration");
        }
        Check(Math.Abs(AudioDiffPlayer.PlaybackPosition(1.25, 0.5, 0, 0, 3) - 1.75) < 1e-9,
            "seek origin plus resumed elapsed time preserves synchronization");
        Check(Math.Abs(AudioDiffPlayer.PlaybackPosition(0.2, 0.017, 0, 0, 2) - 0.217) < 1e-9,
            "display clock advances between decoder buffer fills");
        Check(Math.Abs(AudioDiffPlayer.PlaybackPosition(0.4, 0.35, 0.2, 0.5, 2) - 0.45) < 1e-9,
            "display clock wraps across multiple repeat boundaries");
        Check(AudioDiffPlayer.PlaybackPosition(1.9, 0.3, 0, 0, 2) == 2,
            "normal playback display stops at EOF");
        string fixture = Path.Combine(AppContext.BaseDirectory, "cursor-wave.wav");
        Write(fixture, 1000, 1, 2, 0.5f);
        var envelope = AudioEnvelope.Load(fixture, CancellationToken.None);
        var wave = new AudioWaveform(envelope, 2, null) { Foreground = Brushes.White, Background = Brushes.Black };
        wave.Measure(new Size(800, 100));
        wave.Arrange(new Rect(0, 0, 800, 100));
        wave.UpdateLayout();
        byte[] RenderWave()
        {
            var bitmap = new RenderTargetBitmap(800, 100, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(wave);
            var pixels = new byte[800 * 100 * 4];
            bitmap.CopyPixels(pixels, 800 * 4, 0);
            return pixels;
        }
        RenderWave();
        var originalData = wave.Data;
        wave.Position = 0.5;
        var first = RenderWave();
        wave.Position = 1.5;
        var second = RenderWave();
        bool Red(byte[] pixels, int x) => pixels[(20 * 800 + x) * 4 + 2] > 200 &&
            pixels[(20 * 800 + x) * 4 + 1] < 30;
        Check(Red(first, 200) && !Red(first, 600) && Red(second, 600) && !Red(second, 200),
            "retained playhead moves without rebuilding the waveform");
        Check(ReferenceEquals(originalData, wave.Data), "cursor movement retains waveform data");
        for (int x = 0; x < 800; x++)
            if (Math.Abs(x - 200) > 2 && Math.Abs(x - 600) > 2)
                for (int y = 0; y < 100; y++)
                    for (int c = 0; c < 4; c++)
                        if (first[(y * 800 + x) * 4 + c] != second[(y * 800 + x) * 4 + c])
                            throw new Exception("Cursor movement changed waveform pixels");
        var renderWatch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 120; i++)
        {
            wave.Position = i / 60.0;
            RenderWave();
        }
        Console.WriteLine($"120 fit frames including pixel readback: {renderWatch.Elapsed.TotalMilliseconds:0.0} ms");
        wave.SetView(2, 0);
        wave.UpdateLayout();
        RenderWave();
        Check(wave.ViewDuration == 2, "short clips stay fitted at maximum zoom");
        wave.Fit();
        Check(ReferenceEquals(originalData, wave.Data), "fit and zoom retain waveform data");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 10000; i++) wave.Position = (i % 1000) / 500.0;
        Console.WriteLine($"10,000 retained position updates: {watch.Elapsed.TotalMilliseconds:0.0} ms");

        var overview = new AudioOverview(new[] { envelope, envelope }, 2)
        { Foreground = Brushes.White, Background = Brushes.Black };
        overview.Measure(new Size(900, 64)); overview.Arrange(new Rect(0, 0, 900, 64)); overview.UpdateLayout();
        void RenderOverview()
        {
            var bitmap = new RenderTargetBitmap(900, 64, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(overview);
        }
        RenderOverview();
        var cache = (float[][])typeof(AudioOverview).GetField("barPeaks", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(overview);
        var original = cache[0];
        overview.Update(1, 0.5f, 0.5f);
        overview.UpdateLayout();
        RenderOverview();
        Check(original != null && ReferenceEquals(original, cache[0]), "overview reuses aggregated peaks during playback");
        return 0;
    }
    private sealed class RepeatSamples : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(10, 2);
        internal int Frame;
        public int Read(float[] buffer, int offset, int count)
        {
            int read = Math.Min(count, Math.Max(0, (10 - Frame) * 2));
            for (int i = 0; i < read; i += 2, Frame++)
                buffer[offset + i] = buffer[offset + i + 1] = Frame;
            return read;
        }
    }
    internal static int RunRepeat()
    {
        var samples = new RepeatSamples();
        var repeat = new AudioRepeatProvider(samples, seconds => samples.Frame = (int)Math.Round(seconds * 10));
        var buffer = new float[28];
        repeat.SetRange(0.5, 0.2);
        repeat.Seek(0);
        Check(repeat.Read(buffer, 2, 24) == 24, "repeat fills a buffer across multiple wraps");
        for (int i = 0; i < 12; i++)
            Check(buffer[2 + i * 2] == 2 + i % 3 && buffer[3 + i * 2] == 2 + i % 3,
                "reverse selection loops only A–B and preserves stereo alignment");
        repeat.SetRange(0.9, 1.0);
        repeat.Seek(0.9);
        repeat.Read(buffer, 0, 28);
        Check(buffer.All(x => x == 9), "one-frame loop at EOF never stops or leaks outside the range");
        repeat.SetRange(null, null);
        repeat.Seek(0.8);
        Check(repeat.Read(buffer, 0, 28) == 4, "disabling repeat restores normal EOF");
        repeat.SetRange(0.5, 0.5);
        Check(!repeat.Enabled, "zero-length selection does not loop");
        return 0;
    }
    internal static int RunMetadata()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "audio-metadata-fixtures");
        Directory.CreateDirectory(root);
        AudioEnvelope Make(string name, WaveFormat format)
        {
            string path = Path.Combine(root, name + ".wav");
            using (var writer = new WaveFileWriter(path, format))
                writer.Write(new byte[format.AverageBytesPerSecond], 0, format.AverageBytesPerSecond);
            return AudioEnvelope.Load(path, CancellationToken.None);
        }
        var mono = Make("mono", new WaveFormat(48000, 16, 1));
        var stereo = Make("stereo", new WaveFormat(48000, 24, 2));
        var floating = Make("float", WaveFormat.CreateIeeeFloatWaveFormat(44100, 1));
        Check(mono.SampleRate == 48000 && mono.BitDepth == 16 && mono.BitRate == 768000,
            "original 16-bit mono metadata is retained instead of decoder 32-bit precision");
        Check(stereo.BitDepth == 24 && stereo.BitRate == 2304000, "PCM bit rate includes both stereo channels");
        Check(floating.BitDepth == 32 && floating.FloatingPoint, "floating point source format is identified");
        var header = AudioTimeline.CreateHeader(mono, stereo, true);
        var runs = header.Inlines.OfType<System.Windows.Documents.Run>().ToArray();
        Check(runs.Any(r => r.Text == "48.0 kHz" && r.Foreground != Brushes.Red), "matching sample rate keeps normal text color");
        Check(runs.Any(r => r.Text == "16 bit" && r.Foreground == Brushes.Red) &&
            runs.Any(r => r.Text == "768 kbps" && r.Foreground == Brushes.Red), "differing bit depth and bit rate are red");
        Check(!AudioTimeline.CreateHeader(mono, null, true).Inlines.OfType<System.Windows.Documents.Run>().Any(r => r.Foreground == Brushes.Red),
            "missing peer does not mark all metadata as different");
        string mp3 = Path.Combine(root, "encoded.mp3");
        using (var reader = new AudioFileReader(Path.Combine(root, "mono.wav")))
            MediaFoundationEncoder.EncodeToMp3(reader, mp3, 128000);
        var compressed = AudioEnvelope.Load(mp3, CancellationToken.None);
        Check(compressed.BitDepth == null && compressed.BitRate > 0 && compressed.BitRate < 200000,
            "MP3 reports encoded bitrate and no PCM bit depth");
        header.Measure(new Size(900, 80)); header.Arrange(new Rect(0, 0, 900, 80)); header.UpdateLayout();
        var bitmap = new RenderTargetBitmap(900, 80, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(header);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(root, "header.png"))) encoder.Save(file);
        return 0;
    }

    internal static int RunClipboard()
    {
        var store = new ClipboardInputStore(AppContext.BaseDirectory);
        string first;
        try
        {
            var text = new DataObject(); text.SetText("日本語\r\nclipboard\n");
            first = store.Capture(text)[0];
            Check(File.ReadAllText(first) == "日本語\r\nclipboard\n", "clipboard text preserves Unicode and line endings");
            string second = store.Capture(text)[0];
            Check(first != second, "each paste receives its own temporary file");
            var launch = ExternalDiffLaunch.Parse(new[] { "-diff" }); var snapshot = launch.CreateSnapshot();
            var model = new DesktopIniManager.ViewModels.DiffViewModel(snapshot, snapshot.Files[0], null, "", "");
            Check(model.DropFilesAsync(new[] { first }, true).GetAwaiter().GetResult() &&
                model.DropFilesAsync(new[] { second }, false).GetAwaiter().GetResult() &&
                model.GetPath(true) == first && model.GetPath(false) == second, "clipboard temporary files populate Source and Target independently");
            model.Close();
            var imageData = new DataObject();
            imageData.SetImage(BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4));
            string imagePath = store.Capture(imageData)[0];
            using (var stream = File.OpenRead(imagePath))
                Check(BitmapFrame.Create(stream).PixelWidth == 1, "clipboard bitmap is saved as a readable PNG");
            string original = Path.Combine(AppContext.BaseDirectory, "clipboard-original.wav");
            Write(original, 48000, 1, 0.1, 0.25f);
            var audio = new DataObject();
            using var audioStream = new MemoryStream(File.ReadAllBytes(original));
            audio.SetAudio(audioStream);
            string audioPath = store.Capture(audio)[0];
            using (var reader = new AudioFileReader(audioPath)) Check(reader.TotalTime.TotalSeconds > 0, "clipboard WAVE audio is readable");
            var fileData = new DataObject(DataFormats.FileDrop, new[] { original });
            string copy = store.Capture(fileData)[0];
            Check(copy != original && File.ReadAllBytes(copy).SequenceEqual(File.ReadAllBytes(original)), "copied clipboard files are isolated temporary copies");
            bool unsupported = false;
            try { store.Capture(new DataObject("CustomAudioEditor", "private data")); }
            catch (InvalidOperationException) { unsupported = true; }
            Check(unsupported, "unsupported editor-private clipboard formats are reported");
            store.Dispose();
            Check(!File.Exists(first) && !File.Exists(audioPath) && File.Exists(original), "cleanup deletes temporary inputs while preserving originals");
        }
        finally { store.Dispose(); }
        return 0;
    }

    internal static int RunDifferences()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "audio-difference-fixtures");
        Directory.CreateDirectory(root);
        string a = Path.Combine(root, "source.wav"), b = Path.Combine(root, "target.wav");
        float[] samples = Enumerable.Range(0, 1000).Select(i => (float)(0.5 * Math.Sin(i * 0.1))).ToArray();
        void Save(string path, float[] data, int channels = 1, int rate = 1000)
        {
            using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(rate, channels));
            writer.WriteSamples(data, 0, data.Length);
        }
        Save(a, samples); Save(b, samples);
        Check(AudioDifferences.Load(a, b, CancellationToken.None).Channels[0].Count == 0, "identical PCM has no differences");
        var changed = (float[])samples.Clone();
        for (int i = 400; i < 600; i++) changed[i] *= 0.5f;
        Save(b, changed);
        var differences = AudioDifferences.Load(a, b, CancellationToken.None);
        Check(differences.Channels[0].Count == 1 && differences.Channels[0][0] == (0.4, 0.6), "amplitude edit marks only its sample interval");
        var envelope = AudioEnvelope.Load(a, CancellationToken.None);
        var wave = new AudioWaveform(envelope, 1, a) { Foreground = Brushes.White, Background = Brushes.Black };
        wave.SetDifferences(differences, false);
        byte[] Render(bool enabled)
        {
            wave.ShowDifferences = enabled;
            wave.Measure(new Size(600, 180)); wave.Arrange(new Rect(0, 0, 600, 180)); wave.UpdateLayout();
            var bitmap = new RenderTargetBitmap(600, 180, 96, 96, PixelFormats.Pbgra32); bitmap.Render(wave);
            var bytes = new byte[600 * 180 * 4]; bitmap.CopyPixels(bytes, 2400, 0);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(root, enabled ? "enabled.png" : "disabled.png")); encoder.Save(file);
            return bytes;
        }
        var off = Render(false); var on = Render(true);
        Check(!off.SequenceEqual(on), "comparison adds visible red waveform highlighting");
        Check(off.SequenceEqual(Render(false)), "disabling comparison restores the original waveform exactly");
        Save(b, samples.Take(500).ToArray());
        var shorter = AudioDifferences.Load(a, b, CancellationToken.None);
        Check(shorter.Channels[0].Count == 1 && shorter.Channels[0][0] == (0.5, 1.0), "missing tail is different without stretching time");
        Save(b, samples.SelectMany(value => new[] { value, value }).ToArray(), 2);
        var stereo = AudioDifferences.Load(a, b, CancellationToken.None);
        Check(stereo.Channels[0].Count == 0 && stereo.Channels[1].Count == 1, "missing stereo channel is marked independently");
        Check(AudioDifferences.Load(a, null, CancellationToken.None).Channels[0][0] == (0.0, 1.0), "missing file marks the available waveform");
        Save(a, new float[1000]); Save(b, new float[2000], 1, 2000);
        Check(AudioDifferences.Load(a, b, CancellationToken.None).Channels[0].Count == 0, "different sample rates align on a common time axis");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        bool rejected = false;
        try { AudioDifferences.Load(a, b, canceled.Token); } catch (OperationCanceledException) { rejected = true; }
        Check(rejected, "comparison supports cancellation");
        // Cutting an interior span shifts the suffix, but does not change its audio.
        var original = Enumerable.Range(0, 2000).Select(i => (float)(Math.Sin(i * 0.137) * 0.4 + Math.Cos(i * 0.079) * 0.2)).ToArray();
        var cut = original.Take(800).Concat(original.Skip(836)).ToArray();
        Save(a, original, 2); Save(b, cut, 2);
        var deletion = AudioDifferences.Load(a, b, CancellationToken.None);
        Check(deletion.Channels.All(channel => channel.Count == 1 && channel[0] == (0.4, 0.418)) &&
            deletion.TargetChannels.All(channel => channel.Count == 0) && deletion.TargetJoin == 0.4,
            "middle stereo cut marks only the removed 18 ms and the target join; matching suffix stays unchanged");
        var insertion = AudioDifferences.Load(b, a, CancellationToken.None);
        Check(insertion.Channels.All(channel => channel.Count == 0) && insertion.SourceJoin == 0.4 &&
            insertion.TargetChannels.All(channel => channel.Count == 1 && channel[0] == (0.4, 0.418)),
            "reversed comparison marks the inserted interval on Target");
        cut[1500] += 0.1f; Save(b, cut, 2);
        var editedSuffix = AudioDifferences.Load(a, b, CancellationToken.None);
        Check(editedSuffix.TargetJoin == null && editedSuffix.Channels.Any(channel => channel.Any(range => range.End > 0.7)),
            "a changed suffix is not falsely classified as a pure cut");
        var launch = ExternalDiffLaunch.Parse(new[] { "-diff" });
        var snapshot = launch.CreateSnapshot();
        var model = new DesktopIniManager.ViewModels.DiffViewModel(snapshot, snapshot.Files[0], null, "", "");
        Check(model.DropFilesAsync(new[] { a }, true).GetAwaiter().GetResult() && model.IsAudio,
            "first audio drop opens Source in an empty viewer");
        Check(model.DropFilesAsync(new[] { b }, false).GetAwaiter().GetResult() && model.GetPath(true) == a && model.GetPath(false) == b,
            "second audio drop fills Target and retains Source");
        string replacement = Path.Combine(root, "replacement.wav"); Save(replacement, samples);
        Check(model.DropFilesAsync(new[] { replacement }, true).GetAwaiter().GetResult() && model.CanChangeComparison,
            "replacement audio drop leaves subsequent drops enabled");
        model.Close();
        return 0;
    }

    internal static int RunOverview()
    {
        var envelope = new AudioEnvelope
        {
            Channels = 1, Duration = 2,
            Min = new[] { new[] { -0.2f, -0.5f, -0.8f, -0.4f } },
            Max = new[] { new[] { 0.2f, 0.5f, 0.8f, 0.4f } }
        };
        var overview = new AudioOverview(new[] { envelope, envelope }, 2)
        { Foreground = Brushes.White, Background = Brushes.Black };
        byte[] Render(double seconds, float source, float target, int width, string name)
        {
            overview.Update(seconds, source, target);
            overview.Measure(new Size(width, 64));
            overview.Arrange(new Rect(0, 0, width, 64));
            overview.UpdateLayout();
            var bitmap = new RenderTargetBitmap(width, 64, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(overview);
            var pixels = new byte[width * 64 * 4];
            bitmap.CopyPixels(pixels, width * 4, 0);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(AppContext.BaseDirectory, name + ".png"));
            encoder.Save(file);
            return pixels;
        }
        var idle = Render(0, 0, 0, 900, "audio-overview-idle");
        var playing = Render(1, 0.5f, 0.1f, 900, "audio-overview-playing");
        Check(!idle.SequenceEqual(playing), "overview playback progress and output levels change the rendered pixels");
        var paused = Render(1, 0, 0, 900, "audio-overview-paused");
        Check(!playing.SequenceEqual(paused), "paused meters clear while progress is retained");
        var differences = (AudioDifferences)Activator.CreateInstance(typeof(AudioDifferences),
            BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { 2 }, null);
        differences.Channels[0].Add((0.5, 0.75));
        differences.Channels[1].Add((0.5, 0.75));
        differences.TargetChannels = new[] { new System.Collections.Generic.List<(double Start, double End)>() };
        differences.TargetJoin = 0.5;
        overview.SetDifferences(differences, true);
        var highlighted = Render(1, 0, 0, 900, "audio-overview-differences");
        int sourcePixel = (10 * 900 + 220) * 4;
        int targetPixel = (42 * 900 + 220) * 4;
        Check(highlighted[sourcePixel + 2] > paused[sourcePixel + 2], "overview marks source difference at its time position");
        Check(highlighted[targetPixel + 2] == paused[targetPixel + 2], "source-only difference does not tint target row");
        Check(highlighted.Skip(800 * 4).Take(100 * 4).SequenceEqual(paused.Skip(800 * 4).Take(100 * 4)),
            "difference overlay leaves the output meter area unchanged");
        overview.ShowDifferences = false;
        Check(Render(1, 0, 0, 900, "audio-overview-differences-off").SequenceEqual(paused),
            "turning differences off restores the original overview");
        Render(2, 1, 1, 200, "audio-overview-narrow");
        overview = new AudioOverview(new AudioEnvelope[] { null, envelope }, 2)
        { Foreground = Brushes.Black, Background = Brushes.White };
        Render(0.5, 0, 0.5f, 900, "audio-overview-one-sided-light");
        Check(true, "overview handles resize, missing source and light colors");
        var timeline = new AudioTimeline(new[] { envelope, envelope }, 80, new string[2]);
        void LayoutTimeline(double width)
        {
            timeline.Measure(new Size(width, 400));
            timeline.Arrange(new Rect(0, 0, width, 400));
            timeline.UpdateLayout();
        }
        LayoutTimeline(900);
        double height = timeline.Waves[0].ActualHeight;
        Check(Math.Abs(timeline.Waves[0].TimeAt(timeline.Viewport) - 80) < 0.001, "fit shows the full longer timeline");
        timeline.SetZoom(4);
        LayoutTimeline(900);
        Check(timeline.Offset == 0 && timeline.Waves[0].TimeAt(0) == 0, "zoom starts at time zero");
        Check(timeline.Waves[0].ActualHeight == height, "horizontal zoom preserves waveform height");
        Check(Math.Abs(timeline.Waves[0].TimeAt(450) - timeline.Waves[0].TimeAt(0) - 450 / timeline.Viewport * 20) < 0.001,
            "zoom reduces the visible time interval");
        timeline.ScrollBy(120);
        Check(timeline.Waves[0].TimeAt(100) == timeline.Waves[1].TimeAt(100), "both panes scroll on the same time axis");
        timeline.SetZoom(8);
        Check(timeline.Offset > 0, "zoom preserves the scrolled time");
        timeline.UpdatePosition(0, true);
        Check(timeline.Offset == 0, "seeking to start restores the left viewport");
        timeline.UpdatePosition(78, true);
        Check(timeline.Offset > 0 && timeline.Waves[0].PixelAt(78) - timeline.Offset <= timeline.Viewport,
            "playback follow keeps the playhead visible");
        double pausedOffset = timeline.Offset;
        timeline.UpdatePosition(0.2, false);
        Check(timeline.Offset == pausedOffset, "paused playback allows manual scrolling");
        LayoutTimeline(600);
        Check(timeline.Offset <= timeline.Viewport * (timeline.Zoom - 1), "resize keeps the scroll offset in bounds");
        timeline.Fit(); LayoutTimeline(600);
        Check(timeline.Zoom == 1 && timeline.Offset == 0 && Math.Abs(timeline.Waves[1].TimeAt(timeline.Viewport) - 80) < 0.001,
            "fit resets zoom and horizontal scroll");
        foreach (var wave in timeline.Waves) { wave.Foreground = Brushes.White; wave.Background = Brushes.Black; }
        timeline.SetZoom(4); timeline.UpdatePosition(1, true); LayoutTimeline(600);
        var timelineImage = new RenderTargetBitmap(600, 400, 96, 96, PixelFormats.Pbgra32);
        timelineImage.Render(timeline);
        var timelineEncoder = new PngBitmapEncoder(); timelineEncoder.Frames.Add(BitmapFrame.Create(timelineImage));
        using (var file = File.Create(Path.Combine(AppContext.BaseDirectory, "audio-timeline-zoom.png"))) timelineEncoder.Save(file);
        return 0;
    }

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
        Check(one.Data.Maximum(0, 0) > 0.24 && one.Data.Minimum(0, 0) < -0.24, "waveform signed peaks");
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
