using DesktopIniManager.Models;
using DesktopIniManager.ViewModels;
using System.Collections.Generic;
using System.Windows.Input;

namespace DesktopIniManager.Views;

public partial class IconGroupBrowserWindow
{
    internal IconGroupBrowserViewModel ViewModel { get; }

    /// <summary>
    ///     アイコン グループの選択ウィンドウを作成します。
    /// </summary>
    /// <param name="filePath">アイコン ライブラリのパス。</param>
    /// <param name="groups">選択肢のアイコン グループ。</param>
    /// <param name="currentIndex">現在選択されているアイコンのシェル インデックス。</param>
    internal IconGroupBrowserWindow(string filePath, IEnumerable<IconGroupResource> groups, int currentIndex)
    {
        ViewModel = new IconGroupBrowserViewModel(filePath, groups, currentIndex);
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.SelectionConfirmed += () => DialogResult = true;
        if (CancelButtonIcon != null)
            CancelButtonIcon.Source = DifferencerStatusIcons.GetCustomIcon(25);
        if (SelectButtonIcon != null)
            SelectButtonIcon.Source = DifferencerStatusIcons.GetCustomIcon(34);
        Loaded += (_, _) => { if (GroupList.SelectedItem != null) GroupList.ScrollIntoView(GroupList.SelectedItem); };
    }

    internal IconGroupResource SelectedGroup => ViewModel.SelectedGroup;

    /// <summary>
    ///     アイコンのダブルクリックで選択を確定します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">イベントデータ。</param>
    private void GroupList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    { if (SelectedGroup != null) ConfirmSelection(); }

    /// <summary>
    ///     選択を確定します。
    /// </summary>
    private void ConfirmSelection() => ViewModel.SelectCommand.Execute(null);
}