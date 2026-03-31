using System.Windows;

namespace ImageFolderManager.Services
{
    /// <summary>
    /// Default dialog service implementation backed by WPF MessageBox.
    /// </summary>
    public class WpfDialogService : IDialogService
    {
        public MessageBoxResult Show(
            string message,
            string title,
            MessageBoxButton buttons = MessageBoxButton.OK,
            MessageBoxImage icon = MessageBoxImage.None)
        {
            if (Application.Current?.Dispatcher == null)
            {
                return MessageBox.Show(message, title, buttons, icon);
            }

            if (Application.Current.Dispatcher.CheckAccess())
            {
                return ShowCore(message, title, buttons, icon);
            }

            return Application.Current.Dispatcher.Invoke(
                () => ShowCore(message, title, buttons, icon));
        }

        private static MessageBoxResult ShowCore(
            string message,
            string title,
            MessageBoxButton buttons,
            MessageBoxImage icon)
        {
            var owner = Application.Current?.MainWindow;
            return owner != null
                ? MessageBox.Show(owner, message, title, buttons, icon)
                : MessageBox.Show(message, title, buttons, icon);
        }
    }
}
