using DesktopIniManager.Properties;
using DesktopIniManager.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace DesktopIniManager.Views;

// Retain TextBox behavior (TextChanged, caret, scrolling and IME) while adding history.
public class HistoryTextBox : TextBox
{
    private static InputHistoryStore DefaultStore => new InputHistoryStore(Path.Combine(AppSlot.Directory, "input-history"));
    private readonly InputHistoryStore _store;

    public static readonly DependencyProperty HistoryKeyProperty = DependencyProperty.Register(
        nameof(HistoryKey), typeof(string), typeof(HistoryTextBox), new PropertyMetadata(null));

    public static readonly DependencyProperty PreserveOrderProperty = DependencyProperty.Register(
        nameof(PreserveOrder), typeof(bool), typeof(HistoryTextBox), new PropertyMetadata(false));

    public string HistoryKey
    { get { return (string)GetValue(HistoryKeyProperty); } set { SetValue(HistoryKeyProperty, value); } }

    public bool PreserveOrder
    { get { return (bool)GetValue(PreserveOrderProperty); } set { SetValue(PreserveOrderProperty, value); } }

    public event EventHandler HistoryItemApplied;

    public bool IsHistoryOpen => _popup?.IsOpen == true;
    private Popup _popup;
    private ListBox _list;
    private Button _button;
    private Window _owner;

    /// <summary>
    ///     既定の履歴ストアを使って、入力履歴付きのテキスト ボックスを作成します。
    /// </summary>
    public HistoryTextBox() : this(DefaultStore)
    {
    }

    /// <summary>
    ///     指定した履歴ストアを使って入力履歴付きのテキスト ボックスを作成し、オーナー ウィンドウが閉じるときに履歴を記録するよう設定します。
    /// </summary>
    /// <param name="store">入力履歴のストア。</param>
    internal HistoryTextBox(InputHistoryStore store)
    {
        this._store = store;
        SetResourceReference(StyleProperty, typeof(HistoryTextBox));
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Loaded += (_, _) =>
        {
            _owner = Window.GetWindow(this);
            if (_owner != null) _owner.Closed += OwnerClosed;
            ApplyThinCaret();
        };
        Unloaded += (_, _) =>
        {
            CommitHistory();
            if (_popup != null) _popup.IsOpen = false;
            if (_owner != null) _owner.Closed -= OwnerClosed;
            _owner = null;
        };
        SelectionChanged += (_, _) => ApplyThinCaret();
    }

    /// <summary>
    ///     オーナー ウィンドウが閉じたときに入力値を履歴に記録します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">イベントデータ。</param>
    private void OwnerClosed(object sender, EventArgs e)
    { CommitHistory(); }

    /// <summary>
    ///     テンプレートから履歴ボタン、一覧、ポップアップを取得し、イベント ハンドラーを登録し直します。
    /// </summary>
    public override void OnApplyTemplate()
    {
        if (_button != null) _button.Click -= OpenHistory;
        if (_list != null)
        {
            _list.PreviewMouseLeftButtonUp -= SelectHistory;
            _list.PreviewMouseRightButtonUp -= DeleteHistory;
            _list.PreviewKeyDown -= HistoryKeyDown;
        }
        if (_popup != null) { _popup.IsOpen = false; _popup.Closed -= PopupClosed; }
        base.OnApplyTemplate();
        _popup = GetTemplateChild("PART_HistoryPopup") as Popup;
        _list = GetTemplateChild("PART_HistoryList") as ListBox;
        _button = GetTemplateChild("PART_HistoryButton") as Button;
        if (_button != null) _button.Click += OpenHistory;
        if (_list != null)
        {
            _list.PreviewMouseLeftButtonUp += SelectHistory;
            _list.PreviewMouseRightButtonUp += DeleteHistory;
            _list.PreviewKeyDown += HistoryKeyDown;
        }
        if (_popup != null) _popup.Closed += PopupClosed;
        ApplyThinCaret();
    }

