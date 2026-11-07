using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace DesktopIniManager.Models;

internal sealed class FolderMatch : INotifyPropertyChanged
{
    private ImageSource _iconPreview;
    private bool _isCurrent;
    private bool _isExpanded;
    private bool _isFilterHidden;
    private bool _isHidden;
    private bool _isSearchMatch;
    private bool _isSelected;
    private ImageSource _naturalIcon;
    private string _reason;

    public bool IsSearchMatch
    {
        get => _isSearchMatch;
        set
        {
            if (_isSearchMatch == value) return;
            _isSearchMatch = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSearchMatch)));
        }
    }

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
        }
    }

    internal static ImageSource CheckedFolderIcon { get; set; }
    public FolderMatch Parent { get; set; }
    public bool IsLazyLoaded { get; set; }
    public bool IsLazyLoading { get; set; }
    public bool IsLazyPlaceholder { get; set; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetSelected(value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public string Path { get; set; }
    public string SolutionFile { get; set; }
    public IList<string> BuildConfigurations { get; set; }

    public string Reason
    {
        get => _reason;
        set
        {
            if (_reason == value) return;
            _reason = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Reason)));
        }
    }

    public string DisplayName { get; set; }
    public bool IsActionable { get; set; } = true;

    public bool IsHidden
    {
        get => _isHidden;
        set
        {
            if (_isHidden == value) return;
            _isHidden = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHidden)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ItemVisibility)));
        }
    }

    public bool IsFilterHidden
    {
        get => _isFilterHidden;
        set
        {
            if (_isFilterHidden == value) return;
            _isFilterHidden = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFilterHidden)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ItemVisibility)));
        }
    }

    public Visibility ItemVisibility => IsHidden || IsFilterHidden ? Visibility.Collapsed : Visibility.Visible;
    public ObservableCollection<FolderMatch> Children { get; } = new();

    public ImageSource IconPreview
    {
        get => _iconPreview;
        set
        {
            if (!IsSelected || !ReferenceEquals(value, CheckedFolderIcon))
                _naturalIcon = value;
            var shown = IsSelected && CheckedFolderIcon != null ? CheckedFolderIcon : value;
            if (ReferenceEquals(_iconPreview, shown)) return;
            _iconPreview = shown;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconPreview)));
        }
    }

    public string Name => !string.IsNullOrEmpty(DisplayName)
        ? DisplayName
        : System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar));

    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>
    ///     いずれかのフォルダーの選択状態が変わったときに発生します。
    /// </summary>
    public static event Action SelectionChanged;

    /// <summary>
    ///     親や子の選択状態を変えずに、このフォルダーの選択状態だけを設定します。
    /// </summary>
    /// <param name="value">新しい選択状態。</param>
    public void SetSelected(bool value)
    {
        if (_isSelected == value) return;
        _isSelected = value;
        ApplyCheckedIcon();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        SelectionChanged?.Invoke();
    }

    /// <summary>
    ///     選択状態に応じて、チェック済みアイコンと本来のアイコンを切り替えます。
    /// </summary>
    private void ApplyCheckedIcon()
    {
        if (IsSelected && CheckedFolderIcon != null)
        {
            if (!ReferenceEquals(_iconPreview, CheckedFolderIcon))
                _naturalIcon = _iconPreview;
            _iconPreview = CheckedFolderIcon;
        }
        else
        {
            _iconPreview = _naturalIcon ?? _iconPreview;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconPreview)));
    }

    /// <summary>
    ///     このノードから親方向へたどり、ソリューション ファイルを持つ最初のノードを探します。
    /// </summary>
    /// <returns>ソリューションのルート ノード。見つからない場合は null。</returns>
    public FolderMatch FindSolutionRoot()
    {
        for (var node = this; node != null; node = node.Parent)
            if (!string.IsNullOrWhiteSpace(node.SolutionFile))
                return node;
        return null;
    }
}