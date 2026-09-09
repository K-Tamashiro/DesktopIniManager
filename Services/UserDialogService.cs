using System.Windows;

namespace DesktopIniManager.Services
{
    internal interface IUserDialogService
    {
        MessageBoxResult Show(string message, string title,
            MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None);
    }

    internal sealed class UserDialogService : IUserDialogService
    {
        private readonly Window owner;
        public UserDialogService(Window owner) { this.owner = owner; }
        public MessageBoxResult Show(string message, string title,
            MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
            => owner.IsVisible ? MessageBox.Show(owner, message, title, buttons, image)
                : MessageBox.Show(message, title, buttons, image);
    }
}
