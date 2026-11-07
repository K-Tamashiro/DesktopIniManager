using DesktopIniManager.Models;
using DesktopIniManager.Properties;
using DesktopIniManager.Services;
using DesktopIniManager.ViewModels;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopIniManager.Views;

public partial class GrepWindow
{
    internal GrepWindowViewModel ViewModel { get; }
    private bool _resultGroupsExpanded = true;
    private bool _applyingGroupExpansion;
    private readonly Dictionary<string, bool> _resultGroupStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    private Point _groupDragStart;
    private string _groupDragPath;
    private HwndSource _inputSource;
    private double _matchColumnBaseWidth = 550;

    /// <summary>
    ///     Grep ウィンドウを作成し、ViewModel、アイコン、列幅、検索範囲を初期化します。
    /// </summary>
    /// <param name="scopeProvider">メイン画面でチェックされたフォルダーを取得する処理。</param>
    /// <param name="initialScopes">初期の検索範囲。</param>
    public GrepWindow(Func<IReadOnlyList<string>> scopeProvider, IReadOnlyList<string> initialScopes)
    {
        ViewModel = new GrepWindowViewModel(scopeProvider, Dispatcher, new UserDialogService(this));
        InitializeComponent();
        Icon = DifferencerStatusIcons.GetCustomIcon(71) ?? Icon;
        ClearQueryButton.Click += (_, _) => Dispatcher.BeginInvoke(new Action(() => QueryBox.Focus()), DispatcherPriority.Input);
        ClearListFilterButton.Click += (_, _) => Dispatcher.BeginInvoke(new Action(() => FilterBox.Focus()), DispatcherPriority.Input);
        DataContext = ViewModel;
        QueryBox.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None || QueryBox.IsHistoryOpen) return;
            QueryBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            if (ViewModel.SearchCommand.CanExecute(null)) ViewModel.SearchCommand.Execute(null);
            args.Handled = true;
        };
        ViewModel.DialogTitle = Title;
        ViewModel.SearchHistoryRequested += () => { QueryBox.CommitHistory(); ExtensionsText.CommitHistory(); };
        ViewModel.MatchScrollRequested += ScrollToMatch;
        ViewModel.ResultGroupsResetRequested += () => { _resultGroupStates.Clear(); };
        ViewModel.BrowseEditorRequested += BrowseEditor;
        ViewModel.SaveResultsRequested += SaveResults;
        ViewModel.CloseRequested += Close;
        ViewModel.ResultGroupsExpansionRequested += SetResultGroupsExpanded;
        ViewModel.Initialize(initialScopes);
        AllowDrop = true;
        PreviewDragEnter += FolderDropPreview;
        PreviewDragOver += FolderDropPreview;
        PreviewDrop += GrepWindow_Drop;
        ApplyGrepColumnWidths(ViewModel.ColumnWidths);
        HookPathBox(EditorBox);
        HookPathBox(EditorArgumentsBox);
        // Re-selecting the same history item must also restore its preset arguments.
        EditorBox.HistoryItemApplied += (_, _) => ViewModel.ApplyEditorPresetCommand.Execute(null);
        ResultsGrid.PreviewMouseWheel += ResultsGrid_PreviewMouseWheel;
        SourceInitialized += (_, _) =>
        {
            _inputSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _inputSource?.AddHook(HorizontalWheelMessage);
        };
        Closed += (_, _) =>
        {
            _inputSource?.RemoveHook(HorizontalWheelMessage);
            _inputSource = null;
        };
        Loaded += (_, _) =>
        {
            ApplyGrepColumnWidths(ViewModel.ColumnWidths);
            Dispatcher.BeginInvoke(new Action(() => ApplyGrepColumnWidths(ViewModel.ColumnWidths)), DispatcherPriority.Loaded);
            QueryBox.Focus();
            ShowTextEnd(EditorBox);
            ShowTextEnd(EditorArgumentsBox);
            ApplyGrepIcons();
            if (ResultGroupsToggle != null)
            {
                ResultGroupsToggle.IsChecked = _resultGroupsExpanded;
                ResultGroupsToggle.Checked += ResultGroupsToggle_Changed;
                ResultGroupsToggle.Unchecked += ResultGroupsToggle_Changed;
                UpdateResultGroupsIcon();
            }
        };
    }

    /// <summary>
    ///     ファイルのドラッグ中にコピーのカーソルを表示します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">ドラッグ イベントのデータ。</param>
    private static void FolderDropPreview(object sender, DragEventArgs e)
    {
        if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    /// <summary>
    ///     ドロップされたフォルダーを検索範囲に追加します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">ドラッグ イベントのデータ。</param>
    private void GrepWindow_Drop(object sender, DragEventArgs e)
    {
        List<string> folders = FoldersFromDrop(e.Data);
        if (folders.Count == 0) return;
        ViewModel.AddDroppedFolders(folders);
        e.Handled = true;
    }

    /// <summary>
    ///     ドロップされたデータからフォルダーを重複なく取り出します。ファイルの場合はその親フォルダーを使います。
    /// </summary>
    /// <param name="data">ドロップされたデータ。</param>
    /// <returns>フォルダーのパス。</returns>
    private static List<string> FoldersFromDrop(IDataObject data)
    {
        var folders = new List<string>();
        if (data == null || !data.GetDataPresent(DataFormats.FileDrop))
            return folders;

        var paths = data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            string folder = Directory.Exists(path)
                ? path
                : File.Exists(path) ? Path.GetDirectoryName(path) : null;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
            string normalized = Path.GetFullPath(folder)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (seen.Add(normalized))
                folders.Add(normalized);
        }

        return folders;
    }

    /// <summary>
    ///     Shift キーを押しながらのマウス ホイールで、結果一覧を横にスクロールします。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">マウス ホイールのイベントデータ。</param>
    private void ResultsGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0) return;
        if (ScrollResultsHorizontally(-e.Delta)) e.Handled = true;
    }

    /// <summary>
    ///     結果一覧を横にスクロールします。
    /// </summary>
    /// <param name="delta">ホイールの回転量。</param>
    /// <returns>スクロールした場合は true。</returns>
    private bool ScrollResultsHorizontally(int delta)
    {
        ScrollViewer viewer = FindVisualDescendants<ScrollViewer>(ResultsGrid).FirstOrDefault();
        if (viewer == null) return false;
        viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset + delta * 48.0 / 120);
        return true;
    }

    /// <summary>
    ///     チルト ホイール (WM_MOUSEHWHEEL) のメッセージを処理し、カーソルが結果一覧の上にある場合は横にスクロールします。
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
        if (ResultsGrid == null) return IntPtr.Zero;

        long coordinates = lParam.ToInt64();
        var point = ResultsGrid.PointFromScreen(new Point(
            unchecked((short)(coordinates & 0xffff)),
            unchecked((short)((coordinates >> 16) & 0xffff))));
        if (point.X < 0 || point.Y < 0 || point.X >= ResultsGrid.ActualWidth || point.Y >= ResultsGrid.ActualHeight)
            return IntPtr.Zero;

        int delta = unchecked((short)((wParam.ToInt64() >> 16) & 0xffff));
        handled = ScrollResultsHorizontally(delta);
        return IntPtr.Zero;
    }

    /// <summary>
    ///     パスの入力欄で常に末尾が見えるよう、イベント ハンドラーを登録します。
    /// </summary>
    /// <param name="box">対象の入力欄。</param>
    private void HookPathBox(TextBox box)
    {
        box.GotKeyboardFocus += (_, _) => ShowTextEnd(box);
        box.LostKeyboardFocus += (_, _) => ShowTextEnd(box);
        box.TextChanged += (_, _) =>
        {
            if (!box.IsKeyboardFocusWithin) ShowTextEnd(box);
        };
    }

    /// <summary>
    ///     入力欄のキャレットを末尾に移動し、テキストの末尾を表示します。
    /// </summary>
    /// <param name="box">対象の入力欄。</param>
    private static void ShowTextEnd(TextBox box)
    {
        if (box == null) return;
        box.Dispatcher.BeginInvoke(new Action(() =>
        {
            string text = box.Text;
            box.CaretIndex = text.Length;
            box.ScrollToHorizontalOffset(Math.Max(0, box.ExtentWidth - box.ViewportWidth));
        }), DispatcherPriority.Loaded);
    }

    /// <summary>Applies the current global expansion state to a newly realized file group.</summary>
    private void ResultGroupExpander_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyGroupExpansion(sender as Expander);
    }

    /// <summary>
    ///     グループの表示内容が変わったときに、記録している展開状態を反映します。
    /// </summary>
    /// <param name="sender">対象の Expander。</param>
    /// <param name="e">イベントデータ。</param>
    private void ResultGroupExpander_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        ApplyGroupExpansion(sender as Expander);
    }

    /// <summary>
    ///     グループの展開状態を、記録している状態または全体の既定値に合わせます。
    /// </summary>
    /// <param name="expander">対象の Expander。</param>
    private void ApplyGroupExpansion(Expander expander)
    {
        if (expander == null) return;
        string key = ResultGroupKey(expander);
        bool expanded = !string.IsNullOrEmpty(key) && _resultGroupStates.TryGetValue(key, out bool stored)
            ? stored : _resultGroupsExpanded;
        if (expander.IsExpanded == expanded) return;
        _applyingGroupExpansion = true;
        try { expander.IsExpanded = expanded; }
        finally { _applyingGroupExpansion = false; }
    }

    /// <summary>Remembers an individual file group's expanded state.</summary>
    private void ResultGroupExpander_Expanded(object sender, RoutedEventArgs e)
    {
        if (_applyingGroupExpansion) return;
        string key = ResultGroupKey(sender as Expander);
        if (!string.IsNullOrEmpty(key)) _resultGroupStates[key] = true;
    }

    /// <summary>Remembers an individual file group's collapsed state.</summary>
    private void ResultGroupExpander_Collapsed(object sender, RoutedEventArgs e)
    {
        if (_applyingGroupExpansion) return;
        string key = ResultGroupKey(sender as Expander);
        if (!string.IsNullOrEmpty(key)) _resultGroupStates[key] = false;
    }

    /// <summary>Returns the stable file-path key for a result group.</summary>
    private static string ResultGroupKey(Expander expander)
    { return Convert.ToString((expander.DataContext as CollectionViewGroup)?.Name) ?? string.Empty; }

    /// <summary>Updates realized groups and the default state for groups realized after scrolling.</summary>
    private void SetResultGroupsExpanded(bool expanded)
    {
        _resultGroupsExpanded = expanded;
        if (ResultGroupsToggle != null && ResultGroupsToggle.IsChecked != expanded)
        {
            _updatingGroupToggle = true;
            ResultGroupsToggle.IsChecked = expanded;
            _updatingGroupToggle = false;
            UpdateResultGroupsIcon();
        }
        _resultGroupStates.Clear();
        if (ResultsGrid == null)
            return;
        _applyingGroupExpansion = true;
        try
        {
            foreach (Expander expander in FindVisualDescendants<Expander>(ResultsGrid)
                .Where(item => string.Equals(item.Name, "ResultGroupExpander", StringComparison.Ordinal)))
                expander.IsExpanded = expanded;
        }
        catch (InvalidOperationException) { }
        finally { _applyingGroupExpansion = false; }
    }

    /// <summary>
    ///     指定した結果が見えるよう結果一覧をスクロールします。
    /// </summary>
    /// <param name="match">表示する結果。</param>
    private void ScrollToMatch(GrepMatch match)
    {
        if (match == null || ResultsGrid == null) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                ResultsGrid.ScrollIntoView(match);
            }
            catch
            {
                //無視
            }
            ScrollViewer viewer = FindVisualDescendants<ScrollViewer>(ResultsGrid).FirstOrDefault();
            viewer?.ScrollToEnd();
        }), DispatcherPriority.Background);
    }

    /// <summary>
    ///     グループ見出しからのファイルのドラッグに備えて、押した位置とグループを記録します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">マウス イベントのデータ。</param>
    private void GroupHeader_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _groupDragPath = ResultGroupKey(FindAncestor<Expander>(sender as DependencyObject) ?? sender as Expander);
        _groupDragStart = e.GetPosition(null);
    }

    /// <summary>
    ///     グループ見出しから一定距離ドラッグしたときに、そのファイルを外部へドラッグします。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">マウス イベントのデータ。</param>
    private void GroupHeader_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || string.IsNullOrEmpty(_groupDragPath)) return;
        if (!DropOutService.MovedEnough(_groupDragStart, e.GetPosition(null))) return;
        string group = _groupDragPath;
        _groupDragPath = null;
        string path = ViewModel.FilePathForGroup(group);
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
            DropOutService.DragExisting(sender as DependencyObject, new[] { path });
    }

    /// <summary>
    ///     グループ見出しのクリックで展開と折りたたみを切り替えます。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">マウス イベントのデータ。</param>
    private void GroupHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Button) return;
        var expander = FindAncestor<Expander>(sender as DependencyObject);
        if (expander == null) return;
        expander.IsExpanded = !expander.IsExpanded;
        e.Handled = true;
    }

    /// <summary>
    ///     グループの結果を一覧から削除します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">イベントデータ。</param>
    private void RemoveGroup_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        string key = GroupKeyFromSender(sender as DependencyObject);
        if (!string.IsNullOrEmpty(key)) ViewModel.RemoveGroup(key);
    }

    /// <summary>
    ///     グループのファイル パスをクリップボードにコピーします。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">イベントデータ。</param>
    private void CopyGroupPath_Click(object sender, RoutedEventArgs e)
    {
        string key = GroupKeyFromSender(sender as DependencyObject);
        if (!string.IsNullOrEmpty(key)) ViewModel.CopyGroupPath(key);
    }

    /// <summary>
    ///     グループのファイルがあるフォルダーを開きます。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">イベントデータ。</param>
    private void OpenGroupFolder_Click(object sender, RoutedEventArgs e)
    {
        string key = GroupKeyFromSender(sender as DependencyObject);
        if (!string.IsNullOrEmpty(key)) ViewModel.OpenGroupFolder(key);
    }

    /// <summary>
    ///     メニュー項目やボタンから、対象のグループのキーを取得します。
    /// </summary>
    /// <param name="source">イベントの送信元。</param>
    /// <returns>グループのキー。見つからない場合は null。</returns>
    private static string GroupKeyFromSender(DependencyObject source)
    {
        var menuItem = source as MenuItem;
        var menu = menuItem?.Parent as ContextMenu;
        DependencyObject start = menu?.PlacementTarget as DependencyObject ?? source;
        var expander = FindAncestor<Expander>(start);
        if (expander == null && menuItem != null)
            expander = FindAncestor<Expander>(menuItem);
        return expander == null ? string.Empty : ResultGroupKey(expander);
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
            var match = current as T;
            if (match != null) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    /// <summary>
    ///     保存先を選ばせて、表示中の結果をテキスト ファイルに保存します。
    /// </summary>
    private void SaveResults()
    {
        var dialog = new SaveFileDialog
        {
            Filter = Strings.Grep_SaveFilter,
            FileName = "grep-results.txt",
            AddExtension = true,
            DefaultExt = "txt"
        };
        if (dialog.ShowDialog(this) != true) return;
        try { ViewModel.SaveResultsTo(dialog.FileName); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ErrorMessages.English(ex), Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Enumerates visual descendants of the requested type.</summary>
    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root == null) yield break;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            var match = child as T;
            if (match != null) yield return match;
            foreach (T descendant in FindVisualDescendants<T>(child)) yield return descendant;
        }
    }

    /// <summary>
    ///     エディターの実行ファイルを選ばせて設定します。
    /// </summary>
    private void BrowseEditor()
    {
        var dialog = new OpenFileDialog { Filter = Strings.Grep_EditorFilter, CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        ViewModel.EditorPath = dialog.FileName; EditorBox.CommitHistory();
        ShowTextEnd(EditorBox);
    }

    /// <summary>
    ///     検索範囲を指定したフォルダーに置き換えます。
    /// </summary>
    /// <param name="scopes">検索範囲のフォルダー。</param>
    public void SetExplicitScopes(IReadOnlyList<string> scopes) => ViewModel.SetExplicitScopes(scopes);

    /// <summary>
    ///     メイン画面でチェックされているフォルダーを検索範囲として読み込み直します。
    /// </summary>
    public void ReloadFromMainWindow() => ViewModel.ReloadFromMainWindow();

    /// <summary>
    ///     閉じる前に履歴と設定を保存し、列幅を記録します。履歴を保存できなかった場合は閉じません。
    /// </summary>
    /// <param name="e">イベントデータ。</param>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!ViewModel.Close()) { e.Cancel = true; return; }
        double[] widths = ResultsGrid.Columns.Select(column => column.ActualWidth).ToArray();
        if (widths.Length > 4 && ResultsGrid.Columns[4].Width.UnitType != DataGridLengthUnitType.Pixel)
            widths[4] = _matchColumnBaseWidth;
        ViewModel.SaveColumnWidths(widths);
        base.OnClosing(e);
    }

    /// <summary>
    ///     保存された列幅が有効であれば適用し、そうでなければ既定の列幅を使います。
    /// </summary>
    /// <param name="widths">保存された列幅。</param>
    private void ApplyGrepColumnWidths(double[] widths)
    {
        ResultsGrid.RowHeaderWidth = 0;
        double[] defaults = { 200, 300, 80, 80, 550 };
        double[] mins = { 90, 160, 60, 60, 200 };
        bool saved = widths != null && widths.Length >= 5 &&
            widths[0] >= 90 && widths[1] >= 160 && widths[2] >= 50 && widths[3] >= 50 && widths[4] >= 200;
        for (int index = 0; index < ResultsGrid.Columns.Count && index < defaults.Length; index++)
        {
            if (index == 4)
            {
                // The match column keeps its normal width for short results, but grows to
                // the widest realized match. Horizontal scrolling therefore appears only
                // when the match text actually needs more room.
                _matchColumnBaseWidth = saved ? widths[index] : defaults[index];
                ResultsGrid.Columns[index].MinWidth = Math.Max(mins[index], _matchColumnBaseWidth);
                ResultsGrid.Columns[index].Width = new DataGridLength(1, DataGridLengthUnitType.SizeToCells);
                continue;
            }

            ResultsGrid.Columns[index].MinWidth = mins[index];
            ResultsGrid.Columns[index].Width = new DataGridLength(saved ? widths[index] : defaults[index]);
        }
    }

    /// <summary>
    ///     行のテキストが読み込まれたときに、一致箇所を強調表示します。
    /// </summary>
    /// <param name="sender">対象の TextBlock。</param>
    /// <param name="e">イベントデータ。</param>
    private void MatchLine_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyMatchHighlight(sender as TextBlock);
    }

    /// <summary>
    ///     行の表示内容が変わったときに、一致箇所を強調表示し直します。
    /// </summary>
    /// <param name="sender">対象の TextBlock。</param>
    /// <param name="e">イベントデータ。</param>
    private void MatchLine_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        ApplyMatchHighlight(sender as TextBlock);
    }

    /// <summary>
    ///     行のテキスト内の一致箇所を赤で強調表示します。
    /// </summary>
    /// <param name="block">対象の TextBlock。</param>
    private static void ApplyMatchHighlight(TextBlock block)
    {
        if (block == null) return;
        var match = block.DataContext as GrepMatch;
        string text = match?.LineText ?? string.Empty;
        string needle = match?.Highlight;
        block.Inlines.Clear();
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(needle))
        {
            block.Text = text;
            return;
        }
        int start = 0;
        while (start < text.Length)
        {
            int index = text.IndexOf(needle, start, StringComparison.CurrentCultureIgnoreCase);
            if (index < 0)
            {
                block.Inlines.Add(new Run(text.Substring(start)));
                break;
            }
            if (index > start) block.Inlines.Add(new Run(text.Substring(start, index - start)));
            block.Inlines.Add(new Run(text.Substring(index, needle.Length))
            {
                Foreground = Brushes.Red,
                FontWeight = FontWeights.SemiBold
            });
            start = index + needle.Length;
        }
    }

    private bool _updatingGroupToggle;

    /// <summary>
    ///     Grep ウィンドウの各ボタンにアイコンを設定します。
    /// </summary>
    private void ApplyGrepIcons()
    {
        SetIcon(CloseButtonIcon, 26);
        SetIcon(ReloadScopesIcon, 69);
        SetIcon(ClearQueryIcon, 25);
        SetIcon(SearchButtonIcon, 30);
        SetIcon(ClearResultsIcon, 49);
        SetIcon(CancelButtonIcon, 24);
        SetIcon(GrepFilterLabelIcon, 57);
        SetIcon(ClearListFilterIcon, 25);
        SetIcon(OpenResultsIcon, 81);
        SetIcon(SaveResultsIcon, 82);
        SetIcon(AddHistoryTabIcon, 94);
        SetIcon(BrowseEditorIcon, 80);
        UpdateResultGroupsIcon();
    }

    /// <summary>
    ///     Image にアイコン ライブラリのアイコンを設定します。
    /// </summary>
    /// <param name="image">対象の Image。</param>
    /// <param name="index">アイコンのインデックス。</param>
    private static void SetIcon(Image image, int index)
    {
        if (image != null)
            image.Source = DifferencerStatusIcons.GetCustomIcon(index);
    }

    /// <summary>
    ///     すべてのグループの展開と折りたたみを切り替えます。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">イベントデータ。</param>
    private void ResultGroupsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingGroupToggle) return;
        if (ResultGroupsToggle.IsChecked == true) ViewModel.ExpandResultGroupsCommand.Execute(null);
        else ViewModel.CollapseResultGroupsCommand.Execute(null);
        UpdateResultGroupsIcon();
    }

    /// <summary>
    ///     展開・折りたたみボタンのアイコンとツールチップを更新します。
    /// </summary>
    private void UpdateResultGroupsIcon()
    {
        bool expanded = ResultGroupsToggle != null && ResultGroupsToggle.IsChecked == true;
        SetIcon(ResultGroupsIcon, expanded ? 56 : 55);
        if (ResultGroupsToggle != null)
            ResultGroupsToggle.ToolTip = expanded ? Strings.Common_Collapse : Strings.Common_Expand;
    }

    /// <summary>
    ///     履歴タブのダブルクリックで、そのタブを削除します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">マウス イベントのデータ。</param>
    private void HistoryTabStrip_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ItemsControl control ||
            e.OriginalSource is not DependencyObject source)
            return;

        var item = ItemsControl.ContainerFromElement(control, source) as ListBoxItem;
        if (item?.DataContext == null) return;
        if (ViewModel.DeleteHistoryTabCommand.CanExecute(item.DataContext))
            ViewModel.DeleteHistoryTabCommand.Execute(item.DataContext);
    }

    /// <summary>
    ///     すべての履歴タブを削除します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">イベントデータ。</param>
    private void DeleteAllHistoryTabs_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.DeleteAllHistoryTabsCommand.CanExecute(null))
            ViewModel.DeleteAllHistoryTabsCommand.Execute(null);
    }

    /// <summary>
    ///     メニューを開いたタブ以外の履歴タブを削除します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">イベントデータ。</param>
    private void DeleteOtherHistoryTabs_Click(object sender, RoutedEventArgs e)
    {
        var menu = (sender as MenuItem)?.Parent as ContextMenu;
        object tab = (menu?.PlacementTarget as FrameworkElement)?.DataContext;
        if (tab != null && ViewModel.DeleteOtherHistoryTabsCommand.CanExecute(tab))
            ViewModel.DeleteOtherHistoryTabsCommand.Execute(tab);
    }
}

