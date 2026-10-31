using DesktopIniManager.Models;
using DesktopIniManager.Properties;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DesktopIniManager.ViewModels;

internal sealed class IconGroupBrowserViewModel : ObservableObject
{
    private IconGroupResource selectedGroup;

    internal IconGroupBrowserViewModel(string filePath, IEnumerable<IconGroupResource> groups, int currentIndex)
    {
        FilePath = filePath;
        Groups = groups.ToList();
        SelectCommand = new RelayCommand(() => SelectionConfirmed?.Invoke(), () => SelectedGroup != null);
        SelectedGroup = Groups.FirstOrDefault(item => item.ShellIndex == currentIndex) ?? Groups.FirstOrDefault();
    }

    public string FilePath { get; }
    public IReadOnlyList<IconGroupResource> Groups { get; }
    public string CountLabel => string.Format(Strings.Icon_NIcons, Groups.Count);

    public IconGroupResource SelectedGroup
    {
        get => selectedGroup;
        set
        {
            if (SetProperty(ref selectedGroup, value)) SelectCommand.NotifyCanExecuteChanged();
        }
    }

    public RelayCommand SelectCommand { get; }

    public event Action SelectionConfirmed;
}