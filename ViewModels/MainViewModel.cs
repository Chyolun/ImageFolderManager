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
        public void WatchFolder(FolderInfo folder)
        {
            _folderManager.WatchFolder(folder);
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
                if (Directory.Exists(path))
                {
                    await LoadDirectoryAsync(path);
                }
            }
        }

        // Method to load a directory
        public async Task LoadDirectoryAsync(string path)
        {
            // Stop watching previous folders
       
            _folderManager.UnwatchAllFolders();
           var folders = await _folderManager.LoadFoldersRecursivelyAsync(path);
            
            _allLoadedFolders = folders;

            RootFolders.Clear();
            var root = folders.FirstOrDefault(f => f.FolderPath == path);
            if (root != null)
            {
                root.LoadChildren(); // Initialize for expansion
                RootFolders.Add(root);

                // Start watching the root folder
                _folderManager.WatchFolder(root);
            }

            // Update tag cloud after loading folders;
            await UpdateTagCloudAsync();
        }

        // Method to modify preview size
        public async Task SetPreviewSize(int width, int height)
        {
            bool sizeChanged = PreviewWidth != width || PreviewHeight != height;
            PreviewWidth = width;
            PreviewHeight = height;

            // Save settings
            AppSettings.Instance.PreviewWidth = width;
            AppSettings.Instance.PreviewHeight = height;
            AppSettings.Instance.Save();

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
        public async Task LoadImagesForSelectedFolderAsync()
        {
            if (SelectedFolder == null)
                return;

            // Prevent concurrent or recursive calls
            if (_isLoadingImages)
            {
                return;
            }

            // Cancel any existing loading operation
            if (_imageLoadingCts != null && !_imageLoadingCts.IsCancellationRequested)
            {
                _imageLoadingCts.Cancel();
                _imageLoadingCts.Dispose();
            }

            // Create new cancellation token source
            _imageLoadingCts = new System.Threading.CancellationTokenSource();

            try
            {
                _isLoadingImages = true;

                // Clear existing images
                Images.Clear();

                var folderName = SelectedFolder.Name;
                var path = SelectedFolder.FolderPath;

                // Set initial status message
                StatusMessage = $"Loading images from '{folderName}'...";

                // Get the image files from the folder
                var supportedExtensions = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" };
                var imageFiles = new List<string>();

                if (PathService.DirectoryExists(path))
                {
                    try
                    {
                        var allFiles = Directory.GetFiles(path);
                        Debug.WriteLine($"Found {allFiles.Length} total files");
                        foreach (var file in allFiles)
                        {
                            string ext = Path.GetExtension(file).ToLowerInvariant();
                            if (Array.Exists(supportedExtensions, e => e == ext))
                            {
                                imageFiles.Add(file);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error getting image files: {ex.Message}");
                    }
                }

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

                // Set progress dialog owner
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
                var loadingTask = Task.Run(async () =>
                {
                    try
                    {
                        // Create progress tracking variables
                        int totalImages = imageFiles.Count;
                        int processedImages = 0;
                        var cancellationToken = _imageLoadingCts.Token;
                        var loadedImages = new List<ImageInfo>();

                        // Create progress reporter
                        var progressReporter = new Progress<double>(value =>
                        {
                            // Calculate overall progress
                            double overallProgress = (processedImages + value) / totalImages;

                            // Update progress dialog
                            progressDialog.UpdateProgress(
                                overallProgress,
                                $"Loading image {processedImages + 1} of {totalImages}...");
                        });

                        // Load each image
                        foreach (var file in imageFiles)
                        {
                            // Check for cancellation
                            if (cancellationToken.IsCancellationRequested)
                            {
                                break;
                            }

                            var imageInfo = new ImageInfo { FilePath = file };

                            // Load thumbnail with progress reporting
                            bool success = await imageInfo.LoadThumbnailAsync(cancellationToken, progressReporter);

                            if (cancellationToken.IsCancellationRequested)
                            {
                                // Clean up resources
                                imageInfo.Dispose();
                                break;
                            }

                            if (success)
                            {
                                loadedImages.Add(imageInfo);
                            }
                            else
                            {
                                imageInfo.Dispose();
                            }

                            processedImages++;

                            // Update progress
                            progressDialog.UpdateProgress(
                                (double)processedImages / totalImages,
                                $"Loaded {processedImages} of {totalImages} images");
                        }

                        // If not cancelled, update UI
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            // Final update to progress dialog
                            progressDialog.UpdateProgress(1.0, "Loading complete!");

                            // Add images to collection on UI thread
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                foreach (var img in loadedImages)
                                {
                                    Images.Add(img);
                                }

                                // Update status
                                StatusMessage = $"Loaded {loadedImages.Count} images from '{SelectedFolder.Name}'";
                            });
                        }
                        else
                        {
                            // Handle case where image loading was cancelled
                            foreach (var img in loadedImages)
                            {
                                img.Dispose();
                            }

                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                StatusMessage = "Image loading cancelled.";
                            });
                        }

                        return loadedImages;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Image loading error: {ex.Message}");
                        return new List<ImageInfo>();
                    }
                });

                // Show modal progress dialog
                // Note: This will block the UI thread until the dialog is closed
                progressDialog.ShowDialog();

                // When dialog closes (possibly due to cancel button), ensure loading operation is cancelled
                if (progressDialog.IsCancelled && !_imageLoadingCts.IsCancellationRequested)
                {
                    _imageLoadingCts.Cancel();
                }

                // Wait for loading task to complete
                var result = await loadingTask;

                // If operation was cancelled, clean up resources if necessary
                if (_imageLoadingCts.IsCancellationRequested)
                {
                    foreach (var img in result)
                    {
                        img.Dispose();
                    }

                    StatusMessage = "Image loading cancelled.";
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
            if (folder == null) return;

            var result = MessageBox.Show(
                $"Are you sure you want to delete the folder:\n\n{folder.FolderPath}?",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            try
            {
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

                // Invalidate path cache after deletion
                PathService.InvalidatePathCache(folderPath, true);

                // Remove from _allLoadedFolders
                RemoveFolderAndSubfoldersFromAllLoaded(folderPath);

                // Remove from search results
                RemoveFolderAndSubfoldersFromSearchResults(folderPath);

                // Remove from tree structure - using path for more reliable matching
                RemoveFolderFromTree(folder);

                // Select parent folder if available
                if (parentFolder != null)
                {
                    SelectedFolder = parentFolder;
                    await LoadImagesForSelectedFolderAsync();
                }
                StatusMessage = $"Successfully deleted folder '{folder.Name}'";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to delete folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // New improved method to remove from tree
        public void RemoveFolderFromTree(FolderInfo folder)
        {
            if (folder == null) return;

            // Case 1: Remove from root folders if it's a root folder
            if (RootFolders.Contains(folder))
            {
                RootFolders.Remove(folder);
                return;
            }

            // Case 2: Remove from parent's children if parent is available
            if (folder.Parent != null && folder.Parent.Children != null)
            {
                if (folder.Parent.Children.Contains(folder))
                {
                    folder.Parent.Children.Remove(folder);
                    return;
                }
            }

            // Case 3: Fallback - recursively search through the entire tree
            // by path comparison to handle cases where object references differ
            RecursiveRemoveFolderByPath(RootFolders, folder.FolderPath);
        }

        private bool RemoveFolderRecursive(ObservableCollection<FolderInfo> folders, FolderInfo target)
        {
            if (folders == null) return false;

            // First, check if the target is in this collection
            if (folders.Contains(target))
            {
                folders.Remove(target);
                return true;
            }

            // If not, search through all children
            foreach (var folder in folders.ToList()) // Use ToList to avoid collection modification issues
            {
                if (folder?.Children != null)
                {
                    if (RemoveFolderRecursive(folder.Children, target))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool RecursiveRemoveFolderByPath(ObservableCollection<FolderInfo> folders, string targetPath)
        {
            if (folders == null) return false;

            // Normalize target path
            string normalizedTarget = PathService.NormalizePath(targetPath);
            bool removed = false;  // Track if we found any matches

            // Use ToList() to avoid collection modification exceptions during enumeration
            foreach (var folder in folders.ToList())
            {
                // Check if this is the folder we want to remove using PathService
                if (folder != null && folder.FolderPath != null &&
                    PathService.PathsEqual(folder.FolderPath, normalizedTarget))
                {
                    folders.Remove(folder);
                    removed = true;
                    // Continue searching instead of returning immediately
                }

                // Check children
                if (folder?.Children != null)
                {
                    // Combine result with recursive call
                    bool childRemoved = RecursiveRemoveFolderByPath(folder.Children, normalizedTarget);
                    removed = removed || childRemoved;
                }
            }

            return removed;
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

        // Updated PerformSearch method with proper thread synchronization
        public async Task PerformSearch()
        {
            try
            {
                // Clear search results on UI thread
                await Application.Current.Dispatcher.InvokeAsync(() => {
                    SearchResultFolders.Clear();

                    // Set status in the UI (assuming you have a status text property)
                    StatusMessage = "Searching...";
                    IsSearching = true;  // Add this property to control UI elements visibility
                });

                if (string.IsNullOrWhiteSpace(SearchText))
                {
                    await Application.Current.Dispatcher.InvokeAsync(() => {
                        StatusMessage = "Ready";
                        IsSearching = false;
                    });
                    return;
                }

                // Use a simple Task.Run to perform the search operation
                await Task.Run(async () =>
                {
                    try
                    {
                        // Update UI status
                        await Application.Current.Dispatcher.InvokeAsync(() => {
                            StatusMessage = "Reading folder data...";
                        });

                        // Load latest data from file system
                        _allLoadedFolders = await _folderManager.LoadFoldersRecursivelyAsync(
                            AppSettings.Instance.DefaultRootDirectory);

                        // Update tag cloud with fresh data
                        await Application.Current.Dispatcher.InvokeAsync(async () => {
                            await UpdateTagCloudAsync();
                            StatusMessage = "Ready";
                        });

                        // Find matching folders
                        var matchingFolders = ParseSearchCriteria();

                        // Update UI with search results
                        await Application.Current.Dispatcher.InvokeAsync(() => {
                            foreach (var folder in matchingFolders)
                            {
                                SearchResultFolders.Add(folder);
                            }

                            StatusMessage = $"Found {matchingFolders.Count} matching folders";
                            IsSearching = false;
                        });

                        Debug.WriteLine($"Search completed. Found {matchingFolders.Count} matching folders");
                    }
                    catch (Exception ex)
                    {
                        // Handle exceptions in the background task
                        Debug.WriteLine($"Search error: {ex.Message}");

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
                Debug.WriteLine($"Search error in main thread: {ex.Message}");

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

            try
            {
                // Create progress dialog
                var progressDialog = new Views.ProgressDialog(
                    "Deleting Folders",
                    $"Deleting {folders.Count} folders...");

                // Set progress dialog owner
                progressDialog.Owner = Application.Current.MainWindow;

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

        private FolderInfo FindFolderReferenceInTree(ObservableCollection<FolderInfo> folders, string targetPath)
        {
            if (folders == null) return null;

            foreach (var folder in folders)
            {
                if (folder.FolderPath.Equals(targetPath, StringComparison.OrdinalIgnoreCase))
                    return folder;

                if (folder.Children != null && folder.Children.Count > 0)
                {
                    var found = FindFolderReferenceInTree(folder.Children, targetPath);
                    if (found != null)
                        return found;
                }
            }

            return null; 
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
                                // Move directory
                                Directory.Move(sourcePath, destinationPath);

                                // Update progress
                                progressDialog.UpdateProgress(0.6, "Updating UI...");

                                // Remove from UI (on UI thread)
                                await Application.Current.Dispatcher.InvokeAsync(() =>
                                {
                                    RemoveFolderFromTree(sourceFolder);
                                    RemoveFolderAndSubfoldersFromSearchResults(sourcePath);
                                    RemoveFolderAndSubfoldersFromAllLoaded(sourcePath);
                                    
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


        public async Task CreateNewFolder(FolderInfo parentFolder)
        {
            if (parentFolder == null) return;

            // Show input dialog
            string folderName = Microsoft.VisualBasic.Interaction.InputBox(
                "Enter new folder name:",
                "New Folder",
                "New Folder");

            // If user cancelled or name is empty, do nothing
            if (string.IsNullOrWhiteSpace(folderName))
                return;

            // Check for invalid characters in folder name
            if (folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                MessageBox.Show("The folder name contains invalid characters.",
                    "Invalid Name", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Construct new path
            string normalizedPath = PathService.NormalizePath(parentFolder.FolderPath);
            string newPath = Path.Combine(normalizedPath, folderName);

            // Check if destination already exists
            if (PathService.DirectoryExists(newPath))
            {
                MessageBox.Show($"A folder named '{folderName}' already exists in this location.",
                    "Cannot Create Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                // Create the directory
                Directory.CreateDirectory(newPath);

                // Refresh the parent node to show the new folder
                parentFolder.LoadChildren();

                // Start watching the parent folder to detect changes
                _folderManager.WatchFolder(parentFolder);
                // Add this: Notify ShellTreeView to refresh
                if (Application.Current.MainWindow is MainWindow mainWindow &&
                    mainWindow.ShellTreeViewControl != null)
                {
                    // Call RefreshTree to update the visual tree
                    mainWindow.ShellTreeViewControl.RefreshTree();
                }

                // Find the newly created folder in the parent's children
                FolderInfo newFolder = parentFolder.Children.FirstOrDefault(f => f.FolderPath == newPath);

                if (newFolder != null)
                {
                    // Add to allLoadedFolders for search functionality
                    _allLoadedFolders.Add(newFolder);

                    // Watch the new folder
                    _folderManager.WatchFolder(newFolder);
                }

                // Update tag cloud
                await UpdateTagCloudAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error creating folder: {ex.Message}",
                    "Create Folder Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public async Task RenameFolder(FolderInfo folder)
        {
            if (folder == null) return;

            // Get the old path
            string oldPath = PathService.NormalizePath(folder.FolderPath);
            string oldName = Path.GetFileName(oldPath);
            string parentPath = Path.GetDirectoryName(oldPath);

            // Show input dialog
            string newName = Microsoft.VisualBasic.Interaction.InputBox(
                "Enter new folder name:",
                "Rename Folder",
                oldName);

            // If user cancelled or name is empty or the same as before, do nothing
            if (string.IsNullOrWhiteSpace(newName) || newName == oldName)
                return;

            // Check for invalid characters in folder name
            if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                MessageBox.Show("The folder name contains invalid characters.",
                    "Invalid Name", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Construct new path
            string newPath = Path.Combine(parentPath, newName);

            // Check if destination already exists
            if (PathService.DirectoryExists(newPath))
            {
                MessageBox.Show($"A folder named '{newName}' already exists in this location.",
                    "Cannot Rename", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                // Stop watching the folder before renaming
                _folderManager.UnwatchFolder(oldPath);

                // Rename the directory
                Directory.Move(oldPath, newPath);

                // Update the folder path in the data model
                folder.FolderPath = newPath;

                // Update in all loaded folders to maintain references
                foreach (var f in _allLoadedFolders)
                {
                    if (f.FolderPath == oldPath)
                    {
                        f.FolderPath = newPath;
                    }
                    else if (PathService.IsPathWithin(oldPath, f.FolderPath))
                    {
                        f.FolderPath = newPath + f.FolderPath.Substring(oldPath.Length);
                    }
                }

                // Refresh the parent node in the tree view
                if (folder.Parent != null)
                {
                    folder.Parent.LoadChildren();
                }
                else
                {
                    // If it's a root folder, refresh the whole tree
                    await LoadDirectoryAsync(newPath);
                }

                // Force refresh by triggering the property changed event for FolderPath
                // This will indirectly update the Name property
                string currentPath = folder.FolderPath;
                folder.FolderPath = currentPath;

                // Start watching the renamed folder
                _folderManager.WatchFolder(folder);

                // Update tag cloud
                await UpdateTagCloudAsync();

                // Update search results if needed
                if (!string.IsNullOrEmpty(SearchText))
                {
                    await PerformSearch();
                }
                if (Application.Current.MainWindow is MainWindow mainWindow &&
                    mainWindow.ShellTreeViewControl != null)
                {
                    mainWindow.ShellTreeViewControl.UpdatePathMapping(oldPath, newPath);
                }
                StatusMessage = $"'{oldName}' is renamed to '{newName}'.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error renaming folder: {ex.Message}",
                    "Rename Failed", MessageBoxButton.OK, MessageBoxImage.Error);
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
                                    // Check if destination already exists
                                    

                                    // Temporarily disable FileSystemWatcher
                                    _folderManager.UnwatchFolder(sourcePath);
                                    _folderManager.UnwatchFolder(targetPath);

                                    // Move directory
                                    Directory.Move(sourcePath, destinationPath);

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
        /// Parses search criteria and finds matching folders
        /// </summary>
        /// <returns>List of folders matching the search criteria</returns>
        private List<FolderInfo> ParseSearchCriteria()
        {
            var matchingFolders = new List<FolderInfo>();

            // Trim the input and normalize whitespace
            var input = SearchText?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(input))
            {
                Debug.WriteLine("Search text is empty");
                return matchingFolders;
            }

            // Split by '&' (AND operator)
            var andGroups = input.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries)
                                 .Select(part => part.Trim())
                                 .Where(part => !string.IsNullOrWhiteSpace(part))
                                 .ToList();

            if (andGroups.Count == 0)
            {
                Debug.WriteLine("No valid search conditions found");
                return matchingFolders;
            }

            // Create predicates for each AND group
            var andPredicates = new List<Func<FolderInfo, bool>>();

            foreach (var group in andGroups)
            {
                // Create OR predicates for this group
                var orPredicates = new List<Func<FolderInfo, bool>>();
                bool hasValidCondition = false;

                // Extract tag search terms using TagHelper
                var tagSearchTerms = TagHelper.ParseTagSearchTerms(group);

                if (tagSearchTerms.Any())
                {
                    // Create and add tag search predicate
                    var tagPredicate = TagHelper.CreateTagSearchPredicate(tagSearchTerms);
                    orPredicates.Add(folder => tagPredicate(folder.Tags));

                    hasValidCondition = true;
                    Debug.WriteLine($"Added tag search conditions: {string.Join(", ", tagSearchTerms)}");
                }

                // Process rating search conditions
                var ratingConditions = ExtractRatingConditions(group);

                foreach (var ratingCondition in ratingConditions)
                {
                    orPredicates.Add(ratingCondition);
                    hasValidCondition = true;
                }

                // Process name/path search terms
                var textSearchTerms = ExtractTextSearchTerms(group);

                if (textSearchTerms.Any())
                {
                    orPredicates.Add(folder =>
                        textSearchTerms.Any(term =>
                            folder.Name.ToLowerInvariant().Contains(term) ||
                            folder.FolderPath.ToLowerInvariant().Contains(term)));

                    hasValidCondition = true;
                    Debug.WriteLine($"Added text search conditions: {string.Join(", ", textSearchTerms)}");
                }

                // If this group has valid conditions, add it as an AND predicate
                if (hasValidCondition && orPredicates.Count > 0)
                {
                    // Create a single predicate that combines all OR conditions
                    andPredicates.Add(folder => orPredicates.Any(p => p(folder)));
                }
            }

            // If there are no valid predicates, return empty results
            if (andPredicates.Count == 0)
            {
                Debug.WriteLine("No valid search predicates found");
                return matchingFolders;
            }

            // Apply all AND predicates to find matching folders
            foreach (var folder in _allLoadedFolders)
            {
                bool matches = andPredicates.All(predicate => predicate(folder));
                if (matches)
                {
                    matchingFolders.Add(folder);
                }
            }

            Debug.WriteLine($"Search completed, found {matchingFolders.Count} matching folders");
            return matchingFolders;
        }

        /// <summary>
        /// Extracts rating conditions from a search group
        /// </summary>
        private List<Func<FolderInfo, bool>> ExtractRatingConditions(string searchGroup)
        {
            var ratingPredicates = new List<Func<FolderInfo, bool>>();

            var ratingTerms = searchGroup.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(term => term.StartsWith("*"))
                .ToList();

            foreach (var term in ratingTerms)
            {
                string ratingPattern = term.Substring(1).Trim();

                // Check if pattern is valid (*[operator][value])
                if (ratingPattern.StartsWith(">=") || ratingPattern.StartsWith("<=") ||
                    ratingPattern.StartsWith("=") || ratingPattern.StartsWith(">") ||
                    ratingPattern.StartsWith("<"))
                {
                    string comparisonOperator;
                    string valueStr;

                    if (ratingPattern.StartsWith(">=") || ratingPattern.StartsWith("<="))
                    {
                        comparisonOperator = ratingPattern.Substring(0, 2);
                        valueStr = ratingPattern.Substring(2).Trim();
                    }
                    else
                    {
                        comparisonOperator = ratingPattern.Substring(0, 1);
                        valueStr = ratingPattern.Substring(1).Trim();
                    }

                    if (int.TryParse(valueStr, out int value) && value >= 0 && value <= 5)
                    {
                        switch (comparisonOperator)
                        {
                            case ">=":
                                ratingPredicates.Add(folder => folder.Rating >= value);
                                Debug.WriteLine($"Added rating condition: >= {value}");
                                break;
                            case "<=":
                                ratingPredicates.Add(folder => folder.Rating <= value);
                                Debug.WriteLine($"Added rating condition: <= {value}");
                                break;
                            case "=":
                                ratingPredicates.Add(folder => folder.Rating == value);
                                Debug.WriteLine($"Added rating condition: = {value}");
                                break;
                            case ">":
                                ratingPredicates.Add(folder => folder.Rating > value);
                                Debug.WriteLine($"Added rating condition: > {value}");
                                break;
                            case "<":
                                ratingPredicates.Add(folder => folder.Rating < value);
                                Debug.WriteLine($"Added rating condition: < {value}");
                                break;
                        }
                    }
                    else
                    {
                        Debug.WriteLine($"Invalid rating value: {valueStr}");
                    }
                }
            }

            return ratingPredicates;
        }

        /// <summary>
        /// Extracts text search terms from a search group
        /// </summary>
        private List<string> ExtractTextSearchTerms(string searchGroup)
        {
            // Get terms that don't start with # or *
            return searchGroup.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(term => !term.StartsWith("#") && !term.StartsWith("*") && !string.IsNullOrWhiteSpace(term))
                .Select(term => term.ToLowerInvariant())
                .ToList();
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