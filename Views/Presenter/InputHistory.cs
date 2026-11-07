using DesktopIniManager.Services;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DesktopIniManager.Views.Presenter;

// Adds persisted history without replacing the native TextBox template or input handling.
public static class InputHistory
{
    private static readonly InputHistoryStore Store = new(Path.Combine(AppSlot.Directory, "input-history"));

    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(InputHistory), new PropertyMetadata(null, KeyChanged));

    /// <summary>
    ///     テキスト ボックスに設定された入力履歴のキーを取得します。
    /// </summary>
    /// <param name="target">対象の要素。</param>
    /// <returns>履歴キー。</returns>
    public static string GetKey(DependencyObject target)
    {
        return (string)target.GetValue(KeyProperty);
    }

    /// <summary>
    ///     テキスト ボックスに入力履歴のキーを設定します。
    /// </summary>
    /// <param name="target">対象の要素。</param>
    /// <param name="value">履歴キー。</param>
    public static void SetKey(DependencyObject target, string value)
    {
        target.SetValue(KeyProperty, value);
    }

    /// <summary>
    ///     履歴キーが設定されたテキスト ボックスに、フォーカス喪失・アンロード・コンテキスト メニュー・キー操作のハンドラーを登録し直します。
    /// </summary>
    /// <param name="target">対象の要素。</param>
    /// <param name="e">プロパティ変更のイベントデータ。</param>
    private static void KeyChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        var box = target as TextBox;
        if (box == null) return;
        box.LostKeyboardFocus -= LostFocus;
        box.Unloaded -= Unloaded;
        box.ContextMenuOpening -= Opening;
        box.PreviewKeyDown -= KeyDown;
        if (string.IsNullOrEmpty(e.NewValue as string)) return;
        box.LostKeyboardFocus += LostFocus;
        box.Unloaded += Unloaded;
        box.ContextMenuOpening += Opening;
        box.PreviewKeyDown += KeyDown;
        if (box.ContextMenu == null) box.ContextMenu = new ContextMenu();
    }

    /// <summary>
    ///     テキスト ボックスの現在の値を入力履歴に記録します。読み取り専用の場合は記録しません。
    /// </summary>
    /// <param name="box">対象のテキスト ボックス。</param>
    public static void Commit(TextBox box)
    {
        if (box == null || box.IsReadOnly) return;
        var key = GetKey(box);
        if (!string.IsNullOrEmpty(key)) Store.Remember(key, box.Text);
    }

    /// <summary>
    ///     フォーカスを失ったときに入力値を履歴に記録します。
    /// </summary>
    /// <param name="sender">対象のテキスト ボックス。</param>
    /// <param name="e">イベントデータ。</param>
    private static void LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        Commit((TextBox)sender);
    }

    /// <summary>
    ///     アンロード時に入力値を履歴に記録します。
    /// </summary>
    /// <param name="sender">対象のテキスト ボックス。</param>
    /// <param name="e">イベントデータ。</param>
    private static void Unloaded(object sender, RoutedEventArgs e)
    {
        Commit((TextBox)sender);
    }

    /// <summary>
    ///     Enter キーで入力値を記録し、Ctrl+↓ で履歴メニューを開きます。
    /// </summary>
    /// <param name="sender">対象のテキスト ボックス。</param>
    /// <param name="e">キー イベントのデータ。</param>
    private static void KeyDown(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;
        if (e.Key == Key.Enter) Commit(box);
        if (e.Key == Key.Down && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            Populate(box);
            if (box.ContextMenu is { Items.Count: > 0 }) box.ContextMenu.IsOpen = true;
            e.Handled = true;
        }
    }

    /// <summary>
    ///     コンテキスト メニューを開くときに履歴の項目を作成します。
    /// </summary>
    /// <param name="sender">対象のテキスト ボックス。</param>
    /// <param name="e">イベントデータ。</param>
    private static void Opening(object sender, ContextMenuEventArgs e)
    {
        Populate((TextBox)sender);
    }

    /// <summary>
    ///     入力履歴から、選択すると値を入力するコンテキスト メニューの項目を作成します。
    /// </summary>
    /// <param name="box">対象のテキスト ボックス。</param>
    private static void Populate(TextBox box)
    {
        Commit(box);
        var menu = box.ContextMenu ?? (box.ContextMenu = new ContextMenu());
        menu.Items.Clear();
        foreach (var value in Store.Load(GetKey(box)))
        {
            var item = new MenuItem { Header = value, ToolTip = value };
            item.Click += (_, _) =>
            {
                box.Text = value;
                box.CaretIndex = value.Length;
                box.Focus();
                Commit(box);
            };
            menu.Items.Add(item);
        }
    }
}

internal static class InputHistoryExtensions
{
    /// <summary>
    ///     テキスト ボックスの現在の値を入力履歴に記録します。
    /// </summary>
    /// <param name="box">対象のテキスト ボックス。</param>
    internal static void CommitHistory(this TextBox box)
    {
        InputHistory.Commit(box);
    }
}