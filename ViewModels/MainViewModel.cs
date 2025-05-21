using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using ImageFolderManager.Models;
using ImageFolderManager.Services;
using Microsoft.Win32;
using CommunityToolkit.Mvvm.ComponentModel;
using System.IO;
using System.Windows;
using MessageBox = System.Windows.MessageBox;
using Microsoft.VisualBasic.FileIO;
using MahApps.Metro.Controls.Dialogs;
using Application = System.Windows.Application;
using System.Threading;
using System.Windows.Media;
using Microsoft.WindowsAPICodePack.Shell;


namespace ImageFolderManager.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        public ObservableCollection<FolderInfo> RootFolders { get; set; } = new();
        public ObservableCollection<ImageInfo> Images { get; set; } = new();
        public string DisplayTagLine => string.Join(" ", FolderTags.Select(tag => $"#{tag}"));

        public List<FolderInfo> _allLoadedFolders = new();
        private FolderInfo _selectedFolder;
        private bool _isSavingTags = false;
        private readonly FolderManagementService _folderManager;
        private bool _isLoadingImages = false;
        private CancellationTokenSource _imageLoadingCts;
        private Stack<FolderMoveOperation> _undoStack = new Stack<FolderMoveOperation>();

        public FolderInfo SelectedFolder
        {
            get => _selectedFolder;
            set
            {
                if (_selectedFolder != value)
                {
                    _selectedFolder = value;
                    OnPropertyChanged();

                }
            }
        }
       

        private bool _isSearching;
        public bool IsSearching
        {
            get => _isSearching;
            set
            {
                _isSearching = value;
                OnPropertyChanged();
            }
        }

        private string _statusMessage = "Ready";
        public string StatusMessage
        {
            get => _statusMessage;
            set
            {
                if (_statusMessage != value)
                {
                    _statusMessage = value;
                    OnPropertyChanged();
                }
            }
        }

        private SolidColorBrush _selectedFolderHighlight = new SolidColorBrush(Color.FromArgb(50, 0, 120, 215));
        public ObservableCollection<string> FolderTags { get; set; } = new();
        private FolderInfo _clipboardFolder = null;
        private bool _isCutOperation = false;
        private List<FolderInfo> _clipboardFolders = new List<FolderInfo>();
        private bool _isMultipleSelection = false;
        public FolderInfo ClipboardFolder => _clipboardFolder;
        public bool IsCutOperation => _isCutOperation;
        public ObservableCollection<StarModel> Stars { get; set; }
        public ICommand SaveTagsCommand { get; }
        public ICommand SetRootDirectoryCommand { get; }
        public IAsyncRelayCommand SearchCommand { get; }
        public IRelayCommand<FolderInfo> ShowInExplorerCommand { get; }
        public IAsyncRelayCommand<FolderInfo> DeleteFolderCommand { get; }
        public ICommand SetRatingCommand { get; }
        public ICommand EditTagsCommand { get; }
        public ICommand UndoFolderMovementCommand { get; }
        private ICommand _collapseParentDirectoryCommand;
        public ICommand CollapseParentDirectoryCommand => _collapseParentDirectoryCommand ??=
            new RelayCommand(_ => CollapseParentDirectory(), _ => CanCollapseParentDirectory());
        public ObservableCollection<FolderInfo> SearchResultFolders { get; set; } = new();

        private int _rating;
        public int Rating
        {
            get => _rating;
            set
            {
                _rating = value;
                UpdateStars();
                OnPropertyChanged();
            }
        }

        private string _tagInputText;
        public string TagInputText
        {
            get => _tagInputText;
            set
            {
                _tagInputText = value;
                OnPropertyChanged();
            }
        }

        private string _searchText;
        public string SearchText
        {
            get => _searchText;
            set
            {
                _searchText = value;
                OnPropertyChanged();
            }
        }
        public TagCloudViewModel TagCloud { get; private set; } = new TagCloudViewModel();

        private CancellationTokenSource _tagCloudUpdateCts;

        private readonly FolderTagService _tagService = new FolderTagService();
        private int _previewWidth = AppSettings.Instance.PreviewWidth;
        public int PreviewWidth
        {
            get => _previewWidth;
            set
            {
                if (_previewWidth != value)
                {
                    _previewWidth = value;
                    OnPropertyChanged();
                }
            }
        }

        private int _previewHeight = AppSettings.Instance.PreviewHeight;
        public int PreviewHeight
        {
            get => _previewHeight;
            set
            {
                if (_previewHeight != value)
                {
                    _previewHeight = value;
                    OnPropertyChanged();
                }
            }
        }
        public MainViewModel()
        {
            Stars = new ObservableCollection<StarModel>();

            SetRatingCommand = new RelayCommand(param => SaveRatingImmediately((int)param));
            SaveTagsCommand = new AsyncRelayCommand(SaveFolderTagsAsync);
            SearchCommand = new AsyncRelayCommand(async () => await Task.Run(() => PerformSearch()));
            ShowInExplorerCommand = new RelayCommand<FolderInfo>(ShowInExplorer);
            DeleteFolderCommand = new AsyncRelayCommand<FolderInfo>(DeleteFolderAsync);
            _folderManager = new FolderManagementService(HandleFileSystemEvent);
            EditTagsCommand = new RelayCommand(_ => EditTags());
            UndoFolderMovementCommand = new AsyncRelayCommand(UndoLastFolderMovementAsync, CanUndoFolderMovement);

            UpdateStars();

        }

        // Method to set default root directory
        public async Task SetDefaultRootDirectoryAsync()
        {
            var dialog = new FolderBrowserDialog();
            if (!string.IsNullOrEmpty(AppSettings.Instance.DefaultRootDirectory))
            {
                dialog.SelectedPath = AppSettings.Instance.DefaultRootDirectory;
            }

            if (dialog.ShowDialog() == DialogResult.OK)
            {
                var path = dialog.SelectedPath;
                AppSettings.Instance.DefaultRootDirectory = path;
                AppSettings.Instance.Save();

                // Load the directory if it exists
                if (PathService.DirectoryExists(path))
                {
                    await LoadDirectoryAsync(path);
                }
            }
        }

        // Method to load a directory
        public async Task LoadDirectoryAsync(string path)
        {
            // Validate path
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                StatusMessage = "Invalid directory path.";
                return;
            }

            try
            {
                StatusMessage = $"Loading directory {path}...";

                // Use FolderManagementService to load all folders recursively
                var folders = await _folderManager.LoadFoldersRecursivelyAsync(path, true);
                _allLoadedFolders = folders;

                // Update tag cloud
                await UpdateTagCloudAsync();

                StatusMessage = $"Loaded directory: {path}";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading directory: {ex.Message}");
                StatusMessage = $"Error loading directory: {ex.Message}";
            }
        }

        // Method to modify preview size
        public async Task SetPreviewSize(int width, int height, int maxCacheSize, int threadCount)
        {
            bool sizeChanged = PreviewWidth != width || PreviewHeight != height;
            // Save settings
            AppSettings.Instance.PreviewWidth = width;
            AppSettings.Instance.PreviewHeight = height;
            AppSettings.Instance.MaxCacheSize = maxCacheSize;
            AppSettings.Instance.ParallelThreadCount = threadCount;
            AppSettings.Instance.Save();

            if (AppSettings.Instance.ParallelThreadCount != threadCount)
            {
                ImageCache.UpdateParallelThreadCount(threadCount);
            }

            // Clear thumbnail cache
            if (sizeChanged)
            {
                ImageCache.ClearCache();
            }

            // Reload images for current folder if any is selected
            if (SelectedFolder != null)
            {
                await LoadImagesForSelectedFolderAsync();
            }
        }

        private void UpdateStars()
        {
            Stars.Clear();
            for (int i = 1; i <= 5; i++)
            {
                Stars.Add(new StarModel
                {
                    Value = i,
                    Symbol = i <= Rating ? "★" : "☆"
                });
            }
        }

        public void FolderExpanded(FolderInfo folder)
        {
            folder.LoadChildren();
            folder.IsExpanded = true;

            // Start watching this folder for changes
            _folderManager.WatchFolder(folder);

            // Also watch all immediate child folders
            foreach (var child in folder.Children)
            {
                _folderManager.WatchFolder(child);
            }
        }

        /// <summary>
        /// Sets the selected folder without loading images
        /// This is called when a folder is single-clicked
        /// </summary>
        public void SetSelectedFolderWithoutLoading(FolderInfo folder)
        {
            try
            {
                // Clear previous selection highlight if any
                if (SelectedFolder != null)
                {
                    ClearFolderHighlight(SelectedFolder);
                }

                // Update the selected folder
                SelectedFolder = folder;

                // Apply highlight to the new selection
                if (folder != null)
                {
                    ApplyFolderHighlight(folder);

                    // Update folder tags and rating from the .folderTags file
                    UpdateFolderTagsAndRating(folder);

                    // Clear images collection (but don't load new images)
                    // Images.Clear();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error setting selected folder without loading: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates the folder's tags and rating from the .folderTags file
        /// </summary>
        private async void UpdateFolderTagsAndRating(FolderInfo folder)
        {
            try
            {
                if (folder == null) return;

                // Get tags and rating from file
                string path = folder.FolderPath;
                int rating = await _tagService.GetRatingForFolderAsync(path);
                var tags = await _tagService.GetTagsForFolderAsync(path);

                // Update the UI
                Rating = rating;

                // Create a new collection to avoid duplication issues
                var uniqueTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var tag in tags)
                {
                    if (!string.IsNullOrEmpty(tag))
                    {
                        uniqueTags.Add(tag.Trim());
                    }
                }

                // Clear existing tags only after we've processed the new ones
                FolderTags.Clear();

                // Add unique tags to the collection
                foreach (var tag in uniqueTags)
                {
                    FolderTags.Add(tag);
                }

                // Update the display
                OnPropertyChanged(nameof(DisplayTagLine));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error updating folder tags and rating: {ex.Message}");
            }
        }

        /// <summary>
        /// Sets the selected folder and loads images
        /// This is called when a folder is double-clicked or "Load Images" is selected from context menu
        /// </summary>
        public async Task SetSelectedFolderAsync(FolderInfo folder)
        {
            // First update the selection without loading images
            SetSelectedFolderWithoutLoading(folder);

            // Then load the images
            if (folder != null)
            {
                StatusMessage = $"Loading images from '{folder.Name}'...";
                await LoadImagesForSelectedFolderAsync();
            }
        }

        /// <summary>
        /// Applies a highlight effect to the selected folder
        /// </summary>
        private void ApplyFolderHighlight(FolderInfo folder)
        {
            if (folder != null)
            {
                // In a real implementation, this would apply a visual highlight
                // Since we can't directly modify the TreeViewItem from the ViewModel,
                // this is left as a placeholder for integration with the View

                // The actual highlight is applied in the ShellTreeView when handling selection
                folder.IsSelected = true;
            }
        }
        private async Task<bool> TryImportFolder(string sourcePath, string destinationPath, Views.ProgressDialog progressDialog, double progressStart, double progressEnd, CancellationToken cancellationToken)
        {
            try
            {
                // Check if the operation was cancelled
                if (cancellationToken.IsCancellationRequested)
                    return false;

                // Normalize paths to ensure accurate comparison
                string normalizedSource = PathService.NormalizePath(sourcePath);
                string normalizedDestination = PathService.NormalizePath(destinationPath);

                // 1. Check if source and destination paths are identical
                if (PathService.PathsEqual(normalizedSource, normalizedDestination))
                {
                    await Application.Current.Dispatcher.InvokeAsync(() => {
                        MessageBox.Show($"Cannot move folder: Source and destination are the same path.",
                            "Import Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    });
                    return false;
                }

                // 2. Check if destination folder already exists
                if (Directory.Exists(normalizedDestination))
                {
                    await Application.Current.Dispatcher.InvokeAsync(() => {
                        MessageBox.Show($"Destination folder already exists: {normalizedDestination}",
                            "Import Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    });
                    return false;
                }

                // 3. Check if the destination parent directory exists
                string destParentDir = Path.GetDirectoryName(normalizedDestination);
                if (!Directory.Exists(destParentDir))
                {
                    try
                    {
                        Directory.CreateDirectory(destParentDir);
                    }
                    catch (Exception ex)
                    {
                        await Application.Current.Dispatcher.InvokeAsync(() => {
                            MessageBox.Show($"Cannot create destination directory: {ex.Message}",
                                "Import Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                        });
                        return false;
                    }
                }

                // Update progress
                progressDialog.UpdateProgress(progressStart + (progressEnd - progressStart) * 0.3,
                    $"Moving folder '{Path.GetFileName(sourcePath)}'...");

                try
                {
                    // Try to use robust file operations via FileSystem class 
                    // (handles more edge cases than Directory.Move)
                    Microsoft.VisualBasic.FileIO.FileSystem.MoveDirectory(
                        normalizedSource,
                        normalizedDestination,
                        Microsoft.VisualBasic.FileIO.UIOption.AllDialogs,
                        Microsoft.VisualBasic.FileIO.UICancelOption.DoNothing);

                    return true;
                }
                catch (IOException ioEx)
                {
                    // Common error: if both paths are on the same drive but seen as identical by Windows
                    string errorMsg = $"Error moving folder '{Path.GetFileName(sourcePath)}': {ioEx.Message}";

                    // Try using Copy+Delete as a fallback when Move fails
                    progressDialog.UpdateProgress(progressStart + (progressEnd - progressStart) * 0.4,
                        $"Move failed, trying copy operation for '{Path.GetFileName(sourcePath)}'...");

                    try
                    {
                        // Copy the directory instead
                        CopyDirectory(
                            normalizedSource,
                            normalizedDestination,
                            progressDialog,
                            progressStart + (progressEnd - progressStart) * 0.4,
                            progressStart + (progressEnd - progressStart) * 0.8,
                            cancellationToken);

                        // If the copy succeeds, delete the original
                        progressDialog.UpdateProgress(progressStart + (progressEnd - progressStart) * 0.9,
                            $"Deleting original folder after copy...");

                        // Check if copy succeeded by verifying destination exists
                        if (Directory.Exists(normalizedDestination))
                        {
                            try
                            {
                                // Delete the source directory
                                Directory.Delete(normalizedSource, true);
                                return true;
                            }
                            catch (Exception deleteEx)
                            {
                                await Application.Current.Dispatcher.InvokeAsync(() => {
                                    MessageBox.Show(
                                        $"Folder was copied but the original could not be deleted: {deleteEx.Message}\n" +
                                        $"Original location: {normalizedSource}\n" +
                                        $"New location: {normalizedDestination}",
                                        "Import Partially Successful", MessageBoxButton.OK, MessageBoxImage.Information);
                                });
                                return true; // Still consider it a success since the folder was copied
                            }
                        }
                        else
                        {
                            throw new DirectoryNotFoundException("Destination folder not found after copy operation");
                        }
                    }
                    catch (Exception copyEx)
                    {
                        // If copy+delete also fails, show the error
                        await Application.Current.Dispatcher.InvokeAsync(() => {
                            MessageBox.Show(
                                $"{errorMsg}\n\nTried copy+delete as fallback but also failed: {copyEx.Message}",
                                "Import Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                        });
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() => {
                        MessageBox.Show($"Error moving folder '{Path.GetFileName(sourcePath)}': {ex.Message}",
                            "Import Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    });
                    return false;
                }
            }
            catch (Exception ex)
            {
                await Application.Current.Dispatcher.InvokeAsync(() => {
                    MessageBox.Show($"Unexpected error during import: {ex.Message}",
                        "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
                });
                return false;
            }
        }

        public async Task ImportFolderAsync()
        {
            try
            {
                // Show folder browser dialog to select the folders to import
                var dialog = new FolderBrowserDialog
                {
                    Description = "Select folder to import",
                    ShowNewFolderButton = false,
                    // Set initial directory to the user's Documents folder for convenience
                    SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                };

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    // Get the selected folder path
                    string selectedPath = dialog.SelectedPath;

                    // For batch import, we can either:
                    // 1. Import a single folder
                    // 2. Import the selected folder's immediate subfolders

                    List<string> foldersToImport = new List<string>();
                    bool isBatchImport = false;

                    // Check if we should do a batch import (ask user)
                    if (Directory.GetDirectories(selectedPath).Length > 0)
                    {
                        var result = MessageBox.Show(
                            $"Do you want to import all subfolders of '{Path.GetFileName(selectedPath)}'? \n\n" +
                            "Select 'Yes' to import all subfolders as a batch.\n" +
                            "Select 'No' to import just this folder.",
                            "Import Options",
                            MessageBoxButton.YesNoCancel,
                            MessageBoxImage.Question);

                        if (result == MessageBoxResult.Cancel)
                        {
                            return;
                        }

                        if (result == MessageBoxResult.Yes)
                        {
                            // Get all subfolders for batch import
                            foldersToImport.AddRange(Directory.GetDirectories(selectedPath));
                            isBatchImport = true;
                        }
                        else
                        {
                            // Single folder import
                            foldersToImport.Add(selectedPath);
                        }
                    }
                    else
                    {
                        // No subfolders, just import the selected folder
                        foldersToImport.Add(selectedPath);
                    }

                    // Validate all folders to import
                    foldersToImport = foldersToImport
                        .Where(Directory.Exists)
                        .Where(f => !PathService.IsPathWithin(AppSettings.Instance.DefaultRootDirectory, f))
                        .ToList();

                    if (foldersToImport.Count == 0)
                    {
                        MessageBox.Show("No valid folders to import. Selected folders may already be within the root directory.",
                            "Import Cancelled", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    // Create and show the import dialog with the list of folders
                    var importDialog = new Views.ImportFolderDialog(
                        foldersToImport,
                        AppSettings.Instance.DefaultRootDirectory,
                        _allLoadedFolders
                    );

                    importDialog.Owner = Application.Current.MainWindow;
                    importDialog.ShowDialog();

                    // If user confirmed the import
                    if (importDialog.DialogConfirmed)
                    {
                        string destinationPath = importDialog.DestinationPath;

                        // For batch import, destinationPath is just the parent directory
                        // For single import, it includes the folder name

                        // Create a progress dialog
                        var progressDialog = new Views.ProgressDialog(
                            "Importing Folders",
                            isBatchImport ?
                                $"Moving {foldersToImport.Count} folders to '{destinationPath}'..." :
                                $"Moving folder '{Path.GetFileName(foldersToImport[0])}' to '{Path.GetDirectoryName(destinationPath)}'..."
                        );

                        progressDialog.Owner = Application.Current.MainWindow;

                        using (var cts = new CancellationTokenSource())
                        {
                            // Handle cancellation request
                            progressDialog.CancelRequested += (s, e) =>
                            {
                                cts.Cancel();
                                StatusMessage = "Import operation cancelled.";
                            };

                            // Create a background task for import
                            var importTask = Task.Run(async () =>
                            {
                                try
                                {
                                    List<string> successfullyImportedPaths = new List<string>();
                                    var operation = new FolderMoveOperation
                                    {
                                        SourcePaths = new List<string>(foldersToImport),
                                        DestinationPath = isBatchImport ? destinationPath : Path.GetDirectoryName(destinationPath),
                                        IsMultipleMove = foldersToImport.Count > 1,
                                        Timestamp = DateTime.Now,
                                        SourceParentPaths = foldersToImport
                                            .Select(Path.GetDirectoryName)
                                            .Distinct()
                                            .ToList()
                                    };

                                    // Process each folder to import
                                    for (int i = 0; i < foldersToImport.Count; i++)
                                    {
                                        // Check for cancellation
                                        if (cts.Token.IsCancellationRequested)
                                            break;

                                        string sourcePath = foldersToImport[i];
                                        string targetPath;

                                        if (isBatchImport)
                                        {
                                            // For batch import, combine destination with folder name
                                            string folderName = Path.GetFileName(sourcePath);
                                            targetPath = Path.Combine(destinationPath, folderName);

                                            // Ensure unique name
                                            if (Directory.Exists(targetPath))
                                            {
                                                targetPath = PathService.GetUniqueDirectoryPath(destinationPath, folderName);
                                            }
                                        }
                                        else
                                        {
                                            // For single import, destination already includes folder name
                                            targetPath = destinationPath;
                                        }

                                        // Update progress
                                        double progressStart = (double)i / foldersToImport.Count;
                                        double progressEnd = (double)(i + 1) / foldersToImport.Count;

                                        progressDialog.UpdateProgress(
                                            progressStart,
                                            $"Moving folder {i + 1} of {foldersToImport.Count}: '{Path.GetFileName(sourcePath)}'..."
                                        );

                                        // Try to import the folder
                                        bool success = await TryImportFolder(
                                            sourcePath,
                                            targetPath,
                                            progressDialog,
                                            progressStart,
                                            progressEnd,
                                            cts.Token);

                                        if (success)
                                        {
                                            successfullyImportedPaths.Add(targetPath);
                                        }
                                    }

                                    // Update final progress
                                    progressDialog.UpdateProgress(1.0, "Import completed successfully!");

                                    // Add to undo stack if any folders were successfully imported
                                    if (successfullyImportedPaths.Count > 0)
                                    {
                                        await Application.Current.Dispatcher.InvokeAsync(() =>
                                        {
                                            _undoStack.Push(operation);
                                            CommandManager.InvalidateRequerySuggested();
                                        });
                                    }

                                    return successfullyImportedPaths;
                                }
                                catch (Exception ex)
                                {
                                    await Application.Current.Dispatcher.InvokeAsync(() =>
                                    {
                                        MessageBox.Show($"Error importing folders: {ex.Message}",
                                            "Import Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                                    });
                                    return new List<string>();
                                }
                            }, cts.Token);

                            // Show progress dialog
                            progressDialog.ShowDialog();

                            // If dialog closes due to cancel button, ensure operation is cancelled
                            if (progressDialog.IsCancelled && !cts.IsCancellationRequested)
                            {
                                cts.Cancel();
                            }

                            // Wait for import task to complete
                            var importedFolderPaths = await importTask;

                            // If successful, refresh the tree and select the new folder
                            if (importedFolderPaths.Count > 0 && !cts.IsCancellationRequested)
                            {
                                // Reload root folders
                                await LoadDirectoryAsync(AppSettings.Instance.DefaultRootDirectory);

                                // Find and select the first imported folder in tree view
                                var mainWindow = Application.Current.MainWindow as MainWindow;
                                if (mainWindow?.ShellTreeViewControl != null && importedFolderPaths.Count > 0)
                                {
                                    mainWindow.ShellTreeViewControl.SelectPath(importedFolderPaths[0]);
                                }

                                StatusMessage = importedFolderPaths.Count == 1
                                    ? $"Successfully imported folder to '{importedFolderPaths[0]}'"
                                    : $"Successfully imported {importedFolderPaths.Count} folders";
                            }
                            else if (cts.IsCancellationRequested)
                            {
                                StatusMessage = "Import operation cancelled";
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error importing folder: {ex.Message}",
                    "Import Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusMessage = "Import operation failed";
            }
        }


        /// <summary>
        /// Clears the highlight effect from a folder
        /// </summary>
        private void ClearFolderHighlight(FolderInfo folder)
        {
            if (folder != null)
            {
                // Clear the selection state
                folder.IsSelected = false;
            }
        }


        /// <summary>
        /// Modified image loading method with improved status reporting
        /// </summary>
        /// <summary>
        /// Loads images for the currently selected folder with optimized batch processing
        /// </summary>
        public async Task LoadImagesForSelectedFolderAsync()
        {
            if (SelectedFolder == null)
                return;

            // Prevent concurrent or recursive calls
            if (_isLoadingImages)
                return;

            // Cancel any existing loading operation
            if (_imageLoadingCts != null && !_imageLoadingCts.IsCancellationRequested)
            {
                _imageLoadingCts.Cancel();
                _imageLoadingCts.Dispose();
            }

            // Create new cancellation token source
            _imageLoadingCts = new CancellationTokenSource();

            try
            {
                _isLoadingImages = true;

                // Clear existing images
                Images.Clear();

                var folderName = SelectedFolder.Name;
                var path = SelectedFolder.FolderPath;

                // Set initial status message
                StatusMessage = $"Loading images from '{folderName}'...";

                // Get image files
                var supportedExtensions = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" };
                var imageFiles = Directory.Exists(path)
                    ? Directory.GetFiles(path)
                        .Where(file => Array.Exists(supportedExtensions, e =>
                            e.Equals(Path.GetExtension(file).ToLowerInvariant())))
                        .ToList()
                    : new List<string>();

                // If no images, just return
                if (imageFiles.Count == 0)
                {
                    StatusMessage = $"No images found in '{folderName}'";
                    return;
                }

                // Create progress dialog
                var progressDialog = new Views.ProgressDialog(
                    "Loading Images",
                    $"Loading image previews from '{folderName}'...");

                progressDialog.Owner = Application.Current.MainWindow;

                // Handle cancellation request
                progressDialog.CancelRequested += (s, e) =>
                {
                    if (!_imageLoadingCts.IsCancellationRequested)
                    {
                        _imageLoadingCts.Cancel();
                        StatusMessage = "Image loading cancelled.";
                    }
                };

                // Process images with smart batching for better UI responsiveness
                var loadingTask = Task.Run(async () =>
                {
                    try
                    {
                        var token = _imageLoadingCts.Token;
                        int totalImages = imageFiles.Count;
                        int batchSize = 20; // Process in small batches

                        // Use an appropriate degree of parallelism based on system capabilities
                        int parallelism = Math.Min(Environment.ProcessorCount, AppSettings.Instance.ParallelThreadCount);
                        var options = new ParallelOptions
                        {
                            MaxDegreeOfParallelism = parallelism,
                            CancellationToken = token
                        };

                        for (int i = 0; i < imageFiles.Count; i += batchSize)
                        {
                            // Check for cancellation
                            if (token.IsCancellationRequested)
                                break;

                            // Get current batch
                            int currentBatchSize = Math.Min(batchSize, imageFiles.Count - i);
                            var batch = imageFiles.Skip(i).Take(currentBatchSize).ToList();
                            var batchResults = new List<ImageInfo>(currentBatchSize);

                            // Process each image in the batch
                            await Task.Run(() =>
                            {
                                Parallel.ForEach(batch.Select((file, index) => new { File = file, Index = index }),
                                    options,
                                    item =>
                                    {
                                        try
                                        {
                                            // Create a progress reporter for this specific image
                                            var localProgress = new Progress<double>(value =>
                                            {
                                                double overallProgress = (i + item.Index + value) / totalImages;
                                                progressDialog.UpdateProgress(
                                            overallProgress,
                                            $"Loading image {i + item.Index + 1} of {totalImages}...");
                                            });

                                            // Create and load image
                                            var imageInfo = new ImageInfo { FilePath = item.File };
                                            bool success = imageInfo.LoadThumbnailAsync(token, localProgress).GetAwaiter().GetResult();

                                            if (success && !token.IsCancellationRequested)
                                            {
                                                lock (batchResults)
                                                {
                                                    batchResults.Add(imageInfo);
                                                }
                                            }
                                            else if (!success)
                                            {
                                                imageInfo.Dispose();
                                            }
                                        }
                                        catch (OperationCanceledException)
                                        {
                                            // Expected when cancelling
                                        }
                                        catch (Exception ex)
                                        {
                                            System.Diagnostics.Debug.WriteLine($"Error loading image: {ex.Message}");
                                        }
                                    });
                            }, token);

                            // Add batch to UI
                            if (!token.IsCancellationRequested)
                            {
                                await Application.Current.Dispatcher.InvokeAsync(() =>
                                {
                                    foreach (var img in batchResults)
                                    {
                                        Images.Add(img);
                                    }
                                    StatusMessage = $"Loaded {Images.Count} of {totalImages} images...";
                                });
                            }
                        }

                        // Update final progress
                        progressDialog.UpdateProgress(1.0, "Loading complete!");
                        return true;
                    }
                    catch (OperationCanceledException)
                    {
                        return false;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error loading images: {ex.Message}");
                        return false;
                    }
                }, _imageLoadingCts.Token);

                // Show progress dialog
                progressDialog.ShowDialog();

                // If dialog closes, ensure loading is cancelled
                if (progressDialog.IsCancelled && !_imageLoadingCts.IsCancellationRequested)
                {
                    _imageLoadingCts.Cancel();
                }

                // Wait for loading task to complete
                await loadingTask;

                // Update final status
                if (_imageLoadingCts.IsCancellationRequested)
                {
                    StatusMessage = "Image loading cancelled.";
                }
                else
                {
                    StatusMessage = $"Loaded {Images.Count} images from '{folderName}'";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error loading images: {ex.Message}";
            }
            finally
            {
                _isLoadingImages = false;
                _imageLoadingCts?.Dispose();
                _imageLoadingCts = null;
            }
        }


        private async void SaveRatingImmediately(int rating)
        {
            if (SelectedFolder == null)
                return;

            Rating = rating;

            try
            {
                _isSavingTags = true;

                // Save to local storage without changing tags
                await _tagService.SetTagsAndRatingForFolderAsync(
                    SelectedFolder.FolderPath,
                    new List<string>(FolderTags),
                    Rating
                );
                // Update tag cloud
                await UpdateTagCloudAsync();

                // Update in all loaded folders
                UpdateFolderMetadataInAllLoadedFolders(SelectedFolder.FolderPath, new List<string>(FolderTags), Rating);
            }
            finally
            {
                _isSavingTags = false;
            }
        }
        private void EditTags()
        {
            if (SelectedFolder == null)
                return;

            // Check if there are tags to edit
            if (FolderTags.Count > 0)
            {
                // Copy tags from DisplayTagLine to TagInputText
                // Format tags with # prefix for editing
                string tags = string.Join(" ", FolderTags.Select(tag => $"#{tag}"));
                TagInputText = tags;

                // Update status message
                StatusMessage = "Tags loaded for editing. Click 'Update' to save changes.";
            }
            else
            {
                // No tags to edit
                TagInputText = string.Empty;

                // Update status message
                StatusMessage = "No existing tags. Add tags using the # symbol as prefix.";
            }
        }

        public async Task SaveFolderTagsAsync()
        {
            if (SelectedFolder == null)
                return;

            try
            {
                _isSavingTags = true;

                // Get the old tags for comparison
                var oldTags = new List<string>(FolderTags);

                // Check if input is empty
                bool isTagInputEmpty = string.IsNullOrWhiteSpace(TagInputText) ||
                                       TagInputText.Replace("#", "").Trim().Length == 0;

                if (!isTagInputEmpty)
                {
                    // Use TagHelper to update the collection
                    bool tagsChanged = TagHelper.UpdateObservableCollection(FolderTags, TagInputText);

                    if (tagsChanged)
                    {
                        // Log tag changes for debugging
                        Debug.WriteLine($"Tags changed from: {string.Join(", ", oldTags)} to: {string.Join(", ", FolderTags)}");
                    }
                }
                else
                {
                    // If tag input is empty, keep existing tags
                    Debug.WriteLine($"Tag input is empty, preserving existing tags: {string.Join(", ", FolderTags)}");
                }

                // Save tags to the folder
                await _tagService.SetTagsAndRatingForFolderAsync(
                    SelectedFolder.FolderPath,
                    new List<string>(FolderTags),
                    Rating
                );

                // Clear input box
                TagInputText = string.Empty;

                // Force tag cloud to fully refresh (don't rely on cache)
                TagCloud.InvalidateCache();
                await UpdateTagCloudAsync();

                // Update status message
                StatusMessage = isTagInputEmpty
                    ? "No new tags provided. Existing tags preserved."
                    : "Tags updated successfully.";

                // Notify UI
                OnPropertyChanged(nameof(DisplayTagLine));
                UpdateFolderMetadataInAllLoadedFolders(SelectedFolder.FolderPath, new List<string>(FolderTags), Rating);
            }
            finally
            {
                _isSavingTags = false;
            }
        }

        // Modify the UpdateTagCloudAsync method
        public async Task UpdateTagCloudAsync()
        {
            try
            {
                // Cancel any ongoing update
                if (_tagCloudUpdateCts != null)
                {
                    _tagCloudUpdateCts.Cancel();
                    _tagCloudUpdateCts.Dispose();
                }

                // Create a new cancellation token source
                _tagCloudUpdateCts = new CancellationTokenSource();
                var cancellationToken = _tagCloudUpdateCts.Token;

                // Enable debugging to track tag counts
                System.Diagnostics.Debug.WriteLine("Starting tag cloud update...");

                // First, ensure we have fresh folder data by re-reading from disk
                var freshFolders = await _folderManager.LoadFoldersRecursivelyAsync(
                    AppSettings.Instance.DefaultRootDirectory);

                // Replace _allLoadedFolders with fresh data to ensure tag changes are reflected
                _allLoadedFolders = freshFolders;

                // Then update the tag cloud with fresh data
                await TagCloud.UpdateTagCloudAsync(_allLoadedFolders, cancellationToken);

                System.Diagnostics.Debug.WriteLine("Tag cloud updated successfully");
            }
            catch (TaskCanceledException)
            {
                System.Diagnostics.Debug.WriteLine("Tag cloud update was canceled");
            }
            catch (OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine("Tag cloud update operation was canceled");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating tag cloud: {ex.Message}");
            }
        }

        // Add a cleanup method to properly dispose of resources
        public void Cleanup()
        {
            if (_tagCloudUpdateCts != null)
            {
                _tagCloudUpdateCts.Cancel();
                _tagCloudUpdateCts.Dispose();
                _tagCloudUpdateCts = null;
            }
        }

        /// Determines if the parent directory can be collapsed
        /// </summary>
        private bool CanCollapseParentDirectory()
        {
            // Can collapse if there's a selected folder with a parent directory
            return SelectedFolder != null &&
                   !string.IsNullOrEmpty(SelectedFolder.FolderPath) &&
                   !string.IsNullOrEmpty(Path.GetDirectoryName(SelectedFolder.FolderPath));
        }

        /// <summary>
        /// Collapses the parent directory of the current selected folder
        /// </summary>
        private void CollapseParentDirectory()
        {
            if (SelectedFolder == null || string.IsNullOrEmpty(SelectedFolder.FolderPath))
            {
                StatusMessage = "No folder selected.";
                return;
            }

            string parentPath = Path.GetDirectoryName(SelectedFolder.FolderPath);

            if (string.IsNullOrEmpty(parentPath))
            {
                StatusMessage = "Selected folder has no parent directory.";
                return;
            }

            // This method will be called from the view
            StatusMessage = $"Collapsing parent directory: {Path.GetFileName(parentPath)}";

            // The actual collapsing is handled by the view
        }

        // Add these methods to the MainViewModel class
        public async Task RenameTag(string oldTag, string newTag)
        {
            if (string.IsNullOrWhiteSpace(oldTag) || string.IsNullOrWhiteSpace(newTag) || oldTag == newTag)
                return;

            // Use the MahApps.Metro dialog system instead of a custom dialog
            var metroWindow = System.Windows.Application.Current.MainWindow as MahApps.Metro.Controls.MetroWindow;
            var controller = await metroWindow.ShowProgressAsync(
                "Renaming Tag",
                $"Renaming tag '{oldTag}' to '{newTag}'...");

            try
            {
                // Get folders that contain the old tag
                var foldersToUpdate = _allLoadedFolders
                    .Where(f => f.Tags.Contains(oldTag, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                int totalFolders = foldersToUpdate.Count;
                int processedFolders = 0;

                // If there are many folders, show determinate progress
                if (totalFolders > 0)
                {
                    controller.SetProgress(0);
                    controller.SetMessage($"Processing 0 of {totalFolders} folders");
                }
                else
                {
                    controller.SetIndeterminate();
                }

                foreach (var folder in foldersToUpdate)
                {
                    processedFolders++;

                    // Update progress
                    controller.SetProgress((double)processedFolders / totalFolders);
                    controller.SetMessage($"Processing folder {processedFolders} of {totalFolders}");

                    // Get current tags
                    var currentTags = folder.Tags.ToList();

                    // Remove old tag
                    currentTags.RemoveAll(t => string.Equals(t, oldTag, StringComparison.OrdinalIgnoreCase));

                    // Check if the folder already has the new tag (case-insensitive)
                    bool alreadyHasNewTag = currentTags.Any(t => string.Equals(t, newTag, StringComparison.OrdinalIgnoreCase));

                    // Add new tag if it doesn't already exist
                    if (!alreadyHasNewTag)
                    {
                        currentTags.Add(newTag);
                    }

                    // Update the folder's tags
                    folder.Tags = new ObservableCollection<string>(currentTags);

                    // Save to disk
                    await _tagService.SetTagsAndRatingForFolderAsync(
                        folder.FolderPath,
                        currentTags,
                        folder.Rating
                    );

                    // Small delay to prevent UI freezing and allow progress updates
                    await Task.Delay(10);
                }

                // Update tag cloud
                await UpdateTagCloudAsync();

                // If the current folder has the renamed tag, refresh the UI
                if (SelectedFolder != null && SelectedFolder.Tags.Contains(oldTag, StringComparer.OrdinalIgnoreCase))
                {
                    await LoadImagesForSelectedFolderAsync();
                }

                // Close the progress dialog
                await controller.CloseAsync();

                // Show success message
                await metroWindow.ShowMessageAsync(
                    "Tag Renamed",
                    $"Successfully renamed tag '{oldTag}' to '{newTag}' in {totalFolders} folders.");
            }
            catch (Exception ex)
            {
                // Close the progress dialog
                await controller.CloseAsync();

                // Show error message
                await metroWindow.ShowMessageAsync(
                    "Error",
                    $"Error renaming tag: {ex.Message}");
            }
        }

        public void ShowInExplorer(FolderInfo folder)
        {
            if (folder == null) return;
            if (PathService.DirectoryExists(folder.FolderPath))
            {
                System.Diagnostics.Process.Start("explorer.exe", folder.FolderPath);
            }
        }

        public async Task DeleteFolderAsync(FolderInfo folder)
        {
            if (folder == null || string.IsNullOrEmpty(folder.FolderPath))
            {
                MessageBox.Show("No folder selected to delete.",
                    "Delete Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                // Check if it's the root directory
                if (!string.IsNullOrEmpty(AppSettings.Instance.DefaultRootDirectory) &&
                    folder.FolderPath.Equals(AppSettings.Instance.DefaultRootDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("Cannot delete the root directory.",
                        "Delete Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Confirm deletion
                var result = MessageBox.Show(
                    $"Are you sure you want to delete folder '{folder.Name}'?\nThis will move it to the Recycle Bin.",
                    "Confirm Delete",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                    return;

                // Remember parent path for selection after deletion
                string parentPath = Path.GetDirectoryName(folder.FolderPath);
                FolderInfo parentFolder = null;

                if (!string.IsNullOrEmpty(parentPath))
                {
                    parentFolder = _allLoadedFolders.FirstOrDefault(f =>
                        f.FolderPath.Equals(parentPath, StringComparison.OrdinalIgnoreCase));
                }

                // Stop watching this folder before deletion
                _folderManager.UnwatchFolder(folder.FolderPath);

                // Delete to recycle bin
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                    folder.FolderPath,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

                // Remove from _allLoadedFolders
                _allLoadedFolders.RemoveAll(f =>
                    f.FolderPath.Equals(folder.FolderPath, StringComparison.OrdinalIgnoreCase) ||
                    f.FolderPath.StartsWith(folder.FolderPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

                // Remove from search results
                var itemsToRemove = SearchResultFolders.Where(f =>
                    f.FolderPath.Equals(folder.FolderPath, StringComparison.OrdinalIgnoreCase) ||
                    f.FolderPath.StartsWith(folder.FolderPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var item in itemsToRemove)
                {
                    SearchResultFolders.Remove(item);
                }

                // Select parent folder if available
                if (parentFolder != null)
                {
                    SelectedFolder = parentFolder;
                    await SetSelectedFolderAsync(parentFolder);
                }

                // Update tag cloud
                await UpdateTagCloudAsync();

                // Update status
                StatusMessage = $"Deleted folder: {folder.Name}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error deleting folder: {ex.Message}",
                    "Delete Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        /// <summary>
        /// Gets the source parent directory for the current clipboard content
        /// </summary>
        public string GetClipboardSourceDirectory()
        {
            if (_clipboardFolder != null)
            {
                return Path.GetDirectoryName(_clipboardFolder.FolderPath);
            }
            else if (_isMultipleSelection && _clipboardFolders.Count > 0)
            {
                // Get the first source folder's parent for simplicity
                // For a more comprehensive solution, we could return all parent directories
                return Path.GetDirectoryName(_clipboardFolders[0].FolderPath);
            }
            return null;
        }


        // Remove folder and all its subfolders from _allLoadedFolders list
        public void RemoveFolderAndSubfoldersFromAllLoaded(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath))
                return;

            // Normalize the path
            string normalizedPath = PathService.NormalizePath(folderPath);

            // Remove the folder itself and all subfolders that start with this path
            _allLoadedFolders.RemoveAll(f =>
                PathService.PathsEqual(f.FolderPath, normalizedPath) ||
                PathService.IsPathWithin(normalizedPath, f.FolderPath));

            // Invalidate path cache for this path
            PathService.InvalidatePathCache(normalizedPath, true);
        }


        // Remove folder and all its subfolders from search results
        public void RemoveFolderAndSubfoldersFromSearchResults(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath))
                return;

            // Normalize the path
            string normalizedPath = PathService.NormalizePath(folderPath);

            // Create a temporary list to store items to remove
            var itemsToRemove = SearchResultFolders
                .Where(f =>
                    PathService.PathsEqual(f.FolderPath, normalizedPath) ||
                    PathService.IsPathWithin(normalizedPath, f.FolderPath))
                .ToList();

            // Remove all found items from the search results
            foreach (var item in itemsToRemove)
            {
                SearchResultFolders.Remove(item);
            }
        }

        private void UpdateFolderMetadataInAllLoadedFolders(string folderPath, List<string> newTags, int newRating)
        {
            if (string.IsNullOrEmpty(folderPath))
                return;

            // Normalize the path
            string normalizedPath = PathService.NormalizePath(folderPath);

            // Use FirstOrDefault with a more efficient path comparison
            var folder = _allLoadedFolders.FirstOrDefault(f =>
                PathService.PathsEqual(f.FolderPath, normalizedPath));

            if (folder != null)
            {
                // Create a new collection instead of modifying the existing one
                folder.Tags = new ObservableCollection<string>(newTags);
                folder.Rating = newRating;
            }
        }

        public async Task CreateNewFolderAsync(FolderInfo parentFolder, string folderName)
        {
            if (parentFolder == null || string.IsNullOrEmpty(parentFolder.FolderPath))
            {
                MessageBox.Show("No parent folder selected.",
                    "Create Folder Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(folderName))
            {
                MessageBox.Show("Folder name cannot be empty.",
                    "Create Folder Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                // Check if directory exists
                if (!Directory.Exists(parentFolder.FolderPath))
                {
                    MessageBox.Show("The parent directory does not exist.",
                        "Create Folder Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Check for invalid characters
                if (folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    MessageBox.Show("The folder name contains invalid characters.",
                        "Create Folder Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Build new path
                string newPath = Path.Combine(parentFolder.FolderPath, folderName);

                // Check if already exists
                if (Directory.Exists(newPath))
                {
                    MessageBox.Show($"A folder named '{folderName}' already exists in this location.",
                        "Create Folder Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Create the directory
                Directory.CreateDirectory(newPath);

                // Create FolderInfo for the new folder
                var newFolder = new FolderInfo(newPath)
                {
                    Parent = parentFolder
                };

                // Add to _allLoadedFolders for search functionality
                _allLoadedFolders.Add(newFolder);

                // Refresh all folders to ensure consistent state
                await RefreshAllFoldersDataAsync();

                // Update status
                StatusMessage = $"Created new folder: {folderName}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error creating folder: {ex.Message}",
                    "Create Folder Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        public void CutFolder(FolderInfo folder)
        {
            if (folder == null) return;

            _clipboardFolder = folder;
            _isCutOperation = true;
            StatusMessage = $"Cut folder '{folder.Name}' to clipboard. Select a destination folder and paste.";
        }

        public void CopyFolder(FolderInfo folder)
        {
            if (folder == null) return;

            _clipboardFolder = folder;
            _isCutOperation = false;
            StatusMessage = $"Copied folder '{folder.Name}' to clipboard. Select a destination folder and paste.";
        }

        /// <summary>
        /// Performs batch tag operations on multiple folders
        /// </summary>
        public async Task BatchUpdateTags(List<FolderInfo> folders)
        {
            if (folders == null || folders.Count <= 1)
                return;

            try
            {
                // Find common tags among selected folders
                var commonTags = FindCommonTags(folders);

                // Create and show the batch tags dialog
                var dialog = new Views.BatchTagsDialog(folders.Count, commonTags);
                dialog.Owner = Application.Current.MainWindow;

                var result = dialog.ShowDialog();

                // If user confirmed the operation
                if (result == true)
                {
                    // Get tags to add and remove
                    var tagsToAdd = dialog.TagsToAdd;
                    var tagsToRemove = dialog.TagsToRemove;

                    // If both are empty, nothing to do
                    if (tagsToAdd.Count == 0 && tagsToRemove.Count == 0)
                        return;

                    // Create progress dialog
                    var progressDialog = new Views.ProgressDialog(
                        "Updating Tags",
                        $"Updating tags for {folders.Count} folders...");

                    progressDialog.Owner = Application.Current.MainWindow;

                    using (var cts = new CancellationTokenSource())
                    {
                        // Handle cancellation request
                        progressDialog.CancelRequested += (s, e) =>
                        {
                            cts.Cancel();
                            StatusMessage = "Tag update cancelled.";
                        };

                        // Create background task for tag update
                        var updateTask = Task.Run(async () =>
                        {
                            try
                            {
                                int total = folders.Count;
                                int processed = 0;

                                foreach (var folder in folders)
                                {
                                    // Check for cancellation
                                    if (cts.Token.IsCancellationRequested)
                                        break;

                                    try
                                    {
                                        // Update progress
                                        double progress = (double)processed / total;
                                        progressDialog.UpdateProgress(progress, $"Updating folder {processed + 1} of {total}: {folder.Name}");

                                        // Get current tags
                                        var currentTags = await _tagService.GetTagsForFolderAsync(folder.FolderPath);
                                        var updatedTags = new List<string>(currentTags);

                                        // Remove specified tags
                                        if (tagsToRemove.Count > 0)
                                        {
                                            updatedTags.RemoveAll(tag => tagsToRemove.Contains(tag, StringComparer.OrdinalIgnoreCase));
                                        }

                                        // Add new tags without duplicates
                                        if (tagsToAdd.Count > 0)
                                        {
                                            foreach (var tag in tagsToAdd)
                                            {
                                                // Check if tag already exists (case-insensitive)
                                                if (!updatedTags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
                                                {
                                                    updatedTags.Add(tag);
                                                }
                                            }
                                        }

                                        // Get current rating
                                        int rating = await _tagService.GetRatingForFolderAsync(folder.FolderPath);

                                        // Save updated tags and rating
                                        await _tagService.SetTagsAndRatingForFolderAsync(
                                            folder.FolderPath,
                                            updatedTags,
                                            rating);

                                        // Update folder object
                                        folder.Tags = new ObservableCollection<string>(updatedTags);

                                        // Small delay to prevent UI freezing
                                        await Task.Delay(10, cts.Token);
                                    }
                                    catch (Exception ex)
                                    {
                                        Debug.WriteLine($"Error updating tags for folder {folder.FolderPath}: {ex.Message}");
                                    }

                                    processed++;
                                }

                                // Update final progress
                                progressDialog.UpdateProgress(1.0, "Tag update completed");

                                // Update tag cloud
                                TagCloud.InvalidateCache();
                                await UpdateTagCloudAsync();

                                // If the current folder was affected, refresh its tags in the UI
                                if (SelectedFolder != null && folders.Any(f => f.FolderPath == SelectedFolder.FolderPath))
                                {
                                    await Application.Current.Dispatcher.InvokeAsync(() =>
                                    {
                                        UpdateFolderTagsAndRating(SelectedFolder);
                                    });
                                }

                                return true;
                            }
                            catch (OperationCanceledException)
                            {
                                return false;
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"Error in batch tag update: {ex.Message}");
                                await Application.Current.Dispatcher.InvokeAsync(() =>
                                {
                                    MessageBox.Show($"Error updating tags: {ex.Message}",
                                        "Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
                                });
                                return false;
                            }
                        }, cts.Token);

                        // Show modal progress dialog
                        progressDialog.ShowDialog();

                        // If dialog closes due to cancel button, ensure operation is cancelled
                        if (progressDialog.IsCancelled && !cts.IsCancellationRequested)
                        {
                            cts.Cancel();
                        }

                        // Wait for update task to complete
                        bool success = await updateTask;

                        // Update status
                        if (success && !cts.IsCancellationRequested)
                        {
                            StatusMessage = $"Successfully updated tags for {folders.Count} folders";
                        }
                        else if (cts.IsCancellationRequested)
                        {
                            StatusMessage = "Tag update cancelled";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during batch tag operation: {ex.Message}",
                    "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Finds tags that are common to all selected folders
        /// </summary>
        private List<string> FindCommonTags(List<FolderInfo> folders)
        {
            if (folders == null || folders.Count == 0)
                return new List<string>();

            // Use TagHelper to find common tags
            return TagHelper.FindCommonTags(folders.Select(f => f.Tags)).ToList();
        }

        // Methods for multiple folders cut/copy/paste operations
        public void CutMultipleFolders(List<FolderInfo> folders)
        {
            if (folders == null || folders.Count == 0)
                return;

            _clipboardFolders = new List<FolderInfo>(folders);
            _isMultipleSelection = true;
            _isCutOperation = true;

            StatusMessage = $"Cut {folders.Count} folders to clipboard. Select a destination folder and paste.";
        }

        public void CopyMultipleFolders(List<FolderInfo> folders)
        {
            if (folders == null || folders.Count == 0)
                return;

            _clipboardFolders = new List<FolderInfo>(folders);
            _isMultipleSelection = true;
            _isCutOperation = false;

            StatusMessage = $"Copied {folders.Count} folders to clipboard. Select a destination folder and paste.";
        }

        public bool HasClipboardContent()
        {
            // Check for either single folder or multiple folders in clipboard
            return _clipboardFolder != null || (_isMultipleSelection && _clipboardFolders.Count > 0);
        }

        public async Task DeleteMultipleFolders(List<FolderInfo> folders)
        {
            if (folders == null || folders.Count == 0)
                return;

            // Add confirmation dialog before deleting
            var result = MessageBox.Show(
                $"Are you sure you want to delete {folders.Count} folders?",
                "Confirm Deletion",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            try
            {
                // Create progress dialog
                var progressDialog = new Views.ProgressDialog(
                    "Deleting Folders",
                    $"Deleting {folders.Count} folders...");

                // Set progress dialog owner
                progressDialog.Owner = Application.Current.MainWindow;

                // Track deleted folders for undo operation
                var operation = new FolderMoveOperation
                {
                    SourcePaths = folders.Select(f => f.FolderPath).ToList(),
                    // Use "RecycleBin" as a special destination to indicate deletion
                    DestinationPath = "RecycleBin",
                    IsMultipleMove = true,
                    Timestamp = DateTime.Now,
                    SourceParentPaths = folders
                        .Select(f => Path.GetDirectoryName(f.FolderPath))
                        .Distinct()
                        .ToList()
                };

                using (var cts = new CancellationTokenSource())
                {
                    // Handle cancellation request
                    progressDialog.CancelRequested += (s, e) =>
                    {
                        cts.Cancel();
                        StatusMessage = "Delete operation cancelled.";
                    };

                    // Create background task to delete folders
                    var deleteTask = Task.Run(async () =>
                    {
                        try
                        {
                            int total = folders.Count;
                            int processed = 0;
                            bool anyDeleted = false;

                            foreach (var folder in folders)
                            {
                                // Check for cancellation
                                if (cts.Token.IsCancellationRequested)
                                    break;

                                // Skip the root directory
                                if (!string.IsNullOrEmpty(AppSettings.Instance.DefaultRootDirectory) &&
                                    folder.FolderPath.Equals(AppSettings.Instance.DefaultRootDirectory, StringComparison.OrdinalIgnoreCase))
                                {
                                    processed++;
                                    continue;
                                }

                                try
                                {
                                    // Update progress
                                    double progress = (double)processed / total;
                                    progressDialog.UpdateProgress(progress, $"Deleting {processed + 1} of {total}: {folder.Name}");

                                    // Store the parent before deletion for later use
                                    var parentFolder = folder.Parent;
                                    string folderPath = folder.FolderPath;

                                    // Stop watching this folder before deletion
                                    _folderManager.UnwatchFolder(folderPath);

                                    // Delete to recycle bin
                                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                                        folderPath,
                                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

                                    anyDeleted = true;

                                    // Remove from _allLoadedFolders
                                    RemoveFolderAndSubfoldersFromAllLoaded(folderPath);

                                    // Remove from search results
                                    RemoveFolderAndSubfoldersFromSearchResults(folderPath);

                                    // Remove from tree structure
                                    await Application.Current.Dispatcher.InvokeAsync(() =>
                                    {
                                        RemoveFolderFromTree(folder);
                                    });

                                    // Brief delay to prevent UI freezing
                                    await Task.Delay(50, cts.Token);
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"Error deleting folder {folder.FolderPath}: {ex.Message}");
                                }

                                processed++;
                            }

                            // Update final progress
                            progressDialog.UpdateProgress(1.0, "Delete completed");

                            // Add to undo stack if any folders were deleted
                            if (anyDeleted)
                            {
                                await Application.Current.Dispatcher.InvokeAsync(() =>
                                {
                                    _undoStack.Push(operation);
                                    CommandManager.InvalidateRequerySuggested(); // Refresh command state
                                });
                            }

                            return true;
                        }
                        catch (OperationCanceledException)
                        {
                            // Operation was canceled
                            return false;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error in delete operation: {ex.Message}");
                            await Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                MessageBox.Show($"Error deleting folders: {ex.Message}",
                                    "Delete Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            });
                            return false;
                        }
                    }, cts.Token);

                    // Show modal progress dialog
                    progressDialog.ShowDialog();

                    // If dialog closes due to cancel button, ensure operation is cancelled
                    if (progressDialog.IsCancelled && !cts.IsCancellationRequested)
                    {
                        cts.Cancel();
                    }

                    // Wait for delete task to complete
                    bool success = await deleteTask;

                    // Update status
                    if (success && !cts.IsCancellationRequested)
                    {
                        StatusMessage = $"Successfully deleted {folders.Count} folders";

                        // Update tag cloud
                        await UpdateTagCloudAsync();
                    }
                    else if (cts.IsCancellationRequested)
                    {
                        StatusMessage = "Delete operation cancelled";
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error deleting folders: {ex.Message}",
                    "Delete Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Synchronous wrapper for the async paste operation
        /// </summary>
        public void PasteFolder(FolderInfo targetFolder)
        {
            // Call the async method but don't await it
            // This allows the calling code to continue while the operation runs in the background
            // The progress dialog will still be modal and block user interaction
            _ = PasteFolderAsync(targetFolder);
        }

        public async Task PasteFolderAsync(FolderInfo targetFolder)
        {
            // First check if we have multi-selection or single selection
            if (_isMultipleSelection && _clipboardFolders.Count > 0)
            {
                // Multi-selection paste
                if (_isCutOperation)
                {
                    await MoveMultipleFolders(_clipboardFolders, targetFolder);
                }
                else
                {
                    // For copy, we need to implement a separate method since copying multiple folders is more complex
                    await CopyMultipleFoldersToTarget(_clipboardFolders, targetFolder);
                }
            }
            else
            {
                // Original single-selection paste logic
                // First, validate clipboard folder exists
                if (_clipboardFolder == null)
                {
                    MessageBox.Show("No folder is currently in clipboard. Please copy or cut a folder first.",
                        "Paste Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    StatusMessage = "Paste failed: No folder in clipboard";
                    return;
                }

                // Then validate target folder
                if (targetFolder == null)
                {
                    MessageBox.Show("No target folder selected for paste operation.",
                        "Paste Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    StatusMessage = "Paste failed: No target folder selected";
                    return;
                }

                // Store local copies of values we need
                string clipboardFolderName = _clipboardFolder.Name;
                string targetFolderName = targetFolder.Name;
                string operationType = _isCutOperation ? "moved" : "copied";

                try
                {
                    // Create and show progress dialog
                    var progressDialog = new Views.ProgressDialog(
                        _isCutOperation ? "Moving Folder" : "Copying Folder",
                        $"{(_isCutOperation ? "Moving" : "Copying")} folder '{clipboardFolderName}'...");

                    // Get app's main window to set as progress dialog owner
                    var mainWindow = Application.Current.MainWindow;
                    progressDialog.Owner = mainWindow;

                    using (var cts = new CancellationTokenSource())
                    {
                        // Handle cancellation request
                        progressDialog.CancelRequested += (s, e) =>
                        {
                            cts.Cancel();
                            StatusMessage = $"Folder {operationType} operation cancelled.";
                        };

                        // Set initial progress
                        progressDialog.UpdateProgress(0, "Starting operation...");

                        // Check if trying to copy/move to itself or child
                        if (_clipboardFolder == targetFolder ||
                            PathService.IsPathWithin(_clipboardFolder.FolderPath, targetFolder.FolderPath))
                        {
                            MessageBox.Show("Cannot paste a folder into itself or its subfolder.",
                                "Invalid Operation", MessageBoxButton.OK, MessageBoxImage.Warning);
                            progressDialog.Close();
                            StatusMessage = "Paste failed: Cannot paste into the same folder or its subfolder";
                            return;
                        }

                        // Create background task to process folder operation
                        var processTask = Task.Run(async () =>
                        {
                            try
                            {
                                // Update progress
                                progressDialog.UpdateProgress(0.1, "Preparing operation...");

                                await ProcessFolderOperation(_clipboardFolder, targetFolder, _isCutOperation, progressDialog, cts.Token);

                                // Update progress
                                progressDialog.UpdateProgress(0.9, "Refreshing UI...");

                                // Update tag cloud
                                await UpdateTagCloudAsync();

                                // Clear clipboard after cut operation
                                if (_isCutOperation)
                                {
                                    _clipboardFolder = null;
                                }

                                progressDialog.UpdateProgress(1.0, "Moving folder complete!");
                                return true;
                            }
                            catch (OperationCanceledException)
                            {
                                // Operation was cancelled, this is expected
                                return false;
                            }
                            catch (Exception ex)
                            {
                                // Log error and show on UI thread
                                Debug.WriteLine($"Folder operation error: {ex.Message}");

                                await Application.Current.Dispatcher.InvokeAsync(() =>
                                {
                                    MessageBox.Show($"Error during folder operation: {ex.Message}",
                                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                                });

                                return false;
                            }
                        }, cts.Token);

                        // Show modal progress dialog - this will block UI thread until dialog is closed
                        progressDialog.ShowDialog();

                        // When dialog closes, if due to cancel button, ensure operation is cancelled
                        if (progressDialog.IsCancelled && !cts.IsCancellationRequested)
                        {
                            cts.Cancel();
                        }

                        // Wait for processing task to complete
                        bool success = await processTask;

                        // Update status message - using local variables instead of potentially null objects
                        if (success && !cts.IsCancellationRequested)
                        {
                            StatusMessage = $"Successfully {operationType} folder '{clipboardFolderName}' to '{targetFolderName}'";
                        }
                        else if (cts.IsCancellationRequested)
                        {
                            StatusMessage = $"Folder {operationType} operation cancelled";
                        }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error during folder operation: {ex.Message}",
                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);

                    // Use local variables for status message
                    StatusMessage = $"Unable to {operationType} folder: {ex.Message}";
                }
            }
        }

        // New method to copy multiple folders to a target folder
        private async Task CopyMultipleFoldersToTarget(List<FolderInfo> sourceFolders, FolderInfo targetFolder)
        {
            if (sourceFolders == null || sourceFolders.Count == 0 || targetFolder == null)
                return;

            try
            {
                // Create progress dialog
                var progressDialog = new Views.ProgressDialog(
                    "Copying Folders",
                    $"Copying {sourceFolders.Count} folders...");

                // Set progress dialog owner
                progressDialog.Owner = Application.Current.MainWindow;

                using (var cts = new CancellationTokenSource())
                {
                    // Handle cancellation request
                    progressDialog.CancelRequested += (s, e) =>
                    {
                        cts.Cancel();
                        StatusMessage = "Copy operation cancelled.";
                    };

                    // Create background task to copy folders
                    var copyTask = Task.Run(async () =>
                    {
                        try
                        {
                            int total = sourceFolders.Count;
                            int processed = 0;

                            // Get target path
                            string targetPath = targetFolder.FolderPath;

                            // Process each source folder
                            foreach (var sourceFolder in sourceFolders)
                            {
                                // Check for cancellation
                                if (cts.Token.IsCancellationRequested)
                                    break;

                                // Skip if trying to copy to itself
                                string sourcePath = sourceFolder.FolderPath;
                                if (sourcePath == targetPath)
                                {
                                    processed++;
                                    continue;
                                }

                                try
                                {
                                    // Update progress
                                    double progress = (double)processed / total;
                                    progressDialog.UpdateProgress(progress, $"Copying {processed + 1} of {total}: {sourceFolder.Name}");

                                    // Build destination path
                                    string folderName = Path.GetFileName(sourcePath);
                                    string destinationPath = PathService.GetUniqueDirectoryPath(targetPath, folderName);

                                    // Create the destination directory
                                    Directory.CreateDirectory(destinationPath);

                                    // Copy all files and subdirectories
                                    CopyDirectory(sourcePath, destinationPath, progressDialog, progress, progress + (1.0 / total) * 0.9, cts.Token);

                                    // Create FolderInfo for the new folder
                                    var newFolder = new FolderInfo(destinationPath, targetFolder);

                                    // Add to _allLoadedFolders
                                    _allLoadedFolders.Add(newFolder);

                                    // Brief delay to prevent UI freezing
                                    await Task.Delay(50, cts.Token);
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"Error copying folder {sourceFolder.FolderPath}: {ex.Message}");
                                }

                                processed++;
                            }

                            // Update final progress
                            progressDialog.UpdateProgress(1.0, "Copy completed");

                            // Refresh target folder in UI
                            await Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                // Reload target folder's children
                                targetFolder.Children.Clear();
                                targetFolder.LoadChildren();

                                // Start watching the target folder
                                _folderManager.WatchFolder(targetFolder);
                            });

                            return true;
                        }
                        catch (OperationCanceledException)
                        {
                            // Operation was canceled
                            return false;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error in copy operation: {ex.Message}");
                            await Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                MessageBox.Show($"Error copying folders: {ex.Message}",
                                    "Copy Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            });
                            return false;
                        }
                    }, cts.Token);

                    // Show modal progress dialog
                    progressDialog.ShowDialog();

                    // If dialog closes due to cancel button, ensure operation is cancelled
                    if (progressDialog.IsCancelled && !cts.IsCancellationRequested)
                    {
                        cts.Cancel();
                    }

                    // Wait for copy task to complete
                    bool success = await copyTask;

                    // Update status
                    if (success && !cts.IsCancellationRequested)
                    {
                        StatusMessage = $"Successfully copied {sourceFolders.Count} folders to '{targetFolder.Name}'";

                        // Update tag cloud
                        await UpdateTagCloudAsync();
                    }
                    else if (cts.IsCancellationRequested)
                    {
                        StatusMessage = "Copy operation cancelled";
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error copying folders: {ex.Message}",
                    "Copy Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        

        private async Task<bool> ProcessFolderOperation(
        FolderInfo sourceFolder,
        FolderInfo targetFolder,
        bool isCut,
        Views.ProgressDialog progressDialog,
        CancellationToken cancellationToken)
        {
            string sourcePath = sourceFolder.FolderPath;
            string folderName = Path.GetFileName(sourcePath);
            // Check if cancelled
            if (cancellationToken.IsCancellationRequested)
                return false;

            // Update progress
            progressDialog.UpdateProgress(0.2, "Checking destination path...");
            string destinationPath = PathService.GetUniqueDirectoryPath(targetFolder.FolderPath, folderName);

            // Update progress
            progressDialog.UpdateProgress(0.3, "Temporarily disabling file monitoring...");

            // Temporarily disable FileSystemWatcher for these folders
            _folderManager.UnwatchFolder(sourcePath);
            _folderManager.UnwatchFolder(targetFolder.FolderPath);

            try
            {
                // Check if cancelled
                if (cancellationToken.IsCancellationRequested)
                    return false;

                // Update progress
                progressDialog.UpdateProgress(0.4, isCut ? "Moving folder..." : "Copying folder...");

                if (isCut)
                {
                    // Invalidate path cache for source folder and its parent
                    PathService.InvalidatePathCache(sourcePath, true);
                    string sourceParentPath = Path.GetDirectoryName(sourcePath);
                    if (!string.IsNullOrEmpty(sourceParentPath))
                    {
                        PathService.InvalidatePathCache(sourceParentPath, false);
                    }

                    // Track operation for undo
                    var operation = new FolderMoveOperation
                    {
                        SourcePaths = new List<string> { sourcePath },
                        DestinationPath = targetFolder.FolderPath,
                        IsMultipleMove = false,
                        Timestamp = DateTime.Now,
                        SourceParentPaths = new List<string> { Path.GetDirectoryName(sourcePath) }
                    };

                    // Move the directory
                    Directory.Move(sourcePath, destinationPath);

                    // Invalidate path cache for destination folder and target folder
                    PathService.InvalidatePathCache(destinationPath, true);
                    PathService.InvalidatePathCache(targetFolder.FolderPath, false);

                    await Application.Current.Dispatcher.InvokeAsync(() => {
                        _undoStack.Push(operation);
                        CommandManager.InvalidateRequerySuggested(); // Refresh command state
                    });
                }
                else
                {
                    // Update progress
                    progressDialog.UpdateProgress(0.5, "Copying folder contents...");

                    // Copy directory - this may take longer
                    await Task.Run(() =>
                    {
                        CopyDirectory(sourcePath, destinationPath, progressDialog, 0.5, 0.8, cancellationToken);
                    }, cancellationToken);

                    // Invalidate path cache for destination and target folder
                    PathService.InvalidatePathCache(destinationPath, true);
                    PathService.InvalidatePathCache(targetFolder.FolderPath, false);

                    // Check if cancelled
                    if (cancellationToken.IsCancellationRequested)
                        return false;
                }

                // Update progress
                progressDialog.UpdateProgress(0.8, "Refreshing folder view...");

                // Reload target folder's children (on UI thread)
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    targetFolder.Children.Clear();
                    targetFolder.LoadChildren();
                });

                // Create FolderInfo for the new folder
                var newFolder = new FolderInfo(destinationPath, targetFolder);

                // Add to _allLoadedFolders (on UI thread)
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    _allLoadedFolders.Add(newFolder);
                });

                // Start watching folders again
                _folderManager.WatchFolder(targetFolder);
                _folderManager.WatchFolder(newFolder);

                // If cut operation, also watch source parent
                if (isCut && sourceFolder.Parent != null)
                {
                    _folderManager.WatchFolder(sourceFolder.Parent);
                }
                progressDialog.UpdateProgress(1.0, "Moving folder complete!");
                return true;
            }
            finally
            {
                // Make sure we're watching target folder even if error occurred
                _folderManager.WatchFolder(targetFolder);
            }
        }

        private void CopyDirectory(
                string sourceDir,
                string destinationDir,
                Views.ProgressDialog progressDialog = null,
                double progressStart = 0,
                double progressEnd = 1,
                CancellationToken cancellationToken = default)
        {
            // Check if sourceDir exists using PathService
            if (!PathService.DirectoryExists(sourceDir))
                return;

            // Normalize paths
            sourceDir = PathService.NormalizePath(sourceDir);
            destinationDir = PathService.NormalizePath(destinationDir);

            // Get directory info
            var directory = new DirectoryInfo(sourceDir);

            // Create destination directory if it doesn't exist
            if (!PathService.DirectoryExists(destinationDir))
            {
                Directory.CreateDirectory(destinationDir);
            }

            // Check if cancelled
            if (cancellationToken.IsCancellationRequested)
                return;

            // Calculate total items for progress reporting
            int totalItems = 0;
            int processedItems = 0;

            if (progressDialog != null)
            {
                // Count files and subdirectories
                totalItems = directory.GetFiles().Length + directory.GetDirectories().Length;
                if (totalItems == 0) totalItems = 1; // Prevent division by zero
            }

            // Copy all files
            foreach (FileInfo file in directory.GetFiles())
            {
                // Check if cancelled
                if (cancellationToken.IsCancellationRequested)
                    return;

                string targetFilePath = Path.Combine(destinationDir, file.Name);
                file.CopyTo(targetFilePath, true);

                // Update progress
                if (progressDialog != null)
                {
                    processedItems++;
                    double progress = progressStart + (progressEnd - progressStart) * (processedItems / (double)totalItems);
                    progressDialog.UpdateProgress(progress, $"Copying: {file.Name}");
                }
            }

            // Process subdirectories recursively
            foreach (DirectoryInfo subDir in directory.GetDirectories())
            {
                // Check if cancelled
                if (cancellationToken.IsCancellationRequested)
                    return;

                string newDestinationDir = Path.Combine(destinationDir, subDir.Name);

                // Update progress
                if (progressDialog != null)
                {
                    processedItems++;
                    double progress = progressStart + (progressEnd - progressStart) * (processedItems / (double)totalItems);
                    progressDialog.UpdateProgress(progress, $"Copying folder: {subDir.Name}");
                }

                // Use our recursive method
                CopyDirectory(
                    subDir.FullName,
                    newDestinationDir,
                    progressDialog,
                    progressStart + (progressEnd - progressStart) * (processedItems / (double)totalItems),
                    progressStart + (progressEnd - progressStart) * ((processedItems + 1) / (double)totalItems),
                    cancellationToken);
            }

            // Invalidate path cache for the destination directory
            PathService.InvalidatePathCache(destinationDir, false);
        }

        public async Task RenameFolderAsync(FolderInfo folder, string newName)
        {
            if (folder == null || string.IsNullOrEmpty(folder.FolderPath))
            {
                MessageBox.Show("No folder selected to rename.",
                    "Rename Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(newName))
            {
                MessageBox.Show("New folder name cannot be empty.",
                    "Rename Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                // Get the original path info
                string oldPath = folder.FolderPath;
                string oldName = Path.GetFileName(oldPath);
                string parentPath = Path.GetDirectoryName(oldPath);

                // Skip if name didn't change
                if (oldName.Equals(newName, StringComparison.OrdinalIgnoreCase))
                    return;

                // Check for invalid characters
                if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    MessageBox.Show("The folder name contains invalid characters.",
                        "Rename Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Build new path
                string newPath = Path.Combine(parentPath, newName);

                // Check if already exists
                if (Directory.Exists(newPath))
                {
                    MessageBox.Show($"A folder named '{newName}' already exists in this location.",
                        "Rename Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Stop watching the folder before renaming
                _folderManager.UnwatchFolder(oldPath);

                // Rename the directory
                Directory.Move(oldPath, newPath);

                // Update the folder path
                folder.FolderPath = newPath;

                // Update all relevant paths in _allLoadedFolders
                foreach (var f in _allLoadedFolders.ToList())
                {
                    if (f.FolderPath.Equals(oldPath, StringComparison.OrdinalIgnoreCase))
                    {
                        f.FolderPath = newPath;
                    }
                    else if (f.FolderPath.StartsWith(oldPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        // Update child paths
                        f.FolderPath = newPath + f.FolderPath.Substring(oldPath.Length);
                    }
                }

                // Start watching the renamed folder
                _folderManager.WatchFolder(folder);

                // If this is the selected folder, update selection
                if (SelectedFolder != null && SelectedFolder.FolderPath.Equals(oldPath, StringComparison.OrdinalIgnoreCase))
                {
                    SelectedFolder = folder;
                }

                // Update search results if needed
                if (!string.IsNullOrEmpty(SearchText))
                {
                    await PerformSearch();
                }

                // Refresh folder data
                await RefreshAllFoldersDataAsync();

                // Update status
                StatusMessage = $"Renamed folder from '{oldName}' to '{newName}'";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error renaming folder: {ex.Message}",
                    "Rename Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        private bool CanUndoFolderMovement()
        {
            return _undoStack.Count > 0;
        }

        // Step 6: Add the main undo method
        public async Task UndoLastFolderMovementAsync()
        {
            if (_undoStack.Count == 0)
            {
                StatusMessage = "Nothing to undo.";
                return;
            }

            var lastOperation = _undoStack.Pop();

            // Create progress dialog
            var progressDialog = new Views.ProgressDialog(
                "Undoing Folder Movement",
                "Restoring folders to their original locations...");
            progressDialog.Owner = Application.Current.MainWindow;

            using (var cts = new CancellationTokenSource())
            {
                // Handle cancellation
                progressDialog.CancelRequested += (s, e) =>
                {
                    cts.Cancel();
                    StatusMessage = "Undo operation cancelled.";
                };

                try
                {
                    // Create task to undo the operation
                    var undoTask = Task.Run(async () =>
                    {
                        try
                        {
                            int total = lastOperation.SourcePaths.Count;
                            int processed = 0;

                            // Reverse the operation - move from destination back to sources
                            foreach (var sourcePath in lastOperation.SourcePaths)
                            {
                                if (cts.Token.IsCancellationRequested)
                                    break;

                                try
                                {
                                    // Update progress
                                    double progress = (double)processed / total;
                                    progressDialog.UpdateProgress(progress, $"Restoring folder {processed + 1} of {total}...");

                                    // Get the current path in the destination folder
                                    string folderName = Path.GetFileName(sourcePath);
                                    string currentPath = Path.Combine(lastOperation.DestinationPath, folderName);

                                    // Make sure the path exists - the user may have renamed it
                                    if (!Directory.Exists(currentPath))
                                    {
                                        // Try to find the folder by looking for folders with similar names
                                        var potentialMatches = Directory.GetDirectories(lastOperation.DestinationPath)
                                            .Where(dir => Path.GetFileName(dir).StartsWith(folderName) ||
                                                   Path.GetFileName(dir).EndsWith(folderName))
                                            .ToList();

                                        if (potentialMatches.Count == 1)
                                        {
                                            currentPath = potentialMatches[0];
                                        }
                                        else if (potentialMatches.Count > 0)
                                        {
                                            // If more than one match, just use the first one and log a warning
                                            currentPath = potentialMatches[0];
                                            Debug.WriteLine($"Warning: Multiple potential matches for {folderName}, using {currentPath}");
                                        }
                                        else
                                        {
                                            Debug.WriteLine($"Warning: Could not find folder {folderName} in {lastOperation.DestinationPath}");
                                            processed++;
                                            continue;
                                        }
                                    }

                                    // Ensure the destination directory exists
                                    Directory.CreateDirectory(Path.GetDirectoryName(sourcePath));

                                    // If the source path already exists, create a new unique name
                                    string targetPath = sourcePath;
                                    if (Directory.Exists(targetPath))
                                    {
                                        string originalName = Path.GetFileName(sourcePath);
                                        string parentDir = Path.GetDirectoryName(sourcePath);
                                        targetPath = PathService.GetUniqueDirectoryPath(parentDir, originalName);
                                    }

                                    // Stop watching the source and destination paths
                                    _folderManager.UnwatchFolder(currentPath);

                                    // Move the folder back to its original location
                                    Directory.Move(currentPath, targetPath);

                                    // Brief delay to prevent UI freezing
                                    await Task.Delay(50, cts.Token);
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"Error undoing folder move: {ex.Message}");
                                }

                                processed++;
                            }

                            // Update final progress
                            progressDialog.UpdateProgress(1.0, "Folder restore completed");

                            return true;
                        }
                        catch (OperationCanceledException)
                        {
                            return false;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error in undo operation: {ex.Message}");
                            await Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                MessageBox.Show($"Error undoing folder movement: {ex.Message}",
                                    "Undo Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            });
                            return false;
                        }
                    }, cts.Token);

                    // Show modal progress dialog
                    progressDialog.ShowDialog();

                    // If dialog closes due to cancel button, ensure operation is cancelled
                    if (progressDialog.IsCancelled && !cts.IsCancellationRequested)
                    {
                        cts.Cancel();
                    }

                    // Wait for undo task to complete
                    bool success = await undoTask;

                    // Update status
                    if (success && !cts.IsCancellationRequested)
                    {
                        StatusMessage = "Successfully undid the last folder movement";

                        // Refresh relevant folders in the tree
                        var mainWindow = Application.Current.MainWindow as MainWindow;
                        if (mainWindow?.ShellTreeViewControl != null)
                        {
                            // Refresh both source and destination paths
                            foreach (var sourcePath in lastOperation.SourceParentPaths)
                            {
                                if (PathService.DirectoryExists(sourcePath))
                                {
                                    mainWindow.ShellTreeViewControl.RefreshTree(sourcePath, true);
                                }
                            }

                            if (PathService.DirectoryExists(lastOperation.DestinationPath))
                            {
                                mainWindow.ShellTreeViewControl.RefreshTree(lastOperation.DestinationPath, true);
                            }
                        }

                        // Update tag cloud
                        await UpdateTagCloudAsync();
                    }
                    else if (cts.IsCancellationRequested)
                    {
                        StatusMessage = "Undo operation cancelled";
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error undoing folder movement: {ex.Message}",
                        "Undo Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }


        public async void MoveFolder(FolderInfo sourceFolder, FolderInfo targetFolder)
        {
            if (sourceFolder == null || targetFolder == null)
                return;

            // Log the operation start
            StatusMessage = $"Moving folder '{sourceFolder.Name}' to '{targetFolder.Name}'...";

            // Create progress dialog
            var progressDialog = new Views.ProgressDialog(
                "Moving Folder",
                $"Moving folder '{sourceFolder.Name}'...");

            var mainWindow = Application.Current.MainWindow;
            progressDialog.Owner = mainWindow;

            try
            {
                // Create a cancellation token source
                using (var cts = new CancellationTokenSource())
                {
                    // Handle cancellation request
                    progressDialog.CancelRequested += (s, e) =>
                    {
                        cts.Cancel();
                        StatusMessage = "Folder move operation cancelled.";
                    };

                    // Set initial progress
                    progressDialog.UpdateProgress(0, "Preparing move operation...");

                    // Skip if trying to move to itself or child folder
                    if (sourceFolder == targetFolder ||
                            PathService.IsPathWithin(sourceFolder.FolderPath, targetFolder.FolderPath))
                    {
                        MessageBox.Show("Cannot move a folder into itself or its subfolder.",
                            "Invalid Operation", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    // Track the operation for undo
                    var operation = new FolderMoveOperation
                    {
                        SourcePaths = new List<string> { sourceFolder.FolderPath },
                        DestinationPath = targetFolder.FolderPath,
                        IsMultipleMove = false,
                        Timestamp = DateTime.Now,
                        SourceParentPaths = new List<string> { Path.GetDirectoryName(sourceFolder.FolderPath) }
                    };

                    // Create background move task
                    var moveTask = Task.Run(async () =>
                    {
                        try
                        {
                            // Update progress
                            progressDialog.UpdateProgress(0.1, "Checking destination location...");

                            // Build destination path
                            string sourcePath = sourceFolder.FolderPath;
                            string folderName = Path.GetFileName(sourcePath);
                            string destinationPath = PathService.GetUniqueDirectoryPath(targetFolder.FolderPath, folderName);
                            // Update progress
                            progressDialog.UpdateProgress(0.2, "Preparing to move...");

                            // Temporarily disable FileSystemWatcher
                            _folderManager.UnwatchFolder(sourcePath);
                            _folderManager.UnwatchFolder(targetFolder.FolderPath);

                            // Check if cancelled
                            if (cts.Token.IsCancellationRequested)
                                return false;

                            // Move directory
                            Directory.Move(sourcePath, destinationPath);

                            // Update progress
                            progressDialog.UpdateProgress(0.7, "Updating UI...");

                            // Update UI on the UI thread
                            await Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                // Remove from UI
                                RemoveFolderFromTree(sourceFolder);
                                RemoveFolderAndSubfoldersFromSearchResults(sourcePath);
                                RemoveFolderAndSubfoldersFromAllLoaded(sourcePath);

                                // Refresh target folder's children
                                targetFolder.Children.Clear();
                                targetFolder.LoadChildren();

                                // Create FolderInfo for the new folder
                                var newFolder = new FolderInfo(destinationPath, targetFolder);

                                // Add to _allLoadedFolders
                                _allLoadedFolders.Add(newFolder);
                            });

                            // Update progress
                            progressDialog.UpdateProgress(0.9, "Re-enabling file monitoring...");
                            // Start watching folders again
                            _folderManager.WatchFolder(targetFolder);
                            _folderManager.WatchFolder(new FolderInfo(destinationPath));
                            // Refresh target folder's children
                            await Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                targetFolder.Children.Clear();
                                targetFolder.LoadChildren();
                            });
                            await UpdateTagCloudAsync();
                            progressDialog.UpdateProgress(1.0, "Moving folder complete!");

                            // Track operation for undo
                            await Application.Current.Dispatcher.InvokeAsync(() => {
                                _undoStack.Push(operation);
                                CommandManager.InvalidateRequerySuggested(); // Refresh command state
                            });

                            return true;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error moving folder: {ex.Message}");

                            // Show error message on UI thread
                            await Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                MessageBox.Show($"Error moving folder: {ex.Message}",
                                    "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                            });

                            return false;
                        }
                        finally
                        {
                            // Make sure we're watching the target folder
                            _folderManager.WatchFolder(targetFolder);
                        }
                    }, cts.Token);

                    // Show modal progress dialog - this will block the UI thread
                    progressDialog.ShowDialog();

                    // When dialog closes, ensure operation is cancelled
                    if (progressDialog.IsCancelled && !cts.IsCancellationRequested)
                    {
                        cts.Cancel();
                    }

                    // Wait for move task to complete
                    bool success = await moveTask;

                    // Update status message
                    if (success && !cts.IsCancellationRequested)
                    {
                        StatusMessage = $"Successfully moved folder '{sourceFolder.Name}' to '{targetFolder.Name}'";
                    }
                    else if (cts.IsCancellationRequested)
                    {
                        StatusMessage = $"Move operation for folder '{sourceFolder.Name}' was cancelled";
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during move operation: {ex.Message}",
                    "Move Failed", MessageBoxButton.OK, MessageBoxImage.Error);

                StatusMessage = $"Failed to move folder: {ex.Message}";
            }
        }

        public async Task MoveMultipleFolders(List<FolderInfo> sourceFolders, FolderInfo targetFolder)
        {
            if (sourceFolders == null || sourceFolders.Count == 0 || targetFolder == null)
                return;

            try
            {
                // Create progress dialog
                var progressDialog = new Views.ProgressDialog(
                    "Moving Folders",
                    $"Moving {sourceFolders.Count} folders...");

                // Set progress dialog owner
                progressDialog.Owner = Application.Current.MainWindow;

                // Track operation for undo
                var operation = new FolderMoveOperation
                {
                    SourcePaths = sourceFolders.Select(f => f.FolderPath).ToList(),
                    DestinationPath = targetFolder.FolderPath,
                    IsMultipleMove = true,
                    Timestamp = DateTime.Now,
                    SourceParentPaths = sourceFolders
                        .Select(f => Path.GetDirectoryName(f.FolderPath))
                        .Distinct()
                        .ToList()
                };

                using (var cts = new CancellationTokenSource())
                {
                    // Handle cancellation request
                    progressDialog.CancelRequested += (s, e) =>
                    {
                        cts.Cancel();
                        StatusMessage = "Move operation cancelled.";
                    };

                    // Create background task to move folders
                    var moveTask = Task.Run(async () =>
                    {
                        try
                        {
                            int total = sourceFolders.Count;
                            int processed = 0;
                            bool anySuccess = false;

                            // Get target path
                            string targetPath = targetFolder.FolderPath;

                            // Process each source folder
                            foreach (var sourceFolder in sourceFolders)
                            {
                                // Check for cancellation
                                if (cts.Token.IsCancellationRequested)
                                    break;

                                // Skip if trying to move to itself or child folder
                                string sourcePath = sourceFolder.FolderPath;
                                if (PathService.PathsEqual(sourcePath, targetPath) || PathService.IsPathWithin(sourcePath, targetPath))
                                {
                                    processed++;
                                    continue;
                                }

                                try
                                {
                                    // Update progress
                                    double progress = (double)processed / total;
                                    progressDialog.UpdateProgress(progress, $"Moving {processed + 1} of {total}: {sourceFolder.Name}");

                                    // Build destination path
                                    string folderName = Path.GetFileName(sourcePath);
                                    string destinationPath = PathService.GetUniqueDirectoryPath(targetPath, folderName);

                                    // Temporarily disable FileSystemWatcher
                                    _folderManager.UnwatchFolder(sourcePath);
                                    _folderManager.UnwatchFolder(targetPath);

                                    // Invalidate path cache for source and parent
                                    PathService.InvalidatePathCache(sourcePath, true);
                                    string sourceParentPath = Path.GetDirectoryName(sourcePath);
                                    if (!string.IsNullOrEmpty(sourceParentPath))
                                    {
                                        PathService.InvalidatePathCache(sourceParentPath, false);
                                    }

                                    // Move directory
                                    Directory.Move(sourcePath, destinationPath);

                                    // Invalidate path cache for destination and target
                                    PathService.InvalidatePathCache(destinationPath, true);
                                    PathService.InvalidatePathCache(targetPath, false);

                                    anySuccess = true;

                                    // Update UI
                                    await Application.Current.Dispatcher.InvokeAsync(() =>
                                    {
                                        // Remove from tree
                                        RemoveFolderFromTree(sourceFolder);
                                        RemoveFolderAndSubfoldersFromSearchResults(sourcePath);
                                        RemoveFolderAndSubfoldersFromAllLoaded(sourcePath);
                                    });

                                    // Create FolderInfo for the new folder
                                    var newFolder = new FolderInfo(destinationPath, targetFolder);

                                    // Add to _allLoadedFolders
                                    _allLoadedFolders.Add(newFolder);

                                    // Re-enable file monitoring
                                    _folderManager.WatchFolder(targetFolder);
                                    _folderManager.WatchFolder(newFolder);

                                    // Also watch source parent if available
                                    if (sourceFolder.Parent != null)
                                    {
                                        _folderManager.WatchFolder(sourceFolder.Parent);
                                    }

                                    // Brief delay to prevent UI freezing
                                    await Task.Delay(50, cts.Token);
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"Error moving folder {sourceFolder.FolderPath}: {ex.Message}");
                                }

                                processed++;
                            }
                            await UpdateTagCloudAsync();
                            // Update final progress
                            progressDialog.UpdateProgress(1.0, "Move completed");

                            // Refresh target folder in UI
                            await Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                // Reload target folder's children
                                targetFolder.Children.Clear();
                                targetFolder.LoadChildren();

                                // Track the operation for undo if at least one folder was moved successfully
                                if (anySuccess)
                                {
                                    _undoStack.Push(operation);
                                    CommandManager.InvalidateRequerySuggested(); // Refresh command state
                                }
                            });

                            return true;
                        }
                        catch (OperationCanceledException)
                        {
                            // Operation was canceled
                            return false;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error in move operation: {ex.Message}");
                            await Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                MessageBox.Show($"Error moving folders: {ex.Message}",
                                    "Move Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            });
                            return false;
                        }
                        finally
                        {
                            // Make sure we're watching the target folder
                            _folderManager.WatchFolder(targetFolder);
                        }
                    }, cts.Token);

                    // Show modal progress dialog
                    progressDialog.ShowDialog();

                    // If dialog closes due to cancel button, ensure operation is cancelled
                    if (progressDialog.IsCancelled && !cts.IsCancellationRequested)
                    {
                        cts.Cancel();
                    }

                    // Wait for move task to complete
                    bool success = await moveTask;

                    // Update status
                    if (success && !cts.IsCancellationRequested)
                    {
                        StatusMessage = $"Successfully moved {sourceFolders.Count} folders to '{targetFolder.Name}'";

                        // Clear clipboard if this was a cut operation
                        if (_isMultipleSelection && _isCutOperation)
                        {
                            _clipboardFolders.Clear();
                            _isMultipleSelection = false;
                        }

                        // Update tag cloud
                        await UpdateTagCloudAsync();
                    }
                    else if (cts.IsCancellationRequested)
                    {
                        StatusMessage = "Move operation cancelled";
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error moving folders: {ex.Message}",
                    "Move Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Performs a search for folders matching the criteria in SearchText
        /// </summary>
        private async Task PerformSearch()
        {
            try
            {
                // Clear search results on UI thread
                await Application.Current.Dispatcher.InvokeAsync(() => {
                    SearchResultFolders.Clear();
                    StatusMessage = "Searching...";
                    IsSearching = true;
                });

                if (string.IsNullOrWhiteSpace(SearchText))
                {
                    await Application.Current.Dispatcher.InvokeAsync(() => {
                        StatusMessage = "Ready";
                        IsSearching = false;
                    });
                    return;
                }

                // Use Task.Run to perform the search operation off the UI thread
                await Task.Run(async () =>
                {
                    try
                    {
                        // Reload folder data if needed
                        if (_allLoadedFolders.Count == 0)
                        {
                            _allLoadedFolders = await _folderManager.LoadFoldersRecursivelyAsync(
                                AppSettings.Instance.DefaultRootDirectory);
                        }

                        // Find matching folders
                        var matchingFolders = FindMatchingFolders();

                        // Update UI with search results
                        await Application.Current.Dispatcher.InvokeAsync(() => {
                            foreach (var folder in matchingFolders)
                            {
                                SearchResultFolders.Add(folder);
                            }

                            StatusMessage = $"Found {matchingFolders.Count} matching folders";
                            IsSearching = false;
                        });
                    }
                    catch (Exception ex)
                    {
                        // Handle exceptions in the background task
                        await Application.Current.Dispatcher.InvokeAsync(() => {
                            StatusMessage = $"Error: {ex.Message}";
                            IsSearching = false;
                            MessageBox.Show(
                                $"An error occurred during search: {ex.Message}",
                                "Search Error",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                        });
                    }
                });
            }
            catch (Exception ex)
            {
                await Application.Current.Dispatcher.InvokeAsync(() => {
                    StatusMessage = "Search failed";
                    IsSearching = false;
                    MessageBox.Show(
                        $"An error occurred during search: {ex.Message}",
                        "Search Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                });
            }
        }

        /// <summary>
        /// Finds folders matching the search criteria
        /// </summary>
        private List<FolderInfo> FindMatchingFolders()
        {
            // Create search terms object that will handle parsing
            var searchTerms = new SearchTerms(SearchText);

            // Apply all search criteria to find matching folders
            return _allLoadedFolders.Where(folder => searchTerms.Matches(folder)).ToList();
        }

        // Add this method to MainViewModel.cs to recursively read all folder tags and ratings
        public async Task RefreshAllFoldersDataAsync()
        {
            if (string.IsNullOrEmpty(AppSettings.Instance.DefaultRootDirectory) ||
                !Directory.Exists(AppSettings.Instance.DefaultRootDirectory))
            {
                return;
            }

            // Show progress dialog
            var metroWindow = Application.Current.MainWindow as MahApps.Metro.Controls.MetroWindow;
            var controller = await metroWindow.ShowProgressAsync(
                "Refreshing Data",
                "Reading folder information...");

            controller.SetIndeterminate();

            try
            {
                // Reload all folders from the file system
                _allLoadedFolders = await _folderManager.LoadFoldersRecursivelyAsync(
                    AppSettings.Instance.DefaultRootDirectory);

                // Update tag cloud with fresh data
                await Application.Current.Dispatcher.InvokeAsync(async () => {
                    await UpdateTagCloudAsync();
                    StatusMessage = "Ready";
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error refreshing folder data: {ex.Message}");
                await metroWindow.ShowMessageAsync(
                    "Error",
                    $"Error refreshing folder data: {ex.Message}");
            }
            finally
            {
                await controller.CloseAsync();
            }
        }


        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private async void HandleFileSystemEvent(FolderInfo folder, FileSystemEventArgs e, WatcherChangeTypes changeType)
        {

            if (_isSavingTags)
                return;

            if (folder == null) return;

            // Get the path information
            string changedPath = e.FullPath;
            bool isDirectory = Directory.Exists(changedPath);

            try
            {
                switch (changeType)
                {
                    case WatcherChangeTypes.Created:
                        if (isDirectory)
                        {
                            // A new directory was created
                            var newFolder = await _folderManager.CreateFolderInfoWithoutImagesAsync(changedPath);
                            if (newFolder != null)
                            {
                                newFolder.Parent = folder; // Set parent relationship
                                folder.Children.Add(newFolder);

                                // Add to _allLoadedFolders for search functionality
                                _allLoadedFolders.Add(newFolder);

                                // If we're viewing this folder, refresh the UI
                                if (SelectedFolder == folder)
                                {
                                    await SetSelectedFolderAsync(folder);
                                }

                                // Update search results if needed
                                if (!string.IsNullOrEmpty(SearchText))
                                {
                                    await PerformSearch();
                                }
                            }
                        }
                        else
                        {
                            // A new file was created
                            if (SelectedFolder == folder)
                            {
                                await LoadImagesForSelectedFolderAsync();
                            }
                        }
                        break;

                    case WatcherChangeTypes.Deleted:
                        if (isDirectory)
                        {
                            // A directory was deleted
                            var deletedFolder = folder.Children.FirstOrDefault(f => f.FolderPath == changedPath);
                            if (deletedFolder != null)
                            {
                                // Stop watching this folder
                                _folderManager.UnwatchFolder(changedPath);

                                // Remove from tree
                                folder.Children.Remove(deletedFolder);

                                // Remove folder and all subfolders from _allLoadedFolders
                                RemoveFolderAndSubfoldersFromAllLoaded(changedPath);

                                // Remove from search results
                                RemoveFolderAndSubfoldersFromSearchResults(changedPath);
                            }
                        }
                        else
                        {
                            // A file was deleted
                            if (SelectedFolder == folder)
                            {
                                await LoadImagesForSelectedFolderAsync();
                            }
                        }
                        break;

                    case WatcherChangeTypes.Renamed:
                        if (e is RenamedEventArgs renamedArgs && renamedArgs != null)
                        {
                            if (isDirectory)
                            {
                                if (folder.Children == null)
                                {
                                    Debug.WriteLine($"Warning: folder.Children is null for {folder.FolderPath}");
                                    return;
                                }

                                // Add null check in LINQ query
                                var renamedFolder = folder.Children.FirstOrDefault(f =>
                                    f != null && f.FolderPath != null && f.FolderPath == renamedArgs.OldFullPath);

                                if (renamedFolder != null)
                                {
                                    // Update the path
                                    string oldPath = renamedFolder.FolderPath;
                                    renamedFolder.FolderPath = renamedArgs.FullPath;

                                    // Stop watching old path and start watching new path
                                    _folderManager.UnwatchFolder(oldPath);
                                    _folderManager.WatchFolder(renamedFolder);

                                    // Force refresh of the folder
                                    var index = folder.Children.IndexOf(renamedFolder);
                                    folder.Children.RemoveAt(index);
                                    folder.Children.Insert(index, renamedFolder);

                                    // Update in _allLoadedFolders
                                    UpdateFolderPathsInAllLoadedFolders(oldPath, renamedArgs.FullPath);

                                    // Update search results if needed
                                    if (!string.IsNullOrEmpty(SearchText))
                                    {
                                        await PerformSearch();
                                    }
                                }
                            }
                            else
                            {
                                // A file was renamed
                                if (SelectedFolder == folder)
                                {
                                    await LoadImagesForSelectedFolderAsync();
                                }
                            }
                        }
                        break;

                    case WatcherChangeTypes.Changed:
                        // For most changes to directories, we don't need to do anything
                        // But for changes to files, reload if the current folder is selected
                        if (!isDirectory && SelectedFolder == folder)
                        {
                            await LoadImagesForSelectedFolderAsync();
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in HandleFileSystemEvent: {ex.Message}");
                Debug.WriteLine($"StackTrace: {ex.StackTrace}");
                // Continue execution even if error occurs
            }
        }

        // Helper method to update folder paths in _allLoadedFolders after rename
        private void UpdateFolderPathsInAllLoadedFolders(string oldPath, string newPath)
        {
            foreach (var folder in _allLoadedFolders.ToList())
            {
                if (folder.FolderPath == oldPath)
                {
                    folder.FolderPath = newPath;
                }
                else if (PathService.IsPathWithin(oldPath, folder.FolderPath))
                {
                    // Update subfolders as well
                    folder.FolderPath = newPath + folder.FolderPath.Substring(oldPath.Length);
                }
            }
        }


    }

    public class StarModel
    {
        public int Value { get; set; }
        public string Symbol { get; set; }
    }

    public class FolderMoveOperation
    {
        public List<string> SourcePaths { get; set; } = new List<string>();
        public string DestinationPath { get; set; }
        public bool IsMultipleMove { get; set; }
        public DateTime Timestamp { get; set; }

        // Store parent paths for refreshing after undo
        public List<string> SourceParentPaths { get; set; } = new List<string>();
    }

    /// <summary>
    /// Helper class to encapsulate search term parsing and matching
    /// </summary>
    public class SearchTerms
    {
        // List of AND groups (each containing one or more OR conditions)
        private readonly List<List<Predicate<FolderInfo>>> _andGroups = new List<List<Predicate<FolderInfo>>>();

        /// <summary>
        /// Parses search text into structured search terms
        /// </summary>
        public SearchTerms(string searchText)
        {
            if (string.IsNullOrWhiteSpace(searchText))
                return;

            // Split by '&' (AND operator)
            var andParts = searchText.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .ToList();

            foreach (var andPart in andParts)
            {
                // Parse each AND group into a list of OR predicates
                var orPredicates = ParseAndGroup(andPart);

                if (orPredicates.Count > 0)
                {
                    _andGroups.Add(orPredicates);
                }
            }
        }

        /// <summary>
        /// Parses a single AND group into a list of OR predicates
        /// </summary>
        private List<Predicate<FolderInfo>> ParseAndGroup(string andGroup)
        {
            var orPredicates = new List<Predicate<FolderInfo>>();
            var terms = andGroup.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var term in terms)
            {
                if (term.StartsWith("#")) // Tag search
                {
                    string tagTerm = term.Substring(1).ToLowerInvariant();
                    if (!string.IsNullOrWhiteSpace(tagTerm))
                    {
                        orPredicates.Add(folder =>
                            folder.Tags != null &&
                            folder.Tags.Any(tag => tag.ToLowerInvariant().Contains(tagTerm)));
                    }
                }
                else if (term.StartsWith("*")) // Rating search
                {
                    string ratingPattern = term.Substring(1).Trim();
                    var predicate = CreateRatingPredicate(ratingPattern);
                    if (predicate != null)
                    {
                        orPredicates.Add(predicate);
                    }
                }
                else if (term.StartsWith("@")) // Folder name search
                {
                    string folderNameTerm = term.Substring(1).ToLowerInvariant();
                    if (!string.IsNullOrWhiteSpace(folderNameTerm))
                    {
                        orPredicates.Add(folder =>
                            folder.Name != null &&
                            folder.Name.ToLowerInvariant().Contains(folderNameTerm));
                    }
                }
                else // General text search (name or path)
                {
                    string textTerm = term.ToLowerInvariant();
                    if (!string.IsNullOrWhiteSpace(textTerm))
                    {
                        orPredicates.Add(folder =>
                            (folder.Name?.ToLowerInvariant().Contains(textTerm) ?? false) ||
                            (folder.FolderPath?.ToLowerInvariant().Contains(textTerm) ?? false));
                    }
                }
            }

            return orPredicates;
        }

        /// <summary>
        /// Creates a predicate for rating comparison
        /// </summary>
        private Predicate<FolderInfo> CreateRatingPredicate(string ratingPattern)
        {
            if (string.IsNullOrWhiteSpace(ratingPattern))
                return null;

            // Parse operators: >=, <=, =, >, <
            string comparisonOperator;
            string valueStr;

            if (ratingPattern.StartsWith(">=") || ratingPattern.StartsWith("<="))
            {
                comparisonOperator = ratingPattern.Substring(0, 2);
                valueStr = ratingPattern.Substring(2).Trim();
            }
            else if (ratingPattern.StartsWith("=") || ratingPattern.StartsWith(">") || ratingPattern.StartsWith("<"))
            {
                comparisonOperator = ratingPattern.Substring(0, 1);
                valueStr = ratingPattern.Substring(1).Trim();
            }
            else
            {
                return null;
            }

            // Validate rating value (0-5)
            if (!int.TryParse(valueStr, out int value) || value < 0 || value > 5)
                return null;

            // Create appropriate predicate based on operator
            switch (comparisonOperator)
            {
                case ">=": return folder => folder.Rating >= value;
                case "<=": return folder => folder.Rating <= value;
                case "=": return folder => folder.Rating == value;
                case ">": return folder => folder.Rating > value;
                case "<": return folder => folder.Rating < value;
                default: return null;
            }
        }

        /// <summary>
        /// Determines if a folder matches all search criteria
        /// </summary>
        public bool Matches(FolderInfo folder)
        {
            // If no search terms, everything matches
            if (_andGroups.Count == 0)
                return true;

            // For each AND group, at least one OR condition must match
            foreach (var orPredicates in _andGroups)
            {
                bool anyMatch = false;

                foreach (var predicate in orPredicates)
                {
                    if (predicate(folder))
                    {
                        anyMatch = true;
                        break;
                    }
                }

                // If no conditions matched in this AND group, folder doesn't match
                if (!anyMatch)
                    return false;
            }

            // All AND groups had at least one matching condition
            return true;
        }
    }

    public class RelayCommand : ICommand
    {
        private readonly Action<object> _execute;
        private readonly Predicate<object> _canExecute;

        public RelayCommand(Action<object> execute, Predicate<object> canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter) => _canExecute == null || _canExecute(parameter);

        public void Execute(object parameter) => _execute(parameter);

        public event EventHandler CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
    }
}