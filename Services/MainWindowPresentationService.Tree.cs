using DesktopIniManager.ViewModels;
using DesktopIniManager.Models;
using DesktopIniManager.Services;
using DesktopIniManager.Views;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Controls;
using System.Windows.Threading;
using FastVolumeIndex;
using DesktopIniManager.Properties;

namespace DesktopIniManager.Services
{
    internal sealed partial class MainWindowPresentationService
    {
        private void SelectSearchRootForFileList()
        {
            if (ViewModel._searchRoots.Count == 0) return;
            FolderMatch root = ViewModel._searchRoots[0];
            root.IsExpanded = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ViewModel._treeView != 2 || ViewModel._searchRoots.Count == 0) return;
                FolderMatch current = ViewModel._searchRoots[0];
                current.IsExpanded = true;
                current.IsCurrent = true;
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private async void ResultsTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (_syncingTreeFromFile) return;
            try { await ViewModel.LoadFilesAsync(e.NewValue as FolderMatch); }
            catch (Exception ex) { ViewModel.ShowError(Strings.App_Unhandled, ex); }
        }

        private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingTreeFromFile) return;
            FileListItem file = (sender as System.Windows.Controls.Primitives.Selector)?.SelectedItem as FileListItem;
            if (file == null) return;
            RevealFolderInCurrentTree(Path.GetDirectoryName(file.Path));
        }

        private void RevealFolderInCurrentTree(string directory)
        {
            ObservableCollection<FolderMatch> roots = ViewModel.CurrentTreeRoots();
            if (string.IsNullOrEmpty(directory) || roots.Count == 0) return;
            FolderMatch target = ViewModel.FindFolderInRoots(roots, directory);
            if (target == null) return;
            for (FolderMatch ancestor = target.Parent; ancestor != null; ancestor = ancestor.Parent)
                ancestor.IsExpanded = true;
            target.IsExpanded = true;
            _syncingTreeFromFile = true;
            foreach (FolderMatch item in MainWindowViewModel.Flatten(roots))
                if (item.IsCurrent && item != target) item.IsCurrent = false;
            target.IsCurrent = true;
            if (ViewModel._treeView == 0) ViewModel._physicalCurrent = target;
            else if (ViewModel._treeView == 1) ViewModel._solutionCurrent = target;
            else ViewModel._searchCurrent = target;
            ResultsTree.UpdateLayout();
            ScheduleFolderIntoView(target);
        }

        private void ScheduleFolderIntoView(FolderMatch target)
        {
            if (target == null)
            {
                _syncingTreeFromFile = false;
                return;
            }

            var path = new List<FolderMatch>();
            for (FolderMatch node = target; node != null; node = node.Parent)
            {
                node.IsExpanded = true;
                path.Add(node);
            }
            path.Reverse();

            Dispatcher.BeginInvoke(
                new Action(() => ExpandPathStep(ResultsTree, path, 0, 0)),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void ExpandPathStep(ItemsControl host, List<FolderMatch> path, int level, int retry)
        {
            if (host == null || level >= path.Count)
            {
                _syncingTreeFromFile = false;
                return;
            }

            FolderMatch node = path[level];
            host.ApplyTemplate();
            host.UpdateLayout();

            TreeViewItem container = host.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;
            if (container == null)
            {
                int index = host.Items.IndexOf(node);
                if (index < 0 || retry >= 48)
                {
                    _syncingTreeFromFile = false;
                    return;
                }

                BringSiblingIndexIntoView(host, index);
                Dispatcher.BeginInvoke(
                    new Action(() => ExpandPathStep(host, path, level, retry + 1)),
                    System.Windows.Threading.DispatcherPriority.Loaded);
                return;
            }

            container.IsExpanded = true;
            container.UpdateLayout();

            if (level == path.Count - 1)
            {
                container.IsSelected = true;
                container.BringIntoView();
                ScrollTreeItemIntoView(ResultsTree, container);
                _syncingTreeFromFile = false;
                return;
            }

            container.BringIntoView();
            Dispatcher.BeginInvoke(
                new Action(() => ExpandPathStep(container, path, level + 1, 0)),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void BringSiblingIndexIntoView(ItemsControl host, int index)
        {
            if (index < 0) return;

            ItemsPresenter presenter = FindVisualChild<ItemsPresenter>(host);
            VirtualizingPanel panel = presenter != null ? FindVisualChild<VirtualizingPanel>(presenter) : null;
            if (panel != null)
            {
                System.Reflection.MethodInfo method = typeof(VirtualizingPanel).GetMethod(
                    "BringIndexIntoView",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (method != null)
                {
                    method.Invoke(panel, new object[] { index });
                    return;
                }
            }

            ScrollViewer viewer = FindScrollViewer(ResultsTree);
            if (viewer != null)
                viewer.ScrollToVerticalOffset(Math.Max(0, index - 2));
        }

        private static T FindVisualChild<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) return null;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                T match = child as T ?? FindVisualChild<T>(child);
                if (match != null) return match;
            }
            return null;
        }

        private static bool TryGetFlatIndex(System.Collections.Generic.IEnumerable<FolderMatch> nodes, FolderMatch target, ref int index)
        {
            foreach (FolderMatch node in nodes)
            {
                if (node.IsHidden || node.IsFilterHidden)
                    continue;
                if (ReferenceEquals(node, target))
                    return true;
                index++;
                if (node.IsExpanded && node.Children.Count > 0
                    && TryGetFlatIndex(node.Children, target, ref index))
                    return true;
            }
            return false;
        }

        private static TreeViewItem BringFolderIntoView(TreeView tree, FolderMatch target)
        {
            if (tree == null || target == null) return null;
            var path = new List<FolderMatch>();
            for (FolderMatch node = target; node != null; node = node.Parent)
            {
                node.IsExpanded = true;
                path.Add(node);
            }
            path.Reverse();
            tree.UpdateLayout();
            return ContainerAlongPath(tree, path);
        }

        private static TreeViewItem ContainerAlongPath(ItemsControl parent, List<FolderMatch> path)
        {
            TreeViewItem current = null;
            ItemsControl host = parent;
            foreach (FolderMatch node in path)
            {
                if (host == null) return null;
                host.ApplyTemplate();
                host.UpdateLayout();
                var generator = host.ItemContainerGenerator;
                if (generator.Status != System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
                    return null;

                var item = generator.ContainerFromItem(node) as TreeViewItem;
                if (item == null)
                {
                    int index = host.Items.IndexOf(node);
                    if (index >= 0)
                        item = generator.ContainerFromIndex(index) as TreeViewItem;
                }
                if (item == null)
                {
                    // Bring the parent into view and retry when virtualization has not created the item yet.
                    if (current != null) current.BringIntoView();
                    return null;
                }

                item.IsExpanded = true;
                item.UpdateLayout();
                current = item;
                host = item;
            }
            return current;
        }

        private static void ScrollTreeItemIntoView(TreeView tree, TreeViewItem item)
        {
            if (tree == null || item == null) return;
            item.IsSelected = true;
            item.BringIntoView();
            ScrollViewer viewer = FindScrollViewer(tree);
            if (viewer == null) return;
            try
            {
                Point pos = item.TransformToAncestor(viewer).Transform(new Point(0, 0));
                double top = pos.Y;
                double bottom = top + Math.Max(item.ActualHeight, 1);
                if (top < 0) viewer.ScrollToVerticalOffset(viewer.VerticalOffset + top - 8);
                else if (bottom > viewer.ViewportHeight)
                    viewer.ScrollToVerticalOffset(viewer.VerticalOffset + bottom - viewer.ViewportHeight + 8);
            }
            catch (InvalidOperationException) { }
        }

        private static ScrollViewer FindScrollViewer(DependencyObject root)
        {
            if (root is ScrollViewer viewer) return viewer;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                ScrollViewer found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
                if (found != null) return found;
            }
            return null;
        }

    }
}
