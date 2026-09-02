using DesktopIniManager.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Input;
using System.Windows.Interop;

namespace DesktopIniManager.Views
{
    internal static class DiffMedia
    {
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
        private HwndSource inputSource;
        private int current = -1;
        private bool scrolling, selecting;

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
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            DockPanel.SetDock(toolbar, Dock.Top);
            panel.Children.Add(toolbar);

            var actions = new WrapPanel();
            toolbar.Children.Add(actions);
            AddButton(actions, "Prev", () => Navigate(-1));
            AddButton(actions, "Next", () => Navigate(1));
            AddButton(actions, "Open Source", () => OpenEditor(true));
            AddButton(actions, "Open Target", () => OpenEditor(false));

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
            SourceInitialized += (s, e) =>
            {
                inputSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
                inputSource?.AddHook(HorizontalWheelMessage);
            };
            Closed += (s, e) => { inputSource?.RemoveHook(HorizontalWheelMessage); inputSource = null; };
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

        private Button AddButton(Panel panel, string text, Action action)
        {
            var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(12, 6, 12, 6) };
            button.Click += (s, e) => action();
            panel.Children.Add(button);
            return button;
        }

        private string GetPath(bool source)
        { return MftDifferencerService.SafePath(source ? snapshot.SourceRoot : snapshot.TargetRoot, file.RelativePath); }

        private async Task LoadContent()
        {
            status.Text = "Loading…";
            try
            {
                if (DiffMedia.IsImage(file.RelativePath)) { LoadImages(); return; }
                string leftPath = GetPath(true), rightPath = GetPath(false);
                lines = await Task.Run(() => DiffTextService.Compare(ReadText(leftPath), ReadText(rightPath)));
                if (!IsLoaded) return;
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
            }
            catch (Exception ex) { status.Text = "Unable to display: " + ex.Message; }
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
                throw new IOException("Binary file. Open it in an external editor.");
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
                var marker = new Rectangle
                {
                    Width = 30,
                    Height = Math.Max(4, (end - start) * map.ActualHeight / Math.Max(1, lines.Count)),
                    Fill = MapBrush(lines[start].Kind),
                    ToolTip = "Hunk " + (hunks.IndexOf(start) + 1),
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                Canvas.SetTop(marker, start * map.ActualHeight / Math.Max(1, lines.Count));
                marker.MouseLeftButtonDown += (s, e) => Jump(start);
                map.Children.Add(marker);
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

        private void LoadImages()
        {
            var left = LoadImage(GetPath(true)); var right = LoadImage(GetPath(false));
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
            var zoom = new Slider { Minimum = 0.1, Maximum = 4, Value = 1, Width = 170, ToolTip = "共通倍率 10%–400%" };
            var wrapper = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            wrapper.Children.Add(new TextBlock { Text = "Zoom", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            wrapper.Children.Add(zoom);
            var root = (DockPanel)Content;
            DockPanel.SetDock(wrapper, Dock.Top);
            root.Children.Insert(1, wrapper);
            zoom.ValueChanged += (s, e) => { leftCanvas.LayoutTransform = new ScaleTransform(e.NewValue, e.NewValue); rightCanvas.LayoutTransform = new ScaleTransform(e.NewValue, e.NewValue); };
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
            return canvas;
        }

        private static string ImageSize(BitmapSource image) { return image == null ? "none" : image.PixelWidth + " × " + image.PixelHeight + " px"; }

        private void OpenEditor(bool source)
        {
            try
            {
                string path = GetPath(source); if (!File.Exists(path)) throw new FileNotFoundException((source ? "Source" : "Target") + " file does not exist.");
                string arguments = SettingsService.LoadEditorArguments().Replace("{file}", path).Replace("{line}", "1").Replace("{column}", "1");
                Process.Start(new ProcessStartInfo(SettingsService.LoadEditorPath(), arguments) { UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
        }
    }
}
