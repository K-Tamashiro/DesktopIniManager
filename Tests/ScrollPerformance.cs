using DesktopIniManager.Services;
using DesktopIniManager.Views;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class ScrollPerformance
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Field(object target, string name, object value) { target.GetType().GetField(name, Private).SetValue(target, value); }
    private static IEnumerable<T> Visuals<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T) yield return (T)child;
            foreach (var match in Visuals<T>(child)) yield return match;
        }
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (s, e) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    public static int Run(string appXaml)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var xml = new System.Xml.XmlDocument(); xml.Load(appXaml);
        var namespaces = new System.Xml.XmlNamespaceManager(xml.NameTable);
        namespaces.AddNamespace("p", "http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        string resources = xml.SelectSingleNode("p:Application/p:Application.Resources/p:ResourceDictionary", namespaces).OuterXml;
        var context = new ParserContext(); context.XmlnsDictionary.Add("x", "http://schemas.microsoft.com/winfx/2006/xaml");
        app.Resources = (ResourceDictionary)XamlReader.Parse(resources.Replace("Source=\"Themes/", "Source=\"/DesktopIniManager;component/Themes/"), context);
        string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scroll-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "sample.txt"), "text");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(320, 240, 96, 96, PixelFormats.Bgra32, null, new byte[320 * 240 * 4], 320 * 4)));
        using (var stream = File.Create(Path.Combine(root, "sample.png"))) encoder.Save(stream);
        typeof(MftDifferencerWindow).GetField("StatePath", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, Path.Combine(root, "state.xml"));
        var window = new MftDifferencerWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
        var snapshot = new DiffSnapshot { SourceRoot = root + "\\", TargetRoot = root + "\\" };
        var rows = new List<DiffRow>();
        for (int i = 0; i < 30000; i++)
        {
            string directory = "folder" + (i / 10).ToString("D5"); snapshot.Folders.Add(directory);
            string sample = i % 5 == 0 ? "sample.png" : "sample.txt";
            var stamp = new DiffStamp { Size = 12, ModifiedUtc = DateTime.UtcNow };
            var file = new DiffFile { RelativePath = directory + "\\" + i.ToString("D5") + Path.GetExtension(sample), Source = stamp, Target = new DiffStamp { Size = 11, ModifiedUtc = stamp.ModifiedUtc.AddDays(-1) } };
            snapshot.Files.Add(file);
            rows.Add(new DiffRow { File = file, Source = new DiffSide { Root = snapshot.SourceRoot, Relative = sample, Exists = true, Info = file.SourceInfo }, Target = new DiffSide { Root = snapshot.TargetRoot, Relative = sample, Exists = true, Info = file.TargetInfo } });
        }
        Field(window, "snapshot", snapshot); Field(window, "rows", rows); Field(window, "treeSource", root); Field(window, "treeTarget", root);
        typeof(MftDifferencerWindow).GetMethod("BuildTree", Private).Invoke(window, new object[] { snapshot.Folders, new[] { "" }, "" });
        window.Show(); Pump(500);
        var list = (ListView)window.FindName("FilesGrid");
        var tree = (TreeView)window.FindName("FolderTree");
        var scroll = Visuals<ScrollViewer>(list).First();
        var treeScroll = Visuals<ScrollViewer>(tree).First();
        Console.WriteLine("INITIAL rows=" + Visuals<ListViewItem>(list).Count() + " folders=" + Visuals<TreeViewItem>(tree).Count() + " canContentScroll=" + scroll.CanContentScroll + " extent=" + scroll.ExtentHeight + " viewport=" + scroll.ViewportHeight);
        var heartbeat = Stopwatch.StartNew(); double last = 0, worst = 0;
        var input = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        input.Tick += (s, e) => { double now = heartbeat.Elapsed.TotalMilliseconds; worst = Math.Max(worst, now - last); last = now; };
        input.Start();
        int maxRows = 0, maxFolders = 0;
        var times = new List<double>();
        for (int i = 0; i < 60; i++)
        {
            var step = Stopwatch.StartNew();
            if (i < 30) scroll.ScrollToVerticalOffset(i % 2 == 0 ? i * 900 : i * 30);
            else treeScroll.ScrollToVerticalOffset((i - 30) * 80);
            Pump(20);
            times.Add(step.Elapsed.TotalMilliseconds);
            maxRows = Math.Max(maxRows, Visuals<ListViewItem>(list).Count());
            maxFolders = Math.Max(maxFolders, Visuals<TreeViewItem>(tree).Count());
            if (i % 10 == 0) Console.WriteLine("STEP " + i + " ms=" + step.ElapsedMilliseconds + " rows=" + maxRows + " folders=" + maxFolders);
        }
        input.Stop();
        var cpu = Process.GetCurrentProcess().TotalProcessorTime; Pump(1000);
        double idleCpu = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds;
        Console.WriteLine("RESULT maxRows=" + maxRows + " maxFolders=" + maxFolders + " medianStepMs=" + times.OrderBy(x => x).ElementAt(times.Count / 2).ToString("F1") + " maxStepMs=" + times.Max().ToString("F1") + " maxInputGapMs=" + worst.ToString("F1") + " idleCpuMs=" + idleCpu.ToString("F1") + " privateMB=" + (Process.GetCurrentProcess().PrivateMemorySize64 / 1048576));
        int cachedImages = rows.Count(row => row.Source.Thumbnail != null || row.Target.Thumbnail != null);
        Console.WriteLine("RETAINED imageRows=" + cachedImages);
        bool bounded = maxRows <= Math.Max(32, scroll.ViewportHeight * 4) && cachedImages <= maxRows;
        window.Close(); app.Shutdown();
        if (!bounded) throw new Exception("Off-screen rows or images exceeded the viewport budget.");
        return 0;
    }
}
