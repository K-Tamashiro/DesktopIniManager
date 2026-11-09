using DesktopIniManager.Properties;
using DesktopIniManager.Services;
using DesktopIniManager.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.Windows.Shapes.Path;

namespace DesktopIniManager.Views;

internal sealed partial class DiffViewWindow
{
    private double DiffLineHeight => Math.Ceiling(Math.Max(22, _textFontSize * _textFontFamily.LineSpacing + 4));

    private const double StatusIconHeight = 36;

    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
        "continue",
        "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false",
        "finally",
        "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is",
        "lock", "long",
        "namespace", "new", "null", "object", "operator", "out", "override", "params", "private", "protected", "public",
        "readonly",
        "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch",
        "this",
        "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void",
        "volatile",
        "while", "async", "await", "record", "init", "required", "var", "dynamic", "get", "set", "value", "yield"
    };

    private static readonly Brush MissingImageBrush = new SolidColorBrush(Color.FromRgb(10, 15, 21));
    private static readonly Brush PixelLineGridBrush = CreatePixelLineGridBrush();
    private readonly ClipboardInputStore _clipboardInputs = new();
    private readonly Dictionary<bool, RenderedPane> _renderedPanes = new();
    private ICommand _clearInputsCommand;
    private bool _clipboardSource = true;
    private int _hunkEnd = -1;
    private Path _hunkOverlay;
    private int _hunkStart = -1;
    private SizeChangedEventHandler _imageFitHandler;
    private Point _imagePanLast;
    private bool _imagePanning;
    private Slider _imageZoom;
    private HwndSource _inputSource;
    private RichTextBox _leftList, _rightList;
    private ScrollViewer _leftScroll, _rightScroll;
    private Canvas _map;
    private Border _mapSelection;

    private ICommand _reloadCommand;
    private bool _renderingContent;
    private bool _scrolling;
    private double _sharedTextWidth;
    private bool _sourceImageXorMode = SettingsService.LoadDiffFlag("source-image-xor", false);
    private bool _targetImageXorMode = SettingsService.LoadDiffFlag("target-image-xor", false);

    private ICommand _toggleDisplayCommand;
    private double _viewportDragTop;
    private Thumb _viewportThumb;
    private bool _windowClosed;

    /// <summary>
    ///     DIFF ビュー ウィンドウを作成し、ViewModel、フォント、表示設定、キー操作、ドラッグ アンド ドロップを初期化します。
    /// </summary>
    /// <param name="snapshot">比較スナップショット。</param>
    /// <param name="file">表示するファイル。</param>
    /// <param name="sourcePath">単独で比較する場合の比較元のパス。</param>
    /// <param name="targetPath">単独で比較する場合の比較先のパス。</param>
    internal DiffViewWindow(DiffSnapshot snapshot, DiffFile file, string sourcePath = null, string targetPath = null)
    {
        ViewModel = new DiffViewModel(snapshot, file, new UserDialogService(this), sourcePath, targetPath);
        InitializeComponent();
        InitializeTextFontControls();
        // The compact-fold state is saved on every change and restored for the next window.
        _compactSingleFolds = SettingsService.LoadDiffFlag("compact-folds", false);
        CompactFoldsToggle.IsExpanded = !_compactSingleFolds;
        CompactFoldsToggle.Expanded += (_, _) =>
        {
            SettingsService.SaveDiffFlag("compact-folds", false);
            SetCompactSingleFolds(false);
        };
        CompactFoldsToggle.Collapsed += (_, _) =>
        {
            SettingsService.SaveDiffFlag("compact-folds", true);
            SetCompactSingleFolds(true);
        };
        // The fold state is saved on every change and restored for the next window.
        ToolbarToggle.IsExpanded = SettingsService.LoadDiffFlag("toolbar-expanded", true);
        ToolbarToggle.Expanded += (_, _) =>
        {
            SettingsService.SaveDiffFlag("toolbar-expanded", true);
            UpdateToolbarFold();
        };
        ToolbarToggle.Collapsed += (_, _) =>
        {
            SettingsService.SaveDiffFlag("toolbar-expanded", false);
            UpdateToolbarFold();
        };
        Icon = DifferencerStatusIcons.GetCustomIcon(52) ?? Icon;
        DataContext = ViewModel;
        CloseButton.Command = ViewModel.CloseCommand;
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(DiffViewModel.ShowSpaces) or nameof(DiffViewModel.ShowNewlines))
                RefreshWhitespaceMarkers();
            else if (args.PropertyName == nameof(DiffViewModel.SyntaxHighlight))
                RefreshSyntaxHighlight();
        };
        if (sourcePath != null) SelectedFileText.Visibility = Visibility.Collapsed;
        PreviewKeyDown += DiffViewKeyDown;
        Body.Focusable = true;
        Body.PreviewMouseDown += (_, e) =>
        {
            _clipboardSource = ViewModel.IsAudio
                ? e.GetPosition(Body).Y < Body.ActualHeight / 2
                : e.GetPosition(Body).X < Body.ActualWidth / 2;
            if (ViewModel.IsAudio || ViewModel.IsImage || ViewModel.IsVideo) Body.Focus();
        };
        SourceHeader.MouseLeftButtonDown += (_, _) =>
        {
            _clipboardSource = true;
            Body.Focus();
        };
        TargetHeader.MouseLeftButtonDown += (_, _) =>
        {
            _clipboardSource = false;
            Body.Focus();
        };
        InitializeDiffContextMenu();
        ViewModel.JumpRequested += Jump;
        ViewModel.ReloadRequested = () => LoadContent();
        ViewModel.CloseRequested += Close;
        ViewModel.ConfirmChangesRequested = ConfirmTextChangesAsync;
        if (CloseButtonIcon != null)
            CloseButtonIcon.Source = DifferencerStatusIcons.GetCustomIcon(26);
        if (ExternalDiffLabelIcon != null)
            ExternalDiffLabelIcon.Source = DifferencerStatusIcons.GetCustomIcon(52);
        ViewModel.ExternalDiffHistoryRequested += () => ExternalDiffBox.CommitHistory();
        ViewModel.RefreshFileRequested = async difference =>
        {
            if (Owner is DeveloperDifferencerWindow owner) await owner.RefreshFileAsync(difference);
        };
        ViewModel.VisibleFilesRequested = () =>
            Owner is DeveloperDifferencerWindow owner
                ? owner.GetVisibleComparableFiles()
                : Array.Empty<DiffFile>();
        Closing += async (_, e) =>
        {
            if (!_allowEditedClose && ViewModel.HasEdits)
            {
                e.Cancel = true;
                if (_confirmingTextChanges) return;
                // Let WPF finish the cancelled Closing event before opening a modal
                // dialog or issuing another Close (Discard completes synchronously).
                await Dispatcher.Yield(DispatcherPriority.Background);
                if (_windowClosed || _confirmingTextChanges) return;
                if (await ConfirmTextChangesAsync())
                {
                    _allowEditedClose = true;
                    Close();
                }

                return;
            }

            if (_savingText)
            {
                e.Cancel = true;
                return;
            }

            if (Owner == null)
            {
                var bounds = WindowState == WindowState.Normal
                    ? new Rect(Left, Top, ActualWidth, ActualHeight)
                    : RestoreBounds;
                if (!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0)
                    SettingsService.SaveDiffWindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                        (int)WindowState);
            }

            // WPF can clear Owner before Closed is raised.
            if (!ViewModel.IsDetachedComparison && Owner is DeveloperDifferencerWindow owner)
                owner.SelectLastViewedFile(ViewModel.File);
        };
        BuildToolbar();
        Loaded += async (_, _) => await LoadContent();
        AllowDrop = true;
        DropZoneHighlight.Attach(this, () => !_renderingContent && ViewModel.CanChangeComparison, Body, DropAreas);
        PreviewDragEnter += FileDragOver;
        PreviewDragOver += FileDragOver;
        PreviewDrop += FileDrop;
        Activated += async (_, _) =>
        {
            if (IsLoaded && !_renderingContent) await ViewModel.RefreshAfterExternalEditAsync();
        };
        SourceInitialized += (_, _) =>
        {
            if (Owner == null && SettingsService.TryLoadDiffWindowPlacement(out var x, out var y, out var w, out var h,
                                  out var state)
                              && double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(w) && double.IsFinite(h))
            {
                var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                    SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
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

            _inputSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _inputSource?.AddHook(HorizontalWheelMessage);
        };
        Closed += (_, _) =>
        {
            _windowClosed = true;
            _inputSource?.RemoveHook(HorizontalWheelMessage);
            _inputSource = null;
            DetachImageFitHandler();
            try
            {
                ResetMediaController();
            }
            finally
            {
                ViewModel.Close();
                _clipboardInputs.Dispose();
            }
        };
    }

    internal DiffViewModel ViewModel { get; }

    private List<int> Hunks => ViewModel.Hunks;
    private List<DiffLine> Lines => ViewModel.Lines;

    // When on, a fold row hiding exactly one unchanged line is drawn as a 3 DIP line in
    // both panes, so the hunks around it come together. Rows keep their indexes; only
    // their vertical positions change, and every position goes through RowTop/RowAt.
    private bool _compactSingleFolds;
    private const double CompactRowHeight = 3;
    private double[] _rowTops = [0];
    private object _rowTopsKey;

    /// <summary>
    ///     行が、高さを縮めて表示する「1 filtered lines」の行かどうかを判定します。
    /// </summary>
    /// <param name="row">行のインデックス。</param>
    /// <returns>縮めて表示する場合は true。</returns>
    private bool IsCompactRow(int row) => _compactSingleFolds && Lines != null && row >= 0 && row < Lines.Count &&
                                          Lines[row].FilteredLineCount == 1 && !Lines[row].IsDesignerHeader;

    /// <summary>
    ///     各行の上端位置を、縮めて表示する行を考慮して求め、キャッシュします。
    /// </summary>
    /// <returns>行ごとの上端位置 (末尾に全体の高さを含む)。</returns>
    private double[] RowTops()
    {
        var count = Lines?.Count ?? 0;
        object key = (Lines, count, DiffLineHeight, _compactSingleFolds);
        if (Equals(key, _rowTopsKey)) return _rowTops;
        var tops = new double[count + 1];
        for (var i = 0; i < count; i++) tops[i + 1] = tops[i] + (IsCompactRow(i) ? CompactRowHeight : DiffLineHeight);
        _rowTops = tops;
        _rowTopsKey = key;
        return tops;
    }

    /// <summary>Top of a displayed row in pane coordinates; rows past the end continue at full height.</summary>
    internal double RowTop(int row)
    {
        var tops = RowTops();
        var count = tops.Length - 1;
        if (row <= 0) return row * DiffLineHeight;
        return row <= count ? tops[row] : tops[count] + (row - count) * DiffLineHeight;
    }

    /// <summary>Displayed row at a pane coordinate (may be outside 0..Count-1).</summary>
    internal int RowAt(double y)
    {
        var tops = RowTops();
        var count = tops.Length - 1;
        if (y < 0) return (int)Math.Floor(y / DiffLineHeight);
        if (y >= tops[count]) return count + (int)Math.Floor((y - tops[count]) / DiffLineHeight);
        var index = Array.BinarySearch(tops, y);
        return index >= 0 ? index : ~index - 1;
    }

    private double RowsHeight => RowTop(Lines?.Count ?? 0);

    // Re-render both panes with the new row heights, keeping the same top row.
    /// <summary>
    ///     「1 filtered lines」の行を縮めて表示するかどうかを切り替え、先頭の行を保ったまま両側のペインを描画し直します。
    /// </summary>
    /// <param name="on">縮めて表示する場合は true。</param>
    private void SetCompactSingleFolds(bool on)
    {
        if (_compactSingleFolds == on) return;
        var scroll = _leftList == null ? null : FindScroll(_leftList);
        var topRow = RowAt(scroll?.VerticalOffset ?? 0);
        var horizontal = scroll?.HorizontalOffset ?? 0;
        _compactSingleFolds = on;
        if (_leftList == null || _rightList == null || !ViewModel.IsText || _renderingContent) return;
        // Retained paragraphs carry the previous row height; rebuild them.
        _renderedPanes.Clear();
        RefreshFoldedPanes(RowTop(topRow), horizontal, Current);
    }

    // Line numbers are split at compact rows: each run of full-height rows is one
    // TextBlock, and each compact row is a 3 DIP spacer, matching the panes.
    /// <summary>
    ///     行番号の表示を作成します。通常の高さの行の連続を 1 つの TextBlock にし、縮めた行は 3 DIP の空白にします。
    /// </summary>
    /// <param name="gutter">行番号を表示するパネル。</param>
    /// <param name="source">比較元の場合は true。</param>
    private void FillGutter(StackPanel gutter, bool source)
    {
        gutter.Children.Clear();
        var numbers = new StringBuilder();
        var pending = false;

        void Flush()
        {
            if (!pending) return;
            var block = new TextBlock
            {
                FontFamily = _textFontFamily,
                FontSize = _textFontSize,
                LineHeight = DiffLineHeight,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextAlignment = TextAlignment.Right,
                // A zero-width space keeps a trailing number-less line at full height.
                Text = numbers + "\u200B"
            };
            block.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            gutter.Children.Add(block);
            numbers.Clear();
            pending = false;
        }

        for (var i = 0; i < Lines.Count; i++)
        {
            if (IsCompactRow(i))
            {
                Flush();
                gutter.Children.Add(new Border { Height = CompactRowHeight });
                continue;
            }

            if (pending) numbers.Append('\n');
            var number = source ? Lines[i].LeftNumber : Lines[i].RightNumber;
            if (number > 0) numbers.Append(number);
            pending = true;
        }

        Flush();
    }


    private int Current
    {
        get => ViewModel.CurrentHunk;
        set => ViewModel.CurrentHunk = value;
    }

    internal DiffSnapshot Snapshot => ViewModel.Snapshot;

    private ICommand ToggleDisplayCommand => _toggleDisplayCommand ??=
        new AsyncRelayCommand(async () =>
        {
            if (ViewModel.ToggleDifferencesOnly()) await LoadContent(false);
        }, ViewModel.ReportError);

    private ICommand ClearInputsCommand => _clearInputsCommand ??= new AsyncRelayCommand(async () =>
    {
        if (_renderingContent || !ViewModel.CanChangeComparison) return;
        if (!await ConfirmTextChangesAsync()) return;
        ViewModel.ClearInputs();
        await LoadContent();
    }, ViewModel.ReportError);

    private ICommand ReloadCommand => _reloadCommand ??= new AsyncRelayCommand(ReloadTextAsync, ViewModel.ReportError);

    private double MapHeight => Math.Max(0, Math.Min(_map?.ActualHeight ?? 0,
        _leftScroll != null && _leftScroll.ViewportHeight > 0 ? _leftScroll.ViewportHeight : _map?.ActualHeight ?? 0));

    private double MapExtent => Math.Max(1, Math.Max(RowsHeight,
        Math.Max(_leftScroll?.ExtentHeight ?? 0, _leftScroll?.ViewportHeight ?? 0)));

    /// <summary>
    ///     読み込みの進捗メッセージと進捗率を表示します。
    /// </summary>
    /// <param name="message">進捗メッセージ。</param>
    /// <param name="percent">進捗率。null の場合は不確定表示。</param>
    private void ShowLoadProgress(string message, double? percent = null)
    {
        LoadProgressText.Text = message;
        LoadProgress.IsIndeterminate = !percent.HasValue;
        LoadProgress.Value = percent ?? 0;
    }

    /// <summary>
    ///     描画中かどうかを設定し、進捗表示と操作の有効・無効を切り替えます。
    /// </summary>
    /// <param name="value">描画中の場合は true。</param>
    private void SetRendering(bool value)
    {
        _renderingContent = value;
        LoadProgressPanel.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        ActionsPanel.IsEnabled = FileNavigationPanel.IsEnabled = !value;
        TextFontPanel.IsEnabled = !value;
        SyntaxHighlightBox.IsEnabled = !value;
        PatchControls.IsEnabled = !value;
        Body.IsHitTestVisible = !value;
        ImageToolbar.IsHitTestVisible = ImageDetails.IsHitTestVisible = !value;
    }

    /// <summary>
    ///     ウィンドウが閉じられている場合は、処理を中断するため OperationCanceledException をスローします。
    /// </summary>
    private void CheckWindowOpen()
    {
        if (_windowClosed) throw new OperationCanceledException();
    }

    /// <summary>
    ///     オーナー ウィンドウと同じ位置・サイズ (最大化状態を含む) にします。
    /// </summary>
    internal void MatchOwnerSize()
    {
        var owner = Owner;
        if (owner == null) return;
        if (owner.WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Maximized;
            return;
        }

        var width = owner.ActualWidth > 0 ? owner.ActualWidth : owner.Width;
        var height = owner.ActualHeight > 0 ? owner.ActualHeight : owner.Height;
        if (owner.WindowState == WindowState.Minimized)
        {
            width = owner.RestoreBounds.Width;
            height = owner.RestoreBounds.Height;
        }

        if (width > 0) Width = Math.Max(MinWidth, width);
        if (height > 0) Height = Math.Max(MinHeight, height);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
    }

    /// <summary>
    ///     マウス ホイールの操作を、表示モード (テキスト・画像・音声) に応じたスクロールやズームに振り分けます。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">マウス ホイールのイベントデータ。</param>
    private void Body_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ViewModel.IsAudio)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                e.Handled = ScrollHorizontally(-e.Delta);
            // Other wheel gestures belong to WaveformControl (including Ctrl+wheel).
        }
        else if ((ViewModel.IsImage || ViewModel.IsVideo || ViewModel.IsAudio) && _imageZoom != null)
        {
            ChangeImageZoom(e.Delta / 120.0);
            e.Handled = true;
        }
        else if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            e.Handled = ScrollHorizontally(-e.Delta);
        }
        else
        {
            e.Handled = ScrollVertically(-e.Delta * DiffLineHeight * 3 / 120.0);
        }
    }

    /// <summary>
    ///     DIFF ビューのキー操作を処理します。Ctrl+V で貼り付け、Ctrl+↑↓ で HUNK 移動、Ctrl+←→ でファイル移動、Esc でクリア、F5 で再読み込み、PageUp/PageDown でスクロールまたはズームなどを行います。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">キー イベントのデータ。</param>
    private void DiffViewKeyDown(object sender, KeyEventArgs e)
    {
        if (_renderingContent) return;
        if (e.Handled || Keyboard.FocusedElement is TextBox) return;
        if (HandleTextEditKey(e)) return;
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.V)
        {
            e.Handled = true;
            _ = PasteClipboardAsync(_clipboardSource);
            return;
        }

        if (ViewModel.IsAudio &&
            Keyboard.Modifiers == ModifierKeys.None &&
            e.Key == Key.Space)
        {
            ToggleAudioPlayback();
            e.Handled = true;
            return;
        }

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
                    if (ViewModel.IsImage || ViewModel.IsVideo || ViewModel.IsAudio) return;
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
            // Same actions as the Clear (icon 140) and Reload (icon 69) buttons.
            case Key.Escape:
            case Key.F5:
                var action = e.Key == Key.Escape ? ClearInputsCommand : ReloadCommand;
                e.Handled = true;
                if (action.CanExecute(null)) action.Execute(null);
                break;
            case Key.Up: e.Handled = ScrollVertically(-DiffLineHeight); break;
            case Key.Down: e.Handled = ScrollVertically(DiffLineHeight); break;
            case Key.Left: e.Handled = ScrollHorizontally(-120); break;
            case Key.Right: e.Handled = ScrollHorizontally(120); break;
            case Key.PageUp:
            case Key.PageDown:
                var direction = e.Key == Key.PageUp ? -1 : 1;
                if ((ViewModel.IsImage || ViewModel.IsVideo || ViewModel.IsAudio) && _imageZoom != null)
                {
                    ChangeImageZoom(-direction);
                    e.Handled = true;
                }
                else
                {
                    if (_leftScroll == null) _leftScroll = FindScroll(_leftList);
                    e.Handled = ScrollVertically(direction * (_leftScroll?.ViewportHeight ?? 0));
                }

                break;
        }
    }

    /// <summary>
    ///     画像のズーム倍率を 1.25 倍単位で変更します。
    /// </summary>
    /// <param name="steps">変更する段数。負の値で縮小します。</param>
    private void ChangeImageZoom(double steps)
    {
        _imageZoom.Value = Math.Max(_imageZoom.Minimum,
            Math.Min(_imageZoom.Maximum, _imageZoom.Value * Math.Pow(1.25, steps)));
    }

    /// <summary>
    ///     両側のペインを同じ量だけ縦にスクロールします。
    /// </summary>
    /// <param name="delta">スクロール量。</param>
    /// <returns>スクロールした場合は true。</returns>
    private bool ScrollVertically(double delta)
    {
        if (_leftScroll == null) _leftScroll = FindScroll(_leftList);
        if (_rightScroll == null) _rightScroll = FindScroll(_rightList);
        if (_leftScroll == null || _rightScroll == null) return false;
        var offset = _leftScroll.VerticalOffset + delta;
        _leftScroll.ScrollToVerticalOffset(offset);
        _rightScroll.ScrollToVerticalOffset(offset);
        return true;
    }

    /// <summary>
    ///     ドラッグ中に点線枠を表示する範囲を取得します。ドロップ先の判定と同じく、音声は上下、それ以外は左右に分けます。
    /// </summary>
    /// <returns>Body を基準にした Source 側と Target 側の範囲。</returns>
    private IEnumerable<Rect> DropAreas()
    {
        var width = Body.ActualWidth;
        var height = Body.ActualHeight;
        if (ViewModel.IsAudio)
            return [new Rect(0, 0, width, height / 2), new Rect(0, height / 2, width, height / 2)];

        // FileDrop と同じく中央で左右に分け、中央のマップ列には枠を掛けない。
        var side = Math.Max(0, (width - MapColumn.ActualWidth) / 2);
        return [new Rect(0, 0, side, height), new Rect(width - side, 0, side, height)];
    }

    /// <summary>
    ///     比較対象を変更できる場合に、ファイルのドラッグでコピーのカーソルを表示します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">ドラッグ イベントのデータ。</param>
    private void FileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_renderingContent && ViewModel.CanChangeComparison && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    ///     ドロップされたファイル (2 つまで) を、ドロップ位置に応じた側の比較対象にして読み込みます。音声は上下、それ以外は左右で側を判定します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">ドラッグ イベントのデータ。</param>
    private async void FileDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_renderingContent || !ViewModel.CanChangeComparison || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (paths == null || paths.Length == 0) return;
        if (paths.Length > 2)
        {
            ViewModel.Status = StringOverlay.Get("Diff_DropCount");
            return;
        }

        if (!await ConfirmTextChangesAsync()) return;
        var sourceSide = ViewModel.IsAudio
            ? e.GetPosition(Body).Y < Body.ActualHeight / 2
            : e.GetPosition(Body).X < Body.ActualWidth / 2;
        var accepted = false;
        SetRendering(true);
        try
        {
            accepted = await ViewModel.DropFilesAsync(paths, sourceSide);
        }
        catch (Exception ex)
        {
            ViewModel.ReportError(ex);
        }
        finally
        {
            SetRendering(false);
        }

        if (accepted && !_windowClosed) await LoadContent();
    }

    /// <summary>
    ///     クリップボードの内容を一時ファイルにして、指定した側の比較対象にします。
    /// </summary>
    /// <param name="sourceSide">比較元に貼り付ける場合は true。</param>
    private async Task PasteClipboardAsync(bool sourceSide)
    {
        if (ViewModel.IsPatch) return;
        if (_renderingContent || _windowClosed || !ViewModel.CanChangeComparison) return;
        if (!await ConfirmTextChangesAsync()) return;
        var accepted = false;
        SetRendering(true);
        try
        {
            var paths = _clipboardInputs.Capture(Clipboard.GetDataObject());
            accepted = await ViewModel.DropFilesAsync(paths, sourceSide);
            _clipboardSource = sourceSide;
        }
        catch (Exception ex)
        {
            ViewModel.ReportError(ex);
        }
        finally
        {
            SetRendering(false);
        }

        if (accepted && !_windowClosed) await LoadContent();
    }

    // Text and patch views can fold the external diff row and the toolbar row;
    // image, video and audio views always show them.
    /// <summary>
    ///     テキストとパッチの表示では外部 DIFF の行とツール バーの行を折りたためるようにし、それ以外では常に表示します。
    /// </summary>
    private void UpdateToolbarFold()
    {
        ToolbarToggle.Visibility = ViewModel.IsText ? Visibility.Visible : Visibility.Collapsed;
        CompactFoldsToggle.Visibility = ViewModel.IsText ? Visibility.Visible : Visibility.Collapsed;
        ToolbarArea.Visibility = !ViewModel.IsText || ToolbarToggle.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    ///     表示モードに応じてツール バーのボタンとオプションを作成します。
    /// </summary>
    private void BuildToolbar()
    {
        UpdateToolbarFold();
        UpdatePatchControls();
        TextFontPanel.Visibility = ViewModel.IsText ? Visibility.Visible : Visibility.Collapsed;
        MarkerOptions.Visibility = ViewModel.IsImage || ViewModel.IsVideo || ViewModel.IsAudio
            ? Visibility.Collapsed
            : Visibility.Visible;
        ActionsPanel.Children.Clear();
        FileNavigationPanel.Children.Clear();
        if (!ViewModel.IsImage && !ViewModel.IsVideo && !ViewModel.IsAudio)
        {
            AddButton(ActionsPanel, DifferencerStatusIcons.GetCustomIcon(33), StringOverlay.Get("Diff_PreviousHunk"),
                ViewModel.PreviousHunkCommand);
            AddButton(ActionsPanel, DifferencerStatusIcons.GetCustomIcon(32), StringOverlay.Get("Diff_NextHunk"),
                ViewModel.NextHunkCommand);
        }

        AddButton(ActionsPanel, DifferencerStatusIcons.GetCustomIcon(53), StringOverlay.Get("Diff_OpenSource"),
            ViewModel.OpenSourceCommand);
        AddButton(ActionsPanel, DifferencerStatusIcons.GetCustomIcon(54), StringOverlay.Get("Diff_OpenTarget"),
            ViewModel.OpenTargetCommand);
        var openExtDiffButton = AddButton(ActionsPanel, DifferencerStatusIcons.GetCustomIcon(52), StringOverlay.Get("Diff_OpenExternal"),
            ViewModel.OpenExternalDiffCommand);
        openExtDiffButton.Background = Brushes.Transparent;
        if (!ViewModel.IsImage && !ViewModel.IsVideo && !ViewModel.IsAudio)
            AddButton(ActionsPanel, DifferencerStatusIcons.GetCustomIcon(ViewModel.DifferencesOnly ? 109 : 110),
                StringOverlay.Get(ViewModel.DifferencesOnly ? "Diff_ShowAllLines" : "Diff_ShowDifferences"),
                ToggleDisplayCommand);
        if (!ViewModel.IsImage && !ViewModel.IsVideo && !ViewModel.IsAudio)
        {
            AddComparisonButton(ViewModel.CompareCase ? 116 : 119, StringOverlay.Get("Diff_CompareCase"),
                () => ViewModel.CompareCase = !ViewModel.CompareCase);
            AddComparisonButton(ViewModel.CompareSpaces ? 117 : 120, StringOverlay.Get("Diff_CompareSpaces"),
                () => ViewModel.CompareSpaces = !ViewModel.CompareSpaces);
            AddComparisonButton(ViewModel.CompareNewlines ? 118 : 121, StringOverlay.Get("Diff_CompareNewlines"),
                () => ViewModel.CompareNewlines = !ViewModel.CompareNewlines);
        }

        AddButton(ActionsPanel, DifferencerStatusIcons.GetCustomIcon(140), StringOverlay.Get("Diff_ClearInputs"),
            ClearInputsCommand);
        AddButton(ActionsPanel, DifferencerStatusIcons.GetCustomIcon(69), StringOverlay.Get("Diff_Reload"), ReloadCommand);
        AddButton(FileNavigationPanel, DifferencerStatusIcons.GetCustomIcon(40), StringOverlay.Get("Diff_PreviousFile"),
            ViewModel.PreviousFileCommand);
        AddButton(FileNavigationPanel, DifferencerStatusIcons.GetCustomIcon(41), StringOverlay.Get("Diff_NextFile"),
            ViewModel.NextFileCommand);
    }

    /// <summary>
    ///     比較オプションを切り替えて再比較するボタンを追加します。
    /// </summary>
    /// <param name="icon">ボタンのアイコンのインデックス。</param>
    /// <param name="label">ボタンのラベル。</param>
    /// <param name="toggle">オプションを切り替える処理。</param>
    private void AddComparisonButton(int icon, string label, Action toggle)
    {
        AddButton(ActionsPanel, DifferencerStatusIcons.GetCustomIcon(icon), label,
            new AsyncRelayCommand(async () =>
            {
                if (!ViewModel.CanChangeComparison) return;
                toggle();
                await RecompareWorkingTextAsync();
            }, ViewModel.ReportError));
    }

    /// <summary>
    ///     表示モードに応じて、波形または両側のペインを横にスクロールします。
    /// </summary>
    /// <param name="delta">ホイールの回転量。</param>
    /// <returns>スクロールした場合は true。</returns>
    private bool ScrollHorizontally(int delta)
    {
        if (ViewModel.IsAudio && _audioTimeline != null)
        {
            _audioTimeline.ScrollBy(delta * 48.0 / 120);
            return true;
        }

        if (_leftScroll == null) _leftScroll = FindScroll(_leftList);
        if (_rightScroll == null) _rightScroll = FindScroll(_rightList);
        if (_leftScroll == null || _rightScroll == null) return false;
        // Native horizontal wheel: positive is right; Shift+vertical wheel reverses that sign.
        var offset = Math.Max(_leftScroll.HorizontalOffset, _rightScroll.HorizontalOffset) + delta * 48.0 / 120;
        _leftScroll.ScrollToHorizontalOffset(offset);
        _rightScroll.ScrollToHorizontalOffset(offset);
        return true;
    }

    /// <summary>
    ///     チルト ホイール (WM_MOUSEHWHEEL) のメッセージを処理し、カーソルが本体の上にある場合は横にスクロールします。
    /// </summary>
    /// <param name="hwnd">ウィンドウ ハンドル。</param>
    /// <param name="message">ウィンドウ メッセージ。</param>
    /// <param name="wParam">WPARAM。</param>
    /// <param name="lParam">LPARAM。</param>
    /// <param name="handled">処理済みかどうか。</param>
    /// <returns>メッセージの戻り値。</returns>
    private IntPtr HorizontalWheelMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int mouseHorizontalWheel = 0x020E;
        if (message != mouseHorizontalWheel) return IntPtr.Zero;
        var coordinates = lParam.ToInt64();
        var point = Body.PointFromScreen(new Point(unchecked((short)(coordinates & 0xffff)),
            unchecked((short)((coordinates >> 16) & 0xffff))));
        if (point.X < 0 || point.Y < 0 || point.X >= Body.ActualWidth || point.Y >= Body.ActualHeight)
            return IntPtr.Zero;
        int delta = unchecked((short)((wParam.ToInt64() >> 16) & 0xffff));
        handled = ScrollHorizontally(delta);
        return IntPtr.Zero;
    }

    /// <summary>
    ///     画像を表示領域に合わせるサイズ変更ハンドラーを解除します。
    /// </summary>
    private void DetachImageFitHandler()
    {
        if (_imageFitHandler == null) return;
        Body.SizeChanged -= _imageFitHandler;
        _imageFitHandler = null;
    }

    /// <summary>
    ///     両側のヘッダーの状態アイコンを更新します。
    /// </summary>
    private void UpdateSideStatusIcons()
    {
        FillStatusIcons(SourceStatusIcons, true);
        FillStatusIcons(TargetStatusIcons, false);
    }

    /// <summary>
    ///     ヘッダーに、ファイルの状態 (同一・欠落・新旧など) を表すアイコンを表示します。
    /// </summary>
    /// <param name="panel">アイコンを表示するパネル。</param>
    /// <param name="sourceSide">比較元の場合は true。</param>
    private void FillStatusIcons(Panel panel, bool sourceSide)
    {
        if (panel == null) return;
        panel.Children.Clear();
        var file = ViewModel.HeaderFile;
        if (file == null) return;
        foreach (var index in StatusIconIndexes(file, sourceSide))
            panel.Children.Add(StatusIcon(index));
        var document = sourceSide ? ViewModel.SourceText : ViewModel.TargetText;
        if (!ViewModel.IsImage && document != null)
        {
            var bom = StatusIcon(115);
            bom.Opacity = document.HasBom ? 1 : 0.25;
            bom.ToolTip = document.HasBom ? "BOM" : StringOverlay.Get("Diff_NoBom");
            panel.Children.Add(bom);
            var endings = document.Newlines;
            if (endings == "LF" || endings == "CRLF" || endings.Contains('/'))
            {
                var marker = StatusIcon(endings.Contains('/') ? 122 : endings == "LF" ? 113 : 114);
                marker.ToolTip = endings;
                panel.Children.Add(marker);
            }
            else if (endings.Length == 0)
            {
                var marker = StatusIcon(123);
                marker.ToolTip = StringOverlay.Get("Diff_NoLineEndings");
                panel.Children.Add(marker);
            }
            else
            {
                panel.Children.Add(new TextBlock
                {
                    Text = endings,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = StringOverlay.Get("Diff_LineEndings"),
                    Margin = new Thickness(4, 0, 0, 0)
                });
            }
        }

        panel.Children.Add(StatusIcon(sourceSide ? ViewModel.IsPatch ? 160 : 111 : 112));
    }

    // Image color of the patch (Source) side in Patch View.
    private static readonly SolidColorBrush PatchBrush = CreatePatchBrush();

    /// <summary>
    ///     パッチ側の表示に使う濃い緑のフリーズ済みブラシを作成します。
    /// </summary>
    /// <returns>作成したブラシ。</returns>
    private static SolidColorBrush CreatePatchBrush()
    {
        var brush = new SolidColorBrush(Colors.DarkGreen);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    ///     単独で比較している場合に、両側のファイル名を表示します。
    /// </summary>
    private void UpdateDetachedFileNames()
    {
        var visible = ViewModel.IsDetachedComparison;
        SourceFileNameBorder.Visibility = TargetFileNameBorder.Visibility =
            visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;

        void Update(TextBlock label, string path)
        {
            label.Text = string.IsNullOrEmpty(path) ? StringOverlay.Get("Diff_NoFileParen") : System.IO.Path.GetFileName(path);
            label.ToolTip = path;
        }

        Update(SourceFileNameText, ViewModel.GetPath(true));
        Update(TargetFileNameText, ViewModel.GetPath(false));
        // The patch side uses the patch image color instead of the Source color.
        if (ViewModel.IsPatch)
        {
            SourceFileNameBorder.BorderBrush = PatchBrush;
            // Outline only: white on a dark card, dark green on a light card.
            if (IsDarkBackground()) SourceFileNameText.Foreground = Brushes.White;
            else SourceFileNameText.Foreground = PatchBrush;
        }
        else
        {
            SourceFileNameBorder.SetResourceReference(Border.BorderBrushProperty, "SourceColor");
            SourceFileNameText.SetResourceReference(TextBlock.ForegroundProperty, "SourceColor");
        }
    }

    /// <summary>
    ///     両側のヘッダーに、エンコーディング・BOM・改行コード・ファイル情報を表示します。相手と異なる値は強調します。
    /// </summary>
    private void UpdateEncodingHeaders()
    {
        void Update(TextBlock header, bool source)
        {
            header.ToolTip = ViewModel.GetPath(source);
            header.Inlines.Clear();
            header.Inlines.Add(new Run(source ? ViewModel.SourceHeader : ViewModel.TargetHeader));
            var document = source ? ViewModel.SourceText : ViewModel.TargetText;
            var label = ViewModel.IsAudio || ViewModel.IsVideo
                ? (System.IO.Path.GetExtension(ViewModel.GetPath(source)) ?? string.Empty).TrimStart('.').ToUpperInvariant()
                : ViewModel.IsImage
                    ? source ? ViewModel.SourceImageFormat : ViewModel.TargetImageFormat
                    : document?.EncodingName;
            if (string.IsNullOrEmpty(label)) return;
            var patch = source && ViewModel.IsPatch;
            var color = patch ? PatchBrush.Color
                : source ? Color.FromRgb(255, 210, 64) : Color.FromRgb(64, 190, 255);
            var differs = ViewModel.IsAudio || ViewModel.IsVideo
                ? !string.Equals(System.IO.Path.GetExtension(ViewModel.GetPath(true)),
                    System.IO.Path.GetExtension(ViewModel.GetPath(false)), StringComparison.OrdinalIgnoreCase)
                : ViewModel.IsImage
                    ? ViewModel.SourceImageFormat != null && ViewModel.TargetImageFormat != null &&
                      ViewModel.SourceImageFormat != ViewModel.TargetImageFormat
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
                Effect = new DropShadowEffect { Color = color, BlurRadius = 7, ShadowDepth = 0, Opacity = 0.65 },
                Child = new TextBlock
                {
                    Text = label,
                    FontWeight = FontWeights.SemiBold,
                    // Filled patch labels use white; outlined ones white on dark, dark green on light.
                    Foreground = patch ? differs || IsDarkBackground() ? Brushes.White : brush
                        : differs ? Brushes.Black : brush
                }
            })
            { BaselineAlignment = BaselineAlignment.Center });
            if (ViewModel.IsText) AddTextSaveButton(header, source);
            if (ViewModel.IsVideo)
            {
                header.Inlines.Add(new InlineUIContainer(CreateVideoDelay(source))
                { BaselineAlignment = BaselineAlignment.Center });
                var duration = new TextBlock
                {
                    Text = "--:--:--",
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = FontWeights.SemiBold,
                    ToolTip = StringOverlay.Get(source ? "Diff_SourceDuration" : "Diff_TargetDuration")
                };
                duration.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
                _videoDurationLabels[source ? 0 : 1] = duration;
                header.Inlines.Add(new InlineUIContainer(duration) { BaselineAlignment = BaselineAlignment.Center });
                var path = ViewModel.GetPath(source);
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    var bytes = new FileInfo(path).Length;
                    var sizeLabel = new TextBlock
                    {
                        Text = FormatVideoFileSize(bytes),
                        Margin = new Thickness(10, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        ToolTip = string.Format(StringOverlay.Get("Diff_SizeBytes"), bytes.ToString("N0"))
                    };
                    sizeLabel.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                    header.Inlines.Add(
                        new InlineUIContainer(sizeLabel) { BaselineAlignment = BaselineAlignment.Center });
                }
            }
        }

        Update(SourceHeader, true);
        Update(TargetHeader, false);
    }

    /// <summary>
    ///     ファイルの状態から、表示する状態アイコンのインデックスを求めます。
    /// </summary>
    /// <param name="file">対象のファイル。</param>
    /// <param name="sourceSide">比較元の場合は true。</param>
    /// <returns>アイコンのインデックス。</returns>
    private static IEnumerable<int> StatusIconIndexes(DiffFile file, bool sourceSide)
    {
        var own = sourceSide ? file.Source : file.Target;
        var other = sourceSide ? file.Target : file.Source;
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

    /// <summary>
    ///     状態アイコンの Image を作成します。
    /// </summary>
    /// <param name="index">アイコンのインデックス。</param>
    /// <returns>作成した Image。</returns>
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

    /// <summary>
    ///     状態アイコンのツールチップの文字列を取得します。
    /// </summary>
    /// <param name="index">アイコンのインデックス。</param>
    /// <returns>ツールチップの文字列。</returns>
    private static string StatusIconTip(int index)
    {
        switch (index)
        {
            case 11: return StringOverlay.Get("Diff_IconSame");
            case 12: return StringOverlay.Get("Diff_IconMissing");
            case 13: return StringOverlay.Get("Diff_IconNew");
            case 14: return StringOverlay.Get("Diff_IconOld");
            case 15: return StringOverlay.Get("Diff_IconOnly");
            case 16: return StringOverlay.Get("Diff_IconSize");
            case 124: return StringOverlay.Get("Diff_IconSmall");
            case 125: return StringOverlay.Get("Diff_IconLarge");
            case 111: return StringOverlay.Get("Diff_SideSource");
            case 112: return StringOverlay.Get("Diff_SideTarget");
            case 160: return StringOverlay.Get("Diff_IconPatch");
            default: return string.Empty;
        }
    }

    /// <summary>
    ///     現在のファイルを読み込み、表示モードに応じた画面を描画します。
    /// </summary>
    /// <param name="reload">ファイルを読み込み直す場合は true。</param>
    /// <param name="jumpToFirst">最初の HUNK に移動する場合は true。</param>
    private async Task LoadContent(bool reload = true, bool jumpToFirst = true)
    {
        if (_renderingContent || _windowClosed) return;
        SetRendering(true);
        ShowLoadProgress(StringOverlay.Get("Diff_Loading"));
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            CheckWindowOpen();
            if (reload && !await ViewModel.LoadContentAsync()) return;
            _sourceSelectedEditHunks.Clear();
            _targetSelectedEditHunks.Clear();
            if (!IsLoaded) return;
            DetachImageFitHandler();
            ResetImageNavigation();
            ResetMediaController();
            if (ImageToolbar is Panel p)
            {
                p.Children.Clear();
                p.Visibility = Visibility.Collapsed;
            }

            BuildToolbar();
            UpdateSideStatusIcons();
            _leftList = _rightList = null;
            _patchMoveButtons = null;
            _leftScroll = _rightScroll = null;
            _imageZoom = null;
            _map = null;
            _hunkOverlay = null;
            _hunkStart = _hunkEnd = -1;
            _viewportThumb = null;
            _mapSelection = null;
            UpdateEncodingHeaders();
            UpdateDetachedFileNames();
            if (ViewModel.IsAudio)
            {
                await RenderAudio();
                return;
            }

            if (ViewModel.IsVideo)
            {
                await RenderVideos();
                return;
            }

            if (ViewModel.IsImage)
            {
                await RenderImages();
                return;
            }

            _sharedTextWidth = MeasureSharedTextWidth();
            var leftHost = MakeHost(true, out _leftList);
            var rightHost = MakeHost(false, out _rightList);
            await PopulatePaneAsync(_leftList, true, true);
            await PopulatePaneAsync(_rightList, false, true);
            CheckWindowOpen();
            // Keep the previous comparison visible until both new panes are ready.
            Body.Children.Clear();
            MapColumn.Width = new GridLength(34);
            Body.Children.Add(leftHost);
            Grid.SetColumn(rightHost, 2);
            Body.Children.Add(rightHost);
            _map = new Canvas();
            _map.SetResourceReference(Panel.BackgroundProperty, "CardBackground");
            Grid.SetColumn(_map, 1);
            Body.Children.Add(_map);
            _map.SizeChanged += (_, _) => DrawMap();
            DrawMap();
            _hunkOverlay = new Path
            {
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 2, 2 },
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
                SnapsToDevicePixels = true
            };
            _hunkOverlay.Stroke = new SolidColorBrush(Color.FromRgb(255, 210, 0));
            Grid.SetColumnSpan(_hunkOverlay, 3);
            Panel.SetZIndex(_hunkOverlay, 2);
            Body.Children.Add(_hunkOverlay);
            if (ViewModel.IsEmpty)
                foreach (var column in new[] { 0, 2 })
                {
                    var hint = new TextBlock
                    {
                        Text = StringOverlay.Get(column == 0 ? "Diff_SideSource" : "Diff_SideTarget") + "\n" + StringOverlay.Get("Diff_DropHere"),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextAlignment = TextAlignment.Center,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(16),
                        IsHitTestVisible = false
                    };
                    hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                    Grid.SetColumn(hint, column);
                    Body.Children.Add(hint);
                }

            _leftList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(ScrollChanged));
            _rightList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(ScrollChanged));
            _leftList.PreviewMouseLeftButtonDown += DiffPane_PreviewMouseLeftButtonDown;
            _rightList.PreviewMouseLeftButtonDown += DiffPane_PreviewMouseLeftButtonDown;
            var finalCount = Math.Max(0L, 2L * Lines.Count - 1);
            ShowLoadProgress(string.Format(StringOverlay.Get("Diff_PreparingLayout"), finalCount, finalCount), 100);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            CheckWindowOpen();
            if (jumpToFirst && Hunks.Count > 0)
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!IsLoaded || Hunks.Count == 0) return;
                    Jump(Hunks[0]);
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (IsLoaded && Hunks.Count > 0) Jump(Hunks[0]);
                    }), DispatcherPriority.ContextIdle);
                }), DispatcherPriority.Loaded);
        }
        catch (OperationCanceledException) when (_windowClosed)
        {
        }
        catch (Exception ex)
        {
            ViewModel.ReportError(ex);
        }
        finally
        {
            SetRendering(false);
        }
    }

    /// <summary>
    ///     行番号、テキスト ペイン、編集用の矢印ボタンを配置したペインのホストを作成します。
    /// </summary>
    /// <param name="sourceSide">比較元の場合は true。</param>
    /// <param name="box">作成したテキスト ペイン。</param>
    /// <returns>作成したホスト要素。</returns>
    private FrameworkElement MakeHost(bool sourceSide, out RichTextBox box)
    {
        var font = _textFontFamily;
        var gutter = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(30, 0, 8, 0),
            IsHitTestVisible = false
        };
        gutter.SetResourceReference(Panel.BackgroundProperty, "CardBackground");
        FillGutter(gutter, sourceSide);

        box = MakePane(sourceSide, font);
        // Use explicit coordinates: a second ScrollViewer can arrange a
        // short document differently from the RichTextBox's document view.
        gutter.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var gutterViewport = new Canvas
        {
            Width = gutter.DesiredSize.Width,
            ClipToBounds = true,
            IsHitTestVisible = true,
            VerticalAlignment = VerticalAlignment.Top
        };
        gutterViewport.SetResourceReference(Panel.BackgroundProperty, "CardBackground");
        Canvas.SetLeft(gutter, 0);
        Canvas.SetTop(gutter, 0);
        gutterViewport.Children.Add(gutter);
        box.Tag = gutterViewport;
        // Each gutter follows its own pane, including synchronized scrolling
        // and jumps to a hunk. Match the viewport above the horizontal bar.
        box.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (!(e.OriginalSource is ScrollViewer paneScroll)) return;
            gutterViewport.Height = paneScroll.ViewportHeight;
            Canvas.SetTop(gutter, -paneScroll.VerticalOffset);
            PositionEditArrows(gutterViewport, paneScroll.VerticalOffset);
        }), true);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(gutterViewport);
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);
        var markers = new WhitespaceOverlay(box, ViewModel);
        Grid.SetColumn(markers, 1);
        grid.Children.Add(markers);
        if (sourceSide) AddPatchMoveButtons(grid, box);
        BuildEditArrows(gutterViewport, sourceSide);

        var host = new Border { Child = grid, BorderThickness = new Thickness(5) };
        host.SetResourceReference(Border.BorderBrushProperty, sourceSide ? "SourceColor" : "TargetColor");
        if (sourceSide && ViewModel.IsPatch) host.BorderBrush = Brushes.LimeGreen;
        host.SetResourceReference(Border.BackgroundProperty, "CardBackground");
        return host;
    }

    /// <summary>
    ///     読み取り専用のテキスト ペインを作成します。
    /// </summary>
    /// <param name="sourceSide">比較元の場合は true。</param>
    /// <param name="font">表示に使うフォント。</param>
    /// <returns>作成した RichTextBox。</returns>
    private RichTextBox MakePane(bool sourceSide, FontFamily font)
    {
        var box = new RichTextBox
        {
            IsReadOnly = true,
            IsUndoEnabled = false,
            ContextMenu = Body.ContextMenu,
            AcceptsReturn = true,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            FontFamily = font,
            FontSize = _textFontSize,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            AutoWordSelection = false
        };
        box.SetResourceReference(BackgroundProperty, "CardBackground");
        box.SetResourceReference(ForegroundProperty, "Ink");
        box.SetResourceReference(TextBoxBase.SelectionBrushProperty, "ThemeSelected");
        box.GotKeyboardFocus += (_, _) => _clipboardSource = sourceSide;
        box.SelectionChanged += (_, _) => UpdateTextHunkSelection(box);

        box.SizeChanged += (_, _) =>
        {
            box.Document.PageWidth = Math.Max(_sharedTextWidth + 24, Math.Max(200, box.ActualWidth));
            UpdateHunkOverlay();
        };
        return box;
    }

    /// <summary>
    ///     テキスト ペインに行を同期的に描画します。
    /// </summary>
    /// <param name="box">対象のペイン。</param>
    /// <param name="sourceSide">比較元の場合は true。</param>
    private void PopulatePane(RichTextBox box, bool sourceSide)
    {
        PopulatePaneAsync(box, sourceSide, false, true).GetAwaiter().GetResult();
    }

    /// <summary>
    ///     テキスト ペインに、行の背景色・シンタックス ハイライト・文字単位の差分を付けて描画します。変わっていない行は以前の段落を再利用できます。
    /// </summary>
    /// <param name="box">対象のペイン。</param>
    /// <param name="sourceSide">比較元の場合は true。</param>
    /// <param name="responsive">描画中に UI に処理を譲る場合は true。</param>
    /// <param name="incremental">以前の描画結果を再利用する場合は true。</param>
    private async Task PopulatePaneAsync(RichTextBox box, bool sourceSide, bool responsive, bool incremental = false)
    {
        _syntaxDark = IsDarkBackground();
        // 描画の途中で設定が変わっても、1 回の描画の中では同じ言語で色分けする。
        var language = SyntaxLanguage;
        _renderedPanes.TryGetValue(sourceSide, out var previous);
        incremental &= previous != null;
        var previousRows = incremental
            ? previous.Lines.Select((line, index) => (line, index))
                .ToDictionary(item => item.line.FilteredAnchor ?? item.line, item => item.index)
            : null;
        int prefix = 0, suffix = 0;

        bool SameRow(DiffLine a, DiffLine b)
        {
            return ReferenceEquals(a, b) ||
                   (a.FilteredLineCount > 0 && a.FilteredLineCount == b.FilteredLineCount && a.Left == b.Left);
        }

        if (incremental)
        {
            while (prefix < Lines.Count && prefix < previous.Lines.Count &&
                   SameRow(Lines[prefix], previous.Lines[prefix])) prefix++;
            while (suffix < Lines.Count - prefix && suffix < previous.Lines.Count - prefix &&
                   SameRow(Lines[Lines.Count - 1 - suffix],
                       previous.Lines[previous.Lines.Count - 1 - suffix])) suffix++;
        }

        var document = incremental
            ? box.Document
            : new FlowDocument
            {
                PagePadding = new Thickness(0),
                TextAlignment = TextAlignment.Left,
                LineHeight = DiffLineHeight,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                PageWidth = Math.Max(_sharedTextWidth + 24, Math.Max(200, box.ActualWidth)),
                PageHeight = Math.Max(Math.Max(DiffLineHeight, RowsHeight) + 24, 200),
                IsOptimalParagraphEnabled = false,
                IsHyphenationEnabled = false
            };
        document.SetResourceReference(FlowDocument.BackgroundProperty, "CardBackground");
        document.SetResourceReference(FlowDocument.ForegroundProperty, "Ink");

        var lexState = 0;
        var originalRow = 0;

        var endings = (sourceSide ? ViewModel.SourceText : ViewModel.TargetText)?.Endings;

        bool IsEof(DiffLine line)
        {
            return line.FilteredLineCount == 0 &&
                   (sourceSide ? line.Left : line.Right) != null && endings?.Length > 0 &&
                   (sourceSide ? line.LeftNumber : line.RightNumber) == endings.Length;
        }

        var markerRows = new List<WhitespaceRow>(Lines.Count);
        var lexStates = new List<int>(Lines.Count);
        var editedHunks = new Dictionary<int, bool>();
        var editedBrushes = new Dictionary<string, Brush>();

        // ブロックの一括追加用リスト（都度Addによるイベント発火を防止）
        var blockList = new List<Block>(Lines != null ? Lines.Count : 0);
        var slice = Stopwatch.StartNew();

        foreach (var line in Lines)
        {
            var row = blockList.Count;
            var oldRow = -1;
            if (incremental)
            {
                if (row < prefix) oldRow = row;
                else if (row >= Lines.Count - suffix) oldRow = previous.Lines.Count - (Lines.Count - row);
                else if (previousRows.TryGetValue(line.FilteredAnchor ?? line, out var retained) &&
                         previous.Lines[retained].FilteredLineCount == line.FilteredLineCount) oldRow = retained;
                if (oldRow >= 0 && lexState != (oldRow > 0 ? previous.LexStates[oldRow - 1] : 0)) oldRow = -1;
            }

            if (oldRow >= 0)
            {
                blockList.Add(previous.Blocks[oldRow]);
                markerRows.Add(previous.Markers[oldRow]);
                markerRows[^1].IsEndOfFile = IsEof(line);
                // Folding does not change text: reuse the lexical state as well.
                lexState = previous.LexStates[oldRow];
                lexStates.Add(lexState);
                originalRow += Math.Max(1, line.FilteredLineCount);
                continue;
            }

            if (responsive)
            {
                CheckWindowOpen();
                var completed = (sourceSide ? 0L : Lines.Count) + blockList.Count;
                var total = Math.Max(0L, 2L * Lines.Count - 1);
                ShowLoadProgress(string.Format(StringOverlay.Get("Diff_LoadingSide"), StringOverlay.Get(sourceSide ? "Diff_SideSource" : "Diff_SideTarget"), completed, total),
                    100.0 * completed / Math.Max(1L, total));
                if (blockList.Count == 0 || slice.ElapsedMilliseconds >= 16)
                {
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    CheckWindowOpen();
                    slice.Restart();
                }
            }

            var text = sourceSide ? line.Left : line.Right;

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
                paragraph.ToolTip = StringOverlay.Get("Diff_ExpandUnchanged");
                // 隠れた行も字句解析だけ行い、複数行コメントなどの状態を次の行へ引き継ぐ。
                if (HasLexState(language))
                    for (var i = 0; i < line.FilteredLineCount; i++)
                    {
                        var hidden = ViewModel.AllLines[originalRow + i];
                        AddSyntaxRuns(null, (sourceSide ? hidden.Left : hidden.Right) ?? string.Empty, language,
                            ref lexState);
                    }

                originalRow += line.FilteredLineCount;
                if (IsCompactRow(row))
                {
                    // One hidden line: a 3 DIP line instead of the label row.
                    paragraph.Padding = new Thickness(0);
                    paragraph.LineHeight = CompactRowHeight;
                    paragraph.FontSize = CompactRowHeight;
                    paragraph.Inlines.Add(new Run(string.Empty));
                    paragraph.SetResourceReference(TextElement.BackgroundProperty, "Muted");
                }
                else
                {
                    paragraph.Inlines.Add(new Run(text));
                    paragraph.FontSize = _textFontSize * 10 / 13;
                    paragraph.SetResourceReference(TextElement.ForegroundProperty, "Muted");
                    paragraph.SetResourceReference(TextElement.BackgroundProperty,
                        TryFindResource("DiffFilteredBackground") != null ? "DiffFilteredBackground" : "HeaderBackground");
                }
            }
            else
            {
                if (ViewModel.DifferencesOnly && line.Kind == DiffLineKind.Unchanged)
                {
                    paragraph.Cursor = Cursors.Hand;
                    paragraph.ToolTip = StringOverlay.Get("Diff_CollapseUnchanged");
                }

                // Syntax highlighting uses text without display-only markers.
                AddSyntaxRuns(paragraph, string.IsNullOrEmpty(text) ? " " : text, language, ref lexState);

                if (ViewModel.CompareCase && line.Kind == DiffLineKind.Modified &&
                    !string.Equals(line.Left, line.Right, StringComparison.Ordinal))
                    await MarkInlineDifferencesAsync(paragraph, line, sourceSide, responsive);

                originalRow++;
            }

            var resource = LineBrushKey(line.Kind, sourceSide);
            if (resource != null)
            {
                paragraph.SetResourceReference(TextElement.BackgroundProperty, resource);
                if (line.EditHunkId >= 0)
                {
                    if (!editedHunks.TryGetValue(line.EditHunkId, out var edited))
                        editedHunks[line.EditHunkId] = edited = ViewModel.CanUndoHunk(line.EditHunkId, true) ||
                                                                ViewModel.CanUndoHunk(line.EditHunkId, false);
                    if (edited && TryFindResource(resource) is SolidColorBrush original)
                    {
                        if (!editedBrushes.TryGetValue(resource, out var darker))
                        {
                            var color = original.Color;
                            darker = new SolidColorBrush(Color.FromArgb(color.A,
                                (byte)(color.R * 0.5), (byte)(color.G * 0.5), (byte)(color.B * 0.5)));
                            darker.Freeze();
                            editedBrushes[resource] = darker;
                        }

                        paragraph.Background = darker;
                    }
                }
            }

            blockList.Add(paragraph);
            lexStates.Add(lexState);
            var lineNumber = sourceSide ? line.LeftNumber : line.RightNumber;
            var ending = lineNumber > 0 && endings != null && lineNumber <= endings.Length
                ? endings[lineNumber - 1]
                : string.Empty;
            markerRows.Add(new WhitespaceRow(paragraph,
                    line.FilteredLineCount > 0 ? null : sourceSide ? line.Left : line.Right, ending,
                    ViewModel.CompareCase && line.Kind == DiffLineKind.Modified && line.FilteredLineCount == 0
                        ? sourceSide ? line.LeftChanges : line.RightChanges
                        : null)
            { IsEndOfFile = IsEof(line) });
        }

        // 最後にまとめて追加
        if (incremental)
        {
            box.BeginChange();
            try
            {
                var retained = blockList.ToHashSet();
                var oldBlocks = previous.Blocks.ToHashSet();
                foreach (var block in previous.Blocks)
                    if (!retained.Contains(block))
                        document.Blocks.Remove(block);
                Block anchor = null;
                for (var i = blockList.Count - 1; i >= 0; i--)
                {
                    if (!oldBlocks.Contains(blockList[i]))
                    {
                        if (anchor != null) document.Blocks.InsertBefore(anchor, blockList[i]);
                        else document.Blocks.Add(blockList[i]);
                    }

                    anchor = blockList[i];
                }

                document.PageWidth = Math.Max(_sharedTextWidth + 24, Math.Max(200, box.ActualWidth));
                document.PageHeight = Math.Max(Math.Max(DiffLineHeight, RowsHeight) + 24, 200);
            }
            finally
            {
                box.EndChange();
            }
        }
        else
        {
            document.Blocks.AddRange(blockList);
            box.Document = document;
        }

        _renderedPanes[sourceSide] = new RenderedPane(new List<DiffLine>(Lines), blockList, markerRows, lexStates);
        if (box.Parent is Grid paneGrid)
            paneGrid.Children.OfType<WhitespaceOverlay>().Single().SetRows(markerRows);
    }

    /// <summary>
    ///     段落内の変更された文字の範囲を強調表示します。
    /// </summary>
    /// <param name="paragraph">対象の段落。</param>
    /// <param name="line">行の比較結果。</param>
    /// <param name="sourceSide">比較元の場合は true。</param>
    /// <param name="responsive">処理中に UI に処理を譲る場合は true。</param>
    private async Task MarkInlineDifferencesAsync(Paragraph paragraph, DiffLine line, bool sourceSide, bool responsive)
    {
        var changed = sourceSide ? line.LeftChanges : line.RightChanges;
        if (changed == null) return;

        var ranges = new List<TextRange>();
        var offset = 0;
        foreach (var run in paragraph.Inlines.OfType<Run>())
        {
            var index = 0;
            while (index < run.Text.Length)
            {
                if (offset + index >= changed.Length || !changed[offset + index])
                {
                    index++;
                    continue;
                }

                var start = index++;
                while (index < run.Text.Length && offset + index < changed.Length && changed[offset + index]) index++;
                ranges.Add(new TextRange(run.ContentStart.GetPositionAtOffset(start),
                    run.ContentStart.GetPositionAtOffset(index)));
            }

            offset += run.Text.Length;
        }

        var slice = Stopwatch.StartNew();
        foreach (var range in ranges)
        {
            range.ApplyPropertyValue(TextElement.ForegroundProperty, Brushes.Red);
            range.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Bold);
            if (responsive && slice.ElapsedMilliseconds >= 16)
            {
                await Dispatcher.Yield(DispatcherPriority.Background);
                CheckWindowOpen();
                slice.Restart();
            }
        }
    }


    /// <summary>
    ///     C# のキーワード・文字列・コメントを色分けして、行のテキストを段落に追加します。
    /// </summary>
    /// <param name="paragraph">追加先の段落。</param>
    /// <param name="text">行のテキスト。</param>
    /// <param name="blockComment">複数行コメントの中かどうか。次の行に引き継ぎます。</param>
    private void AddCSharpRuns(Paragraph paragraph, string text, ref bool blockComment)
    {
        int i = 0, plain = 0;
        while (i < text.Length)
        {
            if (blockComment)
            {
                var end = text.IndexOf("*/", i, StringComparison.Ordinal);
                AddColoredRun(paragraph, text.Substring(i, end < 0 ? text.Length - i : end + 2 - i), "comment");
                if (end < 0) return;
                i = plain = end + 2;
                blockComment = false;
                continue;
            }

            if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '/')
            {
                AddPlainRun(paragraph, text, plain, i - plain);
                AddColoredRun(paragraph, text.Substring(i), "comment");
                return;
            }

            if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*')
            {
                AddPlainRun(paragraph, text, plain, i - plain);
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    AddColoredRun(paragraph, text.Substring(i), "comment");
                    blockComment = true;
                    return;
                }

                AddColoredRun(paragraph, text.Substring(i, end + 2 - i), "comment");
                i = plain = end + 2;
                continue;
            }

            if (text[i] == '"' || text[i] == '\'')
            {
                AddPlainRun(paragraph, text, plain, i - plain);
                var quote = text[i];
                var start = i++;
                while (i < text.Length)
                {
                    if (text[i] == '\\')
                    {
                        i += Math.Min(2, text.Length - i);
                        continue;
                    }

                    if (text[i++] == quote) break;
                }

                AddColoredRun(paragraph, text.Substring(start, i - start), "string");
                plain = i;
                continue;
            }

            if (char.IsLetter(text[i]) || text[i] == '_')
            {
                var start = i++;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                var word = text.Substring(start, i - start);
                if (CSharpKeywords.Contains(word))
                {
                    AddPlainRun(paragraph, text, plain, start - plain);
                    AddColoredRun(paragraph, word, "keyword");
                    plain = i;
                }

                continue;
            }

            if (char.IsDigit(text[i]))
            {
                AddPlainRun(paragraph, text, plain, i - plain);
                var start = i++;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '.' || text[i] == '_')) i++;
                AddColoredRun(paragraph, text.Substring(start, i - start), "number");
                plain = i;
                continue;
            }

            i++;
        }

        AddPlainRun(paragraph, text, plain, text.Length - plain);
    }


    private static readonly Regex JsonTokenPattern =
        new(@"""(?:\\.|[^""\\])*""|\b(?:true|false|null)\b|-?\b\d+(?:\.\d+)?(?:[eE][+-]?\d+)?\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);


    /// <summary>
    ///     JSON のキー・文字列・数値などを色分けして、行のテキストを段落に追加します。
    /// </summary>
    /// <param name="paragraph">追加先の段落。</param>
    /// <param name="text">行のテキスト。</param>
    private void AddJsonRuns(Paragraph paragraph, string text)
    {
        var pos = 0;
        foreach (Match match in JsonTokenPattern.Matches(text))
        {
            AddPlainRun(paragraph, text, pos, match.Index - pos);
            var kind = match.Value.StartsWith("\"", StringComparison.Ordinal) ? "string" :
                match.Value == "true" || match.Value == "false" || match.Value == "null" ? "keyword" : "number";
            AddColoredRun(paragraph, match.Value, kind);
            pos = match.Index + match.Length;
        }

        AddPlainRun(paragraph, text, pos, text.Length - pos);
    }

    /// <summary>
    ///     色分けしないテキストを段落に追加します。
    /// </summary>
    /// <param name="paragraph">追加先の段落。</param>
    /// <param name="text">行のテキスト。</param>
    /// <param name="start">追加する範囲の開始位置。</param>
    /// <param name="length">追加する範囲の長さ。</param>
    private static void AddPlainRun(Paragraph paragraph, string text, int start, int length)
    {
        if (paragraph != null && length > 0) paragraph.Inlines.Add(new Run(text.Substring(start, length)));
    }

    // Theme lookup is done once per pane render; brushes are frozen and shared by every run.
    private bool _syntaxDark = true;

    private static readonly Brush DarkCommentBrush = FrozenBrush(106, 153, 85);
    private static readonly Brush LightCommentBrush = FrozenBrush(0, 128, 0);
    private static readonly Brush DarkStringBrush = FrozenBrush(206, 145, 120);
    private static readonly Brush LightStringBrush = FrozenBrush(163, 21, 21);
    private static readonly Brush DarkNumberBrush = FrozenBrush(181, 206, 168);
    private static readonly Brush LightNumberBrush = FrozenBrush(9, 134, 88);
    private static readonly Brush DarkKeywordBrush = FrozenBrush(86, 156, 214);
    private static readonly Brush LightKeywordBrush = FrozenBrush(0, 0, 255);
    private static readonly Brush DarkAttributeBrush = FrozenBrush(156, 220, 254);
    private static readonly Brush LightAttributeBrush = FrozenBrush(38, 127, 153);

    /// <summary>
    ///     指定した色のフリーズ済みブラシを作成します。
    /// </summary>
    /// <param name="r">赤の成分。</param>
    /// <param name="g">緑の成分。</param>
    /// <param name="b">青の成分。</param>
    /// <returns>作成したブラシ。</returns>
    private static Brush FrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    ///     種類に応じた色 (ダーク/ライト テーマ別) でテキストを段落に追加します。
    /// </summary>
    /// <param name="paragraph">追加先の段落。</param>
    /// <param name="text">追加するテキスト。</param>
    /// <param name="kind">トークンの種類。</param>
    private void AddColoredRun(Paragraph paragraph, string text, string kind)
    {
        if (paragraph == null) return;
        var dark = _syntaxDark;
        var run = new Run(text)
        {
            Foreground = kind == "comment" ? dark ? DarkCommentBrush : LightCommentBrush :
                kind == "string" ? dark ? DarkStringBrush : LightStringBrush :
                kind == "number" ? dark ? DarkNumberBrush : LightNumberBrush :
                kind == "attribute" ? dark ? DarkAttributeBrush : LightAttributeBrush :
                dark ? DarkKeywordBrush : LightKeywordBrush
        };
        paragraph.Inlines.Add(run);
    }

    /// <summary>
    ///     現在のテーマの背景が暗いかどうかを判定します。
    /// </summary>
    /// <returns>暗い場合は true。</returns>
    private bool IsDarkBackground()
    {
        var brush = TryFindResource("CardBackground") as SolidColorBrush;
        if (brush == null) return true;
        var c = brush.Color;
        return (c.R * 299 + c.G * 587 + c.B * 114) / 1000 < 128;
    }

    /// <summary>
    ///     行の差分の種類と側に応じた背景ブラシのリソース キーを取得します。
    /// </summary>
    /// <param name="kind">行の差分の種類。</param>
    /// <param name="sourceSide">比較元の場合は true。</param>
    /// <returns>リソース キー。変更なしの場合は null。</returns>
    private static string LineBrushKey(DiffLineKind kind, bool sourceSide)
    {
        if (kind == DiffLineKind.Removed) return sourceSide ? "DiffRemoved" : "DiffRemovedEmpty";
        if (kind == DiffLineKind.Added) return sourceSide ? "DiffAddedEmpty" : "DiffAdded";
        if (kind == DiffLineKind.Modified) return sourceSide ? "DiffRemoved" : "DiffAdded";
        return null;
    }

    /// <summary>
    ///     ペインのクリック位置の行に応じて、折りたたまれた行の展開・折りたたみや、HUNK の選択を行います。
    /// </summary>
    /// <param name="sender">クリックされたペイン。</param>
    /// <param name="e">マウス イベントのデータ。</param>
    private void DiffPane_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Visual visual && FindAncestor<ScrollBar>(visual) != null) return;
        var box = sender as RichTextBox;
        if (box == null || Lines == null || Lines.Count == 0) return;

        if (_leftScroll == null) _leftScroll = FindScroll(_leftList);
        if (_rightScroll == null) _rightScroll = FindScroll(_rightList);
        var scroll = box == _rightList ? _rightScroll : _leftScroll;
        if (scroll == null) scroll = FindScroll(box);
        if (scroll == null) return;

        var y = e.GetPosition(box).Y + scroll.VerticalOffset;
        var index = RowAt(y);
        if (index < 0 || index >= Lines.Count) return;
        if (Lines[index].IsDesignerHeader)
        {
            e.Handled = true;
            if (ViewModel.ExpandFilteredLines(index)) _ = LoadContent(false, false);
            return;
        }
        if (ViewModel.DifferencesOnly && Lines[index].Kind == DiffLineKind.Unchanged)
        {
            e.Handled = true;
            double vertical = scroll.VerticalOffset, horizontal = scroll.HorizontalOffset;
            var selectedHunk = Current;
            if (Lines[index].FilteredLineCount > 0)
            {
                if (!ViewModel.ExpandFilteredLines(index)) return;
            }
            else
            {
                var collapsedIndex = ViewModel.CollapseExpandedLines(index);
                if (collapsedIndex < 0) return;
                vertical = Math.Min(vertical, RowTop(collapsedIndex));
            }

            RefreshFoldedPanes(vertical, horizontal, selectedHunk);
            return;
        }

        // Unchanged rows (patch context included) are never part of a hunk.
        if (Lines[index].Kind == DiffLineKind.Unchanged) return;
        SelectHunk(index, false);
    }

    // Keep the pane controls, toolbar and overlay alive. Complete both document
    // updates and scroll restoration in one dispatcher turn, before rendering.
    /// <summary>
    ///     折りたたみの変更後、ペインやツール バーを作り直さずに両側の文書を更新し、スクロール位置と選択中の HUNK を復元します。
    /// </summary>
    /// <param name="vertical">縦方向のスクロール位置。</param>
    /// <param name="horizontal">横方向のスクロール位置。</param>
    /// <param name="selectedHunk">選択する HUNK のインデックス。</param>
    internal void RefreshFoldedPanes(double vertical, double horizontal, int selectedHunk)
    {
        var wasRendering = _renderingContent;
        _renderingContent = true;
        try
        {
            using (Dispatcher.DisableProcessing())
            {
                _scrolling = true;
                try
                {
                    _sharedTextWidth = Math.Max(_sharedTextWidth, MeasureSharedTextWidth());
                    PopulatePane(_leftList, true);
                    PopulatePane(_rightList, false);
                    UpdateFoldedGutter(_leftList, true);
                    UpdateFoldedGutter(_rightList, false);
                    Body.UpdateLayout();
                    _leftScroll = FindScroll(_leftList);
                    _rightScroll = FindScroll(_rightList);
                    _leftScroll?.ScrollToVerticalOffset(vertical);
                    _rightScroll?.ScrollToVerticalOffset(vertical);
                    _leftScroll?.ScrollToHorizontalOffset(horizontal);
                    _rightScroll?.ScrollToHorizontalOffset(horizontal);
                    Body.UpdateLayout();
                    if (selectedHunk >= 0 && selectedHunk < Hunks.Count)
                        SelectHunk(Hunks[selectedHunk], false);
                    else _hunkStart = _hunkEnd = -1;
                    DrawMap();
                    UpdateHunkOverlay();
                    RefreshPatchMoveButtons();
                }
                finally
                {
                    _scrolling = false;
                }
            }
        }
        finally
        {
            _renderingContent = wasRendering;
        }
    }

    /// <summary>
    ///     折りたたみの変更後に行番号の表示を作り直します。
    /// </summary>
    /// <param name="pane">対象のペイン。</param>
    /// <param name="source">比較元の場合は true。</param>
    private void UpdateFoldedGutter(RichTextBox pane, bool source)
    {
        var viewport = (Canvas)pane.Tag;
        var gutter = (StackPanel)viewport.Children[0];
        FillGutter(gutter, source);
        gutter.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        viewport.Width = Math.Max(viewport.Width, gutter.DesiredSize.Width);
        // Folding only changes displayed row positions, not edit hunk identities.
        // A patch hunk can span several changed runs; its arrow stays on the first.
        var rowsByHunk = Hunks.Where(row => Lines[row].EditHunkId >= 0)
            .GroupBy(row => Lines[row].EditHunkId).ToDictionary(group => group.Key, group => group.First());
        foreach (var button in viewport.Children.OfType<Button>())
        {
            var (row, id, _) = ((int, int, bool))button.Tag;
            button.Tag = (rowsByHunk.TryGetValue(id, out var moved) ? moved : row, id, source);
        }

        PositionEditArrows(viewport, -Canvas.GetTop(gutter));
    }

    /// <summary>
    ///     指定した行の HUNK を選択し、先頭が見えるようスクロールします。
    /// </summary>
    /// <param name="index">行のインデックス。</param>
    private void Jump(int index)
    {
        SelectHunk(index, true);
    }

    /// <summary>
    ///     指定した行を含む変更行の連続を HUNK として選択し、枠を表示します。
    /// </summary>
    /// <param name="index">行のインデックス。</param>
    /// <param name="scrollToStart">HUNK の先頭までスクロールする場合は true。</param>
    private void SelectHunk(int index, bool scrollToStart)
    {
        if (Lines == null || index < 0 || index >= Lines.Count) return;
        var start = index;
        int end;
        // The frame is exactly one run of changed rows. In a patch, runs of
        // different ids are separate hunks even when adjacent.
        bool SameRun(int a, int b) => !ViewModel.IsPatch || Lines[a].EditHunkId == Lines[b].EditHunkId;
        while (start > 0 && Lines[start - 1].Kind != DiffLineKind.Unchanged && SameRun(start - 1, start))
            start--;
        if (Lines[start].Kind == DiffLineKind.Unchanged)
        {
            start = index;
            while (start < Lines.Count && Lines[start].Kind == DiffLineKind.Unchanged)
                start++;
            if (start >= Lines.Count) return;
        }

        end = start + 1;
        while (end < Lines.Count && Lines[end].Kind != DiffLineKind.Unchanged && SameRun(end, start))
            end++;

        _hunkStart = start;
        _hunkEnd = end;
        var hunkIndex = Hunks.IndexOf(start);
        if (hunkIndex < 0)
        {
            hunkIndex = Hunks.FindLastIndex(item => item <= start);
            if (hunkIndex < 0) hunkIndex = 0;
        }

        Current = hunkIndex;

        if (scrollToStart)
        {
            ScrollPaneToLine(_leftList, start);
            ScrollPaneToLine(_rightList, start);
        }

        Dispatcher.BeginInvoke(new Action(UpdateHunkOverlay), DispatcherPriority.Loaded);
    }

    /// <summary>
    ///     選択中の HUNK の枠とマップ上の選択位置を更新します。
    /// </summary>
    private void UpdateHunkOverlay()
    {
        UpdateMapSelection();
        if (_hunkOverlay == null || _hunkStart < 0 || _hunkEnd <= _hunkStart || Lines == null)
        {
            if (_hunkOverlay != null) _hunkOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        if (_leftScroll == null) _leftScroll = FindScroll(_leftList);
        if (_leftScroll == null)
        {
            _hunkOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        var viewTop = _leftScroll.TranslatePoint(new Point(0, 0), Body);
        var viewHeight = _leftScroll.ViewportHeight;
        if (viewHeight <= 0) viewHeight = Body.ActualHeight;
        var clipTop = viewTop.Y;
        var clipBottom = viewTop.Y + viewHeight;

        // Paragraph backgrounds use fixed row boundaries, not glyph bounds.
        // Glyph ascenders/descenders shift the outline below the painted top.
        var hunkTop = viewTop.Y + RowTop(_hunkStart) - _leftScroll.VerticalOffset;
        var hunkBottom = viewTop.Y + RowTop(_hunkEnd) - _leftScroll.VerticalOffset;
        var sourceHost = FindAncestor<Border>(_leftList);
        var targetHost = FindAncestor<Border>(_rightList);
        if (sourceHost == null || targetHost == null) return;
        var innerLeft = sourceHost.TranslatePoint(new Point(sourceHost.BorderThickness.Left, 0), Body).X;
        var innerRight = targetHost
            .TranslatePoint(new Point(targetHost.ActualWidth - targetHost.BorderThickness.Right, 0), Body).X;
        if (hunkBottom <= clipTop || hunkTop >= clipBottom)
        {
            _hunkOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        var showTopEdge = hunkTop >= clipTop - 0.5;
        var showBottomEdge = hunkBottom <= clipBottom + 0.5;
        var y = Math.Max(clipTop, hunkTop);
        var bottom = Math.Min(clipBottom, hunkBottom);
        var height = bottom - y;
        if (height < 1)
        {
            _hunkOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        // Keep the entire stroke inside the arranged Path; a stroke centered
        // on its bottom boundary was clipped to half thickness.
        var dpi = VisualTreeHelper.GetDpi(Body);
        y = Math.Round(y * dpi.DpiScaleY) / dpi.DpiScaleY;
        bottom = Math.Round(bottom * dpi.DpiScaleY) / dpi.DpiScaleY;
        height = bottom - y;
        var inset = _hunkOverlay.StrokeThickness / 2;
        double topEdge = inset, bottomEdge = Math.Max(inset, height - inset);
        var geometry = new StreamGeometry();
        using (var drawing = geometry.Open())
        {
            var left = innerLeft + inset;
            var right = Math.Max(left, innerRight - inset);
            drawing.BeginFigure(new Point(left, topEdge), false, false);
            drawing.LineTo(new Point(left, bottomEdge), true, false);
            if (showBottomEdge) drawing.LineTo(new Point(right, bottomEdge), true, false);
            else drawing.BeginFigure(new Point(right, bottomEdge), false, false);
            drawing.LineTo(new Point(right, topEdge), true, false);
            if (showTopEdge) drawing.LineTo(new Point(left, topEdge), true, false);
        }

        _hunkOverlay.Data = geometry;
        _hunkOverlay.Margin = new Thickness(0, y, 0, Math.Max(0, Body.ActualHeight - y - height));
        _hunkOverlay.Visibility = Visibility.Visible;
    }

    /// <summary>
    ///     ペインを指定した行までスクロールします。
    /// </summary>
    /// <param name="box">対象のペイン。</param>
    /// <param name="index">行のインデックス。</param>
    private static void ScrollPaneToLine(RichTextBox box, int index)
    {
        if (box?.Document == null) return;
        var i = 0;
        foreach (var block in box.Document.Blocks)
        {
            if (i == index)
            {
                block.BringIntoView();
                return;
            }

            i++;
        }
    }

    /// <summary>
    ///     ビジュアル ツリーから ScrollViewer を探します。
    /// </summary>
    /// <param name="parent">探し始める要素。</param>
    /// <returns>見つかった ScrollViewer。見つからない場合は null。</returns>
    private static ScrollViewer FindScroll(DependencyObject parent)
    {
        if (parent == null) return null;
        if (parent is ScrollViewer) return (ScrollViewer)parent;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var found = FindScroll(VisualTreeHelper.GetChild(parent, i));
            if (found != null) return found;
        }

        return null;
    }

    /// <summary>
    ///     一方のペインのスクロールを、もう一方のペインとマップに同期します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">スクロール変更のイベントデータ。</param>
    private void ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_scrolling) return;
        var from = e.OriginalSource as ScrollViewer;
        if (from == null) return;
        if (_leftScroll == null) _leftScroll = FindScroll(_leftList);
        if (_rightScroll == null) _rightScroll = FindScroll(_rightList);
        if (from != _leftScroll && from != _rightScroll) return;
        if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0) DrawMap();
        UpdateImageNavigation();
        UpdateViewport(from);
        UpdateHunkOverlay();
        if (e.VerticalChange == 0 && e.HorizontalChange == 0) return;
        var to = from == _leftScroll ? _rightScroll : _leftScroll;
        if (to == null) return;
        _scrolling = true;
        to.ScrollToVerticalOffset(from.VerticalOffset);
        to.ScrollToHorizontalOffset(from.HorizontalOffset);
        _scrolling = false;
    }

    /// <summary>
    ///     マップ上の表示範囲の枠を、スクロール位置に合わせて更新します。
    /// </summary>
    /// <param name="scroll">基準のスクロール ビューアー。</param>
    private void UpdateViewport(ScrollViewer scroll)
    {
        if (_viewportThumb == null || _map == null || scroll == null) return;
        var height = MapHeight;
        var scale = height / MapExtent;
        _viewportThumb.Height = Math.Min(height, scroll.ViewportHeight * scale);
        _viewportThumb.Width = Math.Max(0, _map.ActualWidth - 2);
        Canvas.SetTop(_viewportThumb, Math.Max(0, scroll.VerticalOffset * scale));
        UpdateMapSelection();
    }

    /// <summary>
    ///     マップ上に、選択中の HUNK の位置を表示します。
    /// </summary>
    private void UpdateMapSelection()
    {
        if (_mapSelection == null || _viewportThumb == null) return;
        _mapSelection.Visibility = Visibility.Collapsed;
        if (_hunkStart < 0 || _hunkEnd <= _hunkStart) return;
        var scale = MapHeight / MapExtent;
        var viewTop = Canvas.GetTop(_viewportThumb);
        if (double.IsNaN(viewTop)) return;
        var top = Math.Max(viewTop + 2, RowTop(_hunkStart) * scale);
        var bottom = Math.Min(viewTop + _viewportThumb.Height - 2, RowTop(_hunkEnd) * scale);
        if (bottom <= top) return;
        Canvas.SetLeft(_mapSelection, 3);
        Canvas.SetTop(_mapSelection, top);
        _mapSelection.Width = Math.Max(0, _map.ActualWidth - 6);
        _mapSelection.Height = bottom - top;
        _mapSelection.Visibility = Visibility.Visible;
    }

    /// <summary>
    ///     マップ上の表示範囲の枠のドラッグに合わせて、両側のペインをスクロールします。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">ドラッグのイベントデータ。</param>
    private void DragViewport(object sender, DragDeltaEventArgs e)
    {
        if (_leftScroll == null || _rightScroll == null) return;
        var travel = Math.Max(0, MapHeight - _viewportThumb.Height);
        _viewportDragTop = Math.Max(0, Math.Min(travel, _viewportDragTop + e.VerticalChange));
        var fraction = travel <= 0 ? 0 : _viewportDragTop / travel;
        _leftScroll.ScrollToVerticalOffset(fraction * _leftScroll.ScrollableHeight);
        _rightScroll.ScrollToVerticalOffset(fraction * _rightScroll.ScrollableHeight);
        e.Handled = true;
    }

    /// <summary>
    ///     全体の差分の位置を左右に分けて示すマップを描画します。
    /// </summary>
    private void DrawMap()
    {
        if (_map == null || Lines == null) return;
        if (_leftScroll == null) _leftScroll = FindScroll(_leftList);
        if (_rightScroll == null) _rightScroll = FindScroll(_rightList);
        _map.Children.Clear();
        foreach (var start in Hunks)
        {
            var end = start + 1;
            while (end < Lines.Count && Lines[end].Kind != DiffLineKind.Unchanged &&
                   (!ViewModel.IsPatch || Lines[end].EditHunkId == Lines[start].EditHunkId)) end++;
            var kind = Lines[start].Kind;
            var mapWidth = Math.Max(0, _map.ActualWidth);
            var halfWidth = mapWidth / 2.0;
            var scale = MapHeight / MapExtent;
            var top = RowTop(start) * scale;
            var height = Math.Min(Math.Max(1, (RowTop(end) - RowTop(start)) * scale), Math.Max(0, MapHeight - top));

            if (kind == DiffLineKind.Modified)
            {
                var leftMarker = new Rectangle
                {
                    Width = halfWidth,
                    Height = height,
                    ToolTip = string.Format(StringOverlay.Get("Diff_HunkN"), Hunks.IndexOf(start) + 1),
                    Cursor = Cursors.Hand
                };
                leftMarker.SetResourceReference(Shape.FillProperty, "DiffRemoved");
                Canvas.SetLeft(leftMarker, 0);
                Canvas.SetTop(leftMarker, top);
                leftMarker.MouseLeftButtonDown += (_, _) => Jump(start);
                _map.Children.Add(leftMarker);

                var rightMarker = new Rectangle
                {
                    Width = halfWidth,
                    Height = height,
                    ToolTip = string.Format(StringOverlay.Get("Diff_HunkN"), Hunks.IndexOf(start) + 1),
                    Cursor = Cursors.Hand
                };
                rightMarker.SetResourceReference(Shape.FillProperty, "DiffAdded");
                Canvas.SetLeft(rightMarker, halfWidth);
                Canvas.SetTop(rightMarker, top);
                rightMarker.MouseLeftButtonDown += (_, _) => Jump(start);
                _map.Children.Add(rightMarker);
            }
            else
            {
                var marker = new Rectangle
                {
                    Width = halfWidth,
                    Height = height,
                    ToolTip = string.Format(StringOverlay.Get("Diff_HunkN"), Hunks.IndexOf(start) + 1),
                    Cursor = Cursors.Hand
                };
                marker.SetResourceReference(Shape.FillProperty,
                    kind == DiffLineKind.Added ? "DiffAdded" : "DiffRemoved");
                Canvas.SetLeft(marker, kind == DiffLineKind.Added ? halfWidth : 0);
                Canvas.SetTop(marker, top);
                marker.MouseLeftButtonDown += (_, _) => Jump(start);
                _map.Children.Add(marker);
            }
        }

        if (_viewportThumb == null)
        {
            _viewportThumb = new Thumb
            { Cursor = Cursors.Hand, ToolTip = StringOverlay.Get("Diff_VisibleRange"), Focusable = false };
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(0));
            border.SetValue(Border.BackgroundProperty, new DynamicResourceExtension("Ink"));
            border.SetValue(OpacityProperty, 0.20);
            _viewportThumb.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = border };
            _viewportThumb.DragStarted += (_, _) => _viewportDragTop = Canvas.GetTop(_viewportThumb);
            _viewportThumb.DragDelta += DragViewport;
        }

        Canvas.SetLeft(_viewportThumb, 1);
        Panel.SetZIndex(_viewportThumb, 1);
        _map.Children.Add(_viewportThumb);
        if (_mapSelection == null)
            _mapSelection = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(255, 210, 0)),
                BorderThickness = new Thickness(2),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
        Panel.SetZIndex(_mapSelection, 2);
        _map.Children.Add(_mapSelection);
        if (_leftScroll == null) _leftScroll = FindScroll(_leftList);
        if (_rightScroll == null) _rightScroll = FindScroll(_rightList);
        UpdateViewport(_leftScroll);
    }

    /// <summary>
    ///     比較元と比較先の画像を並べて表示し、ズーム・XOR オーバーレイ・GIF フレーム選択・ナビゲーターを作成します。
    /// </summary>
    private Task RenderImages()
    {
        var left = ViewModel.SourceImage;
        var right = ViewModel.TargetImage;
        Body.Children.Clear();
        // 前回の描画で登録したウィンドウ側のキーイベントを解除する。
        if (GifFrameHost.Tag is KeyEventHandler previousFrameKeyHandler)
        {
            RemoveHandler(Keyboard.PreviewKeyDownEvent, previousFrameKeyHandler);
            GifFrameHost.Tag = null;
        }

        GifFrameHost.Children.Clear();
        GifFrameHost.Visibility = Visibility.Collapsed;
        Body.ColumnDefinitions[1].Width = new GridLength(12);
        double width = Math.Max(left == null ? 0 : left.PixelWidth, right == null ? 0 : right.PixelWidth);
        double height = Math.Max(left == null ? 0 : left.PixelHeight, right == null ? 0 : right.PixelHeight);
        var leftCanvas = ImageCanvas(left, width, height, out var leftPixelGrid);
        var rightCanvas = ImageCanvas(right, width, height, out var rightPixelGrid);
        _leftScroll = ThemedViewer(leftCanvas, true, left == null);
        _rightScroll = ThemedViewer(rightCanvas, false, right == null);
        EnableImagePan(_leftScroll);
        EnableImagePan(_rightScroll);
        Body.Children.Add(ImagePaneHost(_leftScroll, true, left == null));
        var rightHost = ImagePaneHost(_rightScroll, false, right == null);
        Grid.SetColumn(rightHost, 2);
        Body.Children.Add(rightHost);
        _leftScroll.ScrollChanged += ScrollChanged;
        _rightScroll.ScrollChanged += ScrollChanged;
        // The navigator is supplementary: a failure to generate its preview must
        // not prevent the image zoom toolbar from being initialized.
        try
        {
            BuildImageNavigation(width, height);
        }
        catch (InvalidOperationException)
        {
            ImageDetails.Children.Clear();
            _imageNavigator = null;
            _imageViewport = null;
            _sourcePixels = _targetPixels = null;
            _sourcePixelDetails = _targetPixelDetails = null;
        }

        var zoom = _imageZoom = new Slider
        {
            Minimum = 0.05,
            Maximum = 16,
            Value = 1,
            Width = 120,
            ToolTip = StringOverlay.Get("Diff_SharedZoomTip"),
            VerticalAlignment = VerticalAlignment.Center,
            Style = TryFindResource("ZoomSlider") as Style
        };
        var zoomLabel = new TextBlock
        {
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
            Text = "100%"
        };
        zoomLabel.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        // Fit images and icons to the viewport when first opened.
        bool fitToWindow = true, fitting = false;
        void Fit(double fitWidth, double fitHeight)
        {
            if (!fitToWindow || fitWidth <= 0 || fitHeight <= 0) return;

            var viewportWidth = Math.Min(_leftScroll.ViewportWidth, _rightScroll.ViewportWidth);
            var viewportHeight = Math.Min(_leftScroll.ViewportHeight, _rightScroll.ViewportHeight);
            if (viewportWidth <= 0 || viewportHeight <= 0) return;

            var scale = Math.Min(viewportWidth / fitWidth, viewportHeight / fitHeight);
            scale = Math.Min(1.0, scale);
            if (scale <= 0) return;

            fitting = true;
            zoom.Value = Math.Max(zoom.Minimum, Math.Min(zoom.Maximum, scale));
            fitting = false;
        }

        // GIFフレームの更新では、画像枠・スライダー・ズームを再生成しない。

        void UpdateDisplayedFrames()
        {
            left = ViewModel.SourceImage;
            right = ViewModel.TargetImage;
            width = Math.Max(left?.PixelWidth ?? 0, right?.PixelWidth ?? 0);
            height = Math.Max(left?.PixelHeight ?? 0, right?.PixelHeight ?? 0);
            BitmapSource xorImage = null; // フレームが変わったのでXORを再計算する

            if (left != null && right != null &&
                (_sourceImageXorMode || _targetImageXorMode))
                xorImage = CreateXorBitmap(left, right);

            Canvas UpdateCanvas(Canvas canvas, BitmapSource pixels)
            {
                var replacement = ImageCanvas(pixels, width, height, out var newGrid);
                canvas.Width = width;
                canvas.Height = height;
                canvas.Children.Clear();
                while (replacement.Children.Count > 0)
                {
                    var child = replacement.Children[0];
                    replacement.Children.RemoveAt(0);
                    canvas.Children.Add(child);
                }

                if (newGrid != null)
                    newGrid.Visibility = zoom.Value >= zoom.Maximum - 0.0001
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                return newGrid;
            }

            leftPixelGrid = UpdateCanvas(leftCanvas,
                _sourceImageXorMode && xorImage != null ? xorImage : left);
            rightPixelGrid = UpdateCanvas(rightCanvas,
                _targetImageXorMode && xorImage != null ? xorImage : right);
            UpdateImageNavigation();
        }

        var wrapper = ImageToolbar as Panel;
        if (wrapper != null)
        {
            wrapper.Children.Clear();
            // Reserve the action buttons first; only the zoom track consumes spare width.
            // At very narrow window sizes, scale the complete row rather than hiding XOR.
            var toolbar = new Grid { MinWidth = 420 };
            toolbar.ColumnDefinitions.Add(new ColumnDefinition());
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var toolbarView = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
            // Let the parent measure the Viewbox. Its own ActualWidth may still be
            // zero after the collapsed toolbar is rebuilt during a reload.
            toolbar.SetBinding(WidthProperty, new Binding("ActualWidth") { Source = ImageControlRow });
            toolbarView.Child = toolbar;
            wrapper.Children.Add(toolbarView);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            Grid.SetColumn(buttons, 1);
            toolbar.Children.Add(buttons);
            zoom.Width = double.NaN;
            zoom.HorizontalAlignment = HorizontalAlignment.Stretch;
            var chrome = new Border
            {
                CornerRadius = new CornerRadius(18),
                Padding = new Thickness(10, 4, 6, 4),
                Margin = new Thickness(0, 0, 2, 8),
                Child = new Grid { VerticalAlignment = VerticalAlignment.Center }
            };
            chrome.SetResourceReference(Border.BackgroundProperty, "CardBackground");
            chrome.SetResourceReference(Border.BorderBrushProperty, "Line");
            chrome.BorderThickness = new Thickness(1);
            var chromeRow = (Grid)chrome.Child;
            for (var column = 0; column < 5; column++)
                chromeRow.ColumnDefinitions.Add(new ColumnDefinition
                { Width = column == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            var zoomTitle = new TextBlock
            {
                Text = StringOverlay.Get("Diff_Zoom"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 8, 0),
                FontWeight = FontWeights.SemiBold
            };
            zoomTitle.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            chromeRow.Children.Add(zoomTitle);
            chromeRow.Children.Add(ImageZoomStepButton("\uE738", StringOverlay.Get("Diff_ZoomOut"), () =>
            {
                fitToWindow = false;
                zoom.Value = Math.Max(zoom.Minimum, zoom.Value / 1.25);
            }));
            chromeRow.Children.Add(zoom);
            chromeRow.Children.Add(ImageZoomStepButton("\uE710", StringOverlay.Get("Diff_ZoomIn"), () =>
            {
                fitToWindow = false;
                zoom.Value = Math.Min(zoom.Maximum, zoom.Value * 1.25);
            }));
            chromeRow.Children.Add(zoomLabel);
            for (var column = 0; column < chromeRow.Children.Count; column++)
                Grid.SetColumn(chromeRow.Children[column], column);
            toolbar.Children.Add(chrome);
            var fitWidth = width;
            var fitHeight = height;
            AddIconActionButton(buttons, DifferencerStatusIcons.GetCustomIcon(72), StringOverlay.Get("Diff_Fit"), () =>
            {
                fitToWindow = true;
                Fit(fitWidth, fitHeight);
            });
            AddIconActionButton(buttons, DifferencerStatusIcons.GetCustomIcon(73), "100%", () =>
            {
                fitToWindow = false;
                zoom.Value = 1;
            });

            void AddXorButton(bool sourceSide, int iconIndex, string title)
            {
                var xorIcon = new Image
                { Source = DifferencerStatusIcons.GetCustomIcon(iconIndex), Stretch = Stretch.Uniform };
                RenderOptions.SetBitmapScalingMode(xorIcon, BitmapScalingMode.HighQuality);
                var xorButton = new ToggleButton
                {
                    Content = xorIcon,
                    Style = TryFindResource("IconToggleButton") as Style,
                    ToolTip = title,
                    Margin = new Thickness(0, 0, 2, 8),
                    IsChecked = sourceSide ? _sourceImageXorMode : _targetImageXorMode,
                    IsEnabled = left != null && right != null
                };
                AutomationProperties.SetName(xorButton, title);

                void ApplyXorMode()
                {
                    var enabled = xorButton.IsChecked == true;
                    xorIcon.Opacity = enabled ? 1.0 : 0.35;
                    UpdateDisplayedFrames();
                }

                void SaveXorMode(object sender, RoutedEventArgs e)
                {
                    var enabled = xorButton.IsChecked == true;
                    if (sourceSide) _sourceImageXorMode = enabled;
                    else _targetImageXorMode = enabled;
                    SettingsService.SaveDiffFlag(sourceSide ? "source-image-xor" : "target-image-xor", enabled);
                    ApplyXorMode();
                }

                xorButton.Checked += SaveXorMode;
                xorButton.Unchecked += SaveXorMode;
                buttons.Children.Add(xorButton);
                ApplyXorMode();
            }

            AddXorButton(true, 137, StringOverlay.Get("Diff_SourceXor"));
            AddXorButton(false, 138, StringOverlay.Get("Diff_TargetXor"));
            foreach (FrameworkElement button in buttons.Children)
                button.Margin = new Thickness(0, 0, 2, 8);
            wrapper.Visibility = Visibility.Visible;
            ImageControlRow.Visibility = Visibility.Visible;
        }

        if (ViewModel.ImageFrameCount > 1)
        {
            var frames = new Slider
            {
                Minimum = 0,
                Maximum = ViewModel.ImageFrameCount - 1,
                Value = ViewModel.ImageFrameIndex,
                TickFrequency = 1,
                SmallChange = 1,
                LargeChange = 1,
                IsSnapToTickEnabled = true,
                IsMoveToPointEnabled = false,
                ToolTip = ViewModel.ImageFrameDetails,
                Style = (Style)FindResource("FrameSlider")
            };

            var frameLabel = new TextBlock
            {
                Text = ViewModel.ImageFrameDetails,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4)
            };
            frameLabel.SetResourceReference(TextBlock.ForegroundProperty, "Ink");

            var framePanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 8)
            };
            framePanel.Children.Add(frameLabel);

            // GIFフレームの前後ボタンは、Sliderのテンプレートではなく独立したButtonにする。
            // IsMoveToPointEnabledの影響を受けず、必ず1フレームずつ移動する。
            Button FrameStepButton(string caption, string tip, int step)
            {
                var glyph = new TextBlock
                {
                    Text = caption,
                    FontSize = 24,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    IsHitTestVisible = false
                };
                glyph.SetResourceReference(TextBlock.ForegroundProperty, "Ink");

                var button = new Button
                {
                    Content = glyph,
                    Width = 34,
                    Height = 34,
                    Padding = new Thickness(0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 6, 0),
                    ToolTip = tip
                };
                // 明示的なContentPresenterで、アプリ共通Buttonスタイルによる
                // Content非表示を回避する。
                var border = new FrameworkElementFactory(typeof(Border));
                border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
                border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
                border.SetResourceReference(Border.BackgroundProperty, "CardBackground");
                border.SetResourceReference(Border.BorderBrushProperty, "Line");
                var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
                presenter.SetValue(ContentPresenter.ContentProperty,
                    new TemplateBindingExtension(ContentProperty));
                presenter.SetValue(HorizontalAlignmentProperty,
                    HorizontalAlignment.Center);
                presenter.SetValue(VerticalAlignmentProperty,
                    VerticalAlignment.Center);
                border.AppendChild(presenter);
                button.Template = new ControlTemplate(typeof(Button)) { VisualTree = border };
                button.Click += (_, _) =>
                    frames.Value = Math.Max(frames.Minimum,
                        Math.Min(frames.Maximum, frames.Value + step));
                return button;
            }

            var frameRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            frameRow.Children.Add(FrameStepButton("−", StringOverlay.Get("Diff_PreviousFrame"), -1));
            frameRow.Children.Add(frames);
            frameRow.Children.Add(FrameStepButton("+", StringOverlay.Get("Diff_NextFrame"), 1));
            framePanel.Children.Add(frameRow);

            // ウィンドウ内のどこにフォーカスがあっても左右キーでフレーム移動する。
            // RenderImages再実行時は冒頭で前回の登録を解除する。
            KeyEventHandler frameKeyHandler = (_, e) =>
            {
                if (GifFrameHost.Visibility != Visibility.Visible) return;
                int step;
                if (e.Key == Key.Left) step = -1;
                else if (e.Key == Key.Right) step = 1;
                else return;

                frames.Value = Math.Max(frames.Minimum,
                    Math.Min(frames.Maximum, frames.Value + step));
                e.Handled = true;
            };
            AddHandler(Keyboard.PreviewKeyDownEvent,
                frameKeyHandler, true);
            GifFrameHost.Tag = frameKeyHandler;

            // ラベル、前後ボタン、スライダーをまとめて角丸枠で囲む。
            var frameBorder = new Border
            {
                CornerRadius = new CornerRadius(18),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 8, 0, 8),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = framePanel
            };
            frameBorder.SetResourceReference(Border.BackgroundProperty, "CardBackground");
            frameBorder.SetResourceReference(Border.BorderBrushProperty, "Line");

            // 余白は外枠に集約する。
            framePanel.Margin = new Thickness(0);
            GifFrameHost.Children.Add(frameBorder);
            GifFrameHost.Visibility = Visibility.Visible;

            var requestedFrame = ViewModel.ImageFrameIndex;
            var processingFrame = false;

            async void SelectFrame()
            {
                // ドラッグ中もValueChangedを受け付ける。非同期処理中は最後の要求を保持する。
                if (processingFrame) return;
                processingFrame = true;
                try
                {
                    while (IsLoaded && requestedFrame != ViewModel.ImageFrameIndex)
                    {
                        var index = requestedFrame;
                        var changed = await ViewModel.SelectImageFrameAsync(index);
                        if (!IsLoaded) return;
                        if (!changed) break;

                        // RenderImages()を呼ばないので、Thumbのキャプチャとズームが維持される。
                        UpdateDisplayedFrames();
                        frameLabel.Text = ViewModel.ImageFrameDetails;
                        frames.ToolTip = ViewModel.ImageFrameDetails;
                    }
                }
                catch (Exception ex)
                {
                    ViewModel.ReportError(ex);
                }
                finally
                {
                    processingFrame = false;
                }
            }

            frames.ValueChanged += (_, _) =>
            {
                requestedFrame = (int)Math.Round(frames.Value);
                SelectFrame();
            };
        }

        zoom.ValueChanged += (_, e) =>
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
        _imageFitHandler = (_, _) =>
        {
            Fit(width, height);
            UpdateImageNavigation();
        };
        Body.SizeChanged += _imageFitHandler;
        // Complete layout and fitting in the same dispatcher turn as the swap.
        // Yielding here would expose the initial 100% image before its fitted size.
        Body.UpdateLayout();
        Fit(width, height);
        Body.UpdateLayout();
        UpdateImageNavigation();
        return Task.CompletedTask;
    }

    /// <summary>
    ///     テーマに合わせた画像表示用の ScrollViewer を作成します。
    /// </summary>
    /// <param name="content">表示する内容。</param>
    /// <param name="sourceSide">比較元の場合は true。</param>
    /// <param name="missing">ファイルがない場合は true。</param>
    /// <returns>作成した ScrollViewer。</returns>
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
        viewer.SetResourceReference(BackgroundProperty, "CardBackground");
        viewer.SetResourceReference(ForegroundProperty, "Ink");
        viewer.SetResourceReference(BorderBrushProperty, sourceSide ? "SourceColor" : "TargetColor");
        if (missing)
        {
            viewer.Background = MissingImageBrush;
            viewer.BorderBrush = MissingImageBrush;
        }

        return viewer;
    }

    /// <summary>
    ///     画像のペインを、側ごとの色の枠で囲みます。
    /// </summary>
    /// <param name="viewer">画像の ScrollViewer。</param>
    /// <param name="source">比較元の場合は true。</param>
    /// <param name="missing">ファイルがない場合は true。</param>
    /// <returns>作成した枠。</returns>
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

    /// <summary>
    ///     画像をドラッグで移動できるよう、マウスのハンドラーを登録します。
    /// </summary>
    /// <param name="viewer">対象の ScrollViewer。</param>
    private void EnableImagePan(ScrollViewer viewer)
    {
        if (viewer == null) return;
        viewer.PreviewMouseLeftButtonDown += ImageViewer_PreviewMouseLeftButtonDown;
        viewer.PreviewMouseMove += ImageViewer_PreviewMouseMove;
        viewer.PreviewMouseLeftButtonUp += ImageViewer_PreviewMouseLeftButtonUp;
        viewer.LostMouseCapture += (_, _) => StopImagePan();
    }

    /// <summary>
    ///     画像のドラッグによる移動を開始し、クリック位置のピクセル情報を表示します。スクロール バー上では開始しません。
    /// </summary>
    /// <param name="sender">対象の ScrollViewer。</param>
    /// <param name="e">マウス イベントのデータ。</param>
    private void ImageViewer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ScrollBar>(e.OriginalSource as DependencyObject) != null)
            return;
        var viewer = sender as ScrollViewer;
        if (viewer == null) return;
        if (viewer.Content is Canvas canvas)
            SetImagePointDetails(e.GetPosition(canvas), true);
        _imagePanning = true;
        _imagePanLast = e.GetPosition(viewer);
        viewer.CaptureMouse();
        viewer.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    /// <summary>
    ///     カーソル位置のピクセル情報を表示し、ドラッグ中は両側の画像を移動します。
    /// </summary>
    /// <param name="sender">対象の ScrollViewer。</param>
    /// <param name="e">マウス イベントのデータ。</param>
    private void ImageViewer_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        var viewer = sender as ScrollViewer;
        if (viewer == null) return;
        if (viewer.Content is Canvas canvas)
            SetImagePointDetails(e.GetPosition(canvas), false);
        if (!_imagePanning || e.LeftButton != MouseButtonState.Pressed) return;
        var now = e.GetPosition(viewer);
        var delta = now - _imagePanLast;
        _imagePanLast = now;
        if (_leftScroll == null || _rightScroll == null) return;
        _scrolling = true;
        _leftScroll.ScrollToHorizontalOffset(_leftScroll.HorizontalOffset - delta.X);
        _leftScroll.ScrollToVerticalOffset(_leftScroll.VerticalOffset - delta.Y);
        _rightScroll.ScrollToHorizontalOffset(_rightScroll.HorizontalOffset - delta.X);
        _rightScroll.ScrollToVerticalOffset(_rightScroll.VerticalOffset - delta.Y);
        _scrolling = false;
        e.Handled = true;
    }

    /// <summary>
    ///     画像のドラッグを終了します。
    /// </summary>
    /// <param name="sender">対象の ScrollViewer。</param>
    /// <param name="e">マウス イベントのデータ。</param>
    private void ImageViewer_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_imagePanning) return;
        StopImagePan();
        if (sender is ScrollViewer viewer && viewer.Content is Canvas canvas)
            SetImagePointDetails(e.GetPosition(canvas), false);
        e.Handled = true;
    }

    /// <summary>
    ///     画像のドラッグを終了し、マウスのキャプチャを解放します。
    /// </summary>
    private void StopImagePan()
    {
        if (!_imagePanning) return;
        _imagePanning = false;
        if (_leftScroll != null && _leftScroll.IsMouseCaptured) _leftScroll.ReleaseMouseCapture();
        if (_rightScroll != null && _rightScroll.IsMouseCaptured) _rightScroll.ReleaseMouseCapture();
    }

    /// <summary>
    ///     ビジュアル ツリーを親方向にたどり、指定した型の要素を探します。
    /// </summary>
    /// <typeparam name="T">探す要素の型。</typeparam>
    /// <param name="current">探し始める要素。</param>
    /// <returns>見つかった要素。見つからない場合は null。</returns>
    private static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    /// <summary>
    ///     画像のズームを 1 段階変更するボタンを作成します。
    /// </summary>
    /// <param name="glyph">ボタンの記号。</param>
    /// <param name="tip">ツールチップ。</param>
    /// <param name="action">クリック時の処理。</param>
    /// <returns>作成したボタン。</returns>
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
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>
    ///     拡大時にピクセルの境界線を描くタイル ブラシを作成します。線は 1600% で 1 DIP になる太さです。
    /// </summary>
    /// <returns>作成したブラシ。</returns>
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

    /// <summary>
    ///     画像を表示するキャンバスと、ピクセル境界線のグリッドを作成します。
    /// </summary>
    /// <param name="image">表示する画像。</param>
    /// <param name="width">キャンバスの幅。</param>
    /// <param name="height">キャンバスの高さ。</param>
    /// <param name="pixelGrid">作成したピクセル グリッド。</param>
    /// <returns>作成したキャンバス。</returns>
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
        else
        {
            canvas.Background = MissingImageBrush;
        }

        return canvas;
    }

    /// <summary>
    ///     アイコンとラベル付きのボタンをパネルに追加します。
    /// </summary>
    /// <param name="panel">追加先のパネル。</param>
    /// <param name="icon">ボタンのアイコン。</param>
    /// <param name="text">ボタンのラベル。</param>
    /// <param name="command">実行するコマンド。</param>
    /// <returns>追加したボタン。</returns>
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
        AutomationProperties.SetName(button, text ?? string.Empty);
        button.Command = command;
        panel.Children.Add(button);
        return button;
    }

    /// <summary>
    ///     アイコンとツールチップ付きのボタンをパネルに追加します。
    /// </summary>
    /// <param name="panel">追加先のパネル。</param>
    /// <param name="icon">ボタンのアイコン。</param>
    /// <param name="text">ツールチップ。</param>
    /// <param name="action">クリック時の処理。</param>
    private void AddIconActionButton(Panel panel, ImageSource icon, string text, Action action)
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
        AutomationProperties.SetName(button, text ?? string.Empty);
        button.Click += (_, _) => action();
        panel.Children.Add(button);
    }

    private sealed record RenderedPane(
        List<DiffLine> Lines,
        List<Block> Blocks,
        List<WhitespaceRow> Markers,
        List<int> LexStates);
}
