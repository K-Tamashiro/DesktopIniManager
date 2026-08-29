using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows.Media;
namespace DesktopIniManager.Models
{
    internal sealed class FolderMatch : INotifyPropertyChanged
    {
        private bool _isSelected = true;
        private bool _isExpanded = true;
        private ImageSource _iconPreview;
        public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); } }
        public bool IsExpanded { get => _isExpanded; set { if (_isExpanded == value) return; _isExpanded = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded))); } }
        public string Path { get; set; }
        public string Reason { get; set; }
        public string DisplayName { get; set; }
        public bool IsActionable { get; set; } = true;
        public ObservableCollection<FolderMatch> Children { get; } = new ObservableCollection<FolderMatch>();
        public ImageSource IconPreview { get => _iconPreview; set { if (ReferenceEquals(_iconPreview, value)) return; _iconPreview = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconPreview))); } }
        public string Name => !string.IsNullOrEmpty(DisplayName) ? DisplayName : System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        public event PropertyChangedEventHandler PropertyChanged;
    }
}
