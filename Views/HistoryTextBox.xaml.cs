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

    public HistoryTextBox() : this(DefaultStore)
    {
    }

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

    private void OwnerClosed(object sender, EventArgs e)
    { CommitHistory(); }

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

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        ApplyThinCaret();
    }

    private void ApplyThinCaret()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            NarrowCaretElements(this);
            AdornerLayer layer = AdornerLayer.GetAdornerLayer(this);
            if (layer != null) NarrowCaretElements(layer);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

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

    public void CommitHistory()
    {
        if (!IsReadOnly && !string.IsNullOrEmpty(HistoryKey))
            _store.Remember(HistoryKey, HistoryValue(HistoryKey, Text), !PreserveOrder);
    }

    private static string HistoryValue(string key, string value)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrWhiteSpace(value)) return value;
        if (!key.StartsWith("Differencer-", StringComparison.OrdinalIgnoreCase)) return value;
        string trimmed = value.TrimEnd('\\', '/');
        if (trimmed.Length == 2 && trimmed[1] == ':') return trimmed + "\\";
        return trimmed;
    }

    public void ResetField(string text)
    {
        SetCurrentValue(TextProperty, text ?? string.Empty);
        CaretIndex = Text.Length;
        if (!string.IsNullOrEmpty(HistoryKey)) _store.Clear(HistoryKey);
        if (_list != null) _list.ItemsSource = null;
        if (_popup != null) _popup.IsOpen = false;
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (_popup == null || !_popup.IsOpen) CommitHistory();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F4 || (e.Key == Key.System && e.SystemKey == Key.Down))
        { OpenHistory(this, e); e.Handled = true; }
        else if (e.Key == Key.Enter) CommitHistory();
        base.OnPreviewKeyDown(e);
    }

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

    private void SelectHistory(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(_list, source) is ListBoxItem)
        {
            ApplySelection();
            e.Handled = true;
        }
    }

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
        var delete = new MenuItem { Header = "Delete", Icon = deleteIcon };
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

    private void RemoveHistory(string value)
    {
        if (string.IsNullOrEmpty(HistoryKey) || string.IsNullOrWhiteSpace(value)) return;
        List<string> remaining = _store.Remove(HistoryKey, value);
        if (_list != null) _list.ItemsSource = remaining;
        if (remaining.Count == 0 && _popup != null) _popup.IsOpen = false;
    }

    private void PopupClosed(object sender, EventArgs e)
    {
        if (_list != null && _list.IsKeyboardFocusWithin) Focus();
    }

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

    public PathEndTextBlock()
    {
        TextTrimming = TextTrimming.None;
        TextWrapping = TextWrapping.NoWrap;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Loaded += (_, _) => UpdateText(AvailableWidth());
        SizeChanged += (_, _) => UpdateText(AvailableWidth());
    }

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