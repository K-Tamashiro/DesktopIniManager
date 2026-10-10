using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Text.RegularExpressions;
using DesktopIniManager.Models;
using DesktopIniManager.Services;
using DesktopIniManager.ViewModels;

internal static class ResultHistoryTests
{
    private sealed class Dialogs : IUserDialogService
    {
        internal MessageBoxResult Answer = MessageBoxResult.Yes;
        public MessageBoxResult Show(string message, string title, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
        {
            if (image == MessageBoxImage.Error) throw new Exception(message);
            return Answer;
        }
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private static void Run(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            task.GetAwaiter().OnCompleted(() => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
    private static DeveloperDifferencerViewModel Differencer(Dialogs dialogs)
        => new DeveloperDifferencerViewModel(dialogs, Dispatcher.CurrentDispatcher, null, null);
    private static GrepWindowViewModel Grep(Dialogs dialogs, string root)
    {
        var vm = new GrepWindowViewModel(() => new[] { root }, Dispatcher.CurrentDispatcher, dialogs);
        vm.SetExplicitScopes(new[] { root });
        vm.SelectedProfile = LanguageProfile.All.First(p => !p.IsFree);
        typeof(GrepWindowViewModel).GetMethod("RestoreHistory", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(vm, null);
        vm.Extensions = ".cs";
        return vm;
    }
    internal static int Execute()
    {
        System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "history-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        ResultHistoryStore.DirectoryPath = Path.Combine(root, "history");
        DeveloperDifferencerViewModel.StatePath = Path.Combine(root, "state.xml");
        string source = Path.Combine(root, "source"), target = Path.Combine(root, "target");
        Directory.CreateDirectory(Path.Combine(source, "folder")); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "folder", "sample.cs"), "alpha\nbeta\n");
        var dialogs = new Dialogs();
        var diff = Differencer(dialogs);
        diff.SourcePath = source; diff.TargetPath = target;
        Run(diff.CompareAsync());
        var first = diff.SelectedHistoryTab;
        Check(first != null && first.Title.StartsWith(DateTime.Now.ToString("yyyy.MM.dd")), "comparison creates a dated tab");
        diff.SelectFolder("folder");
        File.WriteAllText(Path.Combine(source, "second.cs"), "gamma");
        Run(diff.CompareAsync());
        Check(diff.HistoryTabs.Count == 2 && diff.Snapshot.Files.Count == 2, "each comparison gets a new snapshot");
        Check(diff.HistoryTabs[0] == diff.SelectedHistoryTab && diff.HistoryTabs[1] == first, "newest comparison is at the left");
        Run(diff.SelectHistoryTabAsync(first));
        Check(diff.Snapshot.Files.Count == 1 && diff.FilePanelTitle.Contains("folder"), "switch restores results and selected folder");
        for (int i = 2; i < 20; i++) Run(diff.CompareAsync());
        var active = diff.SelectedHistoryTab;
        dialogs.Answer = MessageBoxResult.No;
        Run(diff.CompareAsync());
        Check(diff.HistoryTabs.Count == 20 && ReferenceEquals(active, diff.SelectedHistoryTab), "21st comparison declined without clearing current results");
        dialogs.Answer = MessageBoxResult.Yes;
        Run(diff.CompareAsync());
        Check(diff.HistoryTabs.Count == 20 && !diff.HistoryTabs.Contains(first), "21st comparison replaces oldest after confirmation");
        diff.SaveHistory();
        // The historical tree must not depend on the original directories still existing.
        Directory.Move(source, source + "-moved");
        var restored = Differencer(dialogs);
        Run(restored.RestoreHistoryAsync());
        Check(restored.HistoryTabs.Count == 20 && restored.Snapshot.Files.Count == 2, "comparison JSON restores snapshots after restart");
        Check(restored.Folders["folder"].SourceExists, "historical folder state survives missing source directory");
        diff.Close();
        source += "-moved";

        var grep = Grep(dialogs, source);
        grep.Query = "alpha";
        Run(grep.SearchAsync());
        var alpha = grep.SelectedHistoryTab;
        Check(grep.Matches.Count == 1 && alpha.Title == "alpha", "grep title uses query and captures results");
        grep.AddHistoryTabCommand.Execute(null);
        grep.Query = "beta";
        Run(grep.SearchAsync());
        Check(grep.Matches.Count == 1 && grep.Matches[0].LineNumber == 2, "new grep tab starts with separate results");
        grep.SelectedHistoryTab = alpha;
        Check(grep.Query == "alpha" && grep.Matches.Single().LineNumber == 1, "grep switch restores query and results");
        dialogs.Answer = MessageBoxResult.Cancel;
        Run(grep.SearchAsync());
        Check(grep.Matches.Count == 1, "cancel append leaves existing results intact");
        dialogs.Answer = MessageBoxResult.Yes;
        grep.Query = "beta";
        Run(grep.SearchAsync());
        Check(grep.Matches.Count == 2, "confirmed append retains earlier matches");
        dialogs.Answer = MessageBoxResult.No;
        Run(grep.SearchAsync());
        Check(grep.HistoryTabs.Count == 3 && grep.SelectedHistoryTab != alpha && grep.Matches.Count == 1 && alpha.Matches.Count == 2, "No searches in a new tab and preserves prior results");
        for (int i = 3; i < 20; i++) grep.AddHistoryTabCommand.Execute(null);
        var last = grep.SelectedHistoryTab;
        grep.AddHistoryTabCommand.Execute(null);
        Check(grep.HistoryTabs.Count == 20 && ReferenceEquals(last, grep.SelectedHistoryTab), "grep 21st addition preserves tabs and selection");
        grep.SelectedHistoryTab = alpha;
        Run(grep.SearchAsync());
        Check(grep.HistoryTabs.Count == 20 && grep.SelectedHistoryTab == alpha && grep.Matches.Count == 1, "at limit No clears only selected tab before searching");
        Check(grep.SaveHistory(), "explicit grep save succeeds");
        var loadedGrep = Grep(dialogs, source);
        Check(loadedGrep.HistoryTabs.Count == 20 && loadedGrep.Matches.Single().LineNumber == 2 && loadedGrep.Query == "beta", "grep JSON restores active tab and results");
        dialogs.Answer = MessageBoxResult.Yes;
        RenderViews(root, restored, loadedGrep);
        restored.Close();
        dialogs.Answer = MessageBoxResult.Yes;
        loadedGrep.DeleteHistoryTabCommand.Execute(loadedGrep.SelectedHistoryTab);
        Check(loadedGrep.HistoryTabs.Count == 19 && loadedGrep.SelectedHistoryTab != null, "deleting active grep tab selects a remaining tab");
        Console.WriteLine("History fixtures: " + root);
        return 0;
    }

    private static void RenderViews(string root, DeveloperDifferencerViewModel diff, GrepWindowViewModel grep)
    {
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, typeof(DesktopIniManager.MainWindow).Assembly);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var xml = new System.Xml.XmlDocument();
        xml.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestAssets", "App.xaml"));
        var ns = new System.Xml.XmlNamespaceManager(xml.NameTable);
        ns.AddNamespace("p", "http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        string resources = xml.SelectSingleNode("p:Application/p:Application.Resources/p:ResourceDictionary", ns).OuterXml;
        var context = new ParserContext(); context.XmlnsDictionary.Add("x", "http://schemas.microsoft.com/winfx/2006/xaml");
        app.Resources = (ResourceDictionary)XamlReader.Parse(resources.Replace("Source=\"Themes/", "Source=\"/DesktopIniManager;component/Themes/"), context);
        app.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri("/DesktopIniManager;component/Themes/DarkTheme.xaml", UriKind.Relative) };
        app.Resources["GrepGroupDirectory"] = new DesktopIniManager.Views.GrepGroupDirectoryConverter();
        app.Resources["GrepGroupFile"] = new DesktopIniManager.Views.GrepGroupFileConverter();
        foreach (string culture in new[] { "en", "ja", "zh-Hans", "ko" })
        {
            DesktopIniManager.Properties.StringOverlay.Load(System.Globalization.CultureInfo.GetCultureInfo(culture));
            Check(DesktopIniManager.Properties.StringOverlay.Get("History_GrepAppend").Contains("\n") &&
                DesktopIniManager.Properties.StringOverlay.Get("History_AddTab") != "History_AddTab", culture + " history translations resolve");
        }
        DesktopIniManager.Properties.StringOverlay.Load(DesktopIniManager.Properties.StringOverlay.ResolveCulture());
        Check(DifferencerStatusIcons.GetCustomIcon(94) != null && DifferencerStatusIcons.GetCustomIcon(95) != null, "new tab and save tab icons are available");
        foreach (string name in new[] { "DeveloperDifferencerWindow", "GrepWindow" })
        {
            string text = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestAssets", name + ".xaml"));
            text = Regex.Replace(text, @"\s+x:Class=""[^""]+""", "");
            text = text.Replace("clr-namespace:DesktopIniManager.Views", "clr-namespace:DesktopIniManager.Views;assembly=DesktopIniManager")
                .Replace("clr-namespace:DesktopIniManager.Properties", "clr-namespace:DesktopIniManager.Properties;assembly=DesktopIniManager");
            text = Regex.Replace(text, @"\s+(Icon|Loaded|Unloaded|DataContextChanged|SelectedItemChanged|PreviewMouseLeftButtonDown|MouseDoubleClick|MouseLeftButtonDown|Click|Expanded|Collapsed)=""[^""]*""", "");
            text = Regex.Replace(text, @"<EventSetter\s+[^>]+/>", "");
            text = Regex.Replace(text, @"<history:GrepGroup\w+Converter\s+[^>]+/>", "");
            var window = (Window)XamlReader.Parse(text);
            window.DataContext = name == "GrepWindow" ? (object)grep : diff;
            if (name == "GrepWindow")
            {
                ((Image)window.FindName("AddHistoryTabIcon")).Source = DifferencerStatusIcons.GetCustomIcon(94);
                ((Image)window.FindName("SaveHistoryTabIcon")).Source = DifferencerStatusIcons.GetCustomIcon(95);
            }
            var content = (FrameworkElement)window.Content;
            content.SetResourceReference(Panel.BackgroundProperty, "WindowBackground");
            content.Measure(new Size(1200, 740)); content.Arrange(new Rect(0, 0, 1200, 740)); content.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1200, 740, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(root, name + ".png"))) png.Save(stream);
            var strip = Descendants<ListBox>(content).FirstOrDefault(l => l.ItemsSource == (name == "GrepWindow" ? (object)grep.HistoryTabs : diff.HistoryTabs));
            Check(strip != null && strip.ActualHeight > 20 && strip.ActualHeight < 65, name + " tab strip fits layout at 1200px");
            var activeItem = (ListBoxItem)strip.ItemContainerGenerator.ContainerFromItem(strip.SelectedItem);
            var activeChrome = (Border)activeItem.Template.FindName("TabChrome", activeItem);
            Check(((SolidColorBrush)activeChrome.Background).Color == ((SolidColorBrush)app.FindResource("HistoryTabSelectedBackground")).Color &&
                ((SolidColorBrush)activeItem.Foreground).Color == ((SolidColorBrush)app.FindResource("HistoryTabSelectedForeground")).Color &&
                activeItem.FontWeight == FontWeights.Bold, name + " selected tab follows theme and uses bold text");
            var inactiveItem = (ListBoxItem)strip.ItemContainerGenerator.ContainerFromItem(strip.Items.Cast<object>().First(t => t != strip.SelectedItem));
            var inactiveChrome = (Border)inactiveItem.Template.FindName("TabChrome", inactiveItem);
            Check(((SolidColorBrush)inactiveChrome.Background).Color == ((SolidColorBrush)app.FindResource("HistoryTabBackground")).Color &&
                inactiveItem.FontWeight == FontWeights.Normal, name + " inactive tab follows theme");
            if (name == "DeveloperDifferencerWindow")
            {
                var close = Descendants<Button>(activeItem).First();
                diff.SetBusy(true); content.UpdateLayout();
                Check(!close.IsEnabled, "tab deletion disabled during comparison");
                diff.SetBusy(false); content.UpdateLayout();
                Check(close.IsEnabled, "tab deletion re-enabled when comparison finishes");
                close.Command.Execute(close.CommandParameter);
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (diff.IsBusy && DateTime.UtcNow < deadline)
                {
                    var frame = new DispatcherFrame();
                    var timer = new DispatcherTimer(DispatcherPriority.ContextIdle) { Interval = TimeSpan.FromMilliseconds(20) };
                    timer.Tick += (s, e) => { timer.Stop(); frame.Continue = false; };
                    timer.Start(); Dispatcher.PushFrame(frame);
                }
                Check(!diff.IsBusy, "tab deletion finishes restoring next tab");
                Check(diff.HistoryTabs.Count == 19, "bound close button deletes selected comparison tab");
            }
            window.Close();
        }
        app.Shutdown();
    }
    private static System.Collections.Generic.IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
