using System;
using System.IO;
using System.Linq;
using System.Text;
using DesktopIniManager.Services;
using DesktopIniManager.ViewModels;
using DesktopIniManager.Views;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Reflection;

internal static class DiffRegressionTests
{
    internal static int RunHashComparison()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "hash-comparison-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source"), target = Path.Combine(root, "target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        string a = Path.Combine(source, "sample.txt"), b = Path.Combine(target, "sample.txt");
        var time = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int differences = 0; differences < 16; differences++)
        {
            bool timeDiff = (differences & 1) != 0, sizeDiff = (differences & 2) != 0;
            bool attrDiff = (differences & 4) != 0, contentDiff = (differences & 8) != 0;
            File.WriteAllBytes(a, new byte[] { 65, 66, 67, 68 });
            byte[] other = attrDiff ? new byte[] { 239, 187, 191, 65 } : new byte[] { 65, 66, 67, (byte)(contentDiff ? 69 : 68) };
            if (sizeDiff) other = other.Concat(new byte[] { 70 }).ToArray();
            File.WriteAllBytes(b, other);
            File.SetLastWriteTimeUtc(a, time);
            File.SetLastWriteTimeUtc(b, time.AddSeconds(timeDiff ? 2 : 0));
            var left = new System.Collections.Generic.Dictionary<string, DiffStamp> { ["sample.txt"] = DiffStamp.Read(a) };
            var right = new System.Collections.Generic.Dictionary<string, DiffStamp> { ["sample.txt"] = DiffStamp.Read(b) };
            for (int flags = 0; flags < 16; flags++)
            {
                bool timestamp = (flags & 1) != 0, size = (flags & 2) != 0, attributes = (flags & 4) != 0, hash = (flags & 8) != 0;
                bool earlyDifference = (timestamp && timeDiff) || (size && sizeDiff) || (attributes && attrDiff);
                bool hashRequired = hash && !earlyDifference;
                var result = DeveloperDifferencerService.Classify(left, right, includeSame: true, compareTimestamp: timestamp,
                    sourceRoot: source, targetRoot: target, preciseCompare: hash, compareSize: size, compareAttributes: attributes).Single();
                Require((result.SourceHash != null && result.TargetHash != null) == hashRequired,
                    "hash eligibility flags=" + flags + " differences=" + differences);
                bool different = earlyDifference || (hashRequired && (sizeDiff || attrDiff || contentDiff));
                Require(result.Kind == (different ? DiffKind.Different : DiffKind.Same), "classification flags=" + flags + " differences=" + differences);
            }
        }
        var stamp = DiffStamp.Read(a);
        Require(!DeveloperDifferencerService.ShouldHash(stamp, stamp, false, false, true, null, false, true), "unreadable ATTR never counts as same for hashing");
        var only = DeveloperDifferencerService.Classify(new() { ["sample.txt"] = stamp }, new(), includeSame: true,
            sourceRoot: source, targetRoot: target, preciseCompare: true).Single();
        Require(only.Kind == DiffKind.SourceOnly && only.SourceHash == null && only.TargetHash == null, "one-sided files skip hashing");
        Console.WriteLine("PASS 256 file comparison combinations, XXH128 eligibility, unreadable ATTR and one-sided files");
        return 0;
    }

    internal static int RunViewer()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var xml = new System.Xml.XmlDocument();
        xml.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "App.xaml"));
        var namespaces = new System.Xml.XmlNamespaceManager(xml.NameTable);
        namespaces.AddNamespace("p", "http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        string resources = xml.SelectSingleNode("p:Application/p:Application.Resources/p:ResourceDictionary", namespaces).OuterXml;
        var context = new ParserContext();
        context.XmlnsDictionary.Add("x", "http://schemas.microsoft.com/winfx/2006/xaml");
        app.Resources = (ResourceDictionary)XamlReader.Parse(resources.Replace("Source=\"Themes/", "Source=\"/DesktopIniManager;component/Themes/"), context);
        string fixture = Path.Combine(AppContext.BaseDirectory, "diff-viewer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        string left = Path.Combine(fixture, "source.txt"), right = Path.Combine(fixture, "target.txt");
        string shared = string.Join("\n", Enumerable.Range(0, 100).Select(i => "unchanged " + i));
        File.WriteAllText(left, "source marker\t \u3000\n" + shared + "\nsource end", new UTF8Encoding(true));
        File.WriteAllText(right, "target marker\n" + shared + "\ntarget end", new UnicodeEncoding(false, true));
        var launch = ExternalDiffLaunch.Parse(["-diff", left, right]);
        var snapshot = launch.CreateSnapshot();
        var viewer = new DiffViewWindow(snapshot, snapshot.Files[0], left, right)
        {
            Width = 1200, Height = 800, Left = -20000, Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false, ShowActivated = false
        };
        try
        {
            viewer.Show();
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            var leftField = typeof(DiffViewWindow).GetField("leftList", BindingFlags.Instance | BindingFlags.NonPublic);
            var renderingField = typeof(DiffViewWindow).GetField("renderingContent", BindingFlags.Instance | BindingFlags.NonPublic);
            var progressPanel = (FrameworkElement)viewer.FindName("loadProgressPanel");
            Require(progressPanel.Visibility == Visibility.Visible, "progress is visible while loading");
            bool responsiveWhileLoading = false;
            viewer.Dispatcher.BeginInvoke(new Action(() =>
            {
                responsiveWhileLoading = (bool)renderingField.GetValue(viewer);
            }), DispatcherPriority.Normal);
            timer.Tick += (_, _) =>
            {
                if ((leftField.GetValue(viewer) != null && !(bool)renderingField.GetValue(viewer)) || DateTime.UtcNow >= deadline) frame.Continue = false;
            };
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
            Require(responsiveWhileLoading, "dispatcher responds while diff is loading");
            Require(progressPanel.Visibility == Visibility.Hidden, "progress clears without removing its layout space");
            var retainedBody = (Grid)viewer.FindName("body");
            viewer.UpdateLayout();
            double retainedHeight = retainedBody.ActualHeight;
            var retainedChildren = retainedBody.Children.Cast<UIElement>().ToArray();
            var reloadTask = (System.Threading.Tasks.Task)typeof(DiffViewWindow)
                .GetMethod("LoadContent", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(viewer, new object[] { false, true });
            viewer.UpdateLayout();
            Require(retainedChildren.SequenceEqual(retainedBody.Children.Cast<UIElement>()), "loading retains existing comparison frames and content");
            Require(Math.Abs(retainedBody.ActualHeight - retainedHeight) < 1, "progress does not shift comparison layout");
            var reloadFrame = new DispatcherFrame();
            var reloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            reloadTimer.Tick += (_, _) => { if (reloadTask.IsCompleted || DateTime.UtcNow >= deadline) reloadFrame.Continue = false; };
            reloadTimer.Start();
            Dispatcher.PushFrame(reloadFrame);
            reloadTimer.Stop();
            Require(reloadTask.IsCompletedSuccessfully, "replacement comparison finishes rendering");
            foreach (var pair in new[] { ("leftList", "source marker"), ("rightList", "target marker") })
            {
                var pane = typeof(DiffViewWindow).GetField(pair.Item1, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(viewer) as RichTextBox;
                Require(pane != null, "viewer renders panes: " + viewer.ViewModel.Status);
                string text = new TextRange(pane.Document.ContentStart, pane.Document.ContentEnd).Text;
                Require(text.Contains(pair.Item2), "viewer displays decoded content: " + pair.Item1);
            }
            Require(viewer.ViewModel.SourceText.EncodingName == "UTF-8" && viewer.ViewModel.TargetText.EncodingName == "UTF-16 LE", "viewer encoding metadata");
            Require(!viewer.ViewModel.SourceHeader.Contains(left), "standalone header omits redundant path");
            Require(!viewer.ViewModel.PreviousFileCommand.CanExecute(null) && !viewer.ViewModel.NextFileCommand.CanExecute(null),
                "standalone file navigation commands are disabled");
            viewer.UpdateLayout();
            foreach (double width in new[] { 1400.0, 900.0, 1200.0 })
            {
                viewer.Width = width;
                viewer.UpdateLayout();
                foreach (string field in new[] { "leftList", "rightList" })
                {
                    var pane = (RichTextBox)typeof(DiffViewWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(viewer);
                    Require(Math.Abs(pane.Document.PageWidth - pane.ActualWidth) < 1, "short document fills resized pane: " + field);
                }
            }
            typeof(DiffViewWindow).GetMethod("UpdateHunkOverlay", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(viewer, null);
            viewer.UpdateLayout();
            var overlay = (System.Windows.Shapes.Path)typeof(DiffViewWindow).GetField("hunkOverlay", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(viewer);
            Require(overlay.Visibility == Visibility.Visible && overlay.Data != null, "selected hunk overlay is visible");
            Require(overlay.Data.Bounds.Top >= overlay.StrokeThickness / 2 &&
                overlay.Data.Bounds.Bottom + overlay.StrokeThickness / 2 <= overlay.ActualHeight + 0.1,
                "top and bottom strokes fit without clipping");
            var sourcePane = (RichTextBox)leftField.GetValue(viewer);
            var sourceHost = (Border)((Grid)sourcePane.Parent).Parent;
            var body = (Grid)sourceHost.Parent;
            Require(Math.Abs(overlay.Margin.Top - sourcePane.TranslatePoint(new Point(0, 0), body).Y) < 0.1,
                "first hunk starts at the painted row boundary");
            Require(overlay.Data.Bounds.Left - overlay.StrokeThickness / 2 >= sourceHost.BorderThickness.Left &&
                overlay.Data.Bounds.Right + overlay.StrokeThickness / 2 <= body.ActualWidth - 5,
                "outline stays inside source and target outer borders");
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)viewer.ActualWidth, (int)viewer.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(viewer);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var output = File.Create(Path.Combine(AppContext.BaseDirectory, "diff-viewer-preview.png"))) encoder.Save(output);
            if (!viewer.ViewModel.DifferencesOnly) viewer.ViewModel.ToggleDifferencesOnly();
            viewer.RefreshFoldedPanes(0, 0, 0);
            var rightPane = (RichTextBox)typeof(DiffViewWindow).GetField("rightList", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(viewer);
            for (int cycle = 0; cycle < 3; cycle++)
            {
                Require(viewer.ViewModel.ExpandFilteredLines(1), "expand viewer block");
                viewer.RefreshFoldedPanes(0, 0, 0);
                Require(ReferenceEquals(sourcePane, leftField.GetValue(viewer)) && ReferenceEquals(sourceHost, ((Grid)sourcePane.Parent).Parent),
                    "folding retains pane and host controls");
                Require(sourcePane.Document.Blocks.Count == 102 && rightPane.Document.Blocks.Count == 102, "both documents expanded in place");
                if (cycle == 0) VerifyWhitespaceOverlay(viewer, sourcePane);
                sourcePane.ScrollToVerticalOffset(220);
                rightPane.ScrollToVerticalOffset(220);
                viewer.UpdateLayout();
                viewer.RefreshFoldedPanes(220, 0, 0);
                Require(Math.Abs(sourcePane.VerticalOffset - 220) < 1 && Math.Abs(rightPane.VerticalOffset - 220) < 1,
                    "scroll restored before returning from update");
                Require(viewer.ViewModel.CollapseExpandedLines(50) == 1, "collapse viewer block");
                viewer.RefreshFoldedPanes(0, 0, 0);
                Require(sourcePane.Document.Blocks.Count == 3 && rightPane.Document.Blocks.Count == 3, "both documents collapsed in place");
                Require(ReferenceEquals(overlay, typeof(DiffViewWindow).GetField("hunkOverlay", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(viewer)),
                    "folding retains selection overlay");
            }
            Console.WriteLine("PASS repeated in-place folding retains controls and restores synchronized scrolling");
            Console.WriteLine("PASS standalone WPF viewer renders both decoded file contents and metadata");
            VerifyImageNavigator(fixture, body.TranslatePoint(new Point(), viewer).Y);
        }
        finally { viewer.Close(); app.Shutdown(); }
        return 0;
    }

    private static void VerifyWhitespaceOverlay(DiffViewWindow viewer, RichTextBox pane)
    {
        var overlay = ((Grid)pane.Parent).Children.Cast<FrameworkElement>().Single(e => e.GetType().Name == "WhitespaceOverlay");
        var spaces = (CheckBox)viewer.FindName("showSpacesBox");
        var endings = (CheckBox)viewer.FindName("showNewlinesBox");
        bool oldSpaces = viewer.ViewModel.ShowSpaces, oldEndings = viewer.ViewModel.ShowNewlines;
        var document = pane.Document;
        var lines = viewer.ViewModel.Lines;
        string before = new TextRange(document.ContentStart, document.ContentEnd).Text;
        var render = overlay.GetType().GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic);
        int Draw()
        {
            viewer.UpdateLayout();
            var visual = new System.Windows.Media.DrawingVisual();
            using (var context = visual.RenderOpen()) render.Invoke(overlay, new object[] { context });
            return (int)overlay.GetType().GetProperty("LastDrawnMarkCount", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(overlay);
        }
        void Toggle(bool showSpaces, bool showEndings)
        {
            spaces.IsChecked = showSpaces;
            endings.IsChecked = showEndings;
            spaces.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        }
        try
        {
            Toggle(false, false);
            Require(Draw() == 0, "hidden markers produce no drawing");
            Toggle(true, false);
            Require(Draw() > 0, "visible spaces and tabs are drawn");
            int visibleRows = (int)overlay.GetType().GetProperty("LastVisibleRowCount", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(overlay);
            Require(visibleRows < 102, "marker rendering only visits viewport rows");
            Toggle(false, true);
            Require(Draw() > 0, "line endings are drawn independently");
            for (int i = 0; i < 20; i++) Toggle(i % 2 == 0, i % 2 != 0);
            Require(ReferenceEquals(document, pane.Document) && ReferenceEquals(lines, viewer.ViewModel.Lines), "marker toggles retain document and diff results");
            Require(new TextRange(document.ContentStart, document.ContentEnd).Text == before && !before.Contains('\uFF65') && !before.Contains('\u2193'), "markers never enter selected or copied document text");
            pane.ScrollToVerticalOffset(440);
            Require(Draw() > 0, "markers follow scrolling");
            pane.ScrollToVerticalOffset(0);
            Toggle(true, true);
            viewer.UpdateLayout();
            var preview = new System.Windows.Media.Imaging.RenderTargetBitmap((int)viewer.ActualWidth, (int)viewer.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            preview.Render(viewer);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(preview));
            using (var output = File.Create(Path.Combine(AppContext.BaseDirectory, "diff-markers-preview.png"))) png.Save(output);
            Console.WriteLine("PASS viewport marker rendering and display toggles preserve document and comparison");
        }
        finally { Toggle(oldSpaces, oldEndings); }
    }

    internal static int Run()
    {
        for (int flags = 0; flags < 8; flags++)
        {
            bool compareCase = (flags & 1) != 0, compareSpaces = (flags & 2) != 0, compareNewlines = (flags & 4) != 0;
            var rows = DiffTextService.Compare(["Ab c", ""], ["ab\tc", ""], !compareCase, !compareSpaces,
                compareNewlines ? ["\r\n", ""] : null, compareNewlines ? ["\n", ""] : null);
            Require((rows[0].Kind == DiffLineKind.Unchanged) == (flags == 0), "combined comparison flags " + flags);
            Require(rows[0].Left == "Ab c" && rows[0].Right == "ab\tc", "original display text preserved");
        }
        Require(DiffTextService.Compare(["Case"], ["case"], true)[0].Kind == DiffLineKind.Unchanged, "case ignored");
        Require(DiffTextService.Compare(["a b"], ["ab"], false, true)[0].Kind == DiffLineKind.Unchanged, "spaces ignored");
        Require(DiffTextService.Compare(["x"], ["x"], false, false, ["\r"], ["\n"])[0].Kind != DiffLineKind.Unchanged, "CR differs from LF");
        var alignedSpaces = DiffTextService.Compare(["a b", "tail"], ["insert", "ab", "tail"],
            leftEndings: ["\n", ""], rightEndings: ["\n", "\n", ""], calculateInlineDifferences: true);
        Require(alignedSpaces.Count == 3 && alignedSpaces[0].Kind == DiffLineKind.Added &&
            alignedSpaces[1].LeftNumber == 1 && alignedSpaces[1].RightNumber == 2 && alignedSpaces[1].Kind == DiffLineKind.Modified,
            "spacing refinement preserves content alignment across inserted rows");
        Require(alignedSpaces[1].LeftChanges == null && alignedSpaces[1].RightChanges == null,
            "spacing-only changes do not run character diff");
        var textChange = DiffTextService.Compare(["abc"], ["axc"], leftEndings: [], rightEndings: [], calculateInlineDifferences: true)[0];
        Require(textChange.Kind == DiffLineKind.Modified && textChange.LeftChanges.SequenceEqual(new[] { false, true, false }) &&
            textChange.RightChanges.SequenceEqual(new[] { false, true, false }),
            "text-modified rows compute both character masks once and skip newline lookup");
        var spaceChange = DiffTextService.Compare(["a b"], ["a\tb"], leftEndings: [], rightEndings: [], calculateInlineDifferences: true)[0];
        Require(spaceChange.Kind == DiffLineKind.Modified && spaceChange.LeftChanges == null,
            "spacing mismatch short-circuits newline lookup");
        var endingChange = DiffTextService.Compare(["same"], ["same"], leftEndings: ["\r\n"], rightEndings: ["\n"], calculateInlineDifferences: true)[0];
        Require(endingChange.Kind == DiffLineKind.Modified && endingChange.LeftChanges == null,
            "newline-only changes use stored endings without character diff");
        Require(DiffTextService.Compare(["a b"], ["a\tb"], ignoreSpaces: true, leftEndings: ["\r"], rightEndings: ["\n"])[0].Kind == DiffLineKind.Modified,
            "ignored spacing still permits final newline comparison");
        VerifyRows([], ["added"]);
        VerifyRows(["removed"], []);
        VerifyRows([], [""]);
        VerifyRows([""], []);
        VerifyRows(["", "same", "", "old"], ["", "same", "new", ""]);
        var random = new Random(20261008);
        for (int sample = 0; sample < 500; sample++)
        {
            string[] left = Enumerable.Range(0, random.Next(20)).Select(_ => random.Next(4).ToString()).ToArray();
            string[] right = Enumerable.Range(0, random.Next(20)).Select(_ => random.Next(4).ToString()).ToArray();
            VerifyRows(left, right);
        }
        Console.WriteLine("PASS 505 diff cases: empty sides, empty lines, reconstruction, line numbers and minimal edit distance");

        string fixture = Path.Combine(AppContext.BaseDirectory, "diff-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        string path = Path.Combine(fixture, "encoding.txt");
        const string content = "日本語 cafe\r\nnext\rlast\n";
        Encoding[] encodings = [new UTF8Encoding(true, true), new UTF8Encoding(false, true),
            new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true),
            new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true)];
        foreach (Encoding encoding in encodings)
        {
            File.WriteAllBytes(path, [.. encoding.GetPreamble(), .. encoding.GetBytes(content)]);
            Require(DiffViewModel.ReadText(path).SequenceEqual(new[] { "日本語 cafe", "next", "last", "" }),
                "BOM decoding and mixed line endings: " + encoding.WebName);
        }
        var jis = CodePagesEncodingProvider.Instance.GetEncoding(50220);
        File.WriteAllBytes(path, jis.GetBytes("日本語\r\ntext"));
        var jisDocument = DiffViewModel.ReadDocument(path);
        Require(jisDocument.EncodingName == "ISO-2022-JP" && jisDocument.Lines[0] == "日本語", "ISO-2022-JP detected before ASCII-compatible UTF-8");
        Require(!jisDocument.HasBom && jisDocument.Newlines == "CRLF", "JIS attributes");
        File.WriteAllBytes(path, [.. new UTF8Encoding(true).GetPreamble(), .. Encoding.UTF8.GetBytes("x\r\ny\nz\r")]);
        var mixed = DiffViewModel.ReadDocument(path);
        Require(mixed.HasBom && mixed.EncodingName == "UTF-8" &&
            mixed.Newlines.Split('/').OrderBy(e => e).SequenceEqual(new[] { "CR", "CRLF", "LF" }) &&
            mixed.Endings.SequenceEqual(new[] { "\r\n", "\n", "\r", "" }), "BOM and mixed newline metadata");
        File.WriteAllBytes(path, [0xef, 0xbb, 0xbf, 0xff]);
        bool malformedBom = false;
        try { DiffViewModel.ReadDocument(path); } catch (DecoderFallbackException) { malformedBom = true; }
        Require(malformedBom, "invalid BOM-declared encoding never falls back to Shift-JIS");
        Console.WriteLine("PASS comparison flags, original display text, ISO-2022-JP, BOM and newline metadata");
        var sjis = CodePagesEncodingProvider.Instance.GetEncoding(932);
        File.WriteAllBytes(path, sjis.GetBytes("日本語\ntext"));
        Require(DiffViewModel.ReadText(path).SequenceEqual(new[] { "日本語", "text" }), "Shift-JIS decoding");
        File.WriteAllBytes(path, []);
        Require(DiffViewModel.ReadText(path).Length == 0, "empty file has zero lines");
        File.WriteAllBytes(path, [65, 0, 66, 1]);
        bool rejected = false;
        try { DiffViewModel.ReadText(path); } catch (InvalidDataException) { rejected = true; }
        Require(rejected, "binary input is rejected");
        Console.WriteLine("PASS text reading: UTF-8, UTF-16 LE/BE, UTF-32 LE/BE, Shift-JIS, empty and binary inputs");
        Require(DeveloperDifferencerViewModel.FindTargetHistoryMatch(@"C:\source\Project\", new[] { @"D:\other", @"E:\first\project\", @"F:\second\Project" }) == @"E:\first\project\",
            "source matches first target history by folder name, ignoring case and trailing separator");
        Require(DeveloperDifferencerViewModel.FindTargetHistoryMatch(@"C:\source\Missing", new[] { @"D:\Project" }) == null, "no history match keeps target");
        Require(DeveloperDifferencerViewModel.FindTargetHistoryMatch(@"C:\", new[] { @"D:\" }) == null, "drive roots do not match");
        Require(DeveloperDifferencerViewModel.FindTargetHistoryMatch(@"C:\source\Project", new[] { "", "relative", @"D:\Project" }) == @"D:\Project", "invalid history entries are skipped");
        Console.WriteLine("PASS Source-to-Target history matching");
        VerifyFilteredExpansion(fixture);
        VerifyImageReload(fixture);
        return 0;
    }

    private static void VerifyImageNavigator(string fixture, double textBodyTop)
    {
        const int width = 1200, height = 800;
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int p = (y * width + x) * 4;
                pixels[p] = (byte)(x % 256); pixels[p + 1] = (byte)(y % 256); pixels[p + 2] = 100; pixels[p + 3] = 255;
            }
        string left = Path.Combine(fixture, "navigation-source.png"), right = Path.Combine(fixture, "navigation-target.png");
        void Save(string path)
        {
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, width * 4);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var output = File.Create(path); encoder.Save(output);
        }
        Save(left);
        for (int y = 200; y < 400; y++) for (int x = 500; x < 700; x++) pixels[(y * width + x) * 4 + 2] = 255;
        Save(right);
        var snapshot = ExternalDiffLaunch.Parse(["-diff", left, right]).CreateSnapshot();
        var window = new DiffViewWindow(snapshot, snapshot.Files[0], left, right)
        {
            Width = 1300, Height = 850, Left = -20000, Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false, ShowActivated = false
        };
        T Field<T>(string name) => (T)typeof(DiffViewWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(window);
        try
        {
            window.Show();
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            timer.Tick += (_, _) => { if (Field<Slider>("imageZoom") != null || DateTime.UtcNow >= deadline) frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
            var zoom = Field<Slider>("imageZoom");
            Require(zoom != null, "image navigator loaded");
            zoom.Value = 2;
            window.UpdateLayout();
            window.ScrollImagesTo(320, 210);
            window.UpdateLayout();
            var sourceScroll = Field<ScrollViewer>("leftScroll"); var targetScroll = Field<ScrollViewer>("rightScroll");
            Require(Math.Abs(sourceScroll.HorizontalOffset - 320) < 1 && sourceScroll.HorizontalOffset == targetScroll.HorizontalOffset &&
                sourceScroll.VerticalOffset == targetScroll.VerticalOffset, "navigator scrolls both panes together");
            var thumb = Field<System.Windows.Controls.Primitives.Thumb>("imageViewport");
            Require(thumb.Width > 0 && thumb.Width < 160 && Canvas.GetLeft(thumb) > 0, "navigator frame follows zoom and scroll");
            thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragStartedEventArgs(0, 0) { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragStartedEvent });
            thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(5, 3) { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragDeltaEvent });
            window.UpdateLayout();
            Require(sourceScroll.HorizontalOffset > 320 && sourceScroll.HorizontalOffset == targetScroll.HorizontalOffset, "navigator frame drag moves comparison images");
            var detailMethod = typeof(DiffViewWindow).GetMethod("SetImagePointDetails", BindingFlags.NonPublic | BindingFlags.Instance);
            detailMethod.Invoke(window, new object[] { new Point(10, 20), true });
            var panningField = typeof(DiffViewWindow).GetField("imagePanning", BindingFlags.NonPublic | BindingFlags.Instance);
            panningField.SetValue(window, true);
            detailMethod.Invoke(window, new object[] { new Point(600, 300), false });
            Require(Field<TextBlock>("sourcePixelDetails").Text.Contains("X:10 Y:20"), "drag holds clicked pixel");
            panningField.SetValue(window, false);
            detailMethod.Invoke(window, new object[] { new Point(600, 300), false });
            string details = Field<TextBlock>("sourcePixelDetails").Text;
            Require(details.Contains("X:600 Y:300") && !details.Contains("X:10 Y:20") && !details.Contains("Drag origin"), "mouse-up resumes single hover readout");
            Require(Field<TextBlock>("targetPixelDetails").Text.Contains("RGB:255,44,88"), "target RGBA sampled independently");
            var map = DiffViewWindow.CreateNavigationBitmap(window.ViewModel.SourceImage, window.ViewModel.TargetImage);
            string changed = DiffViewWindow.DescribePixel(map, new Point(50, 22));
            string equal = DiffViewWindow.DescribePixel(DiffViewWindow.CreateNavigationBitmap(window.ViewModel.SourceImage, window.ViewModel.SourceImage), new Point(50, 22));
            Require(changed != equal, "navigation map highlights differing regions");
            Require(DiffViewWindow.DescribePixel(window.ViewModel.SourceImage, new Point(-1, 0)).Contains("RGB:\u2014"), "out of bounds pixel is not sampled");
            var imageBody = (FrameworkElement)window.FindName("body");
            Require(Math.Abs(imageBody.TranslatePoint(new Point(), window).Y - textBodyTop) < 1,
                "image and text comparison body starts at the same height");
            var actions = (FrameworkElement)window.FindName("actionsPanel");
            var zoomHost = (FrameworkElement)window.FindName("imageControlRow");
            Require(Math.Abs(actions.TranslatePoint(new Point(), window).Y - zoomHost.TranslatePoint(new Point(), window).Y) < 10,
                "zoom remains alongside action buttons");
            Require(Field<TextBlock>("sourcePixelDetails").ActualHeight <= 56, "pixel readout has fixed compact height");
            var render = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            render.Render(window);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(render));
            using (var output = File.Create(Path.Combine(AppContext.BaseDirectory, "image-navigation-preview.png"))) encoder.Save(output);
            window.ScrollImagesTo(double.MaxValue, double.MaxValue); window.UpdateLayout();
            Require(sourceScroll.HorizontalOffset <= sourceScroll.ScrollableWidth && sourceScroll.VerticalOffset <= sourceScroll.ScrollableHeight, "map navigation clamps at image edges");
            Console.WriteLine("PASS image navigation drag, shared scroll, zoom, RGBA/origin details and difference map");
        }
        finally { window.Close(); }
    }

    private static void VerifyImageReload(string fixture)
    {
        string left = Path.Combine(fixture, "image-left.png"), right = Path.Combine(fixture, "image-right.jpg");
        var image = System.Windows.Media.Imaging.BitmapSource.Create(2, 2, 96, 96,
            System.Windows.Media.PixelFormats.Bgr24, null, new byte[12], 6);
        void Save(string path, System.Windows.Media.Imaging.BitmapEncoder encoder)
        {
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        Save(left, new System.Windows.Media.Imaging.PngBitmapEncoder());
        Save(right, new System.Windows.Media.Imaging.JpegBitmapEncoder());
        var snapshot = ExternalDiffLaunch.Parse(["-diff", left, right]).CreateSnapshot();
        var model = new DiffViewModel(snapshot, snapshot.Files[0], null, left, right);
        Require(model.LoadContentAsync().GetAwaiter().GetResult(), "load image pair");
        Require(model.SourceImageFormat == "PNG" && model.TargetImageFormat == "JPG", "independent image formats");
        Save(right, new System.Windows.Media.Imaging.TiffBitmapEncoder());
        model.ReloadFromDiskAsync().GetAwaiter().GetResult();
        Require(model.TargetImageFormat == "TIF" && model.File.Target.Size == new FileInfo(right).Length,
            "reload updates actual image format and size despite unchanged extension");
        File.Delete(right);
        model.ReloadFromDiskAsync().GetAwaiter().GetResult();
        Require(model.TargetImage == null && model.TargetImageFormat == null && model.File.Target == null,
            "reload clears missing image and metadata");
        Save(right, new System.Windows.Media.Imaging.PngBitmapEncoder());
        model.ReloadFromDiskAsync().GetAwaiter().GetResult();
        Require(model.TargetImage != null && model.SourceImageFormat == model.TargetImageFormat, "reload restores re-created image");
        var canvas = (Canvas)typeof(DiffViewWindow).GetMethod("ImageCanvas", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { null, 100.0, 100.0, null });
        Require(canvas.Children.Count == 0, "missing image has no text");
        var viewer = (ScrollViewer)typeof(DiffViewWindow).GetMethod("ThemedViewer", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { canvas, false, true });
        Require(ReferenceEquals(viewer.Background, canvas.Background) && ReferenceEquals(viewer.BorderBrush, canvas.Background),
            "missing pane and border use the same dark brush");
        Console.WriteLine("PASS image formats, reload, missing/re-created image and dark empty pane");
    }

    private static void VerifyFilteredExpansion(string fixture)
    {
        string left = Path.Combine(fixture, "expand-left.txt"), right = Path.Combine(fixture, "expand-right.txt");
        File.WriteAllText(left, "head0\nhead1\nold1\nmiddle0\nmiddle1\nmiddle2\nold2\ntail0\ntail1");
        File.WriteAllText(right, "head0\nhead1\nnew1\nmiddle0\nmiddle1\nmiddle2\nnew2\ntail0\ntail1");
        var snapshot = ExternalDiffLaunch.Parse(["-diff", left, right]).CreateSnapshot();
        var model = new DiffViewModel(snapshot, snapshot.Files[0], null, left, right);
        model.LoadTextAsync().GetAwaiter().GetResult();
        if (!model.DifferencesOnly) model.ToggleDifferencesOnly();
        Require(model.Lines.Count == 5 && model.Hunks.SequenceEqual(new[] { 1, 3 }), "three separate folded regions");
        Require(model.ExpandFilteredLines(2), "expand middle region");
        Require(model.Lines.Count == 7 && model.Lines[2].Left == "middle0" && model.Lines[4].Right == "middle2", "expand both sides with original content");
        Require(model.Lines[0].FilteredLineCount == 2 && model.Lines[6].FilteredLineCount == 2, "unrelated regions stay folded");
        Require(model.Hunks.SequenceEqual(new[] { 1, 5 }), "hunk navigation follows expanded rows");
        Require(model.ExpandFilteredLines(0), "expand a second region independently");
        Require(model.CollapseExpandedLines(5) == 3, "collapse from last line of expanded middle region");
        Require(model.Lines[0].Left == "head0" && model.Lines[3].FilteredLineCount == 3, "other expanded region survives collapse");
        Require(model.CollapseExpandedLines(1) == 0 && model.Lines.Count == 5, "restore original filtered layout");
        Require(!model.ExpandFilteredLines(1) && model.CollapseExpandedLines(1) == -1, "difference rows cannot be folded");
        model.ExpandFilteredLines(4);
        Require(model.CollapseExpandedLines(5) == 4, "trailing region collapses from its last row");
        model.ExpandFilteredLines(2);
        model.LoadTextAsync().GetAwaiter().GetResult();
        Require(model.Lines.Count == 5, "reload resets expanded regions");
        model.ToggleDifferencesOnly();
        Require(model.Lines.Count == 9 && model.CollapseExpandedLines(0) == -1, "all-lines mode remains unchanged");
        File.WriteAllText(right, "changed after opening");
        model.ReloadFromDiskAsync().GetAwaiter().GetResult();
        Require(model.AllLines.Any(row => row.Right == "changed after opening") && model.File.Target.Size == new FileInfo(right).Length,
            "manual text reload updates content and file size");
        Console.WriteLine("PASS independent expand/collapse, both sides, hunk positions, trailing blocks and reload");
    }

    private static void VerifyRows(string[] left, string[] right)
    {
        var rows = DiffTextService.Compare(left, right);
        Require(rows.Where(row => row.LeftNumber > 0).Select(row => row.Left).SequenceEqual(left), "left reconstruction");
        Require(rows.Where(row => row.RightNumber > 0).Select(row => row.Right).SequenceEqual(right), "right reconstruction");
        Require(rows.Where(row => row.LeftNumber > 0).Select(row => row.LeftNumber).SequenceEqual(Enumerable.Range(1, left.Length)), "left line numbers");
        Require(rows.Where(row => row.RightNumber > 0).Select(row => row.RightNumber).SequenceEqual(Enumerable.Range(1, right.Length)), "right line numbers");
        Require(rows.Where(row => row.Kind == DiffLineKind.Unchanged).All(row => row.Left == row.Right), "unchanged rows match");
        int[,] lcs = new int[left.Length + 1, right.Length + 1];
        for (int i = 1; i <= left.Length; i++)
            for (int j = 1; j <= right.Length; j++)
                lcs[i, j] = left[i - 1] == right[j - 1] ? lcs[i - 1, j - 1] + 1 : Math.Max(lcs[i - 1, j], lcs[i, j - 1]);
        int edits = rows.Sum(row => row.Kind switch { DiffLineKind.Unchanged => 0, DiffLineKind.Modified => 2, _ => 1 });
        Require(edits == left.Length + right.Length - 2 * lcs[left.Length, right.Length], "minimal insertion/deletion count");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
