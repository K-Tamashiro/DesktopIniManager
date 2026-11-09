using DesktopIniManager.Models;
using DesktopIniManager.Properties;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DesktopIniManager.ViewModels;

internal sealed class IconGroupBrowserViewModel : ObservableObject
{
    private IconGroupResource _selectedGroup;

    /// <summary>
    ///     アイコン ライブラリ内のアイコン グループを選択するための ViewModel を作成します。
    /// </summary>
    /// <param name="filePath">アイコン ライブラリのパス。</param>
    /// <param name="groups">選択肢のアイコン グループ。</param>
    /// <param name="currentIndex">現在選択されているアイコンのシェル インデックス。</param>
    internal IconGroupBrowserViewModel(string filePath, IEnumerable<IconGroupResource> groups, int currentIndex)
    {
        FilePath = filePath;
        Groups = groups.ToList();
        SelectCommand = new RelayCommand(() => SelectionConfirmed?.Invoke(), () => SelectedGroup != null);
        SelectedGroup = Groups.FirstOrDefault(item => item.ShellIndex == currentIndex) ?? Groups.FirstOrDefault();
    }

    public string FilePath { get; }
    public IReadOnlyList<IconGroupResource> Groups { get; }
    public string CountLabel => string.Format(StringOverlay.Get("Icon_NIcons"), Groups.Count);

    public IconGroupResource SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (SetProperty(ref _selectedGroup, value)) SelectCommand.NotifyCanExecuteChanged();
        }
    }

    public RelayCommand SelectCommand { get; }

    public event Action SelectionConfirmed;
}