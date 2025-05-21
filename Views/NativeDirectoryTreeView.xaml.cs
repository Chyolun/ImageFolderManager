using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ImageFolderManager.Controls;
using ImageFolderManager.Models;
using ImageFolderManager.Services;
using ImageFolderManager.ViewModels;
using System.Threading.Tasks;

namespace ImageFolderManager.Views
{
    /// <summary>
    /// Interaction logic for NativeDirectoryTreeView.xaml
    /// </summary>
    public partial class NativeDirectoryTreeView : UserControl
    {
        // Event to notify when a folder is selected
        public event Action<FolderInfo> FolderSelected;

        // The native directory tree control
        private NativeDirectoryTreeControl _nativeTreeControl;

        // Reference to the main view model
        private MainViewModel ViewModel
        {
            get
            {
                var vm = DataContext as MainViewModel;
                if (vm == null)
                {
                    // Try to get ViewModel from application level
                    if (Application.Current.MainWindow?.DataContext is MainViewModel mainVM)
                    {
                        return mainVM;
                    }

                    Debug.WriteLine("ERROR: NativeDirectoryTreeView's DataContext is not MainViewModel");
                }
                return vm;
            }
        }

        public NativeDirectoryTreeView()
        {
            InitializeComponent();

            // Add DataContext change handler to ensure MainViewModel is always accessible
            this.DataContextChanged += (s, e) => {
                if (e.NewValue is MainViewModel)
                {
                    Debug.WriteLine("NativeDirectoryTreeView received correct DataContext (MainViewModel)");

                    // Check if root directory has changed
                    if (PathService.DirectoryExists(AppSettings.Instance.DefaultRootDirectory) &&
                        _nativeTreeControl != null)
                    {
                        _nativeTreeControl.SetRootDirectory(AppSettings.Instance.DefaultRootDirectory);
                    }
                }
                else
                {
                    // If DataContext is not MainViewModel, try to get from MainWindow
                    if (Application.Current.MainWindow?.DataContext is MainViewModel)
                    {
                        Debug.WriteLine("Using MainWindow's DataContext as fallback");
                    }
                }
            };
        }

        private void WindowsFormsHost_Initialized(object sender, EventArgs e)
        {
            // Create and initialize the native tree control
            _nativeTreeControl = new NativeDirectoryTreeControl();

            // Set up basic event handlers
            _nativeTreeControl.DirectorySelected += NativeTreeControl_DirectorySelected;
            _nativeTreeControl.DirectoriesSelected += NativeTreeControl_DirectoriesSelected;

            // Set up context menu handlers
            SetupContextMenuHandlers();

            // Set up drag & drop handlers
            SetupDragDropHandlers();

            // Set up multi-selection support
            SetupMultiSelectionSupport();

            // Set the native control as the child of the WindowsFormsHost
            WindowsFormsHost.Child = _nativeTreeControl;

            // Initialize with default root directory
            LoadDefaultRootDirectory();

            // Update CanPaste state if ViewModel is available
            if (ViewModel != null)
            {
                _nativeTreeControl.SetCanPaste(ViewModel.HasClipboardContent());
            }
        }

