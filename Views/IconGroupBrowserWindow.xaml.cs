using DesktopIniManager.Models;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace DesktopIniManager.Views
{
    public partial class IconGroupBrowserWindow : Window
    {
        internal IconGroupBrowserWindow(string filePath, IEnumerable<IconGroupResource> groups, int currentIndex)
        {
            InitializeComponent();
            var items = groups.ToList();
            FilePathText.Text = filePath;
            CountText.Text = items.Count + " icons";
            GroupList.ItemsSource = items;
            GroupList.SelectedItem = items.FirstOrDefault(item => item.ShellIndex == currentIndex) ?? items.FirstOrDefault();
            Loaded += (sender, args) => { if (GroupList.SelectedItem != null) GroupList.ScrollIntoView(GroupList.SelectedItem); };
        }

        internal IconGroupResource SelectedGroup => GroupList.SelectedItem as IconGroupResource;
        private void Select_Click(object sender, RoutedEventArgs e) => ConfirmSelection();
        private void GroupList_MouseDoubleClick(object sender, MouseButtonEventArgs e) { if (SelectedGroup != null) ConfirmSelection(); }
        private void ConfirmSelection() { if (SelectedGroup != null) DialogResult = true; }
    }
}
