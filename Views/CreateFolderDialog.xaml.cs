using System.Windows;
using MahApps.Metro.Controls;

namespace ImageFolderManager.Views
{
    /// <summary>
    /// Dialog for creating a new folder inside a given parent directory.
    /// Replaces the legacy Microsoft.VisualBasic.Interaction.InputBox call
    /// in FolderOperationsViewModel.CreateNewFolderAsync.
    /// </summary>
    public partial class CreateFolderDialog : MetroWindow
    {
        /// <summary>Gets the folder name entered by the user.</summary>
        public string FolderName { get; private set; }

        /// <summary>
        /// Initialises the dialog, displaying <paramref name="parentPath"/> as
        /// context and pre-filling the name field with <paramref name="defaultName"/>.
        /// </summary>
        public CreateFolderDialog(string parentPath, string defaultName = "New Folder")
        {
            InitializeComponent();

            ParentPathText.Text   = parentPath;
            FolderNameTextBox.Text = defaultName;

            // Select all so the user can start typing the real name immediately.
            FolderNameTextBox.Loaded += (s, e) =>
            {
                FolderNameTextBox.Focus();
                FolderNameTextBox.SelectAll();
            };
        }

        private void Create_Click(object sender, RoutedEventArgs e)
        {
            string name = FolderNameTextBox.Text?.Trim();

            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show(
                    "Folder name cannot be empty.",
                    "Invalid Name",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            FolderName = name;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
