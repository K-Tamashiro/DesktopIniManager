using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DesktopIniManager.Services;

namespace DesktopIniManager.Views
{
    // Retain TextBox behavior (TextChanged, caret, scrolling and IME) while adding history.
    public class HistoryTextBox : TextBox
    {
        private static readonly InputHistoryStore DefaultStore = new InputHistoryStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopIniManager", "input-history"));
        private readonly InputHistoryStore store;
        public static readonly DependencyProperty HistoryKeyProperty = DependencyProperty.Register(
            nameof(HistoryKey), typeof(string), typeof(HistoryTextBox), new PropertyMetadata(null));
        public static readonly DependencyProperty PreserveOrderProperty = DependencyProperty.Register(
            nameof(PreserveOrder), typeof(bool), typeof(HistoryTextBox), new PropertyMetadata(false));
        public string HistoryKey { get { return (string)GetValue(HistoryKeyProperty); } set { SetValue(HistoryKeyProperty, value); } }
        public bool PreserveOrder { get { return (bool)GetValue(PreserveOrderProperty); } set { SetValue(PreserveOrderProperty, value); } }
        public event EventHandler HistoryItemApplied;
        private Popup popup;
        private ListBox list;
        private Button button;
        private Window owner;

        public HistoryTextBox() : this(DefaultStore) { }

        internal HistoryTextBox(InputHistoryStore store)
        {
            this.store = store;
            SetResourceReference(StyleProperty, typeof(HistoryTextBox));
            Loaded += (s, e) =>
            {
                owner = Window.GetWindow(this);
                if (owner != null) owner.Closed += OwnerClosed;
            };
            Unloaded += (s, e) =>
            {
                CommitHistory();
                if (popup != null) popup.IsOpen = false;
                if (owner != null) owner.Closed -= OwnerClosed;
                owner = null;
            };
        }

        private void OwnerClosed(object sender, EventArgs e) { CommitHistory(); }

        public override void OnApplyTemplate()
        {
            if (button != null) button.Click -= OpenHistory;
            if (list != null)
            {
                list.PreviewMouseLeftButtonUp -= SelectHistory;
                list.PreviewMouseRightButtonUp -= DeleteHistory;
                list.PreviewKeyDown -= HistoryKeyDown;
            }
            if (popup != null) { popup.IsOpen = false; popup.Closed -= PopupClosed; }
            base.OnApplyTemplate();
            popup = GetTemplateChild("PART_HistoryPopup") as Popup;
            list = GetTemplateChild("PART_HistoryList") as ListBox;
            button = GetTemplateChild("PART_HistoryButton") as Button;
            if (button != null) button.Click += OpenHistory;
            if (list != null)
            {
                list.PreviewMouseLeftButtonUp += SelectHistory;
                list.PreviewMouseRightButtonUp += DeleteHistory;
                list.PreviewKeyDown += HistoryKeyDown;
            }
            if (popup != null) popup.Closed += PopupClosed;
        }

        public void CommitHistory()
        {
            if (!IsReadOnly && !string.IsNullOrEmpty(HistoryKey)) store.Remember(HistoryKey, Text, !PreserveOrder);
        }

        public void ResetField(string text)
        {
            Text = text ?? string.Empty;
            CaretIndex = Text.Length;
            if (!string.IsNullOrEmpty(HistoryKey)) store.Clear(HistoryKey);
            if (list != null) list.ItemsSource = null;
            if (popup != null) popup.IsOpen = false;
        }

        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnLostKeyboardFocus(e);
            if (popup == null || !popup.IsOpen) CommitHistory();
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
            if (IsReadOnly || popup == null || list == null || string.IsNullOrEmpty(HistoryKey)) return;
            if (popup.IsOpen) { popup.IsOpen = false; return; }
            CommitHistory();
            list.ItemsSource = store.Load(HistoryKey);
            if (list.Items.Count == 0) return;
            list.SelectedIndex = -1;
            popup.IsOpen = true;
            list.Focus();
        }

        private void SelectHistory(object sender, MouseButtonEventArgs e)
        {
            if (ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) is ListBoxItem)
            { ApplySelection(); e.Handled = true; }
        }

        private void HistoryKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { ApplySelection(); e.Handled = true; }
            else if (e.Key == Key.Delete)
            {
                if (list.SelectedItem is string value) RemoveHistory(value);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape || e.Key == Key.F4)
            { popup.IsOpen = false; Focus(); e.Handled = true; }
        }

        private void DeleteHistory(object sender, MouseButtonEventArgs e)
        {
            var item = ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) as ListBoxItem;
            if (item == null) return;
            e.Handled = true;
            list.SelectedItem = item.Content;
            var menu = new ContextMenu();
            var delete = new MenuItem { Header = "削除" };
            string value = item.Content as string;
            delete.Click += (s, args) => RemoveHistory(value);
            menu.Items.Add(delete);
            item.ContextMenu = menu;
            if (popup != null) popup.StaysOpen = true;
            menu.Closed += (s, args) =>
            {
                if (popup != null) popup.StaysOpen = false;
                if (popup != null && popup.IsOpen && list != null) list.Focus();
            };
            menu.IsOpen = true;
        }

        private void RemoveHistory(string value)
        {
            if (string.IsNullOrEmpty(HistoryKey) || string.IsNullOrWhiteSpace(value)) return;
            List<string> remaining = store.Remove(HistoryKey, value);
            if (list != null) list.ItemsSource = remaining;
            if (remaining.Count == 0 && popup != null) popup.IsOpen = false;
        }

        private void PopupClosed(object sender, EventArgs e)
        {
            if (list != null && list.IsKeyboardFocusWithin) Focus();
        }

        private void ApplySelection()
        {
            if (!(list.SelectedItem is string value)) return;
            Text = value;
            if (!PreserveOrder) CommitHistory();
            popup.IsOpen = false;
            Focus();
            CaretIndex = Text.Length;
            HistoryItemApplied?.Invoke(this, EventArgs.Empty);
        }
    }
}
