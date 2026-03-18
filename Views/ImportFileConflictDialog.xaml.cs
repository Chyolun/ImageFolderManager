using System.Windows;

namespace ImageFolderManager.Views
{
    /// <summary>
    /// Resolution chosen by the user when a file conflict is encountered during a
    /// folder-merge import operation.
    /// </summary>
    public enum ImportFileConflictResolution
    {
        Overwrite,
        Skip,
        CancelImport
    }

    /// <summary>
    /// Dialog shown when a file in the source folder already exists at the merge
    /// destination. The user chooses Overwrite or Skip, and may opt to apply the
    /// same choice to all subsequent file conflicts in the current folder.
    /// </summary>
    public partial class ImportFileConflictDialog : MahApps.Metro.Controls.MetroWindow
    {
        // ── Outputs ───────────────────────────────────────────────────────

        /// <summary>The action chosen by the user.</summary>
        public ImportFileConflictResolution Resolution { get; private set; } =
            ImportFileConflictResolution.CancelImport;

        /// <summary>
        /// True when the user checked "Apply to all remaining file conflicts in this folder".
        /// Only meaningful for Overwrite and Skip resolutions.
        /// </summary>
        public bool ApplyToAll => ApplyToAllCheck.IsChecked == true;

        // ── Constructor ───────────────────────────────────────────────────

        /// <param name="fileName">Name of the conflicting file.</param>
        /// <param name="destinationFolder">Folder in which the conflict exists.</param>
        public ImportFileConflictDialog(string fileName, string destinationFolder)
        {
            InitializeComponent();

            MessageText.Text =
                $"A file named \"{fileName}\" already exists in the destination folder. " +
                $"What would you like to do?";

            DestPathText.Text = $"Destination: {destinationFolder}";
        }

        // ── Button handlers ───────────────────────────────────────────────

        private void Overwrite_Click(object sender, RoutedEventArgs e)
        {
            Resolution = ImportFileConflictResolution.Overwrite;
            DialogResult = true;
        }

        private void Skip_Click(object sender, RoutedEventArgs e)
        {
            Resolution = ImportFileConflictResolution.Skip;
            DialogResult = true;
        }

        private void CancelImport_Click(object sender, RoutedEventArgs e)
        {
            Resolution = ImportFileConflictResolution.CancelImport;
            DialogResult = false;
        }
    }
}
