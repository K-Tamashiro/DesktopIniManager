using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DesktopIniManager.Services;
using DesktopIniManager.Views;

internal static class InputHistoryTests
{
    internal static int Run()
    {
        int checks = 0;
        Action<bool, string> check = (ok, message) => { if (!ok) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); };
        string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "history-fixtures-" + Guid.NewGuid().ToString("N"));
        var store = new InputHistoryStore(directory);
        check(store.Load("query").Count == 0, "missing history is empty");
        store.Remember("query", "  日本語.* ");
        store.Remember("query", "Case");
        store.Remember("query", "case");
        store.Remember("query", "  日本語.* ");
        check(new InputHistoryStore(directory).Load("query").SequenceEqual(new[] { "  日本語.* ", "case", "Case" }), "history persists exact text, case and MRU order without duplicates");
        store.Remember("query", "  ");
        check(store.Load("query").Count == 3, "blank values do not become history");
        check(store.Load("other").Count == 0, "fields keep independent histories");
        for (int i = 0; i < 25; i++) store.Remember("bounded", i.ToString());
        check(store.Load("bounded").Count == 20 && store.Load("bounded").Last() == "5", "history retains newest 20 entries");
        File.AppendAllText(Path.Combine(directory, "query.txt"), "invalid-base64!\n");
        check(store.Load("query").Count == 3, "malformed history line is skipped");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/DesktopIniManager;component/Themes/LightTheme.xaml", UriKind.Relative) });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/DesktopIniManager;component/Views/HistoryTextBox.xaml", UriKind.Relative) });
        var input = new HistoryTextBox(store) { HistoryKey = "query", Text = "typed value", Width = 360, Height = 36,
            Padding = new Thickness(8, 4, 8, 4), VerticalContentAlignment = VerticalAlignment.Center };
        var window = new Window { Content = input, Width = 420, Height = 140, Left = -10000, Top = -10000, ShowInTaskbar = false };
        window.Show(); window.UpdateLayout(); input.ApplyTemplate();
        var button = (Button)input.Template.FindName("PART_HistoryButton", input);
        var popup = (Popup)input.Template.FindName("PART_HistoryPopup", input);
        var list = (ListBox)input.Template.FindName("PART_HistoryList", input);
        var contentHost = input.Template.FindName("PART_ContentHost", input) as ScrollViewer;
        check(button != null && input.ActualHeight == 36 && contentHost != null && contentHost.IsVisible && contentHost.ActualHeight >= 20,
            "Grep history field stays 36px high with an unclipped editable text host");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(popup.IsOpen && list.Items.Count == 4, "dropdown opens with persisted and current input");
        int changes = 0; input.TextChanged += (s, e) => changes++;
        list.SelectedIndex = 1;
        list.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(list), 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        check(input.Text == "  日本語.* " && changes > 0 && !popup.IsOpen, "keyboard history selection updates Text and existing TextChanged handlers");
        input.IsReadOnly = true; input.Text = "preset extensions"; input.CommitHistory();
        check(((TextBlock)input.Template.FindName("PART_ReadOnlyText", input)).Text == "preset extensions", "read-only history input displays assigned text");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(!popup.IsOpen && !store.Load("query").Contains("preset extensions"), "read-only fields cannot select or record history");
        input.IsReadOnly = false; input.Text = "saved on close";
        window.Close();
        check(store.Load("query")[0] == "saved on close", "closing a focused input saves its history");
        var nativeInput = new TextBox { Text = "検索文字列 日本語 .*" };
        InputHistory.SetKey(nativeInput, "native-query");
        nativeInput.ApplyTemplate();
        check(nativeInput.GetType() == typeof(TextBox) && nativeInput.Text == "検索文字列 日本語 .*",
            "history keeps the native TextBox and its exact input text");
        check(InputHistory.GetKey(nativeInput) == "native-query" && nativeInput.ContextMenu != null,
            "native TextBox receives selectable history without replacing its template");
        check(SettingsService.DefaultGrepFreeExtensions.StartsWith(".txt ") && SettingsService.DefaultGrepFreeExtensions.Contains(".json"),
            "Free Grep profile has a usable default extension list");
        app.Shutdown();
        Console.WriteLine("PASS " + checks + " input history checks");
        return 0;
    }
}
