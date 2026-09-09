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
        private void CompactTree() => ApplyTreeDensity(true, true);

        private void ComfortableTree() => ApplyTreeDensity(false, true);

        private void ApplyTreeDensity(bool compact, bool announce)
        {
            ViewModel.TreeCompact = compact;
            HighlightTreeDensityButtons();
            SettingsService.SaveTreeCompact(compact);
            if (announce) ViewModel.Status = compact ? Strings.Main_TreeCompact : Strings.Main_TreeComfortable;
        }

        private void HighlightTreeDensityButtons()
        {
            System.Windows.Media.Brush selected = (System.Windows.Media.Brush)FindResource("ThemeSelected");
            System.Windows.Media.Brush secondary = (System.Windows.Media.Brush)FindResource("Secondary");
            if (CompactTreeButton != null) CompactTreeButton.Background = ViewModel.TreeCompact ? selected : secondary;
            if (ComfortableTreeButton != null) ComfortableTreeButton.Background = ViewModel.TreeCompact ? secondary : selected;
        }

        private void FileListView()
        {
            FileList.Visibility = Visibility.Visible;
            FileIconList.Visibility = Visibility.Collapsed;
            HighlightFileViewButtons(list: true, large: false);
        }

        private void FileIconSmall()
        {
            ApplyFileIconSize(false);
        }

        private void FileIconLarge()
        {
            ApplyFileIconSize(true);
        }

        private void ApplyFileIconSize(bool large)
        {
            _largeFileIcons = large;
            FileList.Visibility = Visibility.Collapsed;
            FileIconList.Visibility = Visibility.Visible;
            FileIconList.ItemsPanel = (ItemsPanelTemplate)FindResource(large ? "FileIconLargePanel" : "FileIconSmallPanel");
            FileIconList.ItemTemplate = (DataTemplate)FindResource(large ? "FileIconLargeTemplate" : "FileIconSmallTemplate");
            HighlightFileViewButtons(list: false, large: large);
            ShowIconLayoutBusy();
        }

        private void HighlightFileViewButtons(bool list, bool large)
        {
            System.Windows.Media.Brush selected = (System.Windows.Media.Brush)FindResource("ThemeSelected");
            System.Windows.Media.Brush secondary = (System.Windows.Media.Brush)FindResource("Secondary");
            if (FileListViewButton != null) FileListViewButton.Background = list ? selected : secondary;
            if (FileIconLargeButton != null) FileIconLargeButton.Background = !list && large ? selected : secondary;
            if (FileIconSmallButton != null) FileIconSmallButton.Background = !list && !large ? selected : secondary;
        }

        private void ShowIconLayoutBusy()
        {
            if (ViewModel._files.Count < 40) return;
            SetFilePanelBusy(true);
            Dispatcher.BeginInvoke(new Action(() => SetFilePanelBusy(false)), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        private void SetFilePanelBusy(bool busy)
        {
            if (FilePanelBusy != null)
                ViewModel.IsFileBusy = busy;
        }

        private void FolderFilter_TextChanged(object sender, TextChangedEventArgs e)
        { _filterTimer?.Stop(); _filterTimer?.Start(); }

        private void ClearFolderFilter()
        { ViewModel.FolderFilter = string.Empty; }

        private void HookPathBox(TextBox box)
        {
            if (box == null) return;
            box.GotKeyboardFocus += (sender, args) => ShowTextEnd(box);
            box.LostKeyboardFocus += (sender, args) => ShowTextEnd(box);
            box.TextChanged += (sender, args) =>
            {
                if (!box.IsKeyboardFocusWithin) ShowTextEnd(box);
            };
        }

        private void ShowTextEnd(TextBox box)
        {
            if (box == null) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                box.CaretIndex = (box.Text ?? string.Empty).Length;
                box.ScrollToHorizontalOffset(Math.Max(0, box.ExtentWidth - box.ViewportWidth));
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void SetSearching(bool value)
        {
            if (value)
                Dispatcher.BeginInvoke(new Action(() => AnimateSearchProgress(true)), DispatcherPriority.Loaded);
            else
            {
                AnimateSearchProgress(false);
                SetTreePanelBusy(false);
            }
        }

        private void AnimateSearchProgress(bool value)
        {
            SearchProgress.ApplyTemplate();
            var marquee = SearchProgress.Template.FindName("Marquee", SearchProgress) as Border;
            if (marquee == null) return;
            var currentTransform = marquee.RenderTransform as TranslateTransform;
            var transform = currentTransform == null ? new TranslateTransform() : currentTransform.CloneCurrentValue();
            marquee.RenderTransform = transform;
            if (!value) { transform.BeginAnimation(TranslateTransform.XProperty, null); transform.X = 0; return; }
            double distance = Math.Max(0, SearchProgress.ActualWidth - (marquee.ActualWidth > 0 ? marquee.ActualWidth : 150));
            var animation = new DoubleAnimation(0, distance, TimeSpan.FromSeconds(1.2))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            transform.BeginAnimation(TranslateTransform.XProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }

        private void SetTreePanelBusy(bool busy)
        {
            if (TreePanelBusy != null)
                ViewModel.IsTreeBusy = busy;
        }

        private void LightTheme() => SetTheme(false);

        private void DarkTheme() => SetTheme(true);

        private void SetTheme(bool dark)
        {
            ThemeService.Apply(dark);
            LightThemeButton.IsChecked = !dark;
            DarkThemeButton.IsChecked = dark;
            SettingsService.SaveDarkMode(dark);
            HighlightTreeDensityButtons();
            HighlightFileViewButtons(FileList.Visibility == Visibility.Visible, _largeFileIcons);
        }

    }
}
