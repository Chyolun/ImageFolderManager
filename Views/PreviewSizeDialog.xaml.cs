using System.Windows;
using ImageFolderManager.Services;
using MahApps.Metro.Controls;

namespace ImageFolderManager.Views
{
    public partial class PreviewSizeDialog : MetroWindow
    {
        public int SelectedWidth { get; private set; }
        public int SelectedHeight { get; private set; }
        public bool DialogResu { get; private set; } = false;

        public PreviewSizeDialog()
        {
            InitializeComponent();

            // Load current settings
            WidthUpDown.Value = AppSettings.Instance.PreviewWidth;
            HeightUpDown.Value = AppSettings.Instance.PreviewHeight;
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            SelectedWidth = (int)WidthUpDown.Value;
            SelectedHeight = (int)HeightUpDown.Value;
            DialogResu = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResu = false;
            Close();
        }
    }
}