using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ImageFolderManager.Models;
using ImageFolderManager.Services;
using Path = System.IO.Path;

namespace ImageFolderManager.Controls
{
    public partial class ShellTreeView
    {
        #region Context Menu Action Handlers

        /// <summary>
        /// Checks if there are any selected items in the tree view
        /// </summary>
        /// <returns>True if there are selected items</returns>
        public bool HasSelectedItems()
        {
            return _selectedItems != null && _selectedItems.Count > 0;
        }

        private void NewFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Debug.WriteLine("NewFolder_Click handler called");

                var treeViewItem = GetSelectedTreeViewItem();
                if (treeViewItem == null)
                {
                    Debug.WriteLine("No TreeViewItem selected");
                    return;
                }

                var folderNode = treeViewItem.Tag as FolderNode;
                if (folderNode == null)
                {
                    Debug.WriteLine("Selected item has no FolderNode");
                    return;
                }

                string path = folderNode.FullPath;
                if (!PathService.DirectoryExists(path))
                {
                    Debug.WriteLine($"Invalid path: {path}");
                    return;
                }
                //string parentPath = Path.GetDirectoryName(path);
                // Create FolderInfo and call ViewModel
                var folderInfo = new FolderInfo(path);

                if (ViewModel != null)
                {
                    _ = ViewModel.CreateNewFolder(folderInfo);

                }
                else
                {
                    Debug.WriteLine("ViewModel is null");
                    MessageBox.Show("Could not create folder: ViewModel is not available.",
                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }


            }
            catch (Exception ex)
            {
                HandleException("Error creating new folder", ex);
            }
        }

        private void NewSiblingFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Debug.WriteLine("NewSiblingFolder_Click handler called");

                var treeViewItem = GetSelectedTreeViewItem();
                if (treeViewItem == null)
                {
                    Debug.WriteLine("No TreeViewItem selected");
                    return;
                }

                var folderNode = treeViewItem.Tag as FolderNode;
                if (folderNode == null)
                {
                    Debug.WriteLine("Selected item has no FolderNode");
                    return;
                }

                string currentPath = folderNode.FullPath;
                if (!PathService.DirectoryExists(currentPath))
                {
                    Debug.WriteLine($"Invalid path: {currentPath}");
                    return;
                }