internal sealed class GrepGroupDirectoryConverter : IValueConverter
{
    /// <summary>
    ///     グループのパスからフォルダー部分を取り出します。
    /// </summary>
    /// <param name="value">グループのパス。</param>
    /// <param name="targetType">変換先の型。</param>
    /// <param name="parameter">変換パラメーター (使用しません)。</param>
    /// <param name="culture">カルチャ。</param>
    /// <returns>フォルダー部分。</returns>
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        SplitGroupPath(value as string, out string directory, out _);
        return directory;
    }

    /// <summary>
    ///     逆変換には対応しません。
    /// </summary>
    /// <param name="value">変換する値。</param>
    /// <param name="targetType">変換先の型。</param>
    /// <param name="parameter">変換パラメーター。</param>
    /// <param name="culture">カルチャ。</param>
    /// <returns>Binding.DoNothing。</returns>
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        Binding.DoNothing;

    /// <summary>
    ///     グループのパスをフォルダー部分とファイル名に分けます。
    /// </summary>
    /// <param name="name">グループのパス。</param>
    /// <param name="directory">フォルダー部分 (末尾の区切り文字を含む)。</param>
    /// <param name="fileName">ファイル名。</param>
    internal static void SplitGroupPath(string name, out string directory, out string fileName)
    {
        directory = name ?? string.Empty;
        fileName = string.Empty;
        if (string.IsNullOrEmpty(name)) return;

        int separator = name.LastIndexOfAny(['\\', '/']);
        if (separator < 0) return;
        directory = name.Substring(0, separator + 1);
        fileName = name.Substring(separator + 1);
    }
}

internal sealed class GrepGroupFileConverter : IValueConverter
{
    /// <summary>
    ///     グループのパスからファイル名を取り出します。
    /// </summary>
    /// <param name="value">グループのパス。</param>
    /// <param name="targetType">変換先の型。</param>
    /// <param name="parameter">変換パラメーター (使用しません)。</param>
    /// <param name="culture">カルチャ。</param>
    /// <returns>ファイル名。</returns>
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        GrepGroupDirectoryConverter.SplitGroupPath(value as string, out _, out string fileName);
        return fileName;
    }

    /// <summary>
    ///     逆変換には対応しません。
    /// </summary>
    /// <param name="value">変換する値。</param>
    /// <param name="targetType">変換先の型。</param>
    /// <param name="parameter">変換パラメーター。</param>
    /// <param name="culture">カルチャ。</param>
    /// <returns>Binding.DoNothing。</returns>
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        Binding.DoNothing;
}