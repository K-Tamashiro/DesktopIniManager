using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows;
namespace DesktopIniManager.Models
{
    internal sealed class FolderMatch : INotifyPropertyChanged
    {
        private bool _isSelected;
        private bool _isExpanded;
        private bool _isCurrent;
        public bool IsCurrent { get => _isCurrent; set { if (_isCurrent == value) return; _isCurrent = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent))); } }
        private ImageSource _iconPreview;
        private bool _isHidden;
        private bool _isFilterHidden;
        private string _reason;
        public FolderMatch Parent { get; set; }
        public bool IsSelected { get => _isSelected; set { SetSelected(value, false, false); } }

        public void SetSelectedFromUi(bool value)
        {
            SetSelected(value, true, value);
        }

        public void SetSelected(bool value, bool propagateDown, bool propagateUp)
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
            if (propagateDown)
            {
                foreach (FolderMatch child in Children)
                    child.SetSelected(value, true, false);
            }
            if (propagateUp && value && Parent != null)
                Parent.SetSelected(true, false, true);
        }
        public bool IsExpanded { get => _isExpanded; set { if (_isExpanded == value) return; _isExpanded = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded))); } }
        public string Path { get; set; }
        public string Reason { get => _reason; set { if (_reason == value) return; _reason = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Reason))); } }
        public string DisplayName { get; set; }
        public bool IsActionable { get; set; } = true;
        public bool IsHidden { get => _isHidden; set { if (_isHidden == value) return; _isHidden = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHidden))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ItemVisibility))); } }
        public bool IsFilterHidden { get => _isFilterHidden; set { if (_isFilterHidden == value) return; _isFilterHidden = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFilterHidden))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ItemVisibility))); } }
        public Visibility ItemVisibility => IsHidden || IsFilterHidden ? Visibility.Collapsed : Visibility.Visible;
        public ObservableCollection<FolderMatch> Children { get; } = new ObservableCollection<FolderMatch>();
        public ImageSource IconPreview { get => _iconPreview; set { if (ReferenceEquals(_iconPreview, value)) return; _iconPreview = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconPreview))); } }
        public string Name => !string.IsNullOrEmpty(DisplayName) ? DisplayName : System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        public event PropertyChangedEventHandler PropertyChanged;
    }
}
