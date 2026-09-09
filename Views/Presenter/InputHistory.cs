using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DesktopIniManager.Services;

namespace DesktopIniManager.Views
{
    // Adds persisted history without replacing the native TextBox template or input handling.
    public static class InputHistory
    {
        private static readonly InputHistoryStore Store = new InputHistoryStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopIniManager", "input-history"));

        public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
            "Key", typeof(string), typeof(InputHistory), new PropertyMetadata(null, KeyChanged));

        public static string GetKey(DependencyObject target) { return (string)target.GetValue(KeyProperty); }
        public static void SetKey(DependencyObject target, string value) { target.SetValue(KeyProperty, value); }

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

        public static void Commit(TextBox box)
        {
            if (box == null || box.IsReadOnly) return;
            string key = GetKey(box);
            if (!string.IsNullOrEmpty(key)) Store.Remember(key, box.Text);
        }

        private static void LostFocus(object sender, KeyboardFocusChangedEventArgs e) { Commit((TextBox)sender); }
        private static void Unloaded(object sender, RoutedEventArgs e) { Commit((TextBox)sender); }

        private static void KeyDown(object sender, KeyEventArgs e)
        {
            var box = (TextBox)sender;
            if (e.Key == Key.Enter) Commit(box);
            if (e.Key == Key.Down && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                Populate(box);
                if (box.ContextMenu.Items.Count > 0) box.ContextMenu.IsOpen = true;
                e.Handled = true;
            }
        }

        private static void Opening(object sender, ContextMenuEventArgs e) { Populate((TextBox)sender); }

        private static void Populate(TextBox box)
        {
            Commit(box);
            var menu = box.ContextMenu ?? (box.ContextMenu = new ContextMenu());
            menu.Items.Clear();
            foreach (string value in Store.Load(GetKey(box)))
            {
                var item = new MenuItem { Header = value, ToolTip = value };
                item.Click += (s, e) =>
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
        internal static void CommitHistory(this TextBox box) { InputHistory.Commit(box); }
    }
}