                // Cannot create sibling of root
                if (!string.IsNullOrEmpty(_rootDirectory) &&
                    PathService.PathsEqual(currentPath, _rootDirectory))
                {
                    MessageBox.Show("Cannot create a sibling folder for the root directory.",
                        "Operation Not Allowed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string parentPath = Path.GetDirectoryName(currentPath);
                if (string.IsNullOrEmpty(parentPath) || !PathService.DirectoryExists(parentPath))
                {
                    Debug.WriteLine($"Cannot resolve parent path for: {currentPath}");
                    return;
                }

                // Reuse existing CreateNewFolder logic with the parent as the target
                var parentFolderInfo = new FolderInfo(parentPath);

                if (ViewModel != null)
                {
                    _ = ViewModel.CreateNewFolder(parentFolderInfo);
                }
                else
                {
                    Debug.WriteLine("ViewModel is null");
                    MessageBox.Show("Could not create folder: ViewModel is not available.",
                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                HandleException("Error creating sibling folder", ex);
            }
        }

        private void BatchTags_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Debug.WriteLine("BatchTags_Click handler called");

                var selectedFolders = GetSelectedFolderInfos();
                if (selectedFolders.Count <= 1) return;

                if (ViewModel != null)
                {
                    Debug.WriteLine($"Calling ViewModel.BatchUpdateTags for {selectedFolders.Count} folders");
                    _ = ViewModel.BatchUpdateTags(selectedFolders);
                }
                else
                {
                    Debug.WriteLine("ViewModel is null");
                    MessageBox.Show("Could not perform batch tag operation: ViewModel is not available.",
                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                HandleException("Error performing batch tag operation", ex);
            }
        }

        public void MultiFolderCut_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selectedFolders = GetSelectedFolderInfos();
                if (selectedFolders.Count == 0) return;

                if (ViewModel != null)
                {
                    ViewModel.CutFolders(selectedFolders);
                }
                else
                {
                    MessageBox.Show("Could not cut folders: ViewModel is not available.",
                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                HandleException("Error cutting folders", ex);
            }
        }

        public void MultiFolderCopy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selectedFolders = GetSelectedFolderInfos();
                if (selectedFolders.Count == 0) return;

                if (ViewModel != null)
                {
                    ViewModel.CopyFolders(selectedFolders);
                }
                else
                {
                    MessageBox.Show("Could not copy folders: ViewModel is not available.",
                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                HandleException("Error copying folders", ex);
            }
        }

        public void Paste_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Debug.WriteLine("Paste_Click handler called");

                var treeViewItem = GetSelectedTreeViewItem();
                if (treeViewItem == null) return;

                var folderNode = treeViewItem.Tag as FolderNode;
                if (folderNode == null) return;

                string path = folderNode.FullPath;
                if (!PathService.DirectoryExists(path)) return;

                // Store expanded state
                var expandedItems = new HashSet<string>();
                foreach (var item in FindVisualChildren<TreeViewItem>(ShellTreeViewControl))
                {
                    if (item.IsExpanded && item.Tag is FolderNode so)
                    {
                        string expandedPath = so.FullPath;
                        if (!string.IsNullOrEmpty(expandedPath))
                        {
                            expandedItems.Add(expandedPath);
                        }
                    }
                }

                // Create target folder FolderInfo
                var folderInfo = new FolderInfo(path);

                if (ViewModel != null)
                {
                    Debug.WriteLine($"Calling ViewModel.PasteFolder for {path}");

                    if (ViewModel.HasClipboardContent())
                    {
                        _ = ExecutePasteAsync(folderInfo);


                    }
                    else
                    {
                        Debug.WriteLine("No clipboard content available");
                        MessageBox.Show("No folder is currently in clipboard. Please copy or cut a folder first.",
                            "Paste Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                else
                {
                    Debug.WriteLine("ViewModel is null");
                    MessageBox.Show("Could not paste folder: ViewModel is not available.",
                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                HandleException("Error pasting folder", ex);
            }
        }

        private void Rename_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Debug.WriteLine("Rename_Click handler called");

                var treeViewItem = GetSelectedTreeViewItem();
                if (treeViewItem == null) return;

                var folderNode = treeViewItem.Tag as FolderNode;
                if (folderNode == null) return;

                string path = folderNode.FullPath;
                if (!PathService.DirectoryExists(path)) return;

                // Don't allow renaming root directory
                if (!string.IsNullOrEmpty(_rootDirectory) &&
                    PathService.PathsEqual(path, _rootDirectory))
                {
                    Debug.WriteLine("Cannot rename root directory");
                    MessageBox.Show("Cannot rename the root directory.",
                        "Rename Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Save old path and tree view item
                string oldPath = path;
                var oldItem = treeViewItem;
                bool wasExpanded = oldItem.IsExpanded;
                var parentItem = FindParentTreeViewItem(oldItem);

                // Create FolderInfo and call ViewModel
                var folderInfo = new FolderInfo(path);

                if (ViewModel != null)
                {
                    Debug.WriteLine($"Calling ViewModel.RenameFolder for {path}");

                    // Execute rename operation through ViewModel
                    _ = ViewModel.RenameFolder(folderInfo);
                }
                else
                {
                    Debug.WriteLine("ViewModel is null");
                    MessageBox.Show("Could not rename folder: ViewModel is not available.",
                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                HandleException("Error renaming folder", ex);
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Debug.WriteLine("Delete_Click handler called");

                var treeViewItem = GetSelectedTreeViewItem();
                if (treeViewItem == null) return;

                var folderNode = treeViewItem.Tag as FolderNode;
                if (folderNode == null) return;

                string path = folderNode.FullPath;
                if (!PathService.DirectoryExists(path)) return;

                if (!string.IsNullOrEmpty(_rootDirectory) &&
                    PathService.PathsEqual(path, _rootDirectory))
                {
                    Debug.WriteLine("Cannot delete root directory");
                    MessageBox.Show("Cannot delete the root directory.",
                        "Delete Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string parentPath = Path.GetDirectoryName(path);
                var folderInfo = new FolderInfo(path);

                if (ViewModel != null)
                {
                    // Execute delete command through ViewModel
                    _ = ViewModel.DeleteFolders(new[] { folderInfo });
                }
                else
                {
                    Debug.WriteLine("ViewModel is null");
                    MessageBox.Show("Could not delete folder: ViewModel is not available.",
                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                HandleException("Error deleting folder", ex);
            }
        }

        /// <summary>
        /// Handles the "Delete" context menu item click for multiple folders
        /// </summary>
        public void MultiFolderDelete_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selectedFolders = GetSelectedFolderInfos();
                if (selectedFolders.Count == 0) return;

                if (ViewModel != null)
                {
                    // Execute delete operation through ViewModel
                    _ = ViewModel.DeleteFolders(selectedFolders);

                    // Clear selection and refresh tree
                    ClearSelectedItems();
                }

                else
                {
                    MessageBox.Show("Could not delete folders: ViewModel is not available.",
                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                HandleException("Error deleting folders", ex);
            }
        }

        /// <summary>
        /// Handles the "Show in Explorer" context menu item click
        /// </summary>
        private void ShowInExplorer_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Debug.WriteLine("ShowInExplorer_Click handler called");

                var treeViewItem = GetSelectedTreeViewItem();
                if (treeViewItem == null) return;

                var folderNode = treeViewItem.Tag as FolderNode;
                if (folderNode == null) return;

                string path = folderNode.FullPath;
                if (!PathService.DirectoryExists(path)) return;

                // Create FolderInfo and call ViewModel
                var folderInfo = new FolderInfo(path);

                if (ViewModel != null)
                {
                    Debug.WriteLine($"Calling ViewModel.ShowInExplorer for {path}");
                    ViewModel.ShowInExplorer(folderInfo);
                }
                else
                {
                    Debug.WriteLine("ViewModel is null, using direct Process.Start instead");
                    // Fallback if ViewModel is unavailable
                    try
                    {
                        System.Diagnostics.Process.Start("explorer.exe", path);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error opening explorer: {ex.Message}");
                        MessageBox.Show($"Error opening folder in Explorer: {ex.Message}",
                            "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                HandleException("Error showing folder in Explorer", ex);
            }
        }

        #endregion

        private async Task ExecutePasteAsync(FolderInfo targetFolder)
        {
            try
            {
                await ViewModel.PasteFolders(targetFolder);
            }
            catch (Exception ex)
            {
                HandleException("Paste failed", ex);
            }
        }
    }
}