    /// <summary>
    ///     フォーカスを得たときに、キャレットを細く表示します。
    /// </summary>
    /// <param name="e">イベントデータ。</param>
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        ApplyThinCaret();
    }

    /// <summary>
    ///     テキスト ボックスとアドーナー層のキャレットを、描画後に細くします。
    /// </summary>
    private void ApplyThinCaret()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            NarrowCaretElements(this);
            AdornerLayer layer = AdornerLayer.GetAdornerLayer(this);
            if (layer != null) NarrowCaretElements(layer);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    ///     ビジュアル ツリー内のキャレット要素を探し、幅を 1 ピクセルにします。
    /// </summary>
    /// <param name="root">探索を始める要素。</param>
    private static void NarrowCaretElements(DependencyObject root)
    {
        if (root == null) return;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            string typeName = child.GetType().Name;
            if (typeName.IndexOf("Caret", StringComparison.OrdinalIgnoreCase) >= 0 && child is FrameworkElement element)
            {
                element.Width = 1;
                element.MinWidth = 1;
                element.MaxWidth = 1;
                element.SnapsToDevicePixels = true;
                element.UseLayoutRounding = true;
                element.HorizontalAlignment = HorizontalAlignment.Left;
            }
            NarrowCaretElements(child);
        }
    }

    /// <summary>
    ///     現在の値を入力履歴に記録します。読み取り専用の場合や履歴キーがない場合は記録しません。
    /// </summary>
    public void CommitHistory()
    {
        if (!IsReadOnly && !string.IsNullOrEmpty(HistoryKey))
            _store.Remember(HistoryKey, HistoryValue(HistoryKey, Text), !PreserveOrder);
    }

    /// <summary>
    ///     履歴に記録する値を整えます。Differencer のフォルダー履歴では末尾の区切り文字を取り除きます。
    /// </summary>
    /// <param name="key">履歴キー。</param>
    /// <param name="value">入力値。</param>
    /// <returns>記録する値。</returns>
    private static string HistoryValue(string key, string value)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrWhiteSpace(value)) return value;
        if (!key.StartsWith("Differencer-", StringComparison.OrdinalIgnoreCase)) return value;
        string trimmed = value.TrimEnd('\\', '/');
        if (trimmed.Length == 2 && trimmed[1] == ':') return trimmed + "\\";
        return trimmed;
    }

    /// <summary>
    ///     入力値を設定し、この入力欄の履歴を消去します。
    /// </summary>
    /// <param name="text">設定する値。</param>
    public void ResetField(string text)
    {
        SetCurrentValue(TextProperty, text ?? string.Empty);
        CaretIndex = Text.Length;
        if (!string.IsNullOrEmpty(HistoryKey)) _store.Clear(HistoryKey);
        if (_list != null) _list.ItemsSource = null;
        if (_popup != null) _popup.IsOpen = false;
    }

    /// <summary>
    ///     フォーカスを失ったとき、履歴の一覧を開いていなければ入力値を記録します。
    /// </summary>
    /// <param name="e">イベントデータ。</param>
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (_popup == null || !_popup.IsOpen) CommitHistory();
    }

    /// <summary>
    ///     F4 または Alt+↓ で履歴の一覧を開き、Enter で入力値を記録します。
    /// </summary>
    /// <param name="e">キー イベントのデータ。</param>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F4 || (e.Key == Key.System && e.SystemKey == Key.Down))
        { OpenHistory(this, e); e.Handled = true; }
        else if (e.Key == Key.Enter) CommitHistory();
        base.OnPreviewKeyDown(e);
    }

    /// <summary>
    ///     履歴の一覧を開きます。開いている場合は閉じます。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">イベントデータ。</param>
    private void OpenHistory(object sender, RoutedEventArgs e)
    {
        if (IsReadOnly || _popup == null || _list == null || string.IsNullOrEmpty(HistoryKey)) return;
        if (_popup.IsOpen) { _popup.IsOpen = false; return; }
        CommitHistory();
        _list.ItemsSource = _store.Load(HistoryKey);
        if (_list.Items.Count == 0) return;
        _list.SelectedIndex = -1;
        _popup.IsOpen = true;
        _list.Focus();
    }

    /// <summary>
    ///     一覧の項目のクリックで、その履歴を入力値にします。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">マウス イベントのデータ。</param>
    private void SelectHistory(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(_list, source) is ListBoxItem)
        {
            ApplySelection();
            e.Handled = true;
        }
    }

    /// <summary>
    ///     一覧のキー操作を処理します。Enter で選択、Delete で削除、Esc または F4 で閉じます。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">キー イベントのデータ。</param>
    private void HistoryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ApplySelection(); e.Handled = true; }
        else if (e.Key == Key.Delete)
        {
            if (_list.SelectedItem is string value) RemoveHistory(value);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape || e.Key == Key.F4)
        { _popup.IsOpen = false; Focus(); e.Handled = true; }
    }

    /// <summary>
    ///     一覧の項目の右クリックで、その履歴を削除するメニューを表示します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">マウス イベントのデータ。</param>
    private void DeleteHistory(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;

        var item = ItemsControl.ContainerFromElement(_list, source) as ListBoxItem;
        if (item == null) return;
        e.Handled = true;
        _list.SelectedItem = item.Content;
        var menu = new ContextMenu();
        var deleteIcon = new TextBlock
        {
            Text = "\uE711",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        deleteIcon.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        var delete = new MenuItem { Header = StringOverlay.Get("History_MenuDelete"), Icon = deleteIcon };
        string value = item.Content as string;
        delete.Click += (_, _) => RemoveHistory(value);
        menu.Items.Add(delete);
        item.ContextMenu = menu;
        if (_popup != null) _popup.StaysOpen = true;
        menu.Closed += (_, _) =>
        {
            if (_popup != null) _popup.StaysOpen = false;
            if (_popup != null && _popup.IsOpen && _list != null) _list.Focus();
        };
        menu.IsOpen = true;
    }

    /// <summary>
    ///     履歴から値を削除し、一覧を更新します。
    /// </summary>
    /// <param name="value">削除する値。</param>
    private void RemoveHistory(string value)
    {
        if (string.IsNullOrEmpty(HistoryKey) || string.IsNullOrWhiteSpace(value)) return;
        List<string> remaining = _store.Remove(HistoryKey, value);
        if (_list != null) _list.ItemsSource = remaining;
        if (remaining.Count == 0 && _popup != null) _popup.IsOpen = false;
    }

    /// <summary>
    ///     一覧が閉じたときに、フォーカスをテキスト ボックスに戻します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">イベントデータ。</param>
    private void PopupClosed(object sender, EventArgs e)
    {
        if (_list != null && _list.IsKeyboardFocusWithin) Focus();
    }

    /// <summary>
    ///     一覧で選択した履歴を入力値にして一覧を閉じ、HistoryItemApplied を通知します。
    /// </summary>
    private void ApplySelection()
    {
        if (!(_list.SelectedItem is string value)) return;
        SetCurrentValue(TextProperty, value);
        if (!PreserveOrder) CommitHistory();
        _popup.IsOpen = false;
        Focus();
        CaretIndex = Text.Length;
        HistoryItemApplied?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class PathEndTextBlock : TextBlock
{
    public static readonly DependencyProperty FullTextProperty = DependencyProperty.Register(
        nameof(FullText), typeof(string), typeof(PathEndTextBlock),
        new PropertyMetadata(null, (sender, _) => ((PathEndTextBlock)sender).UpdateText(double.NaN)));

    public string FullText
    {
        get { return (string)GetValue(FullTextProperty); }
        set { SetValue(FullTextProperty, value); }
    }

    private bool _updating;

    /// <summary>
    ///     パスの末尾を優先して省略表示するテキスト ブロックを作成します。
    /// </summary>
    public PathEndTextBlock()
    {
        TextTrimming = TextTrimming.None;
        TextWrapping = TextWrapping.NoWrap;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Loaded += (_, _) => UpdateText(AvailableWidth());
        SizeChanged += (_, _) => UpdateText(AvailableWidth());
    }

    /// <summary>
    ///     親の ListBoxItem から、表示に使える幅を求めます。
    /// </summary>
    /// <returns>表示に使える幅。</returns>
    private double AvailableWidth()
    {
        DependencyObject current = this;
        while (current != null)
        {
            if (current is ListBoxItem item && item.ActualWidth > 0)
                return Math.Max(0, item.ActualWidth - item.Padding.Left - item.Padding.Right);
            current = VisualTreeHelper.GetParent(current);
        }

        return ActualWidth;
    }

    /// <summary>
    ///     パスが幅に収まらない場合に、先頭側を省略して末尾を残した文字列を表示します。
    /// </summary>
    /// <param name="width">表示に使える幅。</param>
    private void UpdateText(double width)
    {
        if (_updating) return;

        string value = FullText ?? string.Empty;
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
            width = ActualWidth;

        string next = value;
        if (width > 0 && MeasureWidth(value) > width)
        {
            const string ellipsis = "...";
            if (MeasureWidth(ellipsis) >= width)
                next = ellipsis;
            else
            {
                int low = 0;
                int high = value.Length;
                while (low < high)
                {
                    int mid = (low + high + 1) / 2;
                    string candidate = ellipsis + value.Substring(value.Length - mid);
                    if (MeasureWidth(candidate) <= width) low = mid;
                    else high = mid - 1;
                }
                next = low == 0 ? ellipsis : ellipsis + value.Substring(value.Length - low);
            }
        }

        if (Text == next) return;
        _updating = true;
        try { Text = next; }
        finally { _updating = false; }
    }

    /// <summary>
    ///     現在のフォント設定で文字列を描画したときの幅を求めます。
    /// </summary>
    /// <param name="value">測定する文字列。</param>
    /// <returns>描画幅。</returns>
    private double MeasureWidth(string value)
    {
        var text = new FormattedText(
            value ?? string.Empty,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyle, FontWeight, FontStretch),
            FontSize,
            Foreground ?? Brushes.Black,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return text.Width;
    }
}