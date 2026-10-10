using DesktopIniManager.ViewModels;
using DesktopIniManager.Services;
using DesktopIniManager.Properties;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
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

namespace DesktopIniManager.Views;

internal sealed partial class DiffViewWindow : Window
{
    internal DiffViewModel ViewModel { get; }
    private List<int> hunks => ViewModel.Hunks;
    private List<DiffLine> lines => ViewModel.Lines;
    private RichTextBox leftList, rightList;
    private ScrollViewer leftScroll, rightScroll;
    private Canvas map;
    private System.Windows.Shapes.Path hunkOverlay;
    private int hunkStart = -1;
    private int hunkEnd = -1;
    private Thumb viewportThumb;
    private Border mapSelection;
    private double viewportDragTop;
    private double sharedTextWidth;
    private const double DiffLineHeight = 22;
    private string syntaxExtension => System.IO.Path.GetExtension(ViewModel.File?.RelativePath ?? string.Empty).ToLowerInvariant();
    private HwndSource inputSource;
    private int current { get => ViewModel.CurrentHunk; set => ViewModel.CurrentHunk = value; }
    private bool scrolling;
    private bool imagePanning;
    private Point imagePanLast;
    private SizeChangedEventHandler imageFitHandler;
    private Slider imageZoom;

    internal DiffSnapshot Snapshot => ViewModel.Snapshot;

