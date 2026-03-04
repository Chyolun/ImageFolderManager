using System.IO;
using System.Windows;

namespace ImageFolderManager.Views
{
    /// <summary>
    /// User's choice when a copy-paste conflict is detected.
    /// </summary>
    public enum ConflictResolution
    {
        Skip,
        Overwrite,
        Rename,
        CancelAll
    }

    /// <summary>
    /// Dialog shown when the destination already contains a folder with the same name.
    /// Supports single-conflict and batch-conflict (Apply to all) modes.
    /// </summary>
    public partial class CopyConflictDialog : MahApps.Metro.Controls.MetroWindow
    {
        // ── Outputs ───────────────────────────────────────────────────────

        /// <summary>What the user chose to do.</summary>
        public ConflictResolution Resolution { get; private set; } = ConflictResolution.CancelAll;

        /// <summary>
        /// The new name entered by the user when Resolution == Rename.
        /// This is just the folder name (no path).
        /// </summary>
        public string NewFolderName { get; private set; }

        /// <summary>
        /// True when the "Apply to all" checkbox was checked.
        /// Only relevant for Skip and Overwrite resolutions.
        /// </summary>
        public bool ApplyToAll => ApplyToAllCheck.IsChecked == true;

        // ── State ─────────────────────────────────────────────────────────

        private readonly string _destinationParent;
        private bool _inRenameMode;

        // ── Constructor ───────────────────────────────────────────────────

        /// <param name="folderName">Name of the conflicting folder.</param>
        /// <param name="destinationParent">Parent directory where the conflict exists.</param>
        /// <param name="isBatch">Whether there are multiple conflicts (shows Apply-to-all).</param>
        public CopyConflictDialog(string folderName, string destinationParent, bool isBatch = false)
        {
            InitializeComponent();

            _destinationParent = destinationParent;

            MessageText.Text =
                $"A folder named \"{folderName}\" already exists in the destination.";

            DestPathText.Text = $"Destination: {destinationParent}";

            // Pre-fill the rename box with an auto-generated unique name
            NewNameTextBox.Text = GenerateUniqueName(folderName, destinationParent);

            if (isBatch)
                ApplyToAllCheck.Visibility = Visibility.Visible;
        }

        // ── Button handlers ───────────────────────────────────────────────

        private void Skip_Click(object sender, RoutedEventArgs e)
        {
            Resolution = ConflictResolution.Skip;
            DialogResult = true;
        }

        private void Overwrite_Click(object sender, RoutedEventArgs e)
        {
            Resolution = ConflictResolution.Overwrite;
            DialogResult = true;
        }

        private void Rename_Click(object sender, RoutedEventArgs e)
        {
            if (!_inRenameMode)
            {
                // First click: expand the rename input panel
                _inRenameMode = true;
                RenamePanel.Visibility = Visibility.Visible;
                RenameButton.Content = "Confirm Rename";
                Height = 370;
                NewNameTextBox.Focus();
                NewNameTextBox.SelectAll();
                return;
            }

            // Second click: confirm
            string newName = NewNameTextBox.Text?.Trim();
            if (!ValidateName(newName)) return;

            NewFolderName = newName;
            Resolution = ConflictResolution.Rename;
            DialogResult = true;
        }

        private void CancelAll_Click(object sender, RoutedEventArgs e)
        {
            Resolution = ConflictResolution.CancelAll;
            DialogResult = false;
        }

        // ── Name validation ───────────────────────────────────────────────

        private void NewNameTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (!_inRenameMode) return;
            ValidateName(NewNameTextBox.Text?.Trim());
        }

        /// <returns>True when name is acceptable.</returns>
        private bool ValidateName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                ShowValidationError("Name cannot be empty.");
                RenameButton.IsEnabled = false;
                return false;
            }

            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                ShowValidationError("Name contains invalid characters.");
                RenameButton.IsEnabled = false;
                return false;
            }

            string fullPath = Path.Combine(_destinationParent, name);
            if (Directory.Exists(fullPath))
            {
                ShowValidationError($"\"{name}\" already exists at the destination.");
                RenameButton.IsEnabled = false;
                return false;
            }

            HideValidationError();
            RenameButton.IsEnabled = true;
            return true;
        }

        private void ShowValidationError(string message)
        {
            NameValidationText.Text = message;
            NameValidationText.Visibility = Visibility.Visible;
        }

        private void HideValidationError()
        {
            NameValidationText.Visibility = Visibility.Collapsed;
        }

        // ── Helpers ───────────────────────────────────────────────────────

        /// <summary>Generates "FolderName (1)", "FolderName (2)", … until unused.</summary>
        private static string GenerateUniqueName(string baseName, string parentDir)
        {
            int idx = 1;
            string candidate = $"{baseName} ({idx})";
            while (Directory.Exists(Path.Combine(parentDir, candidate)))
                candidate = $"{baseName} ({++idx})";
            return candidate;
        }
    }
}
