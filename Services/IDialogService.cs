using System.Windows;

namespace ImageFolderManager.Services
{
    /// <summary>
    /// Abstraction for user dialogs used by ViewModels/Services.
    /// </summary>
    public interface IDialogService
    {
        MessageBoxResult Show(
            string message,
            string title,
            MessageBoxButton buttons = MessageBoxButton.OK,
            MessageBoxImage icon = MessageBoxImage.None);
    }
}
