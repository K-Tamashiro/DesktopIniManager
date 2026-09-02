using DesktopIniManager;
using DesktopIniManager.Models;
using DesktopIniManager.Services;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Markup;

internal static class FolderTreePersistenceTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private static ObservableCollection<FolderMatch> Nodes(MainWindow window, string name)
    { return (ObservableCollection<FolderMatch>)typeof(MainWindow).GetField(name, Private).GetValue(window); }
    private static void Save(MainWindow window)
    { typeof(MainWindow).GetMethod("SaveFolderTrees", Private).Invoke(window, null); }

    internal static int Run(string appXaml)
    {
        // The test executable hosts the production window; relative theme URIs belong to DIM.
        typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)
            .SetValue(null, typeof(MainWindow).Assembly);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var xml = new System.Xml.XmlDocument(); xml.Load(appXaml);
        var namespaces = new System.Xml.XmlNamespaceManager(xml.NameTable);
        namespaces.AddNamespace("p", "http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        string resources = xml.SelectSingleNode("p:Application/p:Application.Resources/p:ResourceDictionary", namespaces).OuterXml;
        var context = new ParserContext(); context.XmlnsDictionary.Add("x", "http://schemas.microsoft.com/winfx/2006/xaml");
        app.Resources = (ResourceDictionary)XamlReader.Parse(resources.Replace("Source=\"Themes/", "Source=\"/DesktopIniManager;component/Themes/"), context);
        string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "folder-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        FolderTreeStateService.StatePath = Path.Combine(directory, "state.xml");
        var child = new FolderMatch { Path = Path.Combine(directory, "child"), DisplayName = "Child", Reason = "Git", IsExpanded = true };
        var root = new FolderMatch { Path = directory, Reason = "Folder", IsExpanded = true };
        root.Children.Add(child); child.Parent = root;
        var solution = new FolderMatch { Path = directory, DisplayName = "Solution", IsActionable = false, IsExpanded = true };
        FolderTreeStateService.Save(new FolderTreeState
        {
            Root = directory, View = 1,
            Physical = FolderTreeStateService.Capture(new[] { root }, child),
            Solution = FolderTreeStateService.Capture(new[] { solution }, solution)
        });
        var first = new MainWindow();
        var physical = Nodes(first, "_treeRoots");
        var restoredChild = physical.Single().Children.Single();
        Check(restoredChild.Parent == physical.Single() && restoredChild.IsExpanded && restoredChild.IsCurrent,
            "main window restores hierarchy, parent links, expansion and current folder");
        Check(Nodes(first, "_solutionRoots").Single().DisplayName == "Solution" && (int)typeof(MainWindow).GetField("_treeView", Private).GetValue(first) == 1,
            "solution tree and active base tab restored");
        Check(Nodes(first, "_results").Count == 2 && !restoredChild.IsSelected, "physical index restored without selecting file operations");
        restoredChild.IsFilterHidden = true;
        Nodes(first, "_searchRoots").Add(new FolderMatch { Path = "search-only" });
        typeof(MainWindow).GetMethod("ShowTreeView", Private).Invoke(first, new object[] { 2 });
        Save(first);
        Check(FolderTreeStateService.Load().Icons.Count == 1, "shared icons are stored once for both trees");
        var second = new MainWindow();
        Check(ReferenceEquals(Nodes(second, "_treeRoots").Single().IconPreview, Nodes(second, "_solutionRoots").Single().IconPreview),
            "icons are restored and shared without rescanning directories");
        Check(Nodes(second, "_treeRoots").Single().Children.Single().IsCurrent && Nodes(second, "_searchRoots").Count == 0,
            "Search view cannot replace base trees or their current folder");
        Check(!Nodes(second, "_treeRoots").Single().Children.Single().IsFilterHidden,
            "temporary filtering is not persisted");
        typeof(MainWindow).GetField("_rebuildingFolderTrees", Private).SetValue(second, true);
        Nodes(second, "_treeRoots").Clear();
        Save(second);
        Check(FolderTreeStateService.Load().Physical.Single().Children.Count == 1,
            "incomplete rebuild preserves the last completed snapshot");
        Console.WriteLine("Folder tree persistence checks completed.");
        return 0;
    }
}
