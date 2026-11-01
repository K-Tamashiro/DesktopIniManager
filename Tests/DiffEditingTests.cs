using System;
using System.IO;
using System.Linq;
using System.Text;
using DesktopIniManager.Services;
using DesktopIniManager.ViewModels;
using DesktopIniManager.Views;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Threading.Tasks;

internal static class DiffEditingTests
{
    internal static int RunViewer()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var xml = new System.Xml.XmlDocument();
        xml.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "App.xaml"));
        var ns = new System.Xml.XmlNamespaceManager(xml.NameTable);
        ns.AddNamespace("p", "http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        string resources = xml.SelectSingleNode("p:Application/p:Application.Resources/p:ResourceDictionary", ns).OuterXml;
        var context = new ParserContext(); context.XmlnsDictionary.Add("x", "http://schemas.microsoft.com/winfx/2006/xaml");
        app.Resources = (ResourceDictionary)XamlReader.Parse(resources.Replace("Source=\"Themes/", "Source=\"/DesktopIniManager;component/Themes/"), context);
        string root = Path.Combine(AppContext.BaseDirectory, "edit-viewer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string a = Path.Combine(root, "source.txt"), b = Path.Combine(root, "target.txt");
        File.WriteAllText(a, "source1\ncommon\nsource2\ncommon2\nsource3", new UTF8Encoding(true));
        File.WriteAllText(b, "target1\r\ncommon\r\ntarget2\r\ncommon2\r\ntarget3", new UnicodeEncoding(false, true));
        byte[] originalA = File.ReadAllBytes(a), originalB = File.ReadAllBytes(b);
        var launch = ExternalDiffLaunch.Parse(new[] { "-diff", a, b }); var snapshot = launch.CreateSnapshot();
        var window = new DiffViewWindow(snapshot, snapshot.Files[0], a, b)
        { Width = 1200, Height = 800, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false, ShowActivated = false };
        object Field(string name) => typeof(DiffViewWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
        object Call(string name, params object[] args) => typeof(DiffViewWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, args);
        void Pump(Func<bool> done)
        {
            var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
            var deadline = DateTime.UtcNow.AddSeconds(20);
            timer.Tick += (_, _) => { if (done() || DateTime.UtcNow > deadline) frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Check(done(), "UI operation completes");
        }
        void Wait(Task task) { Pump(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        Button[] Arrows(bool source) => ((Canvas)((RichTextBox)Field(source ? "leftList" : "rightList")).Tag).Children.OfType<Button>().ToArray();
        Button Save(bool source) => ((TextBlock)window.FindName(source ? "sourceHeader" : "targetHeader")).Inlines.OfType<InlineUIContainer>().Select(c => c.Child).OfType<Button>().Single();
        try
        {
            window.ViewModel.CompareCase = true;
            window.Show(); Pump(() => Field("leftList") != null && !(bool)Field("renderingContent"));
            var model = window.ViewModel;
            model.ShowSpaces = model.ShowNewlines = true;
            ((CheckBox)window.FindName("showSpacesBox")).IsChecked = true;
            ((CheckBox)window.FindName("showNewlinesBox")).IsChecked = true;
            var originalLeftPane = (RichTextBox)Field("leftList");
            var originalRightPane = (RichTextBox)Field("rightList");
            var originalLeftDocument = originalLeftPane.Document;
            var originalRightDocument = originalRightPane.Document;
            var retainedCommon = originalLeftDocument.Blocks.ElementAt(1);
            Check(Arrows(true).Length == 3 && Arrows(false).Length == 3, "arrows exist in both gutters");
            Check(!Save(true).IsEnabled && !Save(false).IsEnabled, "save buttons disabled initially");
            System.Windows.Media.Color FirstBackground(bool source) =>
                ((System.Windows.Media.SolidColorBrush)((RichTextBox)Field(source ? "leftList" : "rightList")).Document.Blocks.FirstBlock.Background).Color;
            var sourceBackground = FirstBackground(true);
            var targetBackground = FirstBackground(false);
            var selected = (System.Collections.Generic.HashSet<int>)Field("sourceSelectedEditHunks"); selected.UnionWith(new[] { 0, 1, 2 });
            var targetSelected = (System.Collections.Generic.HashSet<int>)Field("targetSelectedEditHunks"); targetSelected.Add(1);
            originalLeftPane.SelectAll();
            var targetSecondHunk = originalRightDocument.Blocks.ElementAt(model.Lines.FindIndex(row => row.EditHunkId == 1));
            originalRightPane.Selection.Select(targetSecondHunk.ContentStart, targetSecondHunk.ContentEnd);
            Call("RefreshEditArrows");
            Check(Arrows(true)[0].IsEnabled && Arrows(true).Skip(1).All(x => !x.IsEnabled), "only first selected arrow is clickable");
            Check(Arrows(false).All(x => x.IsEnabled), "Source multi-selection does not group Target arrows");
            Arrows(true)[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(() => !(bool)Field("renderingContent"));
            Check(model.IsDirty(false) && !model.IsDirty(true), "copy dirties only destination");
            var copiedBackground = FirstBackground(false);
            Check(FirstBackground(true).R == (byte)(sourceBackground.R * 0.7) && copiedBackground.R == (byte)(targetBackground.R * 0.7) &&
                copiedBackground.G == (byte)(targetBackground.G * 0.7) && copiedBackground.B == (byte)(targetBackground.B * 0.7),
                "copied block darkens both backgrounds by 30 percent");
            Check(ReferenceEquals(originalLeftPane, Field("leftList")) && ReferenceEquals(originalRightPane, Field("rightList")) &&
                ReferenceEquals(originalLeftDocument, originalLeftPane.Document) && ReferenceEquals(originalRightDocument, originalRightPane.Document) &&
                ReferenceEquals(retainedCommon, originalLeftDocument.Blocks.ElementAt(1)), "copy retains panes, documents and unrelated rows");
            Check(selected.SetEquals(new[] { 0, 1, 2 }) && targetSelected.SetEquals(new[] { 1 }), "copy preserves independent selections");
            Check(!originalLeftPane.Selection.IsEmpty && !originalRightPane.Selection.IsEmpty, "copy preserves both visible selections");
            Check(model.ShowSpaces && model.ShowNewlines && ((CheckBox)window.FindName("showSpacesBox")).IsChecked == true &&
                ((CheckBox)window.FindName("showNewlinesBox")).IsChecked == true, "copy preserves whitespace and newline display");
            Check(Arrows(true).All(x => x.IsEnabled) && model.CanUndoHunk(0, true) && model.CanUndoHunk(2, true), "each copied hunk has independent undo arrow");
            Check(Save(false).IsEnabled && !Save(true).IsEnabled && File.ReadAllBytes(b).SequenceEqual(originalB), "save enablement and no implicit write");
            Arrows(true)[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(() => !(bool)Field("renderingContent"));
            Check(DiffViewModel.Text(model.TargetText).StartsWith("target1") && DiffViewModel.Text(model.TargetText).Contains("source2"), "arrow selectively undoes first copy");
            model.UndoEdit(); Wait((Task)Call("RenderTextEditsAsync"));
            Check(!model.HasEdits && !Save(false).IsEnabled, "sequential undo clears remaining group");
            Check(FirstBackground(false) == targetBackground, "undo restores normal block background");
            originalLeftPane.Selection.Select(originalLeftPane.Document.ContentStart, originalLeftPane.Document.ContentStart);
            Check(selected.Count == 0 && targetSelected.SetEquals(new[] { 1 }), "clearing Source selection leaves Target selection intact");
            Check(model.Lines[0].LeftChanges?.Any(value => value) == true &&
                originalLeftPane.Document.Blocks.OfType<Paragraph>().First().Inlines.OfType<Run>().Any(run =>
                    run.Foreground is System.Windows.Media.SolidColorBrush brush && brush.Color == System.Windows.Media.Colors.Red),
                "undo restores visible inline differences");
            model.CopyHunks(new[] { 0 }, true); model.CopyHunks(new[] { 1 }, false);
            Wait((Task)Call("RenderTextEditsAsync"));
            Check(model.IsDirty(true) && model.IsDirty(false), "both sides hold independent edits");
            Wait((Task)Call("RecompareWorkingTextAsync"));
            Check(model.HasEdits && !model.CanUndoHunk(0, true) && File.ReadAllBytes(a).SequenceEqual(originalA) && File.ReadAllBytes(b).SequenceEqual(originalB), "refresh compares memory without disk write and resets undo");
            Wait((Task)Call("SaveTextSideAsync", true));
            Check(!model.IsDirty(true) && model.IsDirty(false) && File.ReadAllBytes(b).SequenceEqual(originalB), "saving Source retains unsaved Target");
            Check(model.SourceText.HasBom && model.SourceText.EncodingName == "UTF-8", "save reloads original source encoding");
            Wait((Task)Call("SaveTextSideAsync", false));
            Check(!model.HasEdits && model.TargetText.EncodingName == "UTF-16 LE" && model.TargetText.Newlines == "CRLF", "saving Target retains its format and reloads");
            window.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1200, 800, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(window); var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(root, "viewer.png"))) png.Save(stream);
            Console.WriteLine("SCREENSHOT " + Path.Combine(root, "viewer.png"));
            var common = string.Join("\n", Enumerable.Range(0, 2000).Select(i => "unchanged line " + i));
            File.WriteAllText(a, "left\n" + common + "\nleft end");
            File.WriteAllText(b, "right\n" + common + "\nright end");
            Wait((Task)Call("ReloadTextAsync"));
            if (!model.DifferencesOnly) model.ToggleDifferencesOnly();
            window.RefreshFoldedPanes(0, 0, 0);
            var leftPane = (RichTextBox)Field("leftList");
            var rightPane = (RichTextBox)Field("rightList");
            var doc = leftPane.Document;
            var first = doc.Blocks.FirstBlock;
            var last = doc.Blocks.LastBlock;
            var arrows = Arrows(true);
            for (int cycle = 0; cycle < 3; cycle++)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                Check(model.ExpandFilteredLines(1), "expand 2000 unchanged rows");
                window.RefreshFoldedPanes(0, 0, 0);
                Console.WriteLine("FOLD expand ms=" + watch.ElapsedMilliseconds);
                Check(ReferenceEquals(doc, leftPane.Document) && ReferenceEquals(first, doc.Blocks.FirstBlock) &&
                    ReferenceEquals(last, doc.Blocks.LastBlock), "fold reuses document and unaffected paragraphs");
                Check(doc.Blocks.Count == 2002 && rightPane.Document.Blocks.Count == 2002, "both panes expand together");
                Check(arrows.SequenceEqual(Arrows(true)), "fold reuses edit buttons");
                window.RefreshFoldedPanes(220, 0, 0);
                window.UpdateLayout();
                Check(Math.Abs(leftPane.VerticalOffset - 220) < 1 && Math.Abs(rightPane.VerticalOffset - 220) < 1,
                    "fold preserves synchronized scrolling");
                watch.Restart();
                Check(model.CollapseExpandedLines(1000) == 1, "collapse from inside unchanged block");
                window.RefreshFoldedPanes(0, 0, 0);
                Console.WriteLine("FOLD collapse ms=" + watch.ElapsedMilliseconds);
                Check(doc.Blocks.Count == 3 && rightPane.Document.Blocks.Count == 3, "both panes collapse together");
            }
            foreach (bool hunkView in new[] { true, false })
            {
                if (model.DifferencesOnly != hunkView)
                {
                    model.ToggleDifferencesOnly();
                    Wait((Task)Call("LoadContent", false, false));
                }
                var paneBefore = (RichTextBox)Field("leftList");
                paneBefore.ScrollToVerticalOffset(hunkView ? 0 : 220);
                ((RichTextBox)Field("rightList")).ScrollToVerticalOffset(hunkView ? 0 : 220);
                window.UpdateLayout();
                var offsetBefore = paneBefore.VerticalOffset;
                var rowsBefore = paneBefore.Document.Blocks.ToArray();
                model.ShowSpaces = hunkView;
                model.ShowNewlines = !hunkView;
                ((CheckBox)window.FindName("showSpacesBox")).IsChecked = hunkView;
                ((CheckBox)window.FindName("showNewlinesBox")).IsChecked = !hunkView;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                model.CopyHunks(new[] { 0 }, true);
                Wait((Task)Call("RenderTextEditsAsync"));
                Console.WriteLine("COPY hunkView=" + hunkView + " ms=" + watch.ElapsedMilliseconds);
                Check(ReferenceEquals(paneBefore, Field("leftList")) &&
                    rowsBefore.Skip(1).SequenceEqual(paneBefore.Document.Blocks.Skip(1)), "single copy retains every unrelated row");
                Check(Math.Abs(paneBefore.VerticalOffset - offsetBefore) < 1, "copy keeps the viewport fixed");
                Check(model.ShowSpaces == hunkView && model.ShowNewlines == !hunkView &&
                    ((CheckBox)window.FindName("showSpacesBox")).IsChecked == hunkView &&
                    ((CheckBox)window.FindName("showNewlinesBox")).IsChecked == !hunkView,
                    "copy retains marker switches in Hunk and All views");
                model.UndoEdit();
                Wait((Task)Call("RenderTextEditsAsync"));
            }
            var contextMenu = ((Grid)window.FindName("body")).ContextMenu;
            Check(ReferenceEquals(((RichTextBox)Field("leftList")).ContextMenu, contextMenu) &&
                ReferenceEquals(((RichTextBox)Field("rightList")).ContextMenu, contextMenu), "both panes use the block context menu instead of the RichTextBox default");
            var clipboardMenu = (MenuItem)contextMenu.Items[0];
            Check((string)clipboardMenu.Header == "クリップボード" && clipboardMenu.Items.Count == 2 &&
                clipboardMenu.Items.Cast<MenuItem>().All(item => item.Icon is Image) &&
                (string)((MenuItem)contextMenu.Items[2]).Header == "右にコピー" &&
                (string)((MenuItem)contextMenu.Items[3]).Header == "左にコピー", "context menu has clipboard submenu and both copy directions with icons");
            int DrawEof(bool source)
            {
                window.UpdateLayout();
                var pane = (RichTextBox)Field(source ? "leftList" : "rightList");
                var overlay = ((Grid)pane.Parent).Children.Cast<FrameworkElement>().Single(item => item.GetType().Name == "WhitespaceOverlay");
                var visual = new System.Windows.Media.DrawingVisual();
                using (var drawing = visual.RenderOpen())
                    overlay.GetType().GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(overlay, new object[] { drawing });
                return (int)overlay.GetType().GetProperty("LastDrawnEofCount", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(overlay);
            }
            model.CompareNewlines = true;
            model.ShowSpaces = false;
            Check(DifferencerStatusIcons.GetCustomIcon(155) != null, "EOF icon 155 is available");
            foreach (var ending in new[] { "", "\n", "\r", "\r\n" })
            {
                File.WriteAllText(a, "last");
                File.WriteAllText(b, "last" + ending);
                if (model.DifferencesOnly) model.ToggleDifferencesOnly();
                Wait((Task)Call("ReloadTextAsync"));
                var cached = (System.Collections.IDictionary)Field("renderedPanes");
                foreach (bool source in new[] { true, false })
                {
                    var state = cached[source];
                    var markers = ((System.Collections.IEnumerable)state.GetType().GetProperty("Markers").GetValue(state)).Cast<object>().ToArray();
                    var eofIndices = markers.Select((marker, index) => (marker, index)).Where(item =>
                        (bool)item.marker.GetType().GetField("IsEndOfFile", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(item.marker)).Select(item => item.index).ToArray();
                    Check(eofIndices.SequenceEqual(new[] { source || ending.Length == 0 ? 0 : 1 }),
                        "EOF appears after last text or on trailing empty line, never on alignment padding");
                }
                model.ShowNewlines = true;
                Check(DrawEof(true) == 1 && DrawEof(false) == 1, "newline display draws exactly one EOF per side");
                model.ShowNewlines = false;
                Check(DrawEof(true) == 0 && DrawEof(false) == 0, "newline display hides EOF too");
            }
            model.ShowNewlines = true;
            if (!model.DifferencesOnly) model.ToggleDifferencesOnly();
            window.RefreshFoldedPanes(0, 0, 0);
            Check(DrawEof(true) == 1 && DrawEof(false) == 1, "EOF is visible for final-newline differences in Hunk view");
            var eofBitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1200, 800, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            eofBitmap.Render(window);
            var eofPng = new System.Windows.Media.Imaging.PngBitmapEncoder();
            eofPng.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(eofBitmap));
            using (var stream = File.Create(Path.Combine(root, "eof.png"))) eofPng.Save(stream);
            Console.WriteLine("EOF SCREENSHOT " + Path.Combine(root, "eof.png"));
            File.WriteAllText(b, "external edit", new UTF8Encoding(true));
            Wait((Task)Call("ReloadTextAsync"));
            Check(model.TargetText.EncodingName == "UTF-8" && model.TargetText.HasBom &&
                DiffViewModel.Text(model.TargetText) == "external edit", "Reload reads external bytes and encoding");

            var sync = new SynchronizeConfirmWindow();
            Check(sync.FindResource("SourceColor") is System.Windows.Media.Brush &&
                sync.FindResource("TargetColor") is System.Windows.Media.Brush, "sync dialog resolves both direction brushes without owner resources");
            sync.Close();

            model.CopyHunks(new[] { 0 }, true);
            Check(model.HasEdits, "close regression starts with unsaved edits");
            var diskBeforeClose = File.ReadAllBytes(b);
            bool closed = false;
            window.Closed += (_, _) => closed = true;
            var discard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            discard.Tick += (_, _) =>
            {
                var dialog = window.OwnedWindows.Cast<Window>().FirstOrDefault();
                if (dialog?.Content is not StackPanel panel) return;
                discard.Stop();
                var buttons = panel.Children.OfType<StackPanel>().Single();
                buttons.Children.OfType<Button>().ElementAt(1).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            discard.Start();
            window.Close();
            Pump(() => closed);
            discard.Stop();
            Check(File.ReadAllBytes(b).SequenceEqual(diskBeforeClose), "Discard closes without writing disk");
        }
        finally { typeof(DiffViewWindow).GetField("allowEditedClose", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, true); window.Close(); app.Shutdown(); }
        return 0;
    }
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }
    internal static int Run()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "diff-editing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string a = Path.Combine(root, "a.txt"), b = Path.Combine(root, "b.txt");
        DiffViewModel.TextDocument Doc(string path, string text, Encoding encoding)
        {
            File.WriteAllBytes(path, encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray());
            return DiffViewModel.ReadDocument(path);
        }
        DiffEditSession Session(DiffViewModel.TextDocument left, DiffViewModel.TextDocument right) =>
            new(left, right, DiffTextService.Compare(left.Lines, right.Lines, false, false, left.Endings, right.Endings));
        var utf8 = new UTF8Encoding(false, true);
        var left = Doc(a, "a\nkeep\nb\nkeep2\nc\n", utf8);
        var right = Doc(b, "x\r\nkeep\r\ny\r\nkeep2\r\nz\r\n", utf8);
        // Ignore newline differences here to keep three independent hunks.
        var session = new DiffEditSession(left, right, DiffTextService.Compare(left.Lines, right.Lines));
        var original = File.ReadAllBytes(b);
        session.Copy(new[] { 0, 1, 2 }, true);
        Check(DiffViewModel.Text(session.Document(false)) == "a\r\nkeep\r\nb\r\nkeep2\r\nc\r\n", "batch copy retains destination CRLF");
        Check(File.ReadAllBytes(b).SequenceEqual(original), "copy never writes disk");
        Check(session.Rows().Count(r => r.EditHunkId >= 0) == 3, "copied hunks remain visible");
        session.UndoHunk(0, true);
        Check(DiffViewModel.Text(session.Document(false)).StartsWith("x\r\nkeep\r\nb"), "selective undo of first hunk retains later copies");
        session.UndoLast();
        Check(DiffViewModel.Text(session.Document(false)) == DiffViewModel.Text(right), "sequential undo restores remaining group");
        session.Copy(new[] { 0 }, true);
        session.Copy(new[] { 1 }, false);
        Check(session.CanUndo(0, true) && session.CanUndo(1, false), "independent undo for both directions");
        session.UndoLast();
        Check(DiffViewModel.Text(session.Document(true)) == DiffViewModel.Text(left) && session.CanUndo(0, true), "Ctrl+Z reverses latest operation only");

        foreach (var encoding in new Encoding[] { new UTF8Encoding(true, true), utf8, new UnicodeEncoding(false, true, true),
            new UnicodeEncoding(true, true, true), new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true),
            CodePagesEncodingProvider.Instance.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback) })
        {
            right = Doc(b, "古い\r\n共通\n最後\r", encoding);
            left = Doc(a, "新しい\n共通\n最後\n", utf8);
            session = new DiffEditSession(left, right, DiffTextService.Compare(left.Lines, right.Lines));
            session.Copy(new[] { 0 }, true);
            var updated = session.Document(false);
            var expected = encoding.GetPreamble().Concat(encoding.GetBytes("新しい\r\n共通\n最後\r")).ToArray();
            Check(DiffViewModel.EncodeDocument(updated).SequenceEqual(expected), "exact encoding/BOM/mixed endings: " + encoding.CodePage + "/" + encoding.GetPreamble().Length);
            File.SetAttributes(b, FileAttributes.Hidden | FileAttributes.Archive);
            DiffViewModel.WriteDocument(b, DiffViewModel.EncodeDocument(updated), right.OriginalBytes);
            Check(File.ReadAllBytes(b).SequenceEqual(expected) && File.GetAttributes(b) == (FileAttributes.Hidden | FileAttributes.Archive), "saved bytes and file attributes retained");
            File.SetAttributes(b, FileAttributes.Normal);
        }
        foreach (var pair in new[] { ("new\n", "old"), ("new", "old\n"), ("same\nextra", "same"), ("same", "same\nextra"), ("", "old"), ("new", "") })
        {
            left = Doc(a, pair.Item1, utf8); right = Doc(b, pair.Item2, utf8);
            session = Session(left, right);
            session.Copy(session.Rows().Where(r => r.EditHunkId >= 0).Select(r => r.EditHunkId).Distinct().ToArray(), true);
            var text = DiffViewModel.Text(session.Document(false));
            Check(!text.Contains("sameextra"), "EOF insertion does not concatenate lines");
            if (pair.Item1 == "new\n" && pair.Item2 == "old") Check(text == "new", "no final newline retained");
            if (pair.Item1 == "new" && pair.Item2 == "old\n") Check(text == "new\n", "final newline retained");
            session.UndoLast();
            Check(DiffViewModel.Text(session.Document(false)) == pair.Item2, "EOF undo: " + pair);
        }
        right = Doc(b, "元\r\n", CodePagesEncodingProvider.Instance.GetEncoding(932));
        var random = new Random(71);
        for (int trial = 0; trial < 120; trial++)
        {
            string src = string.Join("\n", Enumerable.Range(0, random.Next(1, 10)).Select(_ => "line" + random.Next(6)));
            string dst = string.Join("\n", Enumerable.Range(0, random.Next(1, 10)).Select(_ => "line" + random.Next(6)));
            left = Doc(a, src, utf8); right = Doc(b, dst, utf8);
            session = Session(left, right);
            var ids = session.Rows().Where(r => r.EditHunkId >= 0).Select(r => r.EditHunkId).Distinct().ToArray();
            session.Copy(ids, true);
            Check(DiffViewModel.Text(session.Document(false)) == src, "random full copy " + trial + " expected=" + src.Replace("\n", "|") + " actual=" + DiffViewModel.Text(session.Document(false)).Replace("\n", "|").Replace("\r", "<CR>"));
            session.UndoLast();
            Check(DiffViewModel.Text(session.Document(false)) == dst, "random full undo " + trial);
        }
        right = Doc(b, "元\r\n", CodePagesEncodingProvider.Instance.GetEncoding(932));
        left = Doc(a, "絵😀\n", utf8);
        session = Session(left, right); session.Copy(new[] { 0 }, true);
        original = File.ReadAllBytes(b);
        bool failed = false;
        try { DiffViewModel.EncodeDocument(session.Document(false)); }
        catch (EncoderFallbackException ex) { failed = ex.Index == 1; }
        Check(failed && File.ReadAllBytes(b).SequenceEqual(original), "unrepresentable character reports exact position without write");
        File.WriteAllText(b, "external", utf8);
        failed = false;
        try { DiffViewModel.WriteDocument(b, new byte[] { 65 }, original); } catch (IOException) { failed = true; }
        Check(failed && File.ReadAllText(b) == "external", "external modification is not overwritten");
        File.SetAttributes(b, FileAttributes.ReadOnly);
        failed = false;
        try { DiffViewModel.WriteDocument(b, new byte[] { 65 }, File.ReadAllBytes(b)); } catch (UnauthorizedAccessException) { failed = true; }
        Check(failed && File.GetAttributes(b).HasFlag(FileAttributes.ReadOnly), "read-only attribute is not removed");
        File.SetAttributes(b, FileAttributes.Normal);
        Console.WriteLine("Diff editing tests passed");
        return 0;
    }
}
