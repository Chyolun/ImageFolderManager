using System.Windows;
using MahApps.Metro.Controls;

namespace ImageFolderManager.Views
{
    /// <summary>
    /// Dialog for renaming a folder.
    /// Replaces the legacy Microsoft.VisualBasic.Interaction.InputBox call
    /// in FolderOperationsViewModel.RenameFolderAsync so the UI stays
    /// consistent with the rest of the application's MahApps.Metro style.
    /// </summary>
    public partial class RenameFolderDialog : MetroWindow
    {
        /// <summary>Gets the new folder name entered by the user.</summary>
        public string NewName { get; private set; }

        /// <summary>
        /// Initialises the dialog and pre-fills both the read-only current-name
        /// display and the editable new-name field with <paramref name="currentName"/>.
        /// </summary>
        public RenameFolderDialog(string currentName)
        {
            InitializeComponent();

            CurrentNameText.Text = currentName;
            NewNameTextBox.Text  = currentName;

            // Place the cursor at the end and select all text so the user can
            // start typing immediately without having to clear the field first.
            NewNameTextBox.Loaded += (s, e) =>
            {
                NewNameTextBox.Focus();
                NewNameTextBox.SelectAll();
            };
        }

        private void Rename_Click(object sender, RoutedEventArgs e)
        {
            string name = NewNameTextBox.Text?.Trim();

            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show(
                    "Folder name cannot be empty.",
                    "Invalid Name",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            NewName = name;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