        private void LoadDefaultRootDirectory()
        {
            try
            {
                if (!string.IsNullOrEmpty(AppSettings.Instance.DefaultRootDirectory))
                {
                    // Set the root directory from AppSettings
                    _nativeTreeControl.SetRootDirectory(AppSettings.Instance.DefaultRootDirectory);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading default root directory: {ex.Message}");
                MessageBox.Show($"Error loading default root directory: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Changes the root directory of the directory tree
        /// </summary>
        public void ChangeRootDirectory(string newRootDirectory)
        {
            try
            {
                // If null or empty, show all drives
                if (string.IsNullOrEmpty(newRootDirectory))
                {
                    _nativeTreeControl.SetRootDirectory(null);
                    return;
                }

                // Verify directory exists
                if (!PathService.DirectoryExists(newRootDirectory))
                {
                    MessageBox.Show("Cannot set root directory: Directory does not exist.",
                        "Invalid Directory", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Debug.WriteLine($"Changing root directory to: {newRootDirectory}");

                // Set the new root directory in the native control
                _nativeTreeControl.SetRootDirectory(newRootDirectory);

                // Select this path
                SelectPath(newRootDirectory);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error changing root directory: {ex.Message}");
                MessageBox.Show($"Error changing root directory: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Sets up the context menu handlers
        /// </summary>
        private void SetupContextMenuHandlers()
        {
            // Connect the Windows Forms control's context menu events to WPF handlers
            _nativeTreeControl.LoadImagesRequested += NativeTreeControl_LoadImagesRequested;
            _nativeTreeControl.NewFolderRequested += NativeTreeControl_NewFolderRequested;
            _nativeTreeControl.CutRequested += NativeTreeControl_CutRequested;
            _nativeTreeControl.CopyRequested += NativeTreeControl_CopyRequested;
            _nativeTreeControl.PasteRequested += NativeTreeControl_PasteRequested;
            _nativeTreeControl.ShowInExplorerRequested += NativeTreeControl_ShowInExplorerRequested;
            _nativeTreeControl.DeleteRequested += NativeTreeControl_DeleteRequested;
            _nativeTreeControl.RenameRequested += NativeTreeControl_RenameRequested;
        }

        /// <summary>
        /// Handles the LoadImagesRequested event from the native control
        /// </summary>
        private void NativeTreeControl_LoadImagesRequested(object sender, string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                    return;

                // Create a FolderInfo for the selected path
                var folderInfo = new FolderInfo(path);

                // Call the ViewModel to load images
                if (ViewModel != null)
                {
                    ViewModel.SetSelectedFolderAsync(folderInfo);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading images: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles the NewFolderRequested event from the native control
        /// </summary>
        private void NativeTreeControl_NewFolderRequested(object sender, string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                    return;

                // Create a FolderInfo for the selected path
                var folderInfo = new FolderInfo(path);

                // Call the ViewModel to create a new folder
                if (ViewModel != null)
                {
                    ViewModel.CreateNewFolder(folderInfo);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error creating new folder: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles the CutRequested event from the native control
        /// </summary>
        private void NativeTreeControl_CutRequested(object sender, string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                    return;

                // Create a FolderInfo for the selected path
                var folderInfo = new FolderInfo(path);

                // Call the ViewModel to cut the folder
                if (ViewModel != null)
                {
                    ViewModel.CutFolder(folderInfo);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error cutting folder: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles the CopyRequested event from the native control
        /// </summary>
        private void NativeTreeControl_CopyRequested(object sender, string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                    return;

                // Create a FolderInfo for the selected path
                var folderInfo = new FolderInfo(path);

                // Call the ViewModel to copy the folder
                if (ViewModel != null)
                {
                    ViewModel.CopyFolder(folderInfo);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error copying folder: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles the PasteRequested event from the native control
        /// </summary>
        private void NativeTreeControl_PasteRequested(object sender, string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                    return;

                // Create a FolderInfo for the selected path
                var folderInfo = new FolderInfo(path);

                // Call the ViewModel to paste to the folder
                if (ViewModel != null)
                {
                    ViewModel.PasteFolder(folderInfo);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error pasting to folder: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles the ShowInExplorerRequested event from the native control
        /// </summary>
        private void NativeTreeControl_ShowInExplorerRequested(object sender, string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                    return;

                // Create a FolderInfo for the selected path
                var folderInfo = new FolderInfo(path);

                // Call the ViewModel to show the folder in Explorer
                if (ViewModel != null)
                {
                    ViewModel.ShowInExplorer(folderInfo);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error showing folder in Explorer: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles the DeleteRequested event from the native control
        /// </summary>
        private void NativeTreeControl_DeleteRequested(object sender, string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                    return;

                // Create a FolderInfo for the selected path
                var folderInfo = new FolderInfo(path);

                // Call the ViewModel to delete the folder
                if (ViewModel != null)
                {
                    ViewModel.DeleteFolderCommand.ExecuteAsync(folderInfo);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error deleting folder: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles the RenameRequested event from the native control
        /// </summary>
        private void NativeTreeControl_RenameRequested(object sender, string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                    return;

                // Create a FolderInfo for the selected path
                var folderInfo = new FolderInfo(path);

                // Call the ViewModel to rename the folder
                if (ViewModel != null)
                {
                    ViewModel.RenameFolder(folderInfo);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error renaming folder: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates the can-paste state in the native control
        /// </summary>
        public void UpdateCanPaste(bool canPaste)
        {
            if (_nativeTreeControl != null)
            {
                // This should be exposed as a property in NativeDirectoryTreeControl
                _nativeTreeControl.SetCanPaste(canPaste);
            }
        }

        /// <summary>
        /// Sets up drag & drop handlers
        /// </summary>
        private void SetupDragDropHandlers()
        {
            // Connect to the FolderDropped event
            _nativeTreeControl.FolderDropped += NativeTreeControl_FolderDropped;
        }

        // Call SetupDragDropHandlers after initializing _nativeTreeControl
        // Add this line to WindowsFormsHost_Initialized:
        // SetupDragDropHandlers();

        /// <summary>
        /// Handles the FolderDropped event from the native control
        /// </summary>
        private async void NativeTreeControl_FolderDropped(object sender, Controls.FolderDropEventArgs e)
        {
            try
            {
                if (ViewModel == null)
                    return;

                // Create folder infos for the source folders
                var sourceFolders = new List<FolderInfo>();
                foreach (var sourcePath in e.SourcePaths)
                {
                    if (!string.IsNullOrEmpty(sourcePath) && Directory.Exists(sourcePath))
                    {
                        sourceFolders.Add(new FolderInfo(sourcePath));
                    }
                }

                if (sourceFolders.Count == 0)
                    return;

                // Create folder info for the target folder
                if (string.IsNullOrEmpty(e.TargetPath) || !Directory.Exists(e.TargetPath))
                    return;

                var targetFolder = new FolderInfo(e.TargetPath);

                // Perform the appropriate operation
                if (sourceFolders.Count == 1)
                {
                    // Single folder operation
                    if (e.IsCopy)
                    {
                        // Copy operation
                        ViewModel.CopyFolder(sourceFolders[0]);
                        ViewModel.PasteFolder(targetFolder);
                    }
                    else
                    {
                        // Move operation
                        ViewModel.MoveFolder(sourceFolders[0], targetFolder);
                    }
                }
                else
                {
                    // Multiple folders operation
                    if (e.IsCopy)
                    {
                        // Copy operation
                        ViewModel.CopyMultipleFolders(sourceFolders);
                        ViewModel.PasteFolder(targetFolder);
                    }
                    else
                    {
                        // Move operation
                        await ViewModel.MoveMultipleFolders(sourceFolders, targetFolder);
                    }
                }

                // Refresh the tree
                RefreshTree(e.TargetPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during drag & drop operation: {ex.Message}");
                MessageBox.Show($"Error during drag & drop operation: {ex.Message}",
                    "Drag & Drop Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Selects a path in the directory tree
        /// </summary>
        public void SelectPath(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                return;

            try
            {
                _nativeTreeControl.SelectPath(path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error selecting path: {ex.Message}");
            }
        }

        /// <summary>
        /// Refreshes the directory tree
        /// </summary>
        public void RefreshTree(string pathToSelect = null, bool preserveExpanded = true)
        {
            try
            {
                // Reload the tree
                _nativeTreeControl.ReloadTree();

                // If a path is specified, select it
                if (!string.IsNullOrEmpty(pathToSelect) && Directory.Exists(pathToSelect))
                {
                    SelectPath(pathToSelect);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error refreshing tree: {ex.Message}");
            }
        }

        /// <summary>
        /// Collapses a directory in the tree view - placeholder for compatibility
        /// </summary>
        public bool CollapseDirectory(string directoryPath)
        {
            // This is a placeholder since the native control doesn't support this directly
            // We could implement it by manipulating the TreeView nodes if needed
            Debug.WriteLine($"CollapseDirectory called with path: {directoryPath}");
            return false;
        }

        /// <summary>
        /// Updates the path mapping when a path is renamed - placeholder for compatibility
        /// </summary>
        public void UpdatePathMapping(string oldPath, string newPath)
        {
            // This is a placeholder since we don't need a path mapping for the native control
            // The native control will update automatically when it's refreshed
            Debug.WriteLine($"UpdatePathMapping called with oldPath: {oldPath}, newPath: {newPath}");
        }

        /// <summary>
        /// Handler for directory selection events
        /// </summary>
        private void NativeTreeControl_DirectorySelected(object sender, string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                    return;

                Debug.WriteLine($"Selected folder: {path}");

                // Create a FolderInfo for the selected path
                var folderInfo = new FolderInfo(path);

                // Notify listeners
                FolderSelected?.Invoke(folderInfo);

                // If ViewModel is available, use it to watch the folder
                if (ViewModel != null)
                {
                    ViewModel.WatchFolder(folderInfo);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in folder selection notification: {ex.Message}");
            }
        }


      
        /// <summary>
        /// Set up multi-selection support
        /// </summary>
        private void SetupMultiSelectionSupport()
        {
            // Set the native control to support multi-selection
            _nativeTreeControl.SetMultiSelect(true);

            // Handle multi-selection events
            _nativeTreeControl.DirectoriesSelected += NativeTreeControl_DirectoriesSelected;
        }

        // Call this method after initializing _nativeTreeControl
        // Add this line to WindowsFormsHost_Initialized:
        // SetupMultiSelectionSupport();

        /// <summary>
        /// Improved handler for multi-selection events
        /// </summary>
        private void NativeTreeControl_DirectoriesSelected(object sender, List<string> paths)
        {
            try
            {
                if (paths == null || paths.Count == 0 || ViewModel == null)
                    return;

                Debug.WriteLine($"Multiple folders selected: {paths.Count}");

                // Create folder infos for the selected paths
                var selectedFolders = new List<FolderInfo>();
                foreach (var path in paths)
                {
                    if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                    {
                        var folderInfo = new FolderInfo(path);
                        selectedFolders.Add(folderInfo);

                        // Watch the folder
                        ViewModel.WatchFolder(folderInfo);
                    }
                }

                if (selectedFolders.Count > 0)
                {
                    // Update the primary selected folder in ViewModel
                    // For multi-selection, use the first folder as the "main" selected folder
                    ViewModel.SetSelectedFolderWithoutLoading(selectedFolders[0]);

                    // If needed, you can add additional multi-selection handling here
                    // For example, enable batch operations in the UI when multiple folders are selected
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error handling multi-selection: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets improved selected folder infos that uses multi-selection when available
        /// </summary>
        public List<FolderInfo> GetSelectedFolderInfos()
        {
            // First try to get multi-selected folders from the native control
            var selectedFolders = _nativeTreeControl?.GetSelectedFolderInfos();
            if (selectedFolders != null && selectedFolders.Count > 0)
            {
                return selectedFolders;
            }

            // Fallback to the single selected folder
            var selectedFolder = ViewModel?.SelectedFolder;
            if (selectedFolder != null)
            {
                return new List<FolderInfo> { selectedFolder };
            }

            return new List<FolderInfo>();
        }

        /// <summary>
        /// Add methods to support batch operations with multiple selected folders
        /// </summary>
        public async Task BatchUpdateTagsAsync()
        {
            try
            {
                var selectedFolders = GetSelectedFolderInfos();
                if (selectedFolders.Count <= 1 || ViewModel == null)
                    return;

                await ViewModel.BatchUpdateTags(selectedFolders);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error performing batch tag update: {ex.Message}");
                MessageBox.Show($"Error updating tags: {ex.Message}",
                    "Batch Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Cut multiple selected folders
        /// </summary>
        public void CutSelectedFolders()
        {
            try
            {
                var selectedFolders = GetSelectedFolderInfos();
                if (selectedFolders.Count == 0 || ViewModel == null)
                    return;

                if (selectedFolders.Count == 1)
                {
                    ViewModel.CutFolder(selectedFolders[0]);
                }
                else
                {
                    ViewModel.CutMultipleFolders(selectedFolders);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error cutting folders: {ex.Message}");
                MessageBox.Show($"Error cutting folders: {ex.Message}",
                    "Cut Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Copy multiple selected folders
        /// </summary>
        public void CopySelectedFolders()
        {
            try
            {
                var selectedFolders = GetSelectedFolderInfos();
                if (selectedFolders.Count == 0 || ViewModel == null)
                    return;

                if (selectedFolders.Count == 1)
                {
                    ViewModel.CopyFolder(selectedFolders[0]);
                }
                else
                {
                    ViewModel.CopyMultipleFolders(selectedFolders);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error copying folders: {ex.Message}");
                MessageBox.Show($"Error copying folders: {ex.Message}",
                    "Copy Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Delete multiple selected folders
        /// </summary>
        public async Task DeleteSelectedFoldersAsync()
        {
            try
            {
                var selectedFolders = GetSelectedFolderInfos();
                if (selectedFolders.Count == 0 || ViewModel == null)
                    return;

                if (selectedFolders.Count == 1)
                {
                    await ViewModel.DeleteFolderCommand.ExecuteAsync(selectedFolders[0]);
                }
                else
                {
                    await ViewModel.DeleteMultipleFolders(selectedFolders);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error deleting folders: {ex.Message}");
                MessageBox.Show($"Error deleting folders: {ex.Message}",
                    "Delete Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}