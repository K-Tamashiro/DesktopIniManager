using DesktopIniManager.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DesktopIniManager.Views
{
    internal static class DiffMedia
    {
        internal const string BinaryMessage = "Binary and cache files are not supported in Diff View. Only text files and supported images can be displayed.";
        public static bool IsBinary(string path)
        { return new[] { ".exe", ".dll", ".pdb", ".obj", ".lib", ".zip", ".7z", ".rar", ".gz", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".db", ".sqlite", ".mp3", ".mp4", ".wav", ".msi", ".bin", ".icl", ".resources", ".baml", ".cache" }.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant()); }
        public static bool IsImage(string path)
        { return new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".ico", ".tif", ".tiff", ".wdp", ".jxr" }.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant()); }
    }

    internal sealed class DiffViewWindow : Window
    {
        private readonly DiffSnapshot snapshot;
        private readonly DiffFile file;
        private readonly Grid body = new Grid();
        private readonly TextBlock status = new TextBlock { Margin = new Thickness(8, 6, 8, 0), TextWrapping = TextWrapping.Wrap };
        private readonly List<int> hunks = new List<int>();
        private List<DiffLine> lines;
        private ListBox leftList, rightList;
        private ScrollViewer leftScroll, rightScroll;
        private Canvas map;
        private Thumb viewportThumb;
        private double viewportDragTop;
        private double sharedTextWidth;
        private HwndSource inputSource;
        private readonly HistoryTextBox externalDiffBox = new HistoryTextBox
        {
            HistoryKey = "DiffView-ExternalDiff",
            PreserveOrder = false,
            Height = 36,
            Padding = new Thickness(8, 4, 8, 4),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            TextAlignment = TextAlignment.Left,
            TextWrapping = TextWrapping.NoWrap,
            ToolTip = "External diff command. Use {source} and {target}."
        };
        private int current = -1;
        private bool scrolling, selecting;
        private bool externalDiffPending;
        private DiffStamp externalSourceStamp, externalTargetStamp;
        private bool checkingExternalEdit;

        public DiffViewWindow(DiffSnapshot snapshot, DiffFile file)
        {
            this.snapshot = snapshot;
            this.file = file;
            Title = "Diff view — " + file.RelativePath + " — read only";
            Width = 1280;
            Height = 800;
            MinWidth = 700;
            MinHeight = 400;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "WindowBackground");
            SetResourceReference(ForegroundProperty, "Ink");

            var panel = new DockPanel { Margin = new Thickness(16) };
            Content = panel;

            var titleBar = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titleText = new TextBlock
            {
                Text = "Diff view",
                FontSize = 26,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleText.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            titleBar.Children.Add(titleText);

            var close = new Button
            {
                Content = "\uE711",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                Style = TryFindResource("IconButton") as Style,
                ToolTip = "Close",
                VerticalAlignment = VerticalAlignment.Center
            };
            close.Click += (s, e) => Close();
            Grid.SetColumn(close, 1);
            titleBar.Children.Add(close);
            DockPanel.SetDock(titleBar, Dock.Top);
            panel.Children.Add(titleBar);

            var toolbar = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            toolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            toolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            DockPanel.SetDock(toolbar, Dock.Top);
            panel.Children.Add(toolbar);

            var externalDiffPanel = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            externalDiffPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            externalDiffPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var externalDiffLabel = new TextBlock
            {
                Text = "External Diff",
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            externalDiffLabel.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            externalDiffPanel.Children.Add(externalDiffLabel);

            externalDiffBox.SetResourceReference(Control.BackgroundProperty, "CardBackground");
            externalDiffBox.SetResourceReference(Control.ForegroundProperty, "Ink");
            externalDiffBox.SetResourceReference(Control.BorderBrushProperty, "Line");
            Grid.SetColumn(externalDiffBox, 1);
            externalDiffPanel.Children.Add(externalDiffBox);

            toolbar.Children.Add(externalDiffPanel);

            var actions = new WrapPanel();
            Grid.SetRow(actions, 1);
            toolbar.Children.Add(actions);
            if (!DiffMedia.IsImage(file.RelativePath))
            {
                AddButton(actions, "\uE70E", "Prev", () => Navigate(-1));
                AddButton(actions, "\uE70D", "Next", () => Navigate(1));
            }
            AddButton(actions, "\uE8A7", "Open Source", () => OpenAssociatedApplication(true));
            AddButton(actions, "\uE8A7", "Open Target", () => OpenAssociatedApplication(false));

            var openExtDiffButton = AddButton(actions, "\uE8A7", "Open Ext Diff", () => OpenExternalDiff());
            openExtDiffButton.SetResourceReference(Control.BackgroundProperty, "ExternalDiffButton");
            openExtDiffButton.SetResourceReference(Control.ForegroundProperty, "ExternalDiffButtonForeground");

            SeedExternalDiffPresets();
            string savedExternalDiff = SettingsService.LoadExternalDiff();
            externalDiffBox.Text = string.IsNullOrWhiteSpace(savedExternalDiff)
                ? ExternalDiffPresets[0]
                : savedExternalDiff;

            status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            DockPanel.SetDock(status, Dock.Bottom);
            panel.Children.Add(status);

            var headers = new Grid();
            headers.ColumnDefinitions.Add(new ColumnDefinition());
            headers.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            headers.ColumnDefinitions.Add(new ColumnDefinition());
            headers.Children.Add(HeaderBlock("Source", file.SourceInfo));
            var rightHeader = HeaderBlock("Target", file.TargetInfo);
            Grid.SetColumn(rightHeader, 2);
            headers.Children.Add(rightHeader);
            DockPanel.SetDock(headers, Dock.Top);
            panel.Children.Add(headers);
            panel.Children.Add(body);

            body.ColumnDefinitions.Add(new ColumnDefinition());
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            body.ColumnDefinitions.Add(new ColumnDefinition());
            Loaded += async (s, e) => await LoadContent();
            Activated += async (s, e) => await RefreshAfterExternalEditAsync();
            SourceInitialized += (s, e) =>
            {
                inputSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
                inputSource?.AddHook(HorizontalWheelMessage);
            };
            Closed += (s, e) =>
            {
                inputSource?.RemoveHook(HorizontalWheelMessage);
                inputSource = null;

                if (!string.IsNullOrWhiteSpace(externalDiffBox.Text))
                {
                    externalDiffBox.CommitHistory();
                    SettingsService.SaveExternalDiff(externalDiffBox.Text.Trim());
                }
            };
            body.PreviewMouseWheel += (s, e) =>
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0) return;
                if (ScrollHorizontally(-e.Delta)) e.Handled = true;
            };
        }

        private bool ScrollHorizontally(int delta)
        {
            if (leftScroll == null) leftScroll = FindScroll(leftList);
            if (rightScroll == null) rightScroll = FindScroll(rightList);
            if (leftScroll == null || rightScroll == null) return false;
            // Native horizontal wheel: positive is right; Shift+vertical wheel reverses that sign.
            double offset = Math.Max(leftScroll.HorizontalOffset, rightScroll.HorizontalOffset) + delta * 48.0 / 120;
            leftScroll.ScrollToHorizontalOffset(offset);
            rightScroll.ScrollToHorizontalOffset(offset);
            return true;
        }

        private IntPtr HorizontalWheelMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int MouseHorizontalWheel = 0x020E;
            if (message != MouseHorizontalWheel) return IntPtr.Zero;
            long coordinates = lParam.ToInt64();
            var point = body.PointFromScreen(new Point(unchecked((short)(coordinates & 0xffff)), unchecked((short)((coordinates >> 16) & 0xffff))));
            if (point.X < 0 || point.Y < 0 || point.X >= body.ActualWidth || point.Y >= body.ActualHeight) return IntPtr.Zero;
            int delta = unchecked((short)((wParam.ToInt64() >> 16) & 0xffff));
            handled = ScrollHorizontally(delta);
            return IntPtr.Zero;
        }

        private static TextBlock HeaderBlock(string title, string info)
        {
            var block = new TextBlock
            {
                Text = title + "\n" + info,
                Margin = new Thickness(8, 4, 8, 8),
                TextWrapping = TextWrapping.Wrap
            };
            block.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            return block;
        }

        private Button AddButton(Panel panel, string glyph, string text, Action action)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = text, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var button = new Button { Content = content, Style = TryFindResource("IconTextButton") as Style, ToolTip = text, Margin = new Thickness(0, 0, 8, 8) };
            System.Windows.Automation.AutomationProperties.SetName(button, text);
            button.Click += (s, e) => action();
            panel.Children.Add(button);
            return button;
        }

        private string GetPath(bool source)
        { return MftDifferencerService.SafePath(source ? snapshot.SourceRoot : snapshot.TargetRoot, file.RelativePath); }

        private async Task LoadContent()
        {
            body.Children.Clear();
            hunks.Clear();
            current = -1;
            leftList = rightList = null;
            leftScroll = rightScroll = null;
            map = null;
            viewportThumb = null;
            status.Text = "Loading…";
            try
            {
                if (DiffMedia.IsBinary(file.RelativePath)) throw new InvalidDataException(DiffMedia.BinaryMessage);
                if (DiffMedia.IsImage(file.RelativePath)) { await LoadImages(); return; }
                string leftPath = GetPath(true), rightPath = GetPath(false);
                lines = await Task.Run(() => DiffTextService.Compare(ReadText(leftPath), ReadText(rightPath)));
                if (!IsLoaded) return;
                sharedTextWidth = MeasureSharedTextWidth();
                leftList = MakeList("LeftDisplay", true);
                rightList = MakeList("RightDisplay", false);
                body.Children.Add(leftList);
                Grid.SetColumn(rightList, 2);
                body.Children.Add(rightList);
                map = new Canvas();
                map.SetResourceReference(Panel.BackgroundProperty, "CardBackground");
                Grid.SetColumn(map, 1);
                body.Children.Add(map);
                for (int i = 0; i < lines.Count; i++)
                    if (lines[i].Kind != "一致" && (i == 0 || lines[i - 1].Kind == "一致")) hunks.Add(i);
                map.SizeChanged += (s, e) => DrawMap();
                DrawMap();
                leftList.SelectionChanged += (s, e) => SyncSelection(leftList, rightList);
                rightList.SelectionChanged += (s, e) => SyncSelection(rightList, leftList);
                leftList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(ScrollChanged));
                rightList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(ScrollChanged));
                status.Text = hunks.Count + " hunks | left red = removed  right green = added | UTF-8 / BOM / Shift-JIS | large files use a simplified match";
                if (hunks.Count > 0)
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!IsLoaded || hunks.Count == 0) return;
                        Jump(hunks[0]);
                        _ = Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (IsLoaded && hunks.Count > 0) Jump(hunks[0]);
                        }), DispatcherPriority.ContextIdle);
                    }), DispatcherPriority.Loaded);
            }
            catch (InvalidDataException) { MessageBox.Show(Owner ?? this, DiffMedia.BinaryMessage, "Diff View", MessageBoxButton.OK, MessageBoxImage.Information); Close(); }
            catch (DecoderFallbackException) { MessageBox.Show(Owner ?? this, DiffMedia.BinaryMessage, "Diff View", MessageBoxButton.OK, MessageBoxImage.Information); Close(); }
            catch (Exception ex) { status.Text = "Unable to display: " + ErrorMessages.English(ex); }
        }
        private double MeasureSharedTextWidth()
        {
            if (lines == null || lines.Count == 0) return 0;

            string longest = lines
                .SelectMany(line => new[] { line.LeftDisplay ?? string.Empty, line.RightDisplay ?? string.Empty })
                .OrderByDescending(text => text.Length)
                .FirstOrDefault() ?? string.Empty;

            var typeface = new Typeface(new FontFamily("Consolas, Yu Gothic UI, Meiryo UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var formatted = new FormattedText(longest, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 13, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            return formatted.WidthIncludingTrailingWhitespace + 24;
        }
        private static string[] ReadText(string path)
        {
            DiffStamp stamp = DiffStamp.Read(path); if (stamp == null) return new string[0];
            if (stamp.Size > 8 * 1024 * 1024) throw new IOException("Files over 8 MB should be opened in an external editor.");
            byte[] bytes = File.ReadAllBytes(path);
            string text;
            try { using (var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, true), true)) text = reader.ReadToEnd(); }
            catch (DecoderFallbackException) { text = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes); }
            if (text.Any(c => c == '\0' || (char.IsControl(c) && c != '\r' && c != '\n' && c != '\t' && c != '\f')))
                throw new InvalidDataException(DiffMedia.BinaryMessage);
            if (text.Length == 0) return new string[0];
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            if (lines.Length > 100000) throw new IOException("Files over 100,000 lines should be opened in an external editor.");
            return lines;
        }

        private ListBox MakeList(string property, bool sourceSide)
        {
            var list = new ListBox
            {
                ItemsSource = lines,
                FontFamily = new FontFamily("Consolas, Yu Gothic UI, Meiryo UI"),
                FontSize = 13,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(0)
            };
            list.SetResourceReference(Control.BackgroundProperty, "CardBackground");
            list.SetResourceReference(Control.ForegroundProperty, "Ink");
            list.SetResourceReference(Control.BorderBrushProperty, "Line");
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Hidden);

            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new Binding(property));
            text.SetValue(FrameworkElement.HeightProperty, 22.0);
            text.SetValue(FrameworkElement.MinWidthProperty, sharedTextWidth);
            text.SetValue(TextBlock.ForegroundProperty, new DynamicResourceExtension("Ink"));
            list.ItemTemplate = new DataTemplate { VisualTree = text };

            var style = new Style(typeof(ListBoxItem));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 0, 6, 0)));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Ink")));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            BindKind(style, "削除", sourceSide ? "DiffRemoved" : "DiffRemovedEmpty");
            BindKind(style, "追加", sourceSide ? "DiffAddedEmpty" : "DiffAdded");
            BindKind(style, "変更", sourceSide ? "DiffRemoved" : "DiffAdded");
            list.ItemContainerStyle = style;
            return list;
        }

        private static void BindKind(Style style, string kind, string resource)
        {
            var trigger = new DataTrigger { Binding = new Binding("Kind"), Value = kind };
            trigger.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(resource)));
            style.Triggers.Add(trigger);
        }

        private Brush ThemeBrush(string key, Color fallback)
        {
            return TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
        }

        private Brush ColorFor(string kind)
        {
            if (kind == "追加") return ThemeBrush("DiffAdded", Color.FromRgb(0x1A, 0x3F, 0x32));
            if (kind == "削除") return ThemeBrush("DiffRemoved", Color.FromRgb(0x5A, 0x24, 0x30));
            return ThemeBrush("DiffAdded", Color.FromRgb(0x1A, 0x3F, 0x32));
        }

        private Brush MapBrush(string kind)
        {
            if (kind != "変更") return ColorFor(kind);
            return new LinearGradientBrush(new GradientStopCollection
            {
                new GradientStop(((SolidColorBrush)ThemeBrush("DiffRemoved", Color.FromRgb(0x5A, 0x24, 0x30))).Color, 0),
                new GradientStop(((SolidColorBrush)ThemeBrush("DiffRemoved", Color.FromRgb(0x5A, 0x24, 0x30))).Color, 0.5),
                new GradientStop(((SolidColorBrush)ThemeBrush("DiffAdded", Color.FromRgb(0x1A, 0x3F, 0x32))).Color, 0.5),
                new GradientStop(((SolidColorBrush)ThemeBrush("DiffAdded", Color.FromRgb(0x1A, 0x3F, 0x32))).Color, 1)
            }, 0);
        }

        private void SyncSelection(ListBox from, ListBox to)
        { if (selecting) return; selecting = true; to.SelectedIndex = from.SelectedIndex; current = hunks.FindLastIndex(i => i <= from.SelectedIndex); selecting = false; }

        private static ScrollViewer FindScroll(DependencyObject parent)
        {
            if (parent is ScrollViewer) return (ScrollViewer)parent;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var found = FindScroll(VisualTreeHelper.GetChild(parent, i)); if (found != null) return found; }
            return null;
        }

        private void ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (scrolling) return;
            var from = e.OriginalSource as ScrollViewer; if (from == null) return;
            if (leftScroll == null) leftScroll = FindScroll(leftList);
            if (rightScroll == null) rightScroll = FindScroll(rightList);
            if (from != leftScroll && from != rightScroll) return;
            UpdateViewport(from);
            if (e.VerticalChange == 0 && e.HorizontalChange == 0) return;
            var to = from == leftScroll ? rightScroll : leftScroll; if (to == null) return;
            scrolling = true; to.ScrollToVerticalOffset(from.VerticalOffset); to.ScrollToHorizontalOffset(from.HorizontalOffset); scrolling = false;
        }

        private void UpdateViewport(ScrollViewer scroll)
        {
            if (viewportThumb == null || map == null || scroll == null) return;
            double height = map.ActualHeight;
            double fraction = scroll.ExtentHeight <= 0 ? 1 : Math.Min(1, scroll.ViewportHeight / scroll.ExtentHeight);
            viewportThumb.Height = Math.Min(height, Math.Max(12, height * fraction));
            viewportThumb.Width = Math.Max(0, map.ActualWidth - 2);
            double travel = Math.Max(0, height - viewportThumb.Height);
            Canvas.SetTop(viewportThumb, scroll.ScrollableHeight <= 0 ? 0
                : travel * Math.Max(0, Math.Min(1, scroll.VerticalOffset / scroll.ScrollableHeight)));
        }

        private void DragViewport(object sender, DragDeltaEventArgs e)
        {
            if (leftScroll == null || rightScroll == null) return;
            double travel = Math.Max(0, map.ActualHeight - viewportThumb.Height);
            viewportDragTop = Math.Max(0, Math.Min(travel, viewportDragTop + e.VerticalChange));
            double fraction = travel <= 0 ? 0 : viewportDragTop / travel;
            leftScroll.ScrollToVerticalOffset(fraction * leftScroll.ScrollableHeight);
            rightScroll.ScrollToVerticalOffset(fraction * rightScroll.ScrollableHeight);
            e.Handled = true;
        }

        private void DrawMap()
        {
            if (map == null || lines == null) return;
            map.Children.Clear();
            foreach (int start in hunks)
            {
                int end = start + 1; while (end < lines.Count && lines[end].Kind != "一致") end++;
                string kind = lines[start].Kind;
                double mapWidth = Math.Max(0, map.ActualWidth);
                double halfWidth = mapWidth / 2.0;
                double top = start * map.ActualHeight / Math.Max(1, lines.Count);
                double height = Math.Max(4, (end - start) * map.ActualHeight / Math.Max(1, lines.Count));

                if (kind == "変更")
                {
                    var leftMarker = new Rectangle { Width = halfWidth, Height = height, ToolTip = "Hunk " + (hunks.IndexOf(start) + 1), Cursor = System.Windows.Input.Cursors.Hand };
                    leftMarker.SetResourceReference(Shape.FillProperty, "DiffRemoved");
                    Canvas.SetLeft(leftMarker, 0);
                    Canvas.SetTop(leftMarker, top);
                    leftMarker.MouseLeftButtonDown += (s, e) => Jump(start);
                    map.Children.Add(leftMarker);

                    var rightMarker = new Rectangle { Width = halfWidth, Height = height, ToolTip = "Hunk " + (hunks.IndexOf(start) + 1), Cursor = System.Windows.Input.Cursors.Hand };
                    rightMarker.SetResourceReference(Shape.FillProperty, "DiffAdded");
                    Canvas.SetLeft(rightMarker, halfWidth);
                    Canvas.SetTop(rightMarker, top);
                    rightMarker.MouseLeftButtonDown += (s, e) => Jump(start);
                    map.Children.Add(rightMarker);
                }
                else
                {
                    var marker = new Rectangle { Width = halfWidth, Height = height, ToolTip = "Hunk " + (hunks.IndexOf(start) + 1), Cursor = System.Windows.Input.Cursors.Hand };
                    marker.SetResourceReference(Shape.FillProperty, kind == "追加" ? "DiffAdded" : "DiffRemoved");
                    Canvas.SetLeft(marker, kind == "追加" ? halfWidth : 0);
                    Canvas.SetTop(marker, top);
                    marker.MouseLeftButtonDown += (s, e) => Jump(start);
                    map.Children.Add(marker);
                }
            }
            if (viewportThumb == null)
            {
                viewportThumb = new Thumb { Cursor = System.Windows.Input.Cursors.SizeNS, ToolTip = "Visible range — drag to scroll", Focusable = false };
                var border = new FrameworkElementFactory(typeof(Border));
                border.SetValue(Border.BorderBrushProperty, new DynamicResourceExtension("Accent"));
                border.SetValue(Border.BorderThicknessProperty, new Thickness(2));
                border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
                viewportThumb.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = border };
                viewportThumb.DragStarted += (s, e) => viewportDragTop = Canvas.GetTop(viewportThumb);
                viewportThumb.DragDelta += DragViewport;
            }
            Canvas.SetLeft(viewportThumb, 1);
            Panel.SetZIndex(viewportThumb, 1);
            map.Children.Add(viewportThumb);
            if (leftScroll == null) leftScroll = FindScroll(leftList);
            if (rightScroll == null) rightScroll = FindScroll(rightList);
            UpdateViewport(leftScroll);
        }

        private void Navigate(int direction)
        { if (hunks.Count == 0) return; current = current < 0 ? (direction > 0 ? 0 : hunks.Count - 1) : (current + direction + hunks.Count) % hunks.Count; Jump(hunks[current]); }

        private void Jump(int index)
        { leftList.SelectedIndex = rightList.SelectedIndex = index; leftList.ScrollIntoView(lines[index]); rightList.ScrollIntoView(lines[index]); current = hunks.IndexOf(index); }

        private async Task LoadImages()
        {
            string leftPath = GetPath(true), rightPath = GetPath(false);
            var images = await Task.Run(() => new[] { LoadImage(leftPath), LoadImage(rightPath) });
            if (!IsLoaded) return;
            var left = images[0]; var right = images[1];
            body.ColumnDefinitions[1].Width = new GridLength(12);
            double width = Math.Max(left == null ? 0 : left.PixelWidth, right == null ? 0 : right.PixelWidth);
            double height = Math.Max(left == null ? 0 : left.PixelHeight, right == null ? 0 : right.PixelHeight);
            var leftCanvas = ImageCanvas(left, width, height); var rightCanvas = ImageCanvas(right, width, height);
            leftScroll = ThemedViewer(leftCanvas);
            rightScroll = ThemedViewer(rightCanvas);
            body.Children.Add(leftScroll);
            Grid.SetColumn(rightScroll, 2);
            body.Children.Add(rightScroll);
            leftScroll.ScrollChanged += ScrollChanged;
            rightScroll.ScrollChanged += ScrollChanged;
            var zoom = new Slider { Minimum = 0.001, Maximum = 16, Value = 1, Width = 170, ToolTip = "Shared zoom for Source and Target" };
            var wrapper = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            wrapper.Children.Add(new TextBlock { Text = "Zoom", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            wrapper.Children.Add(zoom);
            var root = (DockPanel)Content;
            DockPanel.SetDock(wrapper, Dock.Top);
            root.Children.Insert(1, wrapper);
            zoom.ValueChanged += (s, e) => { leftCanvas.LayoutTransform = new ScaleTransform(e.NewValue, e.NewValue); rightCanvas.LayoutTransform = new ScaleTransform(e.NewValue, e.NewValue); };
            bool fitToWindow = true, fitting = false;
            Action fit = () =>
            {
                if (!fitToWindow || width <= 0 || height <= 0) return;
                double scale = Math.Min((body.ActualWidth - 20) / 2 / width, (body.ActualHeight - 20) / height);
                if (scale <= 0) return;
                fitting = true;
                zoom.Value = Math.Max(zoom.Minimum, Math.Min(zoom.Maximum, scale));
                fitting = false;
            };
            zoom.ValueChanged += (s, e) => { if (!fitting) fitToWindow = false; };
            AddButton(wrapper, "\uE9A6", "Fit", () => { fitToWindow = true; fit(); });
            AddButton(wrapper, "\uE91F", "100%", () => { fitToWindow = false; zoom.Value = 1; });
            body.SizeChanged += (s, e) => fit();
            await Dispatcher.InvokeAsync(fit, System.Windows.Threading.DispatcherPriority.Loaded);
            status.Text = "Source: " + ImageSize(left) + " | Target: " + ImageSize(right) + " | shared zoom, top-left aligned (GIF/ICO first frame)";
        }

        private static ScrollViewer ThemedViewer(object content)
        {
            var viewer = new ScrollViewer
            {
                Content = content,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                BorderThickness = new Thickness(1)
            };
            viewer.SetResourceReference(Control.BackgroundProperty, "CardBackground");
            viewer.SetResourceReference(Control.ForegroundProperty, "Ink");
            viewer.SetResourceReference(Control.BorderBrushProperty, "Line");
            return viewer;
        }

        private static BitmapSource LoadImage(string path)
        {
            if (DiffStamp.Read(path) == null) return null;
            using (var stream = File.OpenRead(path))
            { var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]; frame.Freeze(); return frame; }
        }

        private static Canvas ImageCanvas(BitmapSource image, double width, double height)
        {
            var canvas = new Canvas { Width = width, Height = height };
            canvas.SetResourceReference(Panel.BackgroundProperty, "CardBackground");
            if (image != null) canvas.Children.Add(new Image { Source = image, Width = image.PixelWidth, Height = image.PixelHeight, Stretch = Stretch.Fill });
            else canvas.Children.Add(new TextBlock { Text = "Missing", Margin = new Thickness(12) });
            return canvas;
        }

        private static string ImageSize(BitmapSource image) { return image == null ? "none" : image.PixelWidth + " × " + image.PixelHeight + " px"; }

        private static readonly string[] ExternalDiffPresets =
        {
            @"code --diff ""{source}"" ""{target}""",
            @"""C:\Program Files\MIFES11\MIW.exe"" /diff ""{source}"" ""{target}""",
            @"""C:\Program Files\WinMerge\WinMergeU.exe"" ""{source}"" ""{target}""",
            @"devenv /Diff ""{source}"" ""{target}"""
        };

        private static void SeedExternalDiffPresets()
        {
            string settingsDirectory = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DesktopIniManager");
            string markerPath = System.IO.Path.Combine(settingsDirectory, "diff-view-presets-v2.txt");
            if (File.Exists(markerPath)) return;

            var store = new InputHistoryStore(System.IO.Path.Combine(settingsDirectory, "input-history"));
            store.Replace("DiffView-ExternalDiff", ExternalDiffPresets);
            try
            {
                Directory.CreateDirectory(settingsDirectory);
                File.WriteAllText(markerPath, "code+mifes+winmerge+visualstudio");
            }
            catch { }
        }

        private void OpenAssociatedApplication(bool source)
        {
            try
            {
                string path = GetPath(source);
                if (!File.Exists(path))
                    throw new FileNotFoundException((source ? "Source" : "Target") + " file does not exist.");

                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ErrorMessages.English(ex), Title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenExternalDiff()
        {
            try
            {
                string sourcePath = GetPath(true);
                string targetPath = GetPath(false);
                if (!File.Exists(sourcePath)) throw new FileNotFoundException("Source file does not exist.");
                if (!File.Exists(targetPath)) throw new FileNotFoundException("Target file does not exist.");

                string command = (externalDiffBox.Text ?? string.Empty).Trim();
                if (command.Length == 0) throw new InvalidOperationException("External diff command is empty.");

                externalDiffBox.CommitHistory();

                string executable;
                string arguments;
                SplitCommand(command, out executable, out arguments);

                executable = Environment.ExpandEnvironmentVariables(executable);
                arguments = arguments
                    .Replace("{source}", sourcePath)
                    .Replace("{target}", targetPath);

                externalSourceStamp = DiffStamp.Read(sourcePath);
                externalTargetStamp = DiffStamp.Read(targetPath);
                externalDiffPending = true;

                Process.Start(new ProcessStartInfo(executable, arguments)
                {
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ErrorMessages.English(ex), Title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task RefreshAfterExternalEditAsync()
        {
            if (!externalDiffPending || checkingExternalEdit || !IsLoaded) return;

            try
            {
                checkingExternalEdit = true;
                DiffStamp source = DiffStamp.Read(GetPath(true));
                DiffStamp target = DiffStamp.Read(GetPath(false));
                if (SameStamp(source, externalSourceStamp) && SameStamp(target, externalTargetStamp)) return;

                externalSourceStamp = source;
                externalTargetStamp = target;

                var owner = Owner as MftDifferencerWindow;
                if (owner != null)
                    await owner.RefreshFileAsync(file);

                await LoadContent();
            }
            catch (Exception ex)
            {
                status.Text = "Unable to refresh external edit: " + ErrorMessages.English(ex);
            }
            finally
            {
                checkingExternalEdit = false;
            }
        }

        private static bool SameStamp(DiffStamp left, DiffStamp right)
        {
            return left == null || right == null
                ? left == right
                : left.Size == right.Size && left.ModifiedUtc == right.ModifiedUtc;
        }

        private static void SplitCommand(string command, out string executable, out string arguments)
        {
            command = command.Trim();
            if (command.StartsWith("\"", StringComparison.Ordinal))
            {
                int closingQuote = command.IndexOf('"', 1);
                if (closingQuote < 0) throw new FormatException("The external diff executable path has an unmatched quote.");
                executable = command.Substring(1, closingQuote - 1);
                arguments = command.Substring(closingQuote + 1).TrimStart();
                return;
            }

            int separator = command.IndexOfAny(new[] { ' ', '\t' });
            if (separator < 0)
            {
                executable = command;
                arguments = string.Empty;
                return;
            }

            executable = command.Substring(0, separator);
            arguments = command.Substring(separator + 1).TrimStart();
        }
    }
}
