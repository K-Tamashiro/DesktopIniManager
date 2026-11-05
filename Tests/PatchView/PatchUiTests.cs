using DesktopIniManager.Services;
using DesktopIniManager.Views;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml;

internal static class PatchUiTests
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }

    internal static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var xml = new XmlDocument();
        xml.Load(Path.Combine(AppContext.BaseDirectory, "TestAssets", "App.xaml"));
        var ns = new XmlNamespaceManager(xml.NameTable);
        ns.AddNamespace("p", "http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        var resources = xml.SelectSingleNode("p:Application/p:Application.Resources/p:ResourceDictionary", ns).OuterXml;
        var context = new ParserContext(); context.XmlnsDictionary.Add("x", "http://schemas.microsoft.com/winfx/2006/xaml");
        app.Resources = (ResourceDictionary)XamlReader.Parse(resources.Replace("Source=\"Themes/", "Source=\"/DesktopIniManager;component/Themes/"), context);
        var root = Path.Combine(AppContext.BaseDirectory, "patch-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, "demo.cs");
        File.WriteAllText(target, "// head\nclass Demo\n{\n    int oldValue;\n}\n// tail\n");
        var patch = Path.Combine(root, "demo.patch");
        File.WriteAllText(patch, "From abc Mon Sep 17 00:00:00 2001\nFrom: Test <test@example.test>\nSubject: [PATCH] Patch View UI test\n\nChange the value; keep surrounding lines.\n---\n" +
            "diff --git a/demo.cs b/demo.cs\n--- a/demo.cs\n+++ b/demo.cs\n@@ -3,3 +3,4 @@\n {\n-    int oldValue;\n+    int newValue;\n+    int extraValue;\n }\n" +
            "diff --git a/new.txt b/new.txt\nnew file mode 100644\n--- /dev/null\n+++ b/new.txt\n@@ -0,0 +1 @@\n+new content\n", new UTF8Encoding(false));
        var snapshot = new DiffSnapshot();
        var window = new DiffViewWindow(snapshot, new DiffFile { RelativePath = "demo.patch", Source = DiffStamp.Read(patch) }, patch, "")
        { Width = 1280, Height = 900, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
        object Field(string name) => typeof(DiffViewWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(window);
        object Call(string name, params object[] args) => typeof(DiffViewWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(window, args);
        void Pump(Func<bool> done)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
            var deadline = DateTime.UtcNow.AddSeconds(30);
            timer.Tick += (_, _) => { if (done() || DateTime.UtcNow > deadline) frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
            Check(done(), "dispatcher operation completes");
        }
        void Wait(Task task) { Pump(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        Button[] Arrows(bool source) => ((Canvas)((RichTextBox)Field(source ? "_leftList" : "_rightList")).Tag).Children.OfType<Button>().ToArray();
        window.Show();
        Pump(() => window.ViewModel.IsPatch && Field("_leftList") != null && !(bool)Field("_renderingContent"));
        var model = window.ViewModel;
        Check(model.PatchFiles.Count == 2 && ((ComboBox)Field("_patchSourceBox")).Items.Count == 2, "both patch entries appear in selectors");
        Check(((TextBlock)Field("_patchCommitText")).Text.Contains("Change the value"), "commit summary is visible");
        Check(Arrows(true).Length == 0 && Arrows(false).Length == 0, "preview has no copy arrows");
        Check(model.SelectPatchTarget(target) && Path.TrimEndingDirectorySeparator(model.PatchRoot) == root, "first selected actual file establishes repository root");
        Wait((Task)Call("LoadContent", false, false));
        Check(Arrows(true).Length == 1 && Arrows(false).Length == 0, "actual target enables only source arrows");
        Check(model.Lines.Any(line => line.FilteredLineCount > 0), "unknown patch intervals start folded");
        model.ShowSpaces = model.ShowNewlines = true;
        var oldPane = (RichTextBox)Field("_leftList");
        Arrows(true)[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(() => !(bool)Field("_renderingContent"));
        Check(model.IsDirty(false) && !model.IsDirty(true), "patch application dirties only target");
        Check(ReferenceEquals(oldPane, Field("_leftList")) && model.ShowSpaces && model.ShowNewlines, "patch application retains panes and marker switches");
        var targetText = DesktopIniManager.ViewModels.DiffViewModel.Text(model.TargetText);
        Check(targetText.Contains("    int newValue;\n    int extraValue;\n"), "actual target buffer receives patch hunk");
        ((ComboBox)Field("_patchSourceBox")).SelectedIndex = 1;
        Pump(() => model.PatchIndex == 1 && !(bool)Field("_renderingContent"));
        Check(((ComboBox)Field("_patchTargetBox")).SelectedIndex == 1 && model.HasEdits, "selectors synchronize without losing previous unsaved edit");
        ((ComboBox)Field("_patchTargetBox")).SelectedIndex = 0;
        Pump(() => model.PatchIndex == 0 && !(bool)Field("_renderingContent"));
        Check(DesktopIniManager.ViewModels.DiffViewModel.Text(model.TargetText) == targetText && model.CanUndoHunk(0, true), "returning to a file restores its working text and undo");
        Arrows(true)[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(() => !(bool)Field("_renderingContent"));
        Check(!model.HasEdits, "arrow undo restores target");
        var moveButtons = (Canvas)Field("_patchMoveButtons");
        Check(moveButtons.Children.OfType<Button>().Count() == 2, "hunk has independent up/down controls");
        var down = moveButtons.Children.OfType<Button>().Single(button => ((int, int, int, bool))button.Tag is { Item2: 1 });
        down.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(() => !(bool)Field("_renderingContent"));
        Check(model.CurrentPatch.Positions[0] == 3 && model.Status.Contains("Context mismatch"), "moving a hunk recomputes target mismatch");
        var folded = model.Lines.FindIndex(line => line.FilteredLineCount > 0);
        Check(model.ExpandFilteredLines(folded), "unknown interval expands");
        window.RefreshFoldedPanes(0, 0, 0);
        Check(model.Lines.Any(line => line.EditHunkId < 0 && line.Left == null && line.Right != null), "expanded interval aligns empty patch rows with actual target lines");
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1280, 900, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(AppContext.BaseDirectory, "patch-view.png"))) png.Save(stream);
        Console.WriteLine("SCREENSHOT " + Path.Combine(AppContext.BaseDirectory, "patch-view.png"));
        // End the isolated test process without persisting the operational user's view settings.
        return 0;
    }
}