    internal DiffViewWindow(DiffSnapshot snapshot, DiffFile file, string sourcePath = null, string targetPath = null)
    {
        ViewModel = new DiffViewModel(snapshot, file, new UserDialogService(this), sourcePath, targetPath);
        InitializeComponent();
        DataContext = ViewModel;
        if (sourcePath != null) selectedFileText.Visibility = Visibility.Collapsed;
        PreviewKeyDown += DiffViewKeyDown;
        ViewModel.JumpRequested += Jump;
        ViewModel.ReloadRequested = () => LoadContent();
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
            if (Owner == null)
            {
                Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
                if (!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0)
                    SettingsService.SaveDiffWindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, (int)WindowState);
            }
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
            if (Owner == null && SettingsService.TryLoadDiffWindowPlacement(out double x, out double y, out double w, out double h, out int state)
                && double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(w) && double.IsFinite(h))
            {
                var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
                if (desktop.IntersectsWith(new Rect(x, y, w, h)))
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = Math.Clamp(x, desktop.Left, Math.Max(desktop.Left, desktop.Right - MinWidth));
                    Top = Math.Clamp(y, desktop.Top, Math.Max(desktop.Top, desktop.Bottom - 40));
                    Width = Math.Max(MinWidth, Math.Min(w, desktop.Width));
                    Height = Math.Max(MinHeight, Math.Min(h, desktop.Height));
                    if (state == (int)WindowState.Maximized) WindowState = WindowState.Maximized;
                }
            }
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

    internal void MatchOwnerSize()
    {
        Window owner = Owner;
        if (owner == null) return;
        if (owner.WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Maximized;
            return;
        }

        double width = owner.ActualWidth > 0 ? owner.ActualWidth : owner.Width;
        double height = owner.ActualHeight > 0 ? owner.ActualHeight : owner.Height;
        if (owner.WindowState == WindowState.Minimized)
        {
            width = owner.RestoreBounds.Width;
            height = owner.RestoreBounds.Height;
        }
        if (width > 0) Width = Math.Max(MinWidth, width);
        if (height > 0) Height = Math.Max(MinHeight, height);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
    }

    private void Body_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ViewModel.IsImage && imageZoom != null)
        {
            ChangeImageZoom(e.Delta / 120.0);
            e.Handled = true;
        }
        else if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            e.Handled = ScrollHorizontally(-e.Delta);
        else
            e.Handled = ScrollVertically(-e.Delta * DiffLineHeight * 3 / 120.0);
    }

    private void DiffViewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || Keyboard.FocusedElement is TextBox) return;
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            ICommand command;
            switch (e.Key)
            {
                case Key.Up: command = ViewModel.PreviousHunkCommand; break;
                case Key.Down: command = ViewModel.NextHunkCommand; break;
                case Key.Left: command = ViewModel.PreviousFileCommand; break;
                case Key.Right: command = ViewModel.NextFileCommand; break;
                case Key.D0:
                case Key.NumPad0:
                    if (ViewModel.IsImage) return;
                    command = ToggleDisplayCommand;
                    break;
                default: return;
            }
            e.Handled = true;
            if (command.CanExecute(null)) command.Execute(null);
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        switch (e.Key)
        {
            case Key.Up: e.Handled = ScrollVertically(-DiffLineHeight); break;
            case Key.Down: e.Handled = ScrollVertically(DiffLineHeight); break;
            case Key.Left: e.Handled = ScrollHorizontally(-120); break;
            case Key.Right: e.Handled = ScrollHorizontally(120); break;
            case Key.PageUp:
            case Key.PageDown:
                int direction = e.Key == Key.PageUp ? -1 : 1;
                if (ViewModel.IsImage && imageZoom != null)
                {
                    ChangeImageZoom(-direction);
                    e.Handled = true;
                }
                else
                {
                    if (leftScroll == null) leftScroll = FindScroll(leftList);
                    e.Handled = ScrollVertically(direction * (leftScroll?.ViewportHeight ?? 0));
                }
                break;
        }
    }

    private void ChangeImageZoom(double steps)
    {
        imageZoom.Value = Math.Max(imageZoom.Minimum,
            Math.Min(imageZoom.Maximum, imageZoom.Value * Math.Pow(1.25, steps)));
    }

    private bool ScrollVertically(double delta)
    {
        if (leftScroll == null) leftScroll = FindScroll(leftList);
        if (rightScroll == null) rightScroll = FindScroll(rightList);
        if (leftScroll == null || rightScroll == null) return false;
        double offset = leftScroll.VerticalOffset + delta;
        leftScroll.ScrollToVerticalOffset(offset);
        rightScroll.ScrollToVerticalOffset(offset);
        return true;
    }

    private ICommand toggleDisplayCommand;
    private ICommand ToggleDisplayCommand => toggleDisplayCommand ??=
        new AsyncRelayCommand(async () =>
        {
            if (ViewModel.ToggleDifferencesOnly()) await LoadContent(false);
        }, ViewModel.ReportError);

    private ICommand reloadCommand;
    private ICommand ReloadCommand => reloadCommand ??= new AsyncRelayCommand(ViewModel.ReloadFromDiskAsync, ViewModel.ReportError);

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
        if (!ViewModel.IsImage)
            AddButton(actionsPanel, DifferencerStatusIcons.GetCustomIcon(ViewModel.DifferencesOnly ? 109 : 110),
                StringOverlay.Get(ViewModel.DifferencesOnly ? "Diff_ShowAllLines" : "Diff_ShowDifferences"),
                ToggleDisplayCommand);
        if (!ViewModel.IsImage)
        {
            AddComparisonButton(ViewModel.CompareCase ? 116 : 119, "Compare case", () => ViewModel.CompareCase = !ViewModel.CompareCase);
            AddComparisonButton(ViewModel.CompareSpaces ? 117 : 120, "Compare spaces / tabs", () => ViewModel.CompareSpaces = !ViewModel.CompareSpaces);
            AddComparisonButton(ViewModel.CompareNewlines ? 118 : 121, "Compare line endings", () => ViewModel.CompareNewlines = !ViewModel.CompareNewlines);
        }
        AddButton(actionsPanel, DifferencerStatusIcons.GetCustomIcon(69), "Reload", ReloadCommand);
        AddButton(fileNavigationPanel, DifferencerStatusIcons.GetCustomIcon(40), StringOverlay.Get("Diff_PreviousFile"), ViewModel.PreviousFileCommand);
        AddButton(fileNavigationPanel, DifferencerStatusIcons.GetCustomIcon(41), StringOverlay.Get("Diff_NextFile"), ViewModel.NextFileCommand);
    }

    private void AddComparisonButton(int icon, string label, Action toggle)
    {
        AddButton(actionsPanel, DifferencerStatusIcons.GetCustomIcon(icon), label,
            new AsyncRelayCommand(async () =>
            {
                if (!ViewModel.CanChangeComparison) return;
                toggle();
                await LoadContent();
            }, ViewModel.ReportError));
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

    private const double StatusIconHeight = 36;

    private void UpdateSideStatusIcons()
    {
        FillStatusIcons(sourceStatusIcons, true);
        FillStatusIcons(targetStatusIcons, false);
    }

    private void FillStatusIcons(Panel panel, bool sourceSide)
    {
        if (panel == null) return;
        panel.Children.Clear();
        DiffFile file = ViewModel.File;
        if (file == null) return;
        foreach (int index in StatusIconIndexes(file, sourceSide))
            panel.Children.Add(StatusIcon(index));
        var document = sourceSide ? ViewModel.SourceText : ViewModel.TargetText;
        if (!ViewModel.IsImage && document != null)
        {
            var bom = StatusIcon(115);
            bom.Opacity = document.HasBom ? 1 : 0.25;
            bom.ToolTip = document.HasBom ? "BOM" : "No BOM";
            panel.Children.Add(bom);
            string endings = document.Newlines;
            if (endings == "LF" || endings == "CRLF" || endings.Contains('/'))
            {
                var marker = StatusIcon(endings.Contains('/') ? 122 : endings == "LF" ? 113 : 114);
                marker.ToolTip = endings;
                panel.Children.Add(marker);
            }
            else if (endings.Length == 0)
            {
                var marker = StatusIcon(123);
                marker.ToolTip = "No line endings / missing file";
                panel.Children.Add(marker);
            }
            else panel.Children.Add(new TextBlock { Text = endings, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Line endings", Margin = new Thickness(4, 0, 0, 0) });
        }
        panel.Children.Add(StatusIcon(sourceSide ? 111 : 112));
    }

    private void UpdateEncodingHeaders()
    {
        void Update(TextBlock header, bool source)
        {
            header.ToolTip = ViewModel.GetPath(source);
            header.Inlines.Clear();
            header.Inlines.Add(new Run(source ? ViewModel.SourceHeader : ViewModel.TargetHeader));
            var document = source ? ViewModel.SourceText : ViewModel.TargetText;
            string label = ViewModel.IsImage ? (source ? ViewModel.SourceImageFormat : ViewModel.TargetImageFormat) : document?.EncodingName;
            if (label == null) return;
            var color = source ? Color.FromRgb(255, 210, 64) : Color.FromRgb(64, 190, 255);
            bool differs = ViewModel.IsImage
                ? ViewModel.SourceImageFormat != null && ViewModel.TargetImageFormat != null && ViewModel.SourceImageFormat != ViewModel.TargetImageFormat
                : ViewModel.SourceText?.EncodingName != ViewModel.TargetText?.EncodingName;
            var brush = new SolidColorBrush(color);
            header.Inlines.Add(new InlineUIContainer(new Border
            {
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1.5),
                BorderBrush = brush,
                Background = differs ? brush : (Brush)FindResource("CardBackground"),
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(8, 0, 0, 0),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = color, BlurRadius = 7, ShadowDepth = 0, Opacity = 0.65 },
                Child = new TextBlock
                {
                    Text = label,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = differs ? Brushes.Black : brush
                }
            })
            { BaselineAlignment = BaselineAlignment.Center });
        }
        Update(sourceHeader, true);
        Update(targetHeader, false);
    }

    private static IEnumerable<int> StatusIconIndexes(DiffFile file, bool sourceSide)
    {
        DiffStamp own = sourceSide ? file.Source : file.Target;
        DiffStamp other = sourceSide ? file.Target : file.Source;
        if (own == null)
        {
            yield return 12;
            yield break;
        }
        if (other == null)
        {
            yield return 15;
            yield break;
        }
        if (file.Kind == DiffKind.Same)
        {
            yield return 11;
            yield break;
        }
        if (own.ModifiedUtcSeconds != other.ModifiedUtcSeconds)
            yield return own.ModifiedUtcSeconds < other.ModifiedUtcSeconds ? 14 : 13;
        if (own.Size != other.Size)
            yield return own.Size < other.Size ? 124 : 125;
    }

    private static Image StatusIcon(int index)
    {
        var image = new Image
        {
            Source = DifferencerStatusIcons.GetCustomIcon(index),
            Height = StatusIconHeight,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = StatusIconTip(index)
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return image;
    }

    private static string StatusIconTip(int index)
    {
        switch (index)
        {
            case 11: return "SAME";
            case 12: return "MISS";
            case 13: return "NEW";
            case 14: return "OLD";
            case 15: return "ONLY";
            case 16: return "SIZE";
            case 124: return "SMALL";
            case 125: return "LARGE";
            case 111: return "Source";
            case 112: return "Target";
            default: return string.Empty;
        }
    }

    private async Task LoadContent(bool reload = true, bool jumpToFirst = true)
    {
        DetachImageFitHandler();
        ResetImageNavigation();
        if (imageToolbar is Panel p)
        {
            p.Children.Clear();
            p.Visibility = Visibility.Collapsed;
        }
        body.Children.Clear();
        mapColumn.Width = new GridLength(34);
        BuildToolbar();
        UpdateSideStatusIcons();
        leftList = rightList = null;
        leftScroll = rightScroll = null;
        imageZoom = null;
        map = null;
        hunkOverlay = null;
        hunkStart = hunkEnd = -1;
        viewportThumb = null;
        mapSelection = null;
        try
        {
            if (reload && !await ViewModel.LoadContentAsync()) return;
            if (!IsLoaded) return;
            UpdateSideStatusIcons();
            UpdateEncodingHeaders();
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
            hunkOverlay = new System.Windows.Shapes.Path
            {
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 2, 2 },
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
                SnapsToDevicePixels = true
            };
            hunkOverlay.Stroke = new SolidColorBrush(Color.FromRgb(255, 210, 0));
            Grid.SetColumnSpan(hunkOverlay, 3);
            Panel.SetZIndex(hunkOverlay, 2);
            body.Children.Add(hunkOverlay);
            leftList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(ScrollChanged));
            rightList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(ScrollChanged));
            leftList.PreviewMouseLeftButtonDown += DiffPane_PreviewMouseLeftButtonDown;
            rightList.PreviewMouseLeftButtonDown += DiffPane_PreviewMouseLeftButtonDown;
            if (jumpToFirst && hunks.Count > 0)
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
        box.Tag = gutterViewport;
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

        var host = new Border { Child = grid, BorderThickness = new Thickness(5) };
        host.SetResourceReference(Border.BorderBrushProperty, sourceSide ? "SourceColor" : "TargetColor");
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
            Padding = new Thickness(0),
            FontFamily = font,
            FontSize = 13,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            AutoWordSelection = false
        };
        box.SetResourceReference(Control.BackgroundProperty, "CardBackground");
        box.SetResourceReference(Control.ForegroundProperty, "Ink");
        box.SetResourceReference(TextBoxBase.SelectionBrushProperty, "ThemeSelected");

        box.SizeChanged += (_, _) =>
        {
            box.Document.PageWidth = Math.Max(sharedTextWidth + 24, Math.Max(200, box.ActualWidth));
            UpdateHunkOverlay();
        };
        PopulatePane(box, sourceSide);
        return box;
    }

    private void PopulatePane(RichTextBox box, bool sourceSide)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            TextAlignment = TextAlignment.Left,
            LineHeight = DiffLineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            PageWidth = Math.Max(sharedTextWidth + 24, Math.Max(200, box.ActualWidth)),
            PageHeight = Math.Max(DiffLineHeight * Math.Max(1, lines == null ? 1 : lines.Count) + 24, 200)
        };
        document.SetResourceReference(FlowDocument.BackgroundProperty, "CardBackground");
        document.SetResourceReference(FlowDocument.ForegroundProperty, "Ink");
        bool blockComment = false;
        int originalRow = 0;
        foreach (DiffLine line in lines)
        {
            string text = sourceSide ? line.Left : line.Right;
            text = (text ?? string.Empty).Replace("\t", "    ");
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0),
                Padding = new Thickness(6, 0, 6, 0),
                LineHeight = DiffLineHeight,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextAlignment = TextAlignment.Left
            };
            if (line.FilteredLineCount > 0)
            {
                paragraph.Cursor = Cursors.Hand;
                paragraph.ToolTip = "Click to expand unchanged lines";
                // Hidden text still determines the syntax state of the next visible line.
                if (syntaxExtension == ".cs")
                    for (int i = 0; i < line.FilteredLineCount; i++)
                    {
                        DiffLine hidden = ViewModel.AllLines[originalRow + i];
                        AddCSharpRuns(null, (sourceSide ? hidden.Left : hidden.Right) ?? string.Empty, ref blockComment);
                    }
                originalRow += line.FilteredLineCount;
                paragraph.Inlines.Add(new Run(text));
                paragraph.FontSize = 10;
                paragraph.SetResourceReference(TextElement.ForegroundProperty, "Muted");
                paragraph.SetResourceReference(TextElement.BackgroundProperty,
                                TryFindResource("DiffFilteredBackground") != null
                                    ? "DiffFilteredBackground"
                                    : "HeaderBackground");
            }
            else
            {
                if (ViewModel.DifferencesOnly && line.Kind == DiffLineKind.Unchanged)
                {
                    paragraph.Cursor = Cursors.Hand;
                    paragraph.ToolTip = "Click to collapse unchanged lines";
                }
                AddSyntaxRuns(paragraph, string.IsNullOrEmpty(text) ? " " : text, ref blockComment);
                originalRow++;
            }
            string resource = LineBrushKey(line.Kind, sourceSide);
            if (resource != null)
                paragraph.SetResourceReference(TextElement.BackgroundProperty, resource);
            document.Blocks.Add(paragraph);
        }

        box.Document = document;
    }

    private static readonly HashSet<string> CSharpKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract","as","base","bool","break","byte","case","catch","char","checked","class","const","continue",
            "decimal","default","delegate","do","double","else","enum","event","explicit","extern","false","finally",
            "fixed","float","for","foreach","goto","if","implicit","in","int","interface","internal","is","lock","long",
            "namespace","new","null","object","operator","out","override","params","private","protected","public","readonly",
            "ref","return","sbyte","sealed","short","sizeof","stackalloc","static","string","struct","switch","this",
            "throw","true","try","typeof","uint","ulong","unchecked","unsafe","ushort","using","virtual","void","volatile",
            "while","async","await","record","init","required","var","dynamic","get","set","value","yield"
        };

    private void AddSyntaxRuns(Paragraph paragraph, string text, ref bool blockComment)
    {
        if (syntaxExtension == ".cs") { AddCSharpRuns(paragraph, text, ref blockComment); return; }
        if (syntaxExtension == ".xaml" || syntaxExtension == ".xml" || syntaxExtension == ".csproj" ||
            syntaxExtension == ".props" || syntaxExtension == ".targets")
        { AddXmlRuns(paragraph, text); return; }
        if (syntaxExtension == ".json") { AddJsonRuns(paragraph, text); return; }
        paragraph.Inlines.Add(new Run(text));
    }

    private void AddCSharpRuns(Paragraph paragraph, string text, ref bool blockComment)
    {
        int i = 0, plain = 0;
        while (i < text.Length)
        {
            if (blockComment)
            {
                int end = text.IndexOf("*/", i, StringComparison.Ordinal);
                AddColoredRun(paragraph, text.Substring(i, end < 0 ? text.Length - i : end + 2 - i), "comment");
                if (end < 0) return;
                i = plain = end + 2; blockComment = false; continue;
            }
            if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '/')
            {
                AddPlainRun(paragraph, text, plain, i - plain); AddColoredRun(paragraph, text.Substring(i), "comment"); return;
            }
            if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*')
            {
                AddPlainRun(paragraph, text, plain, i - plain);
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) { AddColoredRun(paragraph, text.Substring(i), "comment"); blockComment = true; return; }
                AddColoredRun(paragraph, text.Substring(i, end + 2 - i), "comment"); i = plain = end + 2; continue;
            }
            if (text[i] == '"' || text[i] == '\'')
            {
                AddPlainRun(paragraph, text, plain, i - plain); char quote = text[i]; int start = i++;
                while (i < text.Length) { if (text[i] == '\\') { i += Math.Min(2, text.Length - i); continue; } if (text[i++] == quote) break; }
                AddColoredRun(paragraph, text.Substring(start, i - start), "string"); plain = i; continue;
            }
            if (char.IsLetter(text[i]) || text[i] == '_')
            {
                int start = i++; while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                string word = text.Substring(start, i - start);
                if (CSharpKeywords.Contains(word)) { AddPlainRun(paragraph, text, plain, start - plain); AddColoredRun(paragraph, word, "keyword"); plain = i; }
                continue;
            }
            if (char.IsDigit(text[i]))
            {
                AddPlainRun(paragraph, text, plain, i - plain); int start = i++;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '.' || text[i] == '_')) i++;
                AddColoredRun(paragraph, text.Substring(start, i - start), "number"); plain = i; continue;
            }
            i++;
        }
        AddPlainRun(paragraph, text, plain, text.Length - plain);
    }

    private void AddXmlRuns(Paragraph paragraph, string text)
    {
        int pos = 0;
        foreach (Match match in Regex.Matches(text, "<!--.*?-->|</?[^>]+>|&[A-Za-z0-9#]+;"))
        {
            AddPlainRun(paragraph, text, pos, match.Index - pos);
            AddColoredRun(paragraph, match.Value, match.Value.StartsWith("<!--", StringComparison.Ordinal) ? "comment" : "keyword");
            pos = match.Index + match.Length;
        }
        AddPlainRun(paragraph, text, pos, text.Length - pos);
    }

    private void AddJsonRuns(Paragraph paragraph, string text)
    {
        int pos = 0;
        foreach (Match match in Regex.Matches(text, @"""(?:\\.|[^""\\])*""|\b(?:true|false|null)\b|-?\b\d+(?:\.\d+)?(?:[eE][+-]?\d+)?\b"))
        {
            AddPlainRun(paragraph, text, pos, match.Index - pos);
            string kind = match.Value.StartsWith("\"", StringComparison.Ordinal) ? "string" :
                (match.Value == "true" || match.Value == "false" || match.Value == "null" ? "keyword" : "number");
            AddColoredRun(paragraph, match.Value, kind);
            pos = match.Index + match.Length;
        }
        AddPlainRun(paragraph, text, pos, text.Length - pos);
    }

    private static void AddPlainRun(Paragraph paragraph, string text, int start, int length)
    {
        if (paragraph != null && length > 0) paragraph.Inlines.Add(new Run(text.Substring(start, length)));
    }

    private void AddColoredRun(Paragraph paragraph, string text, string kind)
    {
        if (paragraph == null) return;
        var run = new Run(text);
        bool dark = IsDarkBackground();
        Color color = kind == "comment" ? (dark ? Color.FromRgb(106, 153, 85) : Color.FromRgb(0, 128, 0)) :
                      kind == "string" ? (dark ? Color.FromRgb(206, 145, 120) : Color.FromRgb(163, 21, 21)) :
                      kind == "number" ? (dark ? Color.FromRgb(181, 206, 168) : Color.FromRgb(9, 134, 88)) :
                                          (dark ? Color.FromRgb(86, 156, 214) : Color.FromRgb(0, 0, 255));
        run.Foreground = new SolidColorBrush(color);
        paragraph.Inlines.Add(run);
    }

    private bool IsDarkBackground()
    {
        var brush = TryFindResource("CardBackground") as SolidColorBrush;
        if (brush == null) return true;
        Color c = brush.Color;
        return (c.R * 299 + c.G * 587 + c.B * 114) / 1000 < 128;
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
        if (e.OriginalSource is Visual visual && FindAncestor<ScrollBar>(visual) != null) return;
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
        if (ViewModel.DifferencesOnly && lines[index].Kind == DiffLineKind.Unchanged)
        {
            e.Handled = true;
            double vertical = scroll.VerticalOffset, horizontal = scroll.HorizontalOffset;
            int selectedHunk = current;
            if (lines[index].FilteredLineCount > 0)
            {
                if (!ViewModel.ExpandFilteredLines(index)) return;
            }
            else
            {
                int collapsedIndex = ViewModel.CollapseExpandedLines(index);
                if (collapsedIndex < 0) return;
                vertical = Math.Min(vertical, collapsedIndex * DiffLineHeight);
            }
            RefreshFoldedPanes(vertical, horizontal, selectedHunk);
            return;
        }
        if (lines[index].Kind == DiffLineKind.Unchanged) return;
        SelectHunk(index, scrollToStart: false);
    }

    // Keep the pane controls, toolbar and overlay alive. Complete both document
    // updates and scroll restoration in one dispatcher turn, before rendering.
    internal void RefreshFoldedPanes(double vertical, double horizontal, int selectedHunk)
    {
        using (Dispatcher.DisableProcessing())
        {
            scrolling = true;
            try
            {
                sharedTextWidth = Math.Max(sharedTextWidth, MeasureSharedTextWidth());
                PopulatePane(leftList, true);
                PopulatePane(rightList, false);
                UpdateFoldedGutter(leftList, true);
                UpdateFoldedGutter(rightList, false);
                body.UpdateLayout();
                leftScroll = FindScroll(leftList);
                rightScroll = FindScroll(rightList);
                leftScroll?.ScrollToVerticalOffset(vertical);
                rightScroll?.ScrollToVerticalOffset(vertical);
                leftScroll?.ScrollToHorizontalOffset(horizontal);
                rightScroll?.ScrollToHorizontalOffset(horizontal);
                body.UpdateLayout();
                if (selectedHunk >= 0 && selectedHunk < hunks.Count)
                    SelectHunk(hunks[selectedHunk], false);
                else hunkStart = hunkEnd = -1;
                DrawMap();
                UpdateHunkOverlay();
            }
            finally { scrolling = false; }
        }
    }

    private void UpdateFoldedGutter(RichTextBox pane, bool source)
    {
        var viewport = (Canvas)pane.Tag;
        var gutter = (TextBlock)viewport.Children[0];
        var numbers = new System.Text.StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0) numbers.Append('\n');
            int number = source ? lines[i].LeftNumber : lines[i].RightNumber;
            if (number > 0) numbers.Append(number);
        }
        gutter.Text = numbers.ToString();
        gutter.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        viewport.Width = Math.Max(viewport.Width, gutter.DesiredSize.Width);
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

        // Paragraph backgrounds use fixed row boundaries, not glyph bounds.
        // Glyph ascenders/descenders shift the outline below the painted top.
        double hunkTop = viewTop.Y + hunkStart * DiffLineHeight - leftScroll.VerticalOffset;
        double hunkBottom = viewTop.Y + hunkEnd * DiffLineHeight - leftScroll.VerticalOffset;
        var sourceHost = FindAncestor<Border>(leftList);
        var targetHost = FindAncestor<Border>(rightList);
        if (sourceHost == null || targetHost == null) return;
        double innerLeft = sourceHost.TranslatePoint(new Point(sourceHost.BorderThickness.Left, 0), body).X;
        double innerRight = targetHost.TranslatePoint(new Point(targetHost.ActualWidth - targetHost.BorderThickness.Right, 0), body).X;
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

        // Keep the entire stroke inside the arranged Path; a stroke centered
        // on its bottom boundary was clipped to half thickness.
        var dpi = VisualTreeHelper.GetDpi(body);
        y = Math.Round(y * dpi.DpiScaleY) / dpi.DpiScaleY;
        bottom = Math.Round(bottom * dpi.DpiScaleY) / dpi.DpiScaleY;
        height = bottom - y;
        double inset = hunkOverlay.StrokeThickness / 2;
        double topEdge = inset, bottomEdge = Math.Max(inset, height - inset);
        var geometry = new StreamGeometry();
        using (var drawing = geometry.Open())
        {
            double left = innerLeft + inset;
            double right = Math.Max(left, innerRight - inset);
            drawing.BeginFigure(new Point(left, topEdge), false, false);
            drawing.LineTo(new Point(left, bottomEdge), true, false);
            if (showBottomEdge) drawing.LineTo(new Point(right, bottomEdge), true, false);
            else drawing.BeginFigure(new Point(right, bottomEdge), false, false);
            drawing.LineTo(new Point(right, topEdge), true, false);
            if (showTopEdge) drawing.LineTo(new Point(left, topEdge), true, false);
        }
        hunkOverlay.Data = geometry;
        hunkOverlay.Margin = new Thickness(0, y, 0, Math.Max(0, body.ActualHeight - y - height));
        hunkOverlay.Visibility = Visibility.Visible;
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
        UpdateImageNavigation();
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
            viewportThumb = new Thumb { Cursor = System.Windows.Input.Cursors.Hand, ToolTip = StringOverlay.Get("Diff_VisibleRange"), Focusable = false };
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(0));
            border.SetValue(Border.BackgroundProperty, new DynamicResourceExtension("Ink"));
            border.SetValue(UIElement.OpacityProperty, 0.20);
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
        var leftCanvas = ImageCanvas(left, width, height, out var leftPixelGrid);
        var rightCanvas = ImageCanvas(right, width, height, out var rightPixelGrid);
        leftScroll = ThemedViewer(leftCanvas, true, left == null);
        rightScroll = ThemedViewer(rightCanvas, false, right == null);
        EnableImagePan(leftScroll);
        EnableImagePan(rightScroll);
        body.Children.Add(ImagePaneHost(leftScroll, true, left == null));
        var rightHost = ImagePaneHost(rightScroll, false, right == null);
        Grid.SetColumn(rightHost, 2);
        body.Children.Add(rightHost);
        leftScroll.ScrollChanged += ScrollChanged;
        rightScroll.ScrollChanged += ScrollChanged;
        BuildImageNavigation(width, height);
        var zoom = imageZoom = new Slider
        {
            Minimum = 0.05,
            Maximum = 16,
            Value = 1,
            Width = 120,
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
            imageControlRow.Visibility = Visibility.Visible;
        }
        zoom.ValueChanged += (s, e) =>
        {
            leftCanvas.LayoutTransform = new ScaleTransform(e.NewValue, e.NewValue);
            rightCanvas.LayoutTransform = new ScaleTransform(e.NewValue, e.NewValue);
            zoomLabel.Text = string.Format("{0:0}%", e.NewValue * 100);
            var gridVisibility = e.NewValue >= zoom.Maximum - 0.0001
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (leftPixelGrid != null) leftPixelGrid.Visibility = gridVisibility;
            if (rightPixelGrid != null) rightPixelGrid.Visibility = gridVisibility;
            if (!fitting) fitToWindow = false;
            Dispatcher.BeginInvoke(new Action(UpdateImageNavigation), DispatcherPriority.Loaded);
        };
        imageFitHandler = (s, e) => { fit(); UpdateImageNavigation(); };
        body.SizeChanged += imageFitHandler;
        await Dispatcher.InvokeAsync(fit, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static ScrollViewer ThemedViewer(object content, bool sourceSide, bool missing)
    {
        var viewer = new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.SizeAll,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Top,
            PanningMode = PanningMode.Both
        };
        viewer.SetResourceReference(Control.BackgroundProperty, "CardBackground");
        viewer.SetResourceReference(Control.ForegroundProperty, "Ink");
        viewer.SetResourceReference(Control.BorderBrushProperty, sourceSide ? "SourceColor" : "TargetColor");
        if (missing)
        {
            viewer.Background = MissingImageBrush;
            viewer.BorderBrush = MissingImageBrush;
        }
        return viewer;
    }

    private static Border ImagePaneHost(ScrollViewer viewer, bool source, bool missing)
    {
        // The system ScrollViewer template may ignore BorderThickness.
        // Use the same explicit outer Border as the text panes.
        var host = new Border { Child = viewer, BorderThickness = new Thickness(5) };
        host.SetResourceReference(Border.BorderBrushProperty, source ? "SourceColor" : "TargetColor");
        host.SetResourceReference(Border.BackgroundProperty, "CardBackground");
        if (missing) host.Background = host.BorderBrush = MissingImageBrush;
        return host;
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
        SetImagePointDetails(e.GetPosition((Canvas)viewer.Content), true);
        imagePanning = true;
        imagePanLast = e.GetPosition(viewer);
        viewer.CaptureMouse();
        viewer.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void ImageViewer_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        var viewer = sender as ScrollViewer;
        if (viewer == null) return;
        SetImagePointDetails(e.GetPosition((Canvas)viewer.Content), false);
        if (!imagePanning || e.LeftButton != MouseButtonState.Pressed) return;
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
        if (sender is ScrollViewer viewer && viewer.Content is Canvas canvas)
            SetImagePointDetails(e.GetPosition(canvas), false);
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

    private static readonly Brush MissingImageBrush = new SolidColorBrush(Color.FromRgb(10, 15, 21));
    private static readonly Brush PixelLineGridBrush = CreatePixelLineGridBrush();

    private static Brush CreatePixelLineGridBrush()
    {
        // One source pixel per tile. The line is 1/16px so it becomes 1 DIP at 1600%.
        // Right and bottom edges only, so the pixel interior stays visible.
        const double line = 1.0 / 16.0;
        var edges = new GeometryGroup();
        edges.Children.Add(new RectangleGeometry(new Rect(1 - line, 0, line, 1)));
        edges.Children.Add(new RectangleGeometry(new Rect(0, 1 - line, 1, line)));
        var drawing = new GeometryDrawing
        {
            Brush = new SolidColorBrush(Color.FromArgb(150, 186, 214, 232)),
            Geometry = edges
        };
        drawing.Freeze();
        var brush = new DrawingBrush
        {
            Drawing = drawing,
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 1, 1),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 1, 1),
            ViewboxUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top
        };
        brush.Freeze();
        return brush;
    }

    private static Canvas ImageCanvas(BitmapSource image, double width, double height, out Canvas pixelGrid)
    {
        pixelGrid = null;
        var canvas = new Canvas { Width = width, Height = height, Cursor = Cursors.SizeAll };
        canvas.SetResourceReference(Panel.BackgroundProperty, "CardBackground");
        if (image != null)
        {
            var picture = new Image
            {
                Source = image,
                Width = image.PixelWidth,
                Height = image.PixelHeight,
                Stretch = Stretch.Fill
            };
            RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.NearestNeighbor);
            canvas.Children.Add(picture);
            pixelGrid = new Canvas
            {
                Width = image.PixelWidth,
                Height = image.PixelHeight,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
                Background = PixelLineGridBrush
            };
            Panel.SetZIndex(pixelGrid, 2);
            canvas.Children.Add(pixelGrid);
        }
        else canvas.Background = MissingImageBrush;
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
