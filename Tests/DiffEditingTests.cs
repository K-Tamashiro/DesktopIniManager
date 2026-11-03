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
    internal static int RunMenuPreview()
    {
        var app = new Application();
        var file = Path.Combine(AppContext.BaseDirectory, "menu-preview.txt");
        File.WriteAllText(file, "Native submenu preview");
        var status = new TextBlock { Text = "Right-click here to test Explorer Menu", Margin = new Thickness(30) };
        var window = new Window { Title = "DIM native submenu verification", Width = 800, Height = 600, Content = status };
        window.MouseRightButtonUp += (_, e) =>
        {
            ExplorerContextMenu.Show(window, new[] { file }, window.PointToScreen(e.GetPosition(window)),
                new[]
                {
                    new ExplorerContextMenu.CommandItem("DIM test command", () => status.Text = "DIM command executed"),
                    new ExplorerContextMenu.CommandItem("SELECT Compare", null, Children: new[]
                    {
                        new ExplorerContextMenu.CommandItem("Comparison test", () => status.Text = "Comparison command executed")
                    })
                });
        };
        return app.Run(window);
    }

    internal static int RunIconModes()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var xml = new System.Xml.XmlDocument();
        xml.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "App.xaml"));
        var ns = new System.Xml.XmlNamespaceManager(xml.NameTable);
        ns.AddNamespace("p", "http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        var resources = xml.SelectSingleNode("p:Application/p:Application.Resources/p:ResourceDictionary", ns).OuterXml;
        var context = new ParserContext();
        context.XmlnsDictionary.Add("x", "http://schemas.microsoft.com/winfx/2006/xaml");
        app.Resources = (ResourceDictionary)XamlReader.Parse(
            resources.Replace("Source=\"Themes/", "Source=\"/DesktopIniManager;component/Themes/"), context);
        System.Threading.SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        // Keep the window unopened so this check cannot persist the user's layout on close.
        var window = new DesktopIniManager.MainWindow(new StartupState { Theme = "Dark" });
        var model = window.ViewModel;
        var list = (ListBox)window.FindName("FileIconList");
        foreach (var columns in new[] { 2, 3, 2 })
        {
            var command = columns == 2 ? model.FileIconLargeCommand : model.FileIconSmallCommand;
            command.Execute(null);
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
            var deadline = DateTime.UtcNow.AddSeconds(20);
            timer.Tick += (_, _) =>
            {
                if (!model.IsFileBusy || DateTime.UtcNow >= deadline) frame.Continue = false;
            };
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
            Check(!model.IsFileBusy, "icon mode switch completes");
            Check(list.Visibility == Visibility.Visible, "icon list becomes visible");
            var panel = (VirtualizingUniformGrid)list.ItemsPanel.LoadContent();
            Check(panel.Columns == columns, "icon mode uses " + columns + " columns");
            var item = (StackPanel)list.ItemTemplate.LoadContent();
            var image = (Image)((Border)item.Children[0]).Child;
            Check(image.Width == (columns == 2 ? 48 : 32), "icon mode uses matching image size");
        }
        model.FileListViewCommand.Execute(null);
        Check(list.Visibility == Visibility.Collapsed, "list mode can be restored");
        ResultHistoryStore.DirectoryPath = Path.Combine(Path.GetTempPath(), "dim-tab-" + Guid.NewGuid().ToString("N"));
        var comparison = new DeveloperDifferencerWindow();
        var tab = new ComparisonHistoryTab { Title = "Close this tab", Snapshot = new DiffSnapshot() };
        comparison.ViewModel.HistoryTabs.Add(tab);
        var tabs = new ListBox
        {
            Style = (Style)comparison.Resources["HistoryTabStrip"],
            DataContext = comparison.ViewModel,
            ItemsSource = comparison.ViewModel.HistoryTabs
        };
        var tabHost = new Window { Content = tabs, Width = 800, Height = 100, Left = -20000, Top = -20000,
            ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        tabHost.Show();
        tabs.Measure(new Size(800, 100));
        tabs.Arrange(new Rect(0, 0, 800, 100));
        tabs.UpdateLayout();
        tabs.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        static Button FindCloseButton(DependencyObject parent)
        {
            if (parent is Button { Content: "×" } button) return button;
            for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var found = FindCloseButton(System.Windows.Media.VisualTreeHelper.GetChild(parent, index));
                if (found != null) return found;
            }
            return null;
        }
        var closeButton = FindCloseButton(tabs);
        Check(closeButton?.Command == comparison.ViewModel.DeleteHistoryTabCommand, "tab close resolves command across the item template");
        typeof(System.Windows.Controls.Primitives.ButtonBase).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(closeButton, null);
        Check(!comparison.ViewModel.HistoryTabs.Contains(tab), "tab close button removes the actual history tab");
        tabHost.Close();
        return 0;
    }

    internal static int RunViewer(bool markersOnly = false)
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
        Button[] Arrows(bool source) => ((Canvas)((RichTextBox)Field(source ? "_leftList" : "_rightList")).Tag).Children.OfType<Button>().ToArray();
        Button Save(bool source) => ((TextBlock)window.FindName(source ? "SourceHeader" : "TargetHeader")).Inlines.OfType<InlineUIContainer>().Select(c => c.Child).OfType<Button>().Single();
        try
        {
            window.ViewModel.CompareCase = true;
            window.ViewModel.CompareNewlines = false;
            window.ViewModel.CompareSpaces = false;
            window.Show(); Pump(() => Field("_leftList") != null && !(bool)Field("_renderingContent"));
            var model = window.ViewModel;
            model.ShowSpaces = model.ShowNewlines = true;
            ((CheckBox)window.FindName("ShowSpacesBox")).SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
            ((CheckBox)window.FindName("ShowNewlinesBox")).SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
            var originalLeftPane = (RichTextBox)Field("_leftList");
            var originalRightPane = (RichTextBox)Field("_rightList");
            var originalLeftDocument = originalLeftPane.Document;
            var originalRightDocument = originalRightPane.Document;
            var retainedCommon = originalLeftDocument.Blocks.ElementAt(1);
            if (!markersOnly)
            {
            Check(Arrows(true).Length == 3 && Arrows(false).Length == 3, "arrows exist in both gutters");
            Check(!Save(true).IsEnabled && !Save(false).IsEnabled, "save buttons disabled initially");
            System.Windows.Media.Color FirstBackground(bool source) =>
                ((System.Windows.Media.SolidColorBrush)((RichTextBox)Field(source ? "_leftList" : "_rightList")).Document.Blocks.FirstBlock.Background).Color;
            var sourceBackground = FirstBackground(true);
            var targetBackground = FirstBackground(false);
            var selected = (System.Collections.Generic.HashSet<int>)Field("_sourceSelectedEditHunks"); selected.UnionWith(new[] { 0, 1, 2 });
            var targetSelected = (System.Collections.Generic.HashSet<int>)Field("_targetSelectedEditHunks"); targetSelected.Add(1);
            originalLeftPane.SelectAll();
            var targetSecondHunk = originalRightDocument.Blocks.ElementAt(model.Lines.FindIndex(row => row.EditHunkId == 1));
            originalRightPane.Selection.Select(targetSecondHunk.ContentStart, targetSecondHunk.ContentEnd);
            Call("RefreshEditArrows");
            Check(Arrows(true)[0].IsEnabled && Arrows(true).Skip(1).All(x => !x.IsEnabled), "only first selected arrow is clickable");
            Check(Arrows(false).All(x => x.IsEnabled), "Source multi-selection does not group Target arrows");
            originalLeftPane.Focus();
            Arrows(true)[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(() => !(bool)Field("_renderingContent"));
            Check(model.IsDirty(false) && !model.IsDirty(true), "copy dirties only destination");
            var copiedBackground = FirstBackground(false);
            Check(FirstBackground(true).R == (byte)(sourceBackground.R * 0.5) && copiedBackground.R == (byte)(targetBackground.R * 0.5) &&
                copiedBackground.G == (byte)(targetBackground.G * 0.5) && copiedBackground.B == (byte)(targetBackground.B * 0.5),
                "copied block darkens both backgrounds by 50 percent");
            Check(ReferenceEquals(originalLeftPane, Field("_leftList")) && ReferenceEquals(originalRightPane, Field("_rightList")) &&
                ReferenceEquals(originalLeftDocument, originalLeftPane.Document) && ReferenceEquals(originalRightDocument, originalRightPane.Document) &&
                ReferenceEquals(retainedCommon, originalLeftDocument.Blocks.ElementAt(1)), "copy retains panes, documents and unrelated rows");
            Check(selected.Count == 0 && targetSelected.SetEquals(new[] { 1 }), "copy clears only the executing side selection");
            Check(originalLeftPane.Selection.IsEmpty && !originalRightPane.Selection.IsEmpty, "copy retains the opposite visible selection");
            Check(!originalLeftPane.IsKeyboardFocusWithin, "copy releases the executing pane keyboard focus");
            Check(model.ShowSpaces && model.ShowNewlines && ((CheckBox)window.FindName("ShowSpacesBox")).IsChecked == true &&
                ((CheckBox)window.FindName("ShowNewlinesBox")).IsChecked == true, "copy preserves whitespace and newline display");
            Check(Arrows(true).All(x => x.IsEnabled) && model.CanUndoHunk(0, true) && model.CanUndoHunk(2, true), "each copied hunk has independent undo arrow");
            Check(Save(false).IsEnabled && !Save(true).IsEnabled && File.ReadAllBytes(b).SequenceEqual(originalB), "save enablement and no implicit write");
            Arrows(true)[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(() => !(bool)Field("_renderingContent"));
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
            var leftPane = (RichTextBox)Field("_leftList");
            var rightPane = (RichTextBox)Field("_rightList");
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
                var paneBefore = (RichTextBox)Field("_leftList");
                paneBefore.ScrollToVerticalOffset(hunkView ? 0 : 220);
                ((RichTextBox)Field("_rightList")).ScrollToVerticalOffset(hunkView ? 0 : 220);
                window.UpdateLayout();
                var offsetBefore = paneBefore.VerticalOffset;
                var rowsBefore = paneBefore.Document.Blocks.ToArray();
                model.ShowSpaces = hunkView;
                model.ShowNewlines = !hunkView;
                ((CheckBox)window.FindName("ShowSpacesBox")).SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, hunkView);
                ((CheckBox)window.FindName("ShowNewlinesBox")).SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, !hunkView);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                model.CopyHunks(new[] { 0 }, true);
                Wait((Task)Call("RenderTextEditsAsync"));
                Console.WriteLine("COPY hunkView=" + hunkView + " ms=" + watch.ElapsedMilliseconds);
                Check(ReferenceEquals(paneBefore, Field("_leftList")) &&
                    rowsBefore.Skip(1).SequenceEqual(paneBefore.Document.Blocks.Skip(1)), "single copy retains every unrelated row");
                Check(Math.Abs(paneBefore.VerticalOffset - offsetBefore) < 1, "copy keeps the viewport fixed");
                Check(model.ShowSpaces == hunkView && model.ShowNewlines == !hunkView &&
                    ((CheckBox)window.FindName("ShowSpacesBox")).IsChecked == hunkView &&
                    ((CheckBox)window.FindName("ShowNewlinesBox")).IsChecked == !hunkView,
                    "copy retains marker switches in Hunk and All views");
                model.UndoEdit();
                Wait((Task)Call("RenderTextEditsAsync"));
            }
            }
            File.WriteAllText(a, "source text\ncommon words\nsource end");
            File.WriteAllText(b, "target text\ncommon words\ntarget end");
            if (model.DifferencesOnly) model.ToggleDifferencesOnly();
            Wait((Task)Call("ReloadTextAsync"));
            var spaceOption = (CheckBox)window.FindName("ShowSpacesBox");
            var newlineOption = (CheckBox)window.FindName("ShowNewlinesBox");
            var clickCheckbox = typeof(System.Windows.Controls.Primitives.ToggleButton).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic);
            if (spaceOption.IsChecked != true) clickCheckbox.Invoke(spaceOption, null);
            if (newlineOption.IsChecked != true) clickCheckbox.Invoke(newlineOption, null);
            void CheckRenderedMarkers(string operation)
            {
                bool idle = false;
                window.Dispatcher.BeginInvoke(new Action(() => idle = true), DispatcherPriority.ApplicationIdle);
                Pump(() => idle);
                Check(model.ShowSpaces && model.ShowNewlines && spaceOption.IsChecked == true && newlineOption.IsChecked == true,
                    operation + ": checkbox and model stay ON");
                foreach (bool source in new[] { true, false })
                {
                    var pane = (RichTextBox)Field(source ? "_leftList" : "_rightList");
                    var overlay = ((Grid)pane.Parent).Children.Cast<FrameworkElement>().Single(element => element.GetType().Name == "WhitespaceOverlay");
                    int marks = (int)overlay.GetType().GetProperty("LastDrawnMarkCount", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(overlay);
                    Console.WriteLine(operation + " source=" + source + " drawn=" + marks);
                    Check(marks >= 5, operation + ": actual WPF render retains space and newline markers");
                }
            }
            var fontSettingsPath = Path.Combine(AppSlot.Directory, "diff-font.txt");
            var previousFontSettings = File.Exists(fontSettingsPath) ? File.ReadAllBytes(fontSettingsPath) : null;
            try
            {
                var fontFamilyBox = (ComboBox)window.FindName("TextFontFamilyBox");
                var fontSizeBox = (ComboBox)window.FindName("TextFontSizeBox");
                var fontPane = (RichTextBox)Field("_leftList");
                var fontDocument = fontPane.Document;
                var fontRows = fontDocument.Blocks.ToArray();
                fontFamilyBox.SelectedItem = "Arial";
                fontSizeBox.SelectedItem = 24.0;
                CheckRenderedMarkers("after font change");
                var fontOverlay = ((Grid)fontPane.Parent).Children.Cast<FrameworkElement>()
                    .Single(element => element.GetType().Name == "WhitespaceOverlay");
                var cachedRows = (System.Collections.IDictionary)fontOverlay.GetType()
                    .GetField("_cachedRows", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(fontOverlay);
                var cachedFirstRow = cachedRows[0];
                fontOverlay.InvalidateVisual();
                CheckRenderedMarkers("cached redraw");
                Check(ReferenceEquals(cachedFirstRow, cachedRows[0]), "redraw reuses cached row drawing without recalculating character positions");
                var originalPageWidth = fontDocument.PageWidth;
                fontDocument.PageWidth = 2000;
                fontPane.UpdateLayout();
                fontPane.ScrollToHorizontalOffset(150);
                CheckRenderedMarkers("cached horizontal scroll");
                Check(fontPane.HorizontalOffset > 0 && ReferenceEquals(cachedFirstRow, cachedRows[0]),
                    "horizontal scrolling translates the cached drawing");
                fontPane.ScrollToHorizontalOffset(0);
                fontDocument.PageWidth = originalPageWidth;
                CheckRenderedMarkers("cached scroll back");
                Check(fontPane.FontFamily.Source == "Arial" && fontPane.FontSize == 24 &&
                    fontPane.Document.Blocks.FirstBlock.FontSize == 24, "font applies immediately to actual document text");
                Check(ReferenceEquals(fontDocument, fontPane.Document) && fontRows.SequenceEqual(fontDocument.Blocks),
                    "font change retains document and paragraphs");
                var gutter = (TextBlock)((Canvas)fontPane.Tag).Children[0];
                Check(gutter.FontSize == 24 && gutter.LineHeight == fontDocument.LineHeight &&
                    fontDocument.LineHeight > 22 && Arrows(true)[0].Height == fontDocument.LineHeight,
                    "font change keeps row numbers and copy arrows aligned");
                Arrows(true)[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                CheckRenderedMarkers("font change then copy");
                Arrows(true)[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                CheckRenderedMarkers("font change then undo");
                var reopened = new DiffViewWindow(snapshot, snapshot.Files[0], a, b);
                Check((string)((ComboBox)reopened.FindName("TextFontFamilyBox")).SelectedItem == "Arial" &&
                    (double)((ComboBox)reopened.FindName("TextFontSizeBox")).SelectedItem == 24,
                    "new viewer restores saved family and size");
                reopened.Close();
                fontFamilyBox.SelectedItem = "Consolas";
                fontSizeBox.SelectedItem = 13.0;
                CheckRenderedMarkers("restored font");
            }
            finally
            {
                if (previousFontSettings == null) File.Delete(fontSettingsPath);
                else File.WriteAllBytes(fontSettingsPath, previousFontSettings);
            }
            CheckRenderedMarkers("before copy");
            Arrows(true)[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            CheckRenderedMarkers("after arrow copy");
            Arrows(true)[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            CheckRenderedMarkers("after arrow undo");
            var contextMenu = ((Grid)window.FindName("Body")).ContextMenu;
            Check(ReferenceEquals(((RichTextBox)Field("_leftList")).ContextMenu, contextMenu) &&
                ReferenceEquals(((RichTextBox)Field("_rightList")).ContextMenu, contextMenu), "both panes use the block context menu instead of the RichTextBox default");
            var clipboardMenu = (MenuItem)contextMenu.Items[0];
            Check((string)clipboardMenu.Header == DesktopIniManager.Properties.StringOverlay.Get("Diff_Clipboard") && clipboardMenu.Items.Count == 2 &&
                clipboardMenu.Items.Cast<MenuItem>().All(item => item.Icon is Image) &&
                (string)((MenuItem)contextMenu.Items[2]).Header == DesktopIniManager.Properties.StringOverlay.Get("Diff_CopyRight") &&
                (string)((MenuItem)contextMenu.Items[3]).Header == DesktopIniManager.Properties.StringOverlay.Get("Diff_CopyLeft"), "context menu has clipboard submenu and both copy directions with icons");
            if (markersOnly) return 0;
            int DrawEof(bool source)
            {
                window.UpdateLayout();
                var pane = (RichTextBox)Field(source ? "_leftList" : "_rightList");
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
                var cached = (System.Collections.IDictionary)Field("_renderedPanes");
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
            foreach (bool source in new[] { true, false })
            {
                File.WriteAllText(a, source ? "last" : "last\r\n");
                File.WriteAllText(b, source ? "last\r\n" : "last");
                if (model.DifferencesOnly) model.ToggleDifferencesOnly();
                Wait((Task)Call("ReloadTextAsync"));
                model.CopyHunks(model.Lines.Where(row => row.EditHunkId >= 0).Select(row => row.EditHunkId).Distinct().ToArray(), source);
                Wait((Task)Call("RenderTextEditsAsync"));
                Check(model.IsDirty(!source) && Save(!source).IsEnabled, "EOF copy enables destination Save");
                model.UndoEdit();
                Wait((Task)Call("RenderTextEditsAsync"));
                Check(!model.HasEdits && model.ShowNewlines && ((CheckBox)window.FindName("ShowNewlinesBox")).IsChecked == true,
                    "EOF undo restores original and retains newline display in All view");
                model.CopyHunks(model.Lines.Where(row => row.EditHunkId >= 0).Select(row => row.EditHunkId).Distinct().ToArray(), source);
                Wait((Task)Call("RenderTextEditsAsync"));
                Wait((Task)Call("SaveTextSideAsync", !source));
                Check(File.ReadAllText(source ? b : a) == "last" && !model.HasEdits, "saved EOF copy removes final CRLF");
            }
            File.WriteAllText(b, "external edit", new UTF8Encoding(true));
            Wait((Task)Call("ReloadTextAsync"));
            Check(model.TargetText.EncodingName == "UTF-8" && model.TargetText.HasBom &&
                DiffViewModel.Text(model.TargetText) == "external edit", "Reload reads external bytes and encoding");

            var sync = new SynchronizeConfirmWindow();
            Check(sync.FindResource("SourceColor") is System.Windows.Media.Brush &&
                sync.FindResource("TargetColor") is System.Windows.Media.Brush, "sync dialog resolves both direction brushes without owner resources");
            sync.Close();
            foreach (bool toTarget in new[] { true, false })
            foreach (bool zipMode in new[] { true, false })
            {
                bool displayed = false;
                var cancelSync = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
                cancelSync.Tick += (_, _) =>
                {
                    var confirmation = app.Windows.OfType<SynchronizeConfirmWindow>().FirstOrDefault(dialog => dialog.IsVisible);
                    if (confirmation == null) return;
                    displayed = true;
                    confirmation.DialogResult = false;
                };
                cancelSync.Start();
                try
                {
                    Check(!SynchronizeConfirmWindow.Confirm(window, "test", snapshot.Files.ToArray(), Array.Empty<DiffFolderSync>(),
                        toTarget, root, root, zipMode, out _, out _) && displayed, "sync/ZIP confirmation opens in both directions without resource or Brush errors");
                }
                finally { cancelSync.Stop(); }
            }
            foreach (bool toTarget in new[] { true, false })
            {
                var sourceRoot = Path.Combine(root, "sync-source");
                var targetRoot = Path.Combine(root, "sync-target");
                Directory.CreateDirectory(sourceRoot);
                Directory.CreateDirectory(targetRoot);
                var sourceFile = Path.Combine(sourceRoot, "payload.txt");
                var targetFile = Path.Combine(targetRoot, "payload.txt");
                File.WriteAllText(sourceFile, toTarget ? "synchronized data" : "old");
                File.WriteAllText(targetFile, toTarget ? "old" : "synchronized data");
                var syncSnapshot = new DiffSnapshot { SourceRoot = sourceRoot, TargetRoot = targetRoot };
                syncSnapshot.Files.Add(new DiffFile
                {
                    RelativePath = "payload.txt", Source = DiffStamp.Read(sourceFile), Target = DiffStamp.Read(targetFile)
                });
                var acceptSync = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
                acceptSync.Tick += (_, _) =>
                {
                    var confirmation = app.Windows.OfType<SynchronizeConfirmWindow>().FirstOrDefault(dialog => dialog.IsVisible);
                    if (confirmation == null) return;
                    ((Button)confirmation.FindName("SyncButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                };
                acceptSync.Start();
                try
                {
                    Check(SynchronizeConfirmWindow.Confirm(window, "test", syncSnapshot.Files.ToArray(), Array.Empty<DiffFolderSync>(),
                        toTarget, sourceRoot, targetRoot), "actual synchronization confirmation accepts");
                }
                finally { acceptSync.Stop(); }
                var syncLog = DeveloperDifferencerService.Synchronize(syncSnapshot, syncSnapshot.Files, toTarget);
                Check(syncLog.Count == 1 && syncLog[0].StartsWith("OK ") &&
                    File.ReadAllText(toTarget ? targetFile : sourceFile) == "synchronized data", "synchronization copies actual file in both directions");
            }

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
        finally { typeof(DiffViewWindow).GetField("_allowEditedClose", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, true); window.Close(); app.Shutdown(); }
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
        var sourceBlock = "Start();\nif (CanStart())\n{\n    RunNew();\n}\nTail();\nsourceEnd";
        var targetBlock = "Start();\nif (CanResume())\n{\n    RunOld();\n}\nTail();\ntargetEnd";
        var braceSource = Doc(a, sourceBlock, utf8);
        var braceTarget = Doc(b, targetBlock, utf8);
        var braceSession = Session(braceSource, braceTarget);
        var braceRows = braceSession.Rows();
        Check(braceRows[1].EditHunkId == braceRows[3].EditHunkId && braceRows[2].EditHunkId >= 0,
            "a lone brace does not split a conditional replacement into separate hunks");
        braceSession.Copy(new[] { braceRows[1].EditHunkId }, true);
        Check(DiffViewModel.Text(braceSession.Document(false)) == sourceBlock.Replace("sourceEnd", "targetEnd"),
            "partial hunk copy replaces the whole conditional and preserves the unrelated change");
        braceSession.UndoLast();
        Check(DiffViewModel.Text(braceSession.Document(false)) == targetBlock, "structural hunk undo restores exact original text");
        var insertedBlock = new[] { "Start();", "{", "    if (!CanStart())", "    {", "        return;", "    }", "    Run();", "}", "Tail();" };
        var originalBlock = new[] { "Start();", "{", "    Run();", "}", "Tail();" };
        var insertedRows = DiffTextService.Compare(insertedBlock, originalBlock);
        Check(insertedRows.Single(row => row.Left == "    Run();").Right == "    Run();" &&
              insertedRows.Where(row => row.LeftNumber >= 3 && row.LeftNumber <= 6).All(row => row.RightNumber == 0),
            "inserted nested braces stay with their conditional instead of matching the enclosing block");
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
            Check(text == pair.Item1, "copy transfers donor EOF and final newline");
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
