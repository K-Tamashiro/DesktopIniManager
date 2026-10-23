using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using DesktopIniManager.Services;

internal static class V430Tests
{
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
    internal static int Run()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "v430-" + Guid.NewGuid().ToString("N"));
        string a = Path.Combine(root, "source"), b = Path.Combine(root, "target");
        Directory.CreateDirectory(a); Directory.CreateDirectory(b);
        void Write(string folder, string name, string content)
        {
            string path = Path.Combine(folder, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, content);
        }
        Write(a, ".gitignore", "trash/\n*.tmp\n!keep.tmp\n/root.txt\n**/cache/**\na/**/secret?.[ch]\n\\#literal\n\\!literal\nspace\\ \n[!a]name\nblocked/\n!blocked/keep.txt\n");
        Write(a, "nested/.gitignore", "!local.tmp\n/only.txt\n");
        var matcher = new GitIgnoreMatcher(a);
        foreach (string name in new[] { "trash/x", "nested/trash/x", "x.tmp", "root.txt", "cache/x", "nested/cache/x", "a/secret1.c", "a/b/c/secret2.h", "#literal", "!literal", "space ", "bname", "blocked/keep.txt", "nested/only.txt" })
            Check(matcher.IsIgnored(name, false), "ignored " + name);
        foreach (string name in new[] { "keep.tmp", "nested/keep.tmp", "nested/local.tmp", "nested/root.txt", "trash", "aname", "a/b/secret12.c", "nested/deep/only.txt" })
            Check(!matcher.IsIgnored(name, false), "included " + name);
        Write(a, "trash/x", "excluded"); Write(a, "keep.tmp", "keep"); Write(a, "target-only-ignore.txt", "do not copy");
        Write(b, ".gitignore", "target-only-ignore.txt\n"); Write(b, "trash/x", "also excluded by source");
        var left = DeveloperDifferencerService.BuildIndex(a, default);
        var right = DeveloperDifferencerService.BuildIndex(b, default);
        var comparison = DeveloperDifferencerService.Compare(left, right, null, false, default);
        Check(!comparison.Files.Any(f => f.RelativePath.Contains("trash") || f.RelativePath == "target-only-ignore.txt"), "both roots' ignores remove comparison paths");
        Check(left.Files.ContainsKey("target-only-ignore.txt"), "comparison does not mutate cached indexes");
        Check(!comparison.Folders.Any(f => f.Contains("trash")), "ignored directory absent from result tree");
        Check(DeveloperDifferencerService.Compare(a, b).Files.All(f => !f.RelativePath.Contains("trash")), "direct compare excludes ignores");
        Check(DeveloperDifferencerService.CompareFolderLevel(a, b, "trash").Files.Count == 0, "folder refresh respects ancestor exclusions");
        var stale = new DiffFile { RelativePath = "target-only-ignore.txt", Source = DiffStamp.Read(Path.Combine(a, "target-only-ignore.txt")) };
        var log = DeveloperDifferencerService.Synchronize(comparison, new[] { stale }, true);
        Check(log.Single().StartsWith("SKIP") && !File.Exists(Path.Combine(b, stale.RelativePath)), "stale snapshot cannot synchronize ignored destination");
        byte[] gifBytes = MakeGif();
        GifFrames gif = Task.Run(() =>
        {
            using var stream = new MemoryStream(gifBytes);
            return new GifFrames(new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad));
        }).GetAwaiter().GetResult();
        Check(gif.Count == 4 && gif.DelayMilliseconds(0) == 50 && gif.DelayMilliseconds(1) == 90, "GIF frame count and original delays");
        uint[] Pixels(int frame)
        {
            var bitmap = Task.Run(() => gif.Compose(frame)).GetAwaiter().GetResult();
            var pixels = new uint[3]; bitmap.CopyPixels(pixels, 12, 0); return pixels;
        }
        Check(Pixels(0).SequenceEqual(new uint[] { 0xffff0000, 0, 0 }), "GIF logical screen and transparency");
        Check(Pixels(1).SequenceEqual(new uint[] { 0xffff0000, 0xff0000ff, 0 }), "GIF delta rectangle composes with preceding frame");
        Check(Pixels(2).SequenceEqual(new uint[] { 0xffff0000, 0, 0xffffffff }), "GIF restore-background disposal");
        Check(Pixels(3).SequenceEqual(new uint[] { 0xffff0000, 0xffff0000, 0 }), "GIF restore-previous disposal");
        Check(Pixels(1).SequenceEqual(new uint[] { 0xffff0000, 0xff0000ff, 0 }), "GIF backward seek is deterministic");
        Check(gif.Compose(4) == null, "shorter GIF has missing frames beyond its range");
        Console.WriteLine("PASS v4.3.0 regression suite"); return 0;
    }
    internal static int RunGifViewer()
    {
        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(app.Dispatcher));
        var xml = new System.Xml.XmlDocument();
        xml.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "App.xaml"));
        var ns = new System.Xml.XmlNamespaceManager(xml.NameTable);
        ns.AddNamespace("p", "http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        var context = new System.Windows.Markup.ParserContext();
        context.XmlnsDictionary.Add("x", "http://schemas.microsoft.com/winfx/2006/xaml");
        app.Resources = (System.Windows.ResourceDictionary)System.Windows.Markup.XamlReader.Parse(
            xml.SelectSingleNode("p:Application/p:Application.Resources/p:ResourceDictionary", ns).OuterXml
                .Replace("Source=\"Themes/", "Source=\"/DesktopIniManager;component/Themes/"), context);
        void Await(Task task)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            task.GetAwaiter().OnCompleted(() => frame.Continue = false);
            if (!task.IsCompleted) System.Windows.Threading.Dispatcher.PushFrame(frame);
            task.GetAwaiter().GetResult();
        }
        string root = Path.Combine(AppContext.BaseDirectory, "gif-viewer-fixtures"); Directory.CreateDirectory(root);
        string path = Path.Combine(root, "animation.gif");
        byte[] original = MakeGif();
        var withoutControl = new System.Collections.Generic.List<byte>();
        for (int i = 0; i < original.Length; i++)
        {
            if (i + 7 < original.Length && original[i] == 0x21 && original[i + 1] == 0xf9 && original[i + 2] == 4) i += 7;
            else withoutControl.Add(original[i]);
        }
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(BitmapSource.Create(3, 1, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, new byte[12], 12)));
        using var pngStream = new MemoryStream();
        png.Save(pngStream);
        foreach (var fixture in new[] { (original, "gif", 4), (withoutControl.ToArray(), "gif", 4), (pngStream.ToArray(), "png", 1) })
        {
            path = Path.Combine(root, "animation." + fixture.Item2);
            File.WriteAllBytes(path, fixture.Item1);
            var snapshot = ExternalDiffLaunch.Parse(new[] { "-diff", path, path }).CreateSnapshot();
            var window = new DesktopIniManager.Views.DiffViewWindow(snapshot, snapshot.Files[0], path, path);
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.SourceInitialized += (_, _) =>
            {
                window.WindowState = System.Windows.WindowState.Normal;
                window.Left = -20000; window.Top = -20000;
                window.Width = 1280; window.Height = 850;
            };
            try
            {
                window.Show();
                async Task WaitForLoad()
                {
                    var deadline = DateTime.UtcNow.AddSeconds(30);
                    var busy = typeof(DesktopIniManager.Views.DiffViewWindow).GetField("renderingContent", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    while ((bool)busy.GetValue(window) && DateTime.UtcNow < deadline) await Task.Delay(10);
                    Check(!(bool)busy.GetValue(window), "initial image load completes");
                }
                Await(WaitForLoad());
                for (int repeat = 0; repeat < 10; repeat++)
                {
                    var reload = typeof(DesktopIniManager.Views.DiffViewWindow).GetMethod("LoadContent", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    Await((Task)reload.Invoke(window, new object[] { true, true }));
                    Check(window.ViewModel.ImageFrameCount == fixture.Item3, "Image reload keeps all frames (" + repeat + ")");
                    var zoom = (System.Windows.Controls.ScrollViewer)window.FindName("imageControlRow");
                    var frames = (System.Windows.Controls.StackPanel)window.FindName("gifFrameHost");
                    Check(zoom.Visibility == System.Windows.Visibility.Visible &&
                        (fixture.Item3 > 1 ? frames.Visibility == System.Windows.Visibility.Visible && frames.Children.Count == 1
                            : frames.Visibility == System.Windows.Visibility.Collapsed),
                        "Image zoom and frame controller visibility");
                    foreach (double width in new[] { 1300.0, 700.0, 900.0, 1300.0 })
                    {
                        var content = (System.Windows.FrameworkElement)window.Content;
                        window.Width = width;
                        window.UpdateLayout();
                        app.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        content.UpdateLayout();
                        var slider = (System.Windows.Controls.Slider)typeof(DesktopIniManager.Views.DiffViewWindow)
                            .GetField("imageZoom", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(window);
                        var bounds = slider.TransformToAncestor(content).TransformBounds(new System.Windows.Rect(slider.RenderSize));
                        Console.WriteLine($"LAYOUT repeat={repeat} width={width} host={zoom.ActualWidth}x{zoom.ActualHeight} slider={bounds} scroll={zoom.ScrollableWidth}");
                        Check(zoom.ActualWidth > 0 && bounds.Width >= 20 && bounds.Height > 0 &&
                            bounds.Left >= 0 && bounds.Right <= width && zoom.ScrollableWidth < 1,
                            "image zoom retains usable width at " + width);
                    }
                    for (int index = fixture.Item3 - 1; index >= 0; index--) Await(window.ViewModel.SelectImageFrameAsync(index));
                }
            }
            finally { window.Close(); }
        }
        app.Shutdown();
        return 0;
    }

    internal static byte[] MakeGif()
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        w.Write(System.Text.Encoding.ASCII.GetBytes("GIF89a"));
        w.Write((ushort)3); w.Write((ushort)1); w.Write((byte)0x81); w.Write((byte)0); w.Write((byte)0);
        w.Write(new byte[] { 0,0,0, 255,0,0, 0,0,255, 255,255,255 });
        void Frame(int x, int color, int disposal, int delay)
        {
            w.Write(new byte[] { 0x21,0xf9,4,(byte)((disposal << 2) | 1) });
            w.Write((ushort)delay); w.Write((byte)0); w.Write((byte)0);
            w.Write((byte)0x2c); w.Write((ushort)x); w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)1); w.Write((byte)0);
            int bits = 4 | (color << 3) | (5 << 6);
            w.Write(new byte[] { 2, 2, (byte)bits, (byte)(bits >> 8), 0 });
        }
        Frame(0,1,1,5); Frame(1,2,2,9); Frame(2,3,3,12); Frame(1,1,1,1);
        w.Write((byte)0x3b); return stream.ToArray();
    }
}
