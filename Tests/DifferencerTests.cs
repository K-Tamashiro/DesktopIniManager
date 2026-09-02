using DesktopIniManager.Services;
using DesktopIniManager.Views;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class DifferencerTests
{
    private static int checks;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
    private static void Reject(Action action, string message) { try { action(); } catch (IOException) { Check(true, message); return; } throw new Exception("Not rejected: " + message); }
    private static readonly DateTime FixedTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static void Write(string root, string relative, string content)
    { string path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, content); File.SetLastWriteTimeUtc(path, FixedTime); }
    private static DiffSnapshot Snapshot(string source, string target, params string[] paths)
    {
        var result = new DiffSnapshot { SourceRoot = MftDifferencerService.Root(source), TargetRoot = MftDifferencerService.Root(target) };
        foreach (string p in paths) result.Files.Add(new DiffFile { RelativePath = p, Source = DiffStamp.Read(Path.Combine(source, p)), Target = DiffStamp.Read(Path.Combine(target, p)) });
        return result;
    }
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--scroll")) return ScrollPerformance.Run(args[1]);
            if (args.Contains("--diff-map")) return ScrollPerformance.Run(args[1], true);
            if (args.Contains("--folder-tree")) return FolderTreePersistenceTests.Run(args[1]);
            string artifacts = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixtures-" + Guid.NewGuid().ToString("N"));
            string source = Path.Combine(artifacts, "source"), target = Path.Combine(artifacts, "target");
            Directory.CreateDirectory(source); Directory.CreateDirectory(target);
            string root = MftDifferencerService.Root(source);
            foreach (string path in new[] { ".git\\config", "nested\\.GIT\\HEAD", ".git", "..\\target\\a", "C:\\a", "a:stream", ".git.\\HEAD", "a\\..\\b" })
                Reject(() => MftDifferencerService.SafePath(root, path), "protected path: " + path);
            Check(MftDifferencerService.SafePath(root, ".gitignore").EndsWith(".gitignore"), ".gitignore is an ordinary file");
            Reject(() => MftDifferencerService.ValidateRoots(root, root), "equal roots");
            Reject(() => MftDifferencerService.ValidateRoots(root, root + "nested\\"), "overlapping roots");
            var stamp = new DiffStamp { Size = 1, ModifiedUtc = FixedTime };
            var left = new Dictionary<string, DiffStamp>(StringComparer.OrdinalIgnoreCase) { { "same", stamp }, { "copy", stamp }, { "size", stamp }, { ".git\\HEAD", stamp }, { "time", stamp } };
            var right = new Dictionary<string, DiffStamp>(StringComparer.OrdinalIgnoreCase) { { "SAME", stamp }, { "delete", stamp }, { "size", new DiffStamp { Size = 2, ModifiedUtc = FixedTime } }, { "time", new DiffStamp { Size = 1, ModifiedUtc = FixedTime.AddSeconds(1) } } };
            var classified = MftDifferencerService.Classify(left, right);
            Check(classified.Count == 4, "relative paths, case-insensitive identity, .git exclusion");
            Check(classified.Single(f => f.RelativePath == "size").SourceInfo.StartsWith("サイズ差異"), "equal timestamp size-only difference");
            Check(classified.Single(f => f.RelativePath == "time").TargetInfo.StartsWith("NEW"), "NEW/OLD timestamps");
            foreach (bool forward in new[] { true, false })
            {
                string from = forward ? source : target, to = forward ? target : source;
                string prefix = forward ? "forward\\" : "reverse\\";
                Write(from, prefix + "copy", "copy"); Write(from, prefix + "overwrite", "new content"); Write(to, prefix + "overwrite", "old"); Write(to, prefix + "delete", "delete"); Write(to, prefix + "unchecked", "keep");
                Write(from, ".git\\HEAD", "protected source"); Write(to, ".git\\HEAD", "protected target");
                var snap = Snapshot(source, target, prefix + "copy", prefix + "overwrite", prefix + "delete", prefix + "unchecked");
                var log = MftDifferencerService.Synchronize(snap, snap.Files.Take(3), forward);
                Check(log.Count == 3 && log.All(l => l.StartsWith("OK ")), "copy/overwrite/delete direction " + forward + " " + string.Join(";", log));
                Check(File.ReadAllText(Path.Combine(to, prefix + "overwrite")) == "new content", "overwrite contents");
                Check(DiffStamp.Same(DiffStamp.Read(Path.Combine(from, prefix + "overwrite")), DiffStamp.Read(Path.Combine(to, prefix + "overwrite"))), "replacement preserves comparison metadata");
                Check(!File.Exists(Path.Combine(to, prefix + "delete")) && File.Exists(Path.Combine(to, prefix + "unchecked")), "only selected files changed");
                Check(File.ReadAllText(Path.Combine(to, ".git\\HEAD")) == "protected target", "Git content unchanged");
            }
            Write(source, "stale", "before"); Write(source, "valid", "valid");
            var stale = Snapshot(source, target, "stale", "valid"); Write(source, "stale", "changed after comparison");
            var staleLog = MftDifferencerService.Synchronize(stale, stale.Files, true);
            Check(staleLog[0].StartsWith("FAIL ") && staleLog[1].StartsWith("OK ") && !File.Exists(Path.Combine(target, "stale")), "stale snapshot refused; remaining files continue");
            var malicious = Snapshot(source, target, ".git\\HEAD");
            Check(MftDifferencerService.Synchronize(malicious, malicious.Files, true)[0].StartsWith("FAIL "), "sync rejects .git even if manually selected");
            var changes = DiffTextService.Compare(new[] { "a", "old", "z" }, new[] { "a", "new", "added", "z" });
            Check(changes.Count == 4 && changes[1].Kind == "変更" && changes[2].Kind == "追加", "aligned text modifications and additions");
            Check(DiffTextService.Compare(new[] { "gone" }, new string[0])[0].Kind == "削除", "one-sided text deletion");
            var random = new Random(17);
            for (int n = 0; n < 100; n++)
            {
                string[] a = Enumerable.Range(0, random.Next(50)).Select(i => random.Next(9).ToString()).ToArray(), b = Enumerable.Range(0, random.Next(50)).Select(i => random.Next(9).ToString()).ToArray();
                var result = DiffTextService.Compare(a, b);
                if (!a.SequenceEqual(result.Where(l => l.LeftNumber > 0).Select(l => l.Left)) || !b.SequenceEqual(result.Where(l => l.RightNumber > 0).Select(l => l.Right))) throw new Exception("Diff lost lines");
            }
            Check(true, "100 randomized text diffs preserve both inputs");
            var node = new DiffFolder(); node.Files.AddRange(classified); node.Toggle = (f, value) => { foreach (var item in f.Files) item.Selected = value; };
            foreach (var file in classified) file.PropertyChanged += (s, e) => node.Refresh();
            node.Checked = true; Check(node.Checked == true, "folder selects descendants"); classified[0].Selected = false; Check(node.Checked == null, "individual deselection makes folder indeterminate"); node.Checked = false; Check(node.Files.All(f => !f.Selected), "folder clears descendants");
            if (args.Contains("--mft"))
            {
                var mft = MftDifferencerService.Compare(source, target);
                Check(mft.Files.All(f => !MftDifferencerService.Protected(f.RelativePath)) && mft.Folders.All(p => !MftDifferencerService.Protected(p)), "real MFT scan excludes .git");
                Check(mft.Files.Any(f => f.RelativePath == "stale"), "real MFT scan finds expected difference");
            }
            var app = new DesktopIniManager.App(); app.InitializeComponent();
            typeof(MftDifferencerWindow).GetField("StatePath", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, Path.Combine(artifacts, "state.xml"));
            var window = new MftDifferencerWindow();
            ((TextBox)window.FindName("SourceBox")).Text = source; ((TextBox)window.FindName("TargetBox")).Text = target;
            Write(source, "nested\\child\\changed.txt", "new");
            var display = Snapshot(source, target, "stale", "forward\\unchecked", "nested\\child\\changed.txt");
            typeof(MftDifferencerWindow).GetField("snapshot", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(window, display);
            var displayRows = display.Files.Select(f => new DiffRow { File = f, Source = new DiffSide { Info = f.SourceInfo, Root = display.SourceRoot, Relative = f.RelativePath, Exists = f.Source != null }, Target = new DiffSide { Info = f.TargetInfo, Root = display.TargetRoot, Relative = f.RelativePath, Exists = f.Target != null } }).ToList();
            typeof(MftDifferencerWindow).GetField("rows", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(window, displayRows);
            typeof(MftDifferencerWindow).GetField("treeSource", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(window, source);
            typeof(MftDifferencerWindow).GetField("treeTarget", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(window, target);
            typeof(MftDifferencerWindow).GetMethod("BuildTree", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(window, new object[] { new[] { "", "forward", "nested", "nested\\child", "nested\\identical", "identicalOnly" }, new[] { "", "nested" }, "nested" });
            typeof(MftDifferencerWindow).GetField("selectedFolder", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(window, "");
            typeof(MftDifferencerWindow).GetMethod("Filter", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(window, null);
            var content = (FrameworkElement)window.Content; content.SetResourceReference(Panel.BackgroundProperty, "WindowBackground"); content.Measure(new Size(1280, 800)); content.Arrange(new Rect(0, 0, 1280, 800)); content.UpdateLayout();
            var liveTree = (TreeView)window.FindName("FolderTree"); ((TreeViewItem)liveTree.ItemContainerGenerator.ContainerFromIndex(0)).IsSelected = true;
            content.UpdateLayout();
            Check(((ListView)window.FindName("FilesGrid")).Items.Count == 3, "root selection displays all descendant differences");
            var filteredRoot = (DiffFolder)liveTree.Items[0];
            Check(filteredRoot.DisplayChildren.Select(f => f.Path).OrderBy(p => p).SequenceEqual(new[] { "forward", "nested" }), "folders without differences are hidden");
            Check(filteredRoot.DisplayChildren.Single(f => f.Path == "nested").DisplayChildren.Single().Path == "nested\\child", "difference folder retains its parent; identical siblings are hidden");
            var bitmap = new RenderTargetBitmap(1280, 800, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var stream = File.Create(Path.Combine(artifacts, "window.png"))) encoder.Save(stream);
            typeof(MftDifferencerWindow).GetField("selectedFolder", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(window, "nested");
            typeof(MftDifferencerWindow).GetMethod("SaveState", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(window, null);
            var restored = new MftDifferencerWindow();
            Check(((TextBox)restored.FindName("SourceBox")).Text == source, "WPF window restores roots");
            var tree = (TreeView)restored.FindName("FolderTree"); var restoredRoot = (DiffFolder)tree.Items[0];
            var restoredNested = restoredRoot.DisplayChildren.Single(f => f.Path == "nested");
            Check(restoredNested.Expanded && restoredNested.Active && restoredNested.DisplayChildren.Count() == 1, "tree hierarchy, expansion and selected folder persist");
            Check(restoredRoot.Children.Any(f => f.Path == "identicalOnly") && restoredRoot.DisplayChildren.All(f => f.Path != "identicalOnly"), "filtered view persists without deleting the base tree");
            Check(!((Button)restored.FindName("ForwardButton")).IsEnabled, "cached tree cannot authorize synchronization");
            int unrelatedNotifications = 0;
            filteredRoot.Children.Single(f => f.Path == "forward").PropertyChanged += (s, e) => unrelatedNotifications++;
            display.Files.Single(f => f.RelativePath == "nested\\child\\changed.txt").Selected = true;
            Check(filteredRoot.SelectedCount == 1 && filteredRoot.Children.Single(f => f.Path == "nested").Checked == true && unrelatedNotifications == 0, "file checkbox updates ancestors only");
            Check(ReferenceEquals(filteredRoot.DisplayChildren, filteredRoot.DisplayChildren), "tree bindings reuse the same child collection");
            var invalidSide = new DiffSide { Exists = true, Root = "not a valid root", Relative = "preview.png" };
            Check(invalidSide.Thumbnail == null && invalidSide.Dimensions == null, "thumbnail binding getters do not perform filesystem work");
            string previewPath = Path.Combine(source, "preview.png");
            var previewEncoder = new PngBitmapEncoder();
            previewEncoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8)));
            using (var stream = File.Create(previewPath)) previewEncoder.Save(stream);
            var imageRow = new DiffRow { File = Snapshot(source, target, "preview.png").Files[0], Source = new DiffSide { Exists = true, Root = display.SourceRoot, Relative = "preview.png" }, Target = new DiffSide { Exists = false, Root = display.TargetRoot, Relative = "preview.png" } };
            using (var imageWorkers = new System.Threading.SemaphoreSlim(2))
            {
                Check(imageRow.LoadPreviewAsync(imageWorkers, System.Threading.CancellationToken.None).Wait(3000) && imageRow.Source.Thumbnail != null && imageRow.Source.Thumbnail.IsFrozen && imageRow.Source.Dimensions == "2 × 2", "background image decoding returns a frozen preview with dimensions");
                var firstIcon = imageRow.Icon;
                Check(imageRow.LoadPreviewAsync(imageWorkers, System.Threading.CancellationToken.None).IsCompleted && ReferenceEquals(firstIcon, imageRow.Icon), "completed previews are reused without scheduling more work");
                imageRow.ReleasePreview();
                Check(imageRow.Source.Thumbnail == null && imageRow.Icon == null, "scrolled-away image rows release their bitmap references");
                var tallEncoder = new PngBitmapEncoder();
                tallEncoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(4, 4000, 96, 96, PixelFormats.Bgra32, null, new byte[4 * 4000 * 4], 16)));
                using (var stream = File.Create(Path.Combine(source, "tall.png"))) tallEncoder.Save(stream);
                var tall = new DiffSide { Exists = true, Root = display.SourceRoot, Relative = "tall.png" }.ReadPreview();
                Check(tall.Thumbnail != null && tall.Thumbnail.PixelWidth <= 96 && tall.Thumbnail.PixelHeight <= 64, "tall image thumbnail dimensions are bounded on both axes");
            }
            var pendingRow = new DiffRow { File = display.Files[0], Source = displayRows[0].Source, Target = displayRows[0].Target };
            using (var blockedWorker = new System.Threading.SemaphoreSlim(0, 1))
            using (var scope = new System.Threading.CancellationTokenSource())
            {
                var pending = pendingRow.LoadPreviewAsync(blockedWorker, scope.Token);
                scope.Cancel();
                Check(pending.Wait(3000) && pendingRow.Icon == null, "obsolete preview requests stop while waiting for a worker");
            }
            typeof(MftDifferencerWindow).GetMethod("ClearComparisonView", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(window, null);
            Check(liveTree.Items.Count == 0 && ((ListView)window.FindName("FilesGrid")).Items.Count == 0, "comparison start clears both visible lists immediately");
            Check(filteredRoot.Children.Any(f => f.Path == "identicalOnly"), "clearing the view preserves the saved base hierarchy");
            var filterWindow = new MftDifferencerWindow();
            Check(((CheckBox)filterWindow.FindName("SameFilter")).IsChecked == false && ((CheckBox)filterWindow.FindName("DifferentFilter")).IsChecked == true && ((CheckBox)filterWindow.FindName("SourceOnlyFilter")).IsChecked == true && ((CheckBox)filterWindow.FindName("TargetOnlyFilter")).IsChecked == true, "category filters default to differences and both one-sided categories");
            var filterFiles = MftDifferencerService.Classify(
                new Dictionary<string, DiffStamp>(StringComparer.OrdinalIgnoreCase) { { "parent\\same\\file.txt", stamp }, { "parent\\different\\file.txt", stamp }, { "parent\\left\\file.txt", stamp } },
                new Dictionary<string, DiffStamp>(StringComparer.OrdinalIgnoreCase) { { "parent\\same\\file.txt", stamp }, { "parent\\different\\file.txt", new DiffStamp { Size = 2, ModifiedUtc = FixedTime } }, { "parent\\right\\file.txt", stamp } }, true);
            Check(filterFiles.Count == 4 && filterFiles.Single(f => f.Kind == DiffKind.Same).State == "同一", "comparison retains identical metadata as a separate category");
            var filterSnapshot = new DiffSnapshot { SourceRoot = display.SourceRoot, TargetRoot = display.TargetRoot, Files = filterFiles };
            typeof(MftDifferencerWindow).GetField("snapshot", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(filterWindow, filterSnapshot);
            typeof(MftDifferencerWindow).GetField("rows", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(filterWindow, filterFiles.Select(f => new DiffRow { File = f, SourceRoot = display.SourceRoot, TargetRoot = display.TargetRoot }).ToList());
            typeof(MftDifferencerWindow).GetMethod("BuildTree", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(filterWindow, new object[] { filterFiles.Select(f => Path.GetDirectoryName(f.RelativePath)), new[] { "", "parent" }, "" });
            var leftOnly = filterFiles.Single(f => f.Kind == DiffKind.SourceOnly); leftOnly.Selected = true;
            var filterNames = new[] { "SameFilter", "DifferentFilter", "SourceOnlyFilter", "TargetOnlyFilter" };
            Action<int> setMask = mask =>
            {
                for (int i = 0; i < 4; i++) ((CheckBox)filterWindow.FindName(filterNames[i])).IsChecked = (mask & (1 << i)) != 0;
                typeof(MftDifferencerWindow).GetMethod("KindFilter_Click", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(filterWindow, new object[] { null, new RoutedEventArgs() });
            };
            for (int mask = 0; mask < 16; mask++)
            {
                setMask(mask);
                var shown = ((ListView)filterWindow.FindName("FilesGrid")).Items.Cast<DiffRow>().Select(r => r.File).ToList();
                var expected = filterFiles.Where(f => ((int)f.Kind & mask) != 0).ToList();
                var rootNode = (DiffFolder)((TreeView)filterWindow.FindName("FolderTree")).Items[0];
                Check(shown.SequenceEqual(expected) && rootNode.CountFor((DiffKind)mask) == expected.Count && (expected.Count == 0 ? rootNode.DisplayChildren.Count == 0 : rootNode.DisplayChildren.Single().DisplayChildren.Count == expected.Count) && leftOnly.Selected, "category combination " + mask + " filters files, folders and ancestors without losing selection");
            }
            setMask((int)DiffKind.Same);
            var sameRoot = (DiffFolder)((TreeView)filterWindow.FindName("FolderTree")).Items[0];
            var sameFile = filterFiles.Single(f => f.Kind == DiffKind.Same); sameFile.Selected = true;
            Check(!sameFile.Selected && !sameRoot.CanSelect && ((TextBlock)filterWindow.FindName("CountText")).Text.Contains("非表示 1"), "identical files cannot be selected for sync; hidden selections remain visible in the count");
            setMask((int)(DiffKind.SourceOnly | DiffKind.TargetOnly)); sameRoot.Checked = true;
            setMask((int)DiffKind.SourceOnly); sameRoot.Checked = false;
            Check(!leftOnly.Selected && filterFiles.Single(f => f.Kind == DiffKind.TargetOnly).Selected, "folder checkbox changes only currently enabled categories");
            filterWindow.Close();
            window.Close(); restored.Close();
            Console.WriteLine("PASS " + checks + " checks. Artifacts: " + artifacts); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
