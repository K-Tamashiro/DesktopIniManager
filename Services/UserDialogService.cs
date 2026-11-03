using System.Windows;

namespace DesktopIniManager.Services;

internal interface IUserDialogService
{
    MessageBoxResult Show(string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None);
}

internal sealed class UserDialogService : IUserDialogService
{
    private readonly Window _owner;

    public UserDialogService(Window owner)
    {
        _owner = owner;
    }

    public MessageBoxResult Show(string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
    {
        return _owner.IsVisible
            ? MessageBox.Show(_owner, message, title, buttons, image)
            : MessageBox.Show(message, title, buttons, image);
    }
}