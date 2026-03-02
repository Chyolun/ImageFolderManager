using System.Collections.Generic;
using System.Windows;
using MahApps.Metro.Controls;

namespace ImageFolderManager.Views
{
    /// <summary>
    /// Confirmation dialog for folder deletion.
    /// Supports both single-folder and multi-folder scenarios and replaces
    /// the raw MessageBox.Show(YesNo) calls in FolderOperationsViewModel
    /// so the UI stays consistent with the MahApps.Metro style.
    /// </summary>
    public partial class DeleteConfirmDialog : MetroWindow
    {
        // ── Factories ────────────────────────────────────────────────────

        /// <summary>
        /// Creates a confirmation dialog for deleting a single folder.
        /// </summary>
        /// <param name="folderPath">Full path of the folder to delete.</param>
        public static DeleteConfirmDialog ForSingle(string folderPath)
        {
            var dlg = new DeleteConfirmDialog();
            dlg.MessageText.Text = "Are you sure you want to delete this folder?";
            dlg.ShowDetail(new[] { folderPath });
            return dlg;
        }

        /// <summary>
        /// Creates a confirmation dialog for deleting multiple folders.
        /// </summary>
        /// <param name="folderPaths">Full paths of every folder to delete.</param>
        public static DeleteConfirmDialog ForMultiple(IEnumerable<string> folderPaths)
        {
            var paths = new List<string>(folderPaths);
            var dlg = new DeleteConfirmDialog();
            dlg.MessageText.Text = $"Are you sure you want to delete {paths.Count} folders?";
            dlg.ShowDetail(paths);
            return dlg;
        }

        // ── Constructor ──────────────────────────────────────────────────

        private DeleteConfirmDialog()
        {
            InitializeComponent();
        }

        // ── Helpers ──────────────────────────────────────────────────────

        /// <summary>
        /// Populates the scrollable path list and makes the detail border visible.
        /// </summary>
        private void ShowDetail(IEnumerable<string> paths)
        {
            FolderList.ItemsSource  = paths;
            DetailBorder.Visibility = Visibility.Visible;
        }

        // ── Event handlers ───────────────────────────────────────────────

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
