using System.Windows;

namespace ImageFolderManager.Views
{
    /// <summary>
    /// Resolution chosen by the user when an import destination folder already exists.
    /// </summary>
    public enum ImportFolderMergeResolution
    {
        Merge,
        Skip,
        CancelAll
    }

    /// <summary>
    /// Dialog shown during Import Folder when the destination already contains
    /// a folder with the same name. The user can choose to merge the two folders,
    /// skip this particular source folder, or cancel the entire import.
    /// </summary>
    public partial class ImportMergeFolderDialog : MahApps.Metro.Controls.MetroWindow
    {
        // ── Output ────────────────────────────────────────────────────────

        /// <summary>The action chosen by the user.</summary>
        public ImportFolderMergeResolution Resolution { get; private set; } =
            ImportFolderMergeResolution.CancelAll;

        // ── Constructor ───────────────────────────────────────────────────

        /// <param name="folderName">Name of the conflicting folder.</param>
        /// <param name="destinationPath">Full path of the existing destination folder.</param>
        public ImportMergeFolderDialog(string folderName, string destinationPath)
        {
            InitializeComponent();

            MessageText.Text =
                $"A folder named \"{folderName}\" already exists at the destination. " +
                $"Would you like to merge its contents with the source folder?";

            DestPathText.Text = $"Destination: {destinationPath}";
        }

        // ── Button handlers ───────────────────────────────────────────────

        private void Merge_Click(object sender, RoutedEventArgs e)
        {
            Resolution = ImportFolderMergeResolution.Merge;
            DialogResult = true;
        }

        private void Skip_Click(object sender, RoutedEventArgs e)
        {
            Resolution = ImportFolderMergeResolution.Skip;
            DialogResult = true;
        }

        private void CancelAll_Click(object sender, RoutedEventArgs e)
        {
            Resolution = ImportFolderMergeResolution.CancelAll;
            DialogResult = false;
        }
    }
}
