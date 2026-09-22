using DesktopIniManager.ViewModels;
using DesktopIniManager.Services;
using DesktopIniManager.Properties;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Documents;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DesktopIniManager.Views
{
    internal sealed partial class DiffViewWindow : Window
    {
        internal DiffViewModel ViewModel { get; }
        private List<int> hunks => ViewModel.Hunks;
        private List<DiffLine> lines => ViewModel.Lines;
        private RichTextBox leftList, rightList;
        private ScrollViewer leftScroll, rightScroll;
        private Canvas map;
        private Border hunkOverlay;
        private int hunkStart = -1;
        private int hunkEnd = -1;
        private Thumb viewportThumb;
        private Border mapSelection;
        private double viewportDragTop;
        private double sharedTextWidth;
        private const double DiffLineHeight = 22;
        private HwndSource inputSource;
        private int current { get => ViewModel.CurrentHunk; set => ViewModel.CurrentHunk = value; }
        private bool scrolling;
        private bool imagePanning;
        private Point imagePanLast;
        private SizeChangedEventHandler imageFitHandler;

        internal DiffSnapshot Snapshot => ViewModel.Snapshot;

        internal DiffViewWindow(DiffSnapshot snapshot, DiffFile file)
        {
            ViewModel = new DiffViewModel(snapshot, file, new UserDialogService(this));
            InitializeComponent();
            DataContext = ViewModel;
            ViewModel.JumpRequested += Jump;
            ViewModel.ReloadRequested = LoadContent;
            ViewModel.CloseRequested += Close;
            if (CloseButtonIcon != null)
                CloseButtonIcon.Source = DifferencerStatusIcons.GetCustomIcon(26);
            if (ExternalDiffLabelIcon != null)
                ExternalDiffLabelIcon.Source = DifferencerStatusIcons.GetCustomIcon(52);
            ViewModel.ExternalDiffHistoryRequested += () => externalDiffBox.CommitHistory();
            ViewModel.RefreshFileRequested = async difference =>
            {
                if (Owner is DeveloperDifferencerWindow owner) await owner.RefreshFileAsync(difference);
            };
            ViewModel.VisibleFilesRequested = () =>
                Owner is DeveloperDifferencerWindow owner
                    ? owner.GetVisibleComparableFiles()
                    : Array.Empty<DiffFile>();
            Closing += (s, e) =>
            {
                // WPF can clear Owner before Closed is raised.
                if (Owner is DeveloperDifferencerWindow owner) owner.SelectLastViewedFile(ViewModel.File);
            };
            BuildToolbar();
            Loaded += async (s, e) => await LoadContent();
            Activated += async (s, e) =>
            {
                if (IsLoaded) await ViewModel.RefreshAfterExternalEditAsync();
            };
            SourceInitialized += (s, e) =>
            {
                inputSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
                inputSource?.AddHook(HorizontalWheelMessage);
            };
            Closed += (s, e) =>
            {
                inputSource?.RemoveHook(HorizontalWheelMessage);
                inputSource = null;
                DetachImageFitHandler();
                ViewModel.Close();
            };
        }

        private void Body_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0) return;
            if (ScrollHorizontally(-e.Delta)) e.Handled = true;
        }

        private void BuildToolbar()
        {
            actionsPanel.Children.Clear();
            fileNavigationPanel.Children.Clear();
            if (!ViewModel.IsImage)
            {
                AddButton(actionsPanel, DifferencerStatusIcons.GetCustomIcon(33), StringOverlay.Get("Diff_PreviousHunk"), ViewModel.PreviousHunkCommand);
                AddButton(actionsPanel, DifferencerStatusIcons.GetCustomIcon(32), StringOverlay.Get("Diff_NextHunk"), ViewModel.NextHunkCommand);
            }
            AddButton(actionsPanel, DifferencerStatusIcons.GetCustomIcon(53), StringOverlay.Get("Diff_OpenSource"), ViewModel.OpenSourceCommand);
            AddButton(actionsPanel, DifferencerStatusIcons.GetCustomIcon(54), StringOverlay.Get("Diff_OpenTarget"), ViewModel.OpenTargetCommand);
            var openExtDiffButton = AddButton(actionsPanel, DifferencerStatusIcons.GetCustomIcon(52), "Open Ext Diff", ViewModel.OpenExternalDiffCommand);
            openExtDiffButton.Background = Brushes.Transparent;
            AddButton(fileNavigationPanel, DifferencerStatusIcons.GetCustomIcon(40), StringOverlay.Get("Diff_PreviousFile"), ViewModel.PreviousFileCommand);
            AddButton(fileNavigationPanel, DifferencerStatusIcons.GetCustomIcon(41), StringOverlay.Get("Diff_NextFile"), ViewModel.NextFileCommand);
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

        private void DetachImageFitHandler()
        {
            if (imageFitHandler == null) return;
            body.SizeChanged -= imageFitHandler;
            imageFitHandler = null;
        }

        private async Task LoadContent()
        {
            DetachImageFitHandler();
            if (imageToolbar is Panel p)
            {
                p.Children.Clear();
                p.Visibility = Visibility.Collapsed;
            }
            body.Children.Clear();
            mapColumn.Width = new GridLength(34);
            BuildToolbar();
            leftList = rightList = null;
            leftScroll = rightScroll = null;
            map = null;
            hunkOverlay = null;
            hunkStart = hunkEnd = -1;
            viewportThumb = null;
            mapSelection = null;
            try
            {
                if (!await ViewModel.LoadContentAsync() || !IsLoaded) return;
                if (ViewModel.IsImage) { await RenderImages(); return; }
                sharedTextWidth = MeasureSharedTextWidth();
                FrameworkElement leftHost = MakeHost(true, out leftList);
                FrameworkElement rightHost = MakeHost(false, out rightList);
                body.Children.Add(leftHost);
                Grid.SetColumn(rightHost, 2);
                body.Children.Add(rightHost);
                map = new Canvas();
                map.SetResourceReference(Panel.BackgroundProperty, "CardBackground");
                Grid.SetColumn(map, 1);
                body.Children.Add(map);
                map.SizeChanged += (s, e) => DrawMap();
                DrawMap();
                hunkOverlay = new Border
                {
                    BorderThickness = new Thickness(2),
                    Background = Brushes.Transparent,
                    IsHitTestVisible = false,
                    Visibility = Visibility.Collapsed,
                    SnapsToDevicePixels = true
                };
                hunkOverlay.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 210, 0));
                Grid.SetColumnSpan(hunkOverlay, 3);
                Panel.SetZIndex(hunkOverlay, 2);
                body.Children.Add(hunkOverlay);
                leftList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(ScrollChanged));
                rightList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(ScrollChanged));
                leftList.PreviewMouseLeftButtonDown += DiffPane_PreviewMouseLeftButtonDown;
                rightList.PreviewMouseLeftButtonDown += DiffPane_PreviewMouseLeftButtonDown;
                if (hunks.Count > 0)
                {
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
            }
            catch (Exception ex) { ViewModel.ReportError(ex); }
        }
        private double MeasureSharedTextWidth()
        {
            if (lines == null || lines.Count == 0) return 0;

            string longest = string.Empty;
            foreach (var line in lines)
            {
                if (line.Left != null && line.Left.Length > longest.Length) longest = line.Left;
                if (line.Right != null && line.Right.Length > longest.Length) longest = line.Right;
            }

            var typeface = new Typeface(new FontFamily("Consolas, Yu Gothic UI, Meiryo UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var formatted = new FormattedText(longest, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 13, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            return formatted.WidthIncludingTrailingWhitespace + 24;
        }

        private FrameworkElement MakeHost(bool sourceSide, out RichTextBox box)
        {
            var font = new FontFamily("Consolas, Yu Gothic UI, Meiryo UI");
            var gutter = new TextBlock
            {
                FontFamily = font,
                FontSize = 13,
                LineHeight = DiffLineHeight,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Padding = new Thickness(8, 0, 8, 0),
                IsHitTestVisible = false
            };
            gutter.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            gutter.SetResourceReference(TextBlock.BackgroundProperty, "CardBackground");

            var numbers = new System.Text.StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                DiffLine line = lines[i];
                int number = sourceSide ? line.LeftNumber : line.RightNumber;
                if (i > 0) numbers.Append('\n');
                numbers.Append(number == 0 ? string.Empty : number.ToString());
            }
            gutter.Text = numbers.ToString();

            box = MakePane(sourceSide, font);
            // Use explicit coordinates: a second ScrollViewer can arrange a
            // short document differently from the RichTextBox's document view.
            gutter.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var gutterViewport = new Canvas
            {
                Width = gutter.DesiredSize.Width,
                ClipToBounds = true,
                IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Top
            };
            gutterViewport.SetResourceReference(Panel.BackgroundProperty, "CardBackground");
            Canvas.SetLeft(gutter, 0);
            Canvas.SetTop(gutter, 0);
            gutterViewport.Children.Add(gutter);
            // Each gutter follows its own pane, including synchronized scrolling
            // and jumps to a hunk. Match the viewport above the horizontal bar.
            box.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((s, e) =>
            {
                if (!(e.OriginalSource is ScrollViewer paneScroll)) return;
                gutterViewport.Height = paneScroll.ViewportHeight;
                Canvas.SetTop(gutter, -paneScroll.VerticalOffset);
            }), true);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(gutterViewport);
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);

            var host = new Border { Child = grid, BorderThickness = new Thickness(1) };
            host.SetResourceReference(Border.BorderBrushProperty, "Line");
            host.SetResourceReference(Border.BackgroundProperty, "CardBackground");
            return host;
        }

        private RichTextBox MakePane(bool sourceSide, FontFamily font)
        {
            var box = new RichTextBox
            {
                IsReadOnly = true,
                IsUndoEnabled = false,
                AcceptsReturn = true,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 0, 6, 0),
                FontFamily = font,
                FontSize = 13,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                AutoWordSelection = false
            };
            box.SetResourceReference(Control.BackgroundProperty, "CardBackground");
            box.SetResourceReference(Control.ForegroundProperty, "Ink");
            box.SetResourceReference(TextBoxBase.SelectionBrushProperty, "ThemeSelected");

            var document = new FlowDocument
            {
                PagePadding = new Thickness(0),
                TextAlignment = TextAlignment.Left,
                LineHeight = DiffLineHeight,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                PageWidth = Math.Max(sharedTextWidth + 24, 200),
                PageHeight = Math.Max(DiffLineHeight * Math.Max(1, lines == null ? 1 : lines.Count) + 24, 200)
            };
            document.SetResourceReference(FlowDocument.BackgroundProperty, "CardBackground");
            document.SetResourceReference(FlowDocument.ForegroundProperty, "Ink");

            foreach (DiffLine line in lines)
            {
                string text = sourceSide ? line.Left : line.Right;
                text = (text ?? string.Empty).Replace("\t", "    ");
                var paragraph = new Paragraph(new Run(string.IsNullOrEmpty(text) ? " " : text))
                {
                    Margin = new Thickness(0),
                    Padding = new Thickness(0),
                    LineHeight = DiffLineHeight,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    TextAlignment = TextAlignment.Left
                };
                string resource = LineBrushKey(line.Kind, sourceSide);
                if (resource != null)
                    paragraph.SetResourceReference(TextElement.BackgroundProperty, resource);
                document.Blocks.Add(paragraph);
            }

            box.Document = document;
            return box;
        }

        private static string LineBrushKey(DiffLineKind kind, bool sourceSide)
        {
            if (kind == DiffLineKind.Removed) return sourceSide ? "DiffRemoved" : "DiffRemovedEmpty";
            if (kind == DiffLineKind.Added) return sourceSide ? "DiffAddedEmpty" : "DiffAdded";
            if (kind == DiffLineKind.Modified) return sourceSide ? "DiffRemoved" : "DiffAdded";
            return null;
        }

        private void DiffPane_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var box = sender as RichTextBox;
            if (box == null || lines == null || lines.Count == 0) return;

            if (leftScroll == null) leftScroll = FindScroll(leftList);
            if (rightScroll == null) rightScroll = FindScroll(rightList);
            ScrollViewer scroll = box == rightList ? rightScroll : leftScroll;
            if (scroll == null) scroll = FindScroll(box);
            if (scroll == null) return;

            double y = e.GetPosition(box).Y + scroll.VerticalOffset;
            int index = (int)Math.Floor(y / DiffLineHeight);
            if (index < 0 || index >= lines.Count) return;
            if (lines[index].Kind == DiffLineKind.Unchanged) return;
            SelectHunk(index, scrollToStart: false);
        }

        private void Jump(int index) => SelectHunk(index, scrollToStart: true);

        private void SelectHunk(int index, bool scrollToStart)
        {
            if (lines == null || index < 0 || index >= lines.Count) return;
            int start = index;
            while (start > 0 && lines[start - 1].Kind != DiffLineKind.Unchanged)
                start--;
            if (lines[start].Kind == DiffLineKind.Unchanged)
            {
                start = index;
                while (start < lines.Count && lines[start].Kind == DiffLineKind.Unchanged)
                    start++;
                if (start >= lines.Count) return;
            }
            int end = start + 1;
            while (end < lines.Count && lines[end].Kind != DiffLineKind.Unchanged)
                end++;

            hunkStart = start;
            hunkEnd = end;
            int hunkIndex = hunks.IndexOf(start);
            if (hunkIndex < 0)
            {
                hunkIndex = hunks.FindLastIndex(item => item <= start);
                if (hunkIndex < 0) hunkIndex = 0;
            }
            current = hunkIndex;

            if (scrollToStart)
            {
                ScrollPaneToLine(leftList, start);
                ScrollPaneToLine(rightList, start);
            }
            Dispatcher.BeginInvoke(new Action(UpdateHunkOverlay), DispatcherPriority.Loaded);
        }

        private void UpdateHunkOverlay()
        {
            UpdateMapSelection();
            if (hunkOverlay == null || hunkStart < 0 || hunkEnd <= hunkStart || lines == null)
            {
                if (hunkOverlay != null) hunkOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            if (leftScroll == null) leftScroll = FindScroll(leftList);
            if (leftScroll == null)
            {
                hunkOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            Point viewTop = leftScroll.TranslatePoint(new Point(0, 0), body);
            double viewHeight = leftScroll.ViewportHeight;
            if (viewHeight <= 0) viewHeight = body.ActualHeight;
            double clipTop = viewTop.Y;
            double clipBottom = viewTop.Y + viewHeight;

            Rect startRect;
            Rect endRect;
            if (!TryGetLineRect(leftList, hunkStart, out startRect)
                || !TryGetLineRect(leftList, Math.Max(hunkStart, hunkEnd - 1), out endRect))
            {
                hunkOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            double hunkTop = leftList.TranslatePoint(new Point(0, startRect.Top), body).Y;
            double hunkBottom = leftList.TranslatePoint(new Point(0, endRect.Bottom), body).Y;

            if (hunkBottom <= clipTop || hunkTop >= clipBottom)
            {
                hunkOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            bool showTopEdge = hunkTop >= clipTop - 0.5;
            bool showBottomEdge = hunkBottom <= clipBottom + 0.5;
            double y = Math.Max(clipTop, hunkTop);
            double bottom = Math.Min(clipBottom, hunkBottom);
            double height = bottom - y;
            if (height < 1)
            {
                hunkOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            hunkOverlay.BorderThickness = new Thickness(2, showTopEdge ? 2 : 0, 2, showBottomEdge ? 2 : 0);
            hunkOverlay.Margin = new Thickness(0, y, 0, Math.Max(0, body.ActualHeight - y - height));
            hunkOverlay.Visibility = Visibility.Visible;
        }

        private static bool TryGetLineRect(RichTextBox box, int index, out Rect rect)
        {
            rect = Rect.Empty;
            if (box?.Document == null || index < 0) return false;
            int i = 0;
            foreach (Block block in box.Document.Blocks)
            {
                if (i == index)
                {
                    Rect start = block.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                    Rect end = block.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
                    if (start.IsEmpty && end.IsEmpty) return false;
                    if (start.IsEmpty) start = end;
                    if (end.IsEmpty) end = start;
                    double glyphTop = Math.Min(start.Top, end.Top);
                    double glyphBottom = Math.Max(start.Bottom, end.Bottom);
                    if (glyphBottom <= glyphTop) glyphBottom = glyphTop + DiffLineHeight;
                    double mid = (glyphTop + glyphBottom) / 2;
                    double top = mid - DiffLineHeight / 2;
                    rect = new Rect(0, top, 1, DiffLineHeight);
                    return true;
                }
                i++;
            }
            return false;
        }

        private static void ScrollPaneToLine(RichTextBox box, int index)
        {
            if (box?.Document == null) return;
            int i = 0;
            foreach (Block block in box.Document.Blocks)
            {
                if (i == index)
                {
                    block.BringIntoView();
                    return;
                }
                i++;
            }
        }

        private static ScrollViewer FindScroll(DependencyObject parent)
        {
            if (parent == null) return null;
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
            if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0) DrawMap();
            UpdateViewport(from);
            UpdateHunkOverlay();
            if (e.VerticalChange == 0 && e.HorizontalChange == 0) return;
            var to = from == leftScroll ? rightScroll : leftScroll; if (to == null) return;
            scrolling = true; to.ScrollToVerticalOffset(from.VerticalOffset); to.ScrollToHorizontalOffset(from.HorizontalOffset); scrolling = false;
        }

        private void UpdateViewport(ScrollViewer scroll)
        {
            if (viewportThumb == null || map == null || scroll == null) return;
            double height = MapHeight;
            double scale = height / MapExtent;
            viewportThumb.Height = Math.Min(height, scroll.ViewportHeight * scale);
            viewportThumb.Width = Math.Max(0, map.ActualWidth - 2);
            Canvas.SetTop(viewportThumb, Math.Max(0, scroll.VerticalOffset * scale));
            UpdateMapSelection();
        }

        private double MapHeight => Math.Max(0, Math.Min(map?.ActualHeight ?? 0,
            leftScroll != null && leftScroll.ViewportHeight > 0 ? leftScroll.ViewportHeight : map?.ActualHeight ?? 0));

        private double MapExtent => Math.Max(1, Math.Max((lines?.Count ?? 0) * DiffLineHeight,
            Math.Max(leftScroll?.ExtentHeight ?? 0, leftScroll?.ViewportHeight ?? 0)));

        private void UpdateMapSelection()
        {
            if (mapSelection == null || viewportThumb == null) return;
            mapSelection.Visibility = Visibility.Collapsed;
            if (hunkStart < 0 || hunkEnd <= hunkStart) return;
            double scale = MapHeight / MapExtent;
            double viewTop = Canvas.GetTop(viewportThumb);
            if (double.IsNaN(viewTop)) return;
            double top = Math.Max(viewTop + 2, hunkStart * DiffLineHeight * scale);
            double bottom = Math.Min(viewTop + viewportThumb.Height - 2, hunkEnd * DiffLineHeight * scale);
            if (bottom <= top) return;
            Canvas.SetLeft(mapSelection, 3);
            Canvas.SetTop(mapSelection, top);
            mapSelection.Width = Math.Max(0, map.ActualWidth - 6);
            mapSelection.Height = bottom - top;
            mapSelection.Visibility = Visibility.Visible;
        }

        private void DragViewport(object sender, DragDeltaEventArgs e)
        {
            if (leftScroll == null || rightScroll == null) return;
            double travel = Math.Max(0, MapHeight - viewportThumb.Height);
            viewportDragTop = Math.Max(0, Math.Min(travel, viewportDragTop + e.VerticalChange));
            double fraction = travel <= 0 ? 0 : viewportDragTop / travel;
            leftScroll.ScrollToVerticalOffset(fraction * leftScroll.ScrollableHeight);
            rightScroll.ScrollToVerticalOffset(fraction * rightScroll.ScrollableHeight);
            e.Handled = true;
        }

        private void DrawMap()
        {
            if (map == null || lines == null) return;
            if (leftScroll == null) leftScroll = FindScroll(leftList);
            if (rightScroll == null) rightScroll = FindScroll(rightList);
            map.Children.Clear();
            foreach (int start in hunks)
            {
                int end = start + 1; while (end < lines.Count && lines[end].Kind != DiffLineKind.Unchanged) end++;
                DiffLineKind kind = lines[start].Kind;
                double mapWidth = Math.Max(0, map.ActualWidth);
                double halfWidth = mapWidth / 2.0;
                double scale = MapHeight / MapExtent;
                double top = start * DiffLineHeight * scale;
                double height = Math.Min(Math.Max(1, (end - start) * DiffLineHeight * scale), Math.Max(0, MapHeight - top));

                if (kind == DiffLineKind.Modified)
                {
                    var leftMarker = new Rectangle { Width = halfWidth, Height = height, ToolTip = string.Format(StringOverlay.Get("Diff_HunkN"), hunks.IndexOf(start) + 1), Cursor = System.Windows.Input.Cursors.Hand };
                    leftMarker.SetResourceReference(Shape.FillProperty, "DiffRemoved");
                    Canvas.SetLeft(leftMarker, 0);
                    Canvas.SetTop(leftMarker, top);
                    leftMarker.MouseLeftButtonDown += (s, e) => Jump(start);
                    map.Children.Add(leftMarker);

                    var rightMarker = new Rectangle { Width = halfWidth, Height = height, ToolTip = string.Format(StringOverlay.Get("Diff_HunkN"), hunks.IndexOf(start) + 1), Cursor = System.Windows.Input.Cursors.Hand };
                    rightMarker.SetResourceReference(Shape.FillProperty, "DiffAdded");
                    Canvas.SetLeft(rightMarker, halfWidth);
                    Canvas.SetTop(rightMarker, top);
                    rightMarker.MouseLeftButtonDown += (s, e) => Jump(start);
                    map.Children.Add(rightMarker);
                }
                else
                {
                    var marker = new Rectangle { Width = halfWidth, Height = height, ToolTip = string.Format(StringOverlay.Get("Diff_HunkN"), hunks.IndexOf(start) + 1), Cursor = System.Windows.Input.Cursors.Hand };
                    marker.SetResourceReference(Shape.FillProperty, kind == DiffLineKind.Added ? "DiffAdded" : "DiffRemoved");
                    Canvas.SetLeft(marker, kind == DiffLineKind.Added ? halfWidth : 0);
                    Canvas.SetTop(marker, top);
                    marker.MouseLeftButtonDown += (s, e) => Jump(start);
                    map.Children.Add(marker);
                }
            }
            if (viewportThumb == null)
            {
                viewportThumb = new Thumb { Cursor = System.Windows.Input.Cursors.SizeNS, ToolTip = StringOverlay.Get("Diff_VisibleRange"), Focusable = false };
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
            if (mapSelection == null)
                mapSelection = new Border
                {
                    BorderBrush = new SolidColorBrush(Color.FromRgb(255, 210, 0)),
                    BorderThickness = new Thickness(2),
                    IsHitTestVisible = false,
                    Visibility = Visibility.Collapsed
                };
            Panel.SetZIndex(mapSelection, 2);
            map.Children.Add(mapSelection);
            if (leftScroll == null) leftScroll = FindScroll(leftList);
            if (rightScroll == null) rightScroll = FindScroll(rightList);
            UpdateViewport(leftScroll);
        }

        private async Task RenderImages()
        {
            var left = ViewModel.SourceImage;
            var right = ViewModel.TargetImage;
            body.ColumnDefinitions[1].Width = new GridLength(12);
            double width = Math.Max(left == null ? 0 : left.PixelWidth, right == null ? 0 : right.PixelWidth);
            double height = Math.Max(left == null ? 0 : left.PixelHeight, right == null ? 0 : right.PixelHeight);
            var leftCanvas = ImageCanvas(left, width, height); var rightCanvas = ImageCanvas(right, width, height);
            leftScroll = ThemedViewer(leftCanvas);
            rightScroll = ThemedViewer(rightCanvas);
            EnableImagePan(leftScroll);
            EnableImagePan(rightScroll);
            body.Children.Add(leftScroll);
            Grid.SetColumn(rightScroll, 2);
            body.Children.Add(rightScroll);
            leftScroll.ScrollChanged += ScrollChanged;
            rightScroll.ScrollChanged += ScrollChanged;
            var zoom = new Slider
            {
                Minimum = 0.05,
                Maximum = 16,
                Value = 1,
                Width = 180,
                ToolTip = "Shared zoom for Source and Target",
                VerticalAlignment = VerticalAlignment.Center,
                Style = TryFindResource("ZoomSlider") as Style
            };
            var zoomLabel = new TextBlock
            {
                MinWidth = 48,
                Margin = new Thickness(8, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold,
                Text = "100%"
            };
            zoomLabel.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            // Fit images and icons to the viewport when first opened.
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

            var wrapper = imageToolbar as Panel;
            if (wrapper != null)
            {
                wrapper.Children.Clear();
                var chrome = new Border
                {
                    CornerRadius = new CornerRadius(18),
                    Padding = new Thickness(10, 4, 12, 4),
                    Margin = new Thickness(0, 0, 8, 8),
                    Child = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
                chrome.SetResourceReference(Border.BackgroundProperty, "CardBackground");
                chrome.SetResourceReference(Border.BorderBrushProperty, "Line");
                chrome.BorderThickness = new Thickness(1);
                var chromeRow = (StackPanel)chrome.Child;
                var zoomTitle = new TextBlock
                {
                    Text = StringOverlay.Get("Diff_Zoom"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 8, 0),
                    FontWeight = FontWeights.SemiBold
                };
                zoomTitle.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                chromeRow.Children.Add(zoomTitle);
                chromeRow.Children.Add(ImageZoomStepButton("\uE738", "Zoom out", () =>
                {
                    fitToWindow = false;
                    zoom.Value = Math.Max(zoom.Minimum, zoom.Value / 1.25);
                }));
                chromeRow.Children.Add(zoom);
                chromeRow.Children.Add(ImageZoomStepButton("\uE710", "Zoom in", () =>
                {
                    fitToWindow = false;
                    zoom.Value = Math.Min(zoom.Maximum, zoom.Value * 1.25);
                }));
                chromeRow.Children.Add(zoomLabel);
                wrapper.Children.Add(chrome);
                AddIconActionButton(wrapper, DifferencerStatusIcons.GetCustomIcon(72), StringOverlay.Get("Diff_Fit"), () => { fitToWindow = true; fit(); });
                AddIconActionButton(wrapper, DifferencerStatusIcons.GetCustomIcon(73), "100%", () => { fitToWindow = false; zoom.Value = 1; });
                wrapper.Visibility = Visibility.Visible;
            }
            zoom.ValueChanged += (s, e) =>
            {
                leftCanvas.LayoutTransform = new ScaleTransform(e.NewValue, e.NewValue);
                rightCanvas.LayoutTransform = new ScaleTransform(e.NewValue, e.NewValue);
                zoomLabel.Text = string.Format("{0:0}%", e.NewValue * 100);
                if (!fitting) fitToWindow = false;
            };
            imageFitHandler = (s, e) => fit();
            body.SizeChanged += imageFitHandler;
            await Dispatcher.InvokeAsync(fit, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private static ScrollViewer ThemedViewer(object content)
        {
            var viewer = new ScrollViewer
            {
                Content = content,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.SizeAll,
                PanningMode = PanningMode.Both
            };
            viewer.SetResourceReference(Control.BackgroundProperty, "CardBackground");
            viewer.SetResourceReference(Control.ForegroundProperty, "Ink");
            viewer.SetResourceReference(Control.BorderBrushProperty, "Line");
            return viewer;
        }

        private void EnableImagePan(ScrollViewer viewer)
        {
            if (viewer == null) return;
            viewer.PreviewMouseLeftButtonDown += ImageViewer_PreviewMouseLeftButtonDown;
            viewer.PreviewMouseMove += ImageViewer_PreviewMouseMove;
            viewer.PreviewMouseLeftButtonUp += ImageViewer_PreviewMouseLeftButtonUp;
            viewer.LostMouseCapture += (s, e) => StopImagePan();
        }

        private void ImageViewer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (FindAncestor<System.Windows.Controls.Primitives.ScrollBar>(e.OriginalSource as DependencyObject) != null)
                return;
            var viewer = sender as ScrollViewer;
            if (viewer == null) return;
            imagePanning = true;
            imagePanLast = e.GetPosition(viewer);
            viewer.CaptureMouse();
            viewer.Cursor = Cursors.SizeAll;
            e.Handled = true;
        }

        private void ImageViewer_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!imagePanning || e.LeftButton != MouseButtonState.Pressed) return;
            var viewer = sender as ScrollViewer;
            if (viewer == null) return;
            Point now = e.GetPosition(viewer);
            Vector delta = now - imagePanLast;
            imagePanLast = now;
            if (leftScroll == null || rightScroll == null) return;
            scrolling = true;
            leftScroll.ScrollToHorizontalOffset(leftScroll.HorizontalOffset - delta.X);
            leftScroll.ScrollToVerticalOffset(leftScroll.VerticalOffset - delta.Y);
            rightScroll.ScrollToHorizontalOffset(rightScroll.HorizontalOffset - delta.X);
            rightScroll.ScrollToVerticalOffset(rightScroll.VerticalOffset - delta.Y);
            scrolling = false;
            e.Handled = true;
        }

        private void ImageViewer_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!imagePanning) return;
            StopImagePan();
            e.Handled = true;
        }

        private void StopImagePan()
        {
            if (!imagePanning) return;
            imagePanning = false;
            if (leftScroll != null && leftScroll.IsMouseCaptured) leftScroll.ReleaseMouseCapture();
            if (rightScroll != null && rightScroll.IsMouseCaptured) rightScroll.ReleaseMouseCapture();
        }

        private static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T match) return match;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private Button ImageZoomStepButton(string glyph, string tip, Action action)
        {
            var button = new Button
            {
                Content = new TextBlock
                {
                    Text = glyph,
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                },
                Width = 28,
                Height = 28,
                Margin = new Thickness(2, 0, 2, 0),
                Background = Brushes.Transparent,
                ToolTip = tip,
                Style = TryFindResource("IconButton") as Style
            };
            button.Click += (s, e) => action();
            return button;
        }

        private static Canvas ImageCanvas(BitmapSource image, double width, double height)
        {
            var canvas = new Canvas { Width = width, Height = height, Cursor = Cursors.SizeAll };
            canvas.SetResourceReference(Panel.BackgroundProperty, "CardBackground");
            if (image != null) canvas.Children.Add(new Image { Source = image, Width = image.PixelWidth, Height = image.PixelHeight, Stretch = Stretch.Fill });
            else canvas.Children.Add(new TextBlock { Text = "Missing", Margin = new Thickness(12) });
            return canvas;
        }

        private Button AddButton(Panel panel, ImageSource icon, string text, ICommand command)
        {
            var image = new Image
            {
                Source = icon,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            var button = new Button
            {
                Content = image,
                Style = TryFindResource("IconButton") as Style,
                Background = Brushes.Transparent,
                ToolTip = text,
                Margin = new Thickness(0, 0, 8, 8)
            };
            System.Windows.Automation.AutomationProperties.SetName(button, text ?? string.Empty);
            button.Command = command;
            panel.Children.Add(button);
            return button;
        }

        private Button AddIconActionButton(Panel panel, ImageSource icon, string text, Action action)
        {
            var image = new Image
            {
                Source = icon,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            var button = new Button
            {
                Content = image,
                Style = TryFindResource("IconButton") as Style,
                Background = Brushes.Transparent,
                ToolTip = text,
                Margin = new Thickness(0, 0, 8, 8)
            };
            System.Windows.Automation.AutomationProperties.SetName(button, text ?? string.Empty);
            button.Click += (s, e) => action();
            panel.Children.Add(button);
            return button;
        }
    }

}
