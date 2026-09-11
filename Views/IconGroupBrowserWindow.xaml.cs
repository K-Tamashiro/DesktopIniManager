using DesktopIniManager.Models;
using DesktopIniManager.ViewModels;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;

namespace DesktopIniManager.Views
{
    public partial class IconGroupBrowserWindow : Window
    {
        internal IconGroupBrowserViewModel ViewModel { get; }
        internal IconGroupBrowserWindow(string filePath, IEnumerable<IconGroupResource> groups, int currentIndex)
        {
            ViewModel = new IconGroupBrowserViewModel(filePath, groups, currentIndex);
            InitializeComponent();
            DataContext = ViewModel;
            ViewModel.SelectionConfirmed += () => DialogResult = true;
            Loaded += (sender, args) => { if (GroupList.SelectedItem != null) GroupList.ScrollIntoView(GroupList.SelectedItem); };
        }

        internal IconGroupResource SelectedGroup => ViewModel.SelectedGroup;
        private void GroupList_MouseDoubleClick(object sender, MouseButtonEventArgs e) { if (SelectedGroup != null) ConfirmSelection(); }
        private void ConfirmSelection() => ViewModel.SelectCommand.Execute(null);
    }
}
