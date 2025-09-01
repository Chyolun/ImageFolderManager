using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using ImageFolderManager.Models;
using ImageFolderManager.Services;
using ImageFolderManager.Views;
using MahApps.Metro.Controls.Dialogs;

namespace ImageFolderManager.ViewModels
{
    /// <summary>
    /// Handles tag and rating operations for folders
    /// </summary>
    public class TagManagementViewModel : ViewModelBase
    {
        #region Properties

        private FolderTagService _tagService;
       
        private readonly TagCloudViewModel _tagCloud;
        private ObservableCollection<string> _folderTags = new ObservableCollection<string>();
        public ObservableCollection<string> FolderTags
        {
            get => _folderTags;
            set => SetProperty(ref _folderTags, value);
        }

        private int _rating;
        public int Rating
        {
            get => _rating;
            set
            {
                if (SetProperty(ref _rating, value))
                {
                    UpdateStars();
                }
            }
        }

        private string _tagInputText;
        public string TagInputText
        {
            get => _tagInputText;
            set => SetProperty(ref _tagInputText, value);
        }

        private ObservableCollection<StarModel> _stars = new ObservableCollection<StarModel>();
        public ObservableCollection<StarModel> Stars
        {
            get => _stars;
            private set => SetProperty(ref _stars, value);
        }
        private ObservableCollection<TagDisplayInfo> _tagDisplayItems = new ObservableCollection<TagDisplayInfo>();
        public ObservableCollection<TagDisplayInfo> TagDisplayItems
        {
            get => _tagDisplayItems;
            private set => SetProperty(ref _tagDisplayItems, value);
        }

        public string DisplayTagLine
        {
            get
            {
                if (FolderTags == null || FolderTags.Count == 0)
                    return "No tags";

                // Show only tag names without categories for cleaner display
                var displayTags = FolderTags.Select(tag =>
                {
                    var parsed = TagHelper.ParseTagWithCategory(tag);
                    return $"#{parsed?.TagName ?? tag}";
                });

                return string.Join(" ", displayTags);
            }
        }

        public TagCloudViewModel TagCloud => _tagCloud;

        private TagCategoryService _categoryService;

        private FolderInfo _currentFolder;
        public FolderInfo CurrentFolder
        {
            get => _currentFolder;
            private set => SetProperty(ref _currentFolder, value);
        }

        #endregion

        #region Commands

        public IAsyncRelayCommand SaveTagsCommand { get; }
        public ICommand SetRatingCommand { get; }
        public ICommand EditTagsCommand { get; }
        public IAsyncRelayCommand TagsCloudCommand { get; } 

        #endregion

        #region Events

        public event EventHandler<string> StatusMessageChanged;
        public event EventHandler<TagsUpdatedEventArgs> TagsUpdated;
        public event EventHandler TagCloudRequested;

        #endregion

        public TagManagementViewModel(FolderTagService tagService, TagCloudViewModel tagCloud)
        {
            _tagService = tagService ?? throw new ArgumentNullException(nameof(tagService));
            _tagCloud = tagCloud ?? throw new ArgumentNullException(nameof(tagCloud));

            // Initialize commands
            SaveTagsCommand = new AsyncRelayCommand(SaveFolderTagsAsync);
            SetRatingCommand = new RelayCommand<int>(SaveRatingImmediately);
            EditTagsCommand = new RelayCommand(EditTags);
            TagsCloudCommand = new AsyncRelayCommand(ShowTagCloud);
            _categoryService = _tagService.CategoryService;

            // Initialize stars
            UpdateStars();
        }

        #region Public Methods

        public async Task LoadFolderMetadataAsync(FolderInfo folder)
        {
            if (folder == null)
            {
                System.Diagnostics.Debug.WriteLine("LoadFolderMetadataAsync called with null folder");
                return;
            }

            CurrentFolder = folder;
            System.Diagnostics.Debug.WriteLine($"Loading metadata for folder: {folder.FolderPath}");

            try
            {
                // Load tags and rating from file
                int rating = await _tagService.GetRatingForFolderAsync(folder.FolderPath);
                var tags = await _tagService.GetTagsForFolderAsync(folder.FolderPath);
                var tagsWithCategories = await _tagService.GetTagsWithCategoriesForFolderAsync(folder.FolderPath);

                System.Diagnostics.Debug.WriteLine($"Loaded {tags.Count} tags and {tagsWithCategories.Count} categorized tags for folder: {folder.Name}");

                // Update UI on the dispatcher thread
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Rating = rating;

                    // Update tags collection
                    var uniqueTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var tag in tags.Where(t => !string.IsNullOrEmpty(t)))
                    {
                        uniqueTags.Add(tag.Trim());
                    }

                    FolderTags.Clear();
                    foreach (var tag in uniqueTags)
                    {
                        FolderTags.Add(tag);
                    }

                    // Update tag display items
                    TagDisplayItems.Clear();
                    foreach (var tagWithCategory in tagsWithCategories)
                    {
                        TagDisplayItems.Add(new TagDisplayInfo(tagWithCategory.TagName, tagWithCategory.Category));
                    }

                    // Explicitly notify that these properties changed
                    OnPropertyChanged(nameof(TagDisplayItems));
                    OnPropertyChanged(nameof(FolderTags));
                    OnPropertyChanged(nameof(DisplayTagLine));

                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading folder metadata: {ex.Message}");
                System.Diagnostics.Debug.WriteLine(ex.StackTrace);
            }
        }


        public async Task BatchUpdateTagsAsync(List<FolderInfo> folders)
        {
            if (folders == null || folders.Count <= 1)
                return;

            try
            {
                // Find common tags
                var commonTags = TagHelper.FindCommonTags(
                    folders.Select(f => f.Tags)).ToList();

                // Show batch tags dialog
                var dialog = new BatchTagsDialog(folders.Count, commonTags);
                dialog.Owner = Application.Current.MainWindow;

                if (dialog.ShowDialog() == true)
                {
                    var tagsToAdd = dialog.TagsToAdd;
                    var tagsToRemove = dialog.TagsToRemove;

                    if (tagsToAdd.Count == 0 && tagsToRemove.Count == 0)
                        return;

                    await ProcessBatchTagUpdate(folders, tagsToAdd, tagsToRemove);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during batch tag operation: {ex.Message}",
                    "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public async Task RenameTagAsync(string oldTag, string newTag, IEnumerable<string> folderPaths = null)
        {
            if (string.IsNullOrWhiteSpace(oldTag) || string.IsNullOrWhiteSpace(newTag) || oldTag == newTag)
                return;

            try
            {
                // Use provided folder paths or get all folder paths
                folderPaths = folderPaths ?? GetAllFolderPaths();

                if (folderPaths == null || !folderPaths.Any())
                {
                    MessageBox.Show(
                        "No folders available to search for tags. Please ensure folders are indexed.",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                // Extract category from new tag if it has category format
                string category = null;
                string tagNameOnly = newTag;

                if (newTag.Contains("::"))
                {
                    var parts = newTag.Split(new[] { "::" }, 2, StringSplitOptions.None);
                    if (parts.Length == 2)
                    {
                        category = parts[0];
                        tagNameOnly = parts[1];
                    }
                }

                // Perform the tag renaming operation - pass the category separately to ensure it's preserved
                await _tagService.RenameTagAsync(oldTag, tagNameOnly, folderPaths, category);

                // Update tag cloud
                _tagCloud.InvalidateCache();
                await UpdateTagCloudAsync();

                // If current folder has the renamed tag, refresh
                if (CurrentFolder != null &&
                    CurrentFolder.Tags.Contains(oldTag, StringComparer.OrdinalIgnoreCase))
                {
                    await LoadFolderMetadataAsync(CurrentFolder);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Error renaming tag: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }


        public async Task UpdateTagCloudAsync(IEnumerable<FolderInfo> allFolders = null)
        {
            if (allFolders != null)
            {
                await _tagCloud.UpdateTagCloudAsync(allFolders);
            }
        }

        #endregion

        #region Private Methods

        public async Task SaveFolderTagsAsync()
        {
            if (CurrentFolder == null) return;

            try
            {
                var oldTags = new List<TagWithCategory>();
                foreach (var displayItem in TagDisplayItems)
                {
                    oldTags.Add(new TagWithCategory
                    {
                        TagName = displayItem.TagName,
                        Category = displayItem.Category
                    });
                }

                bool isTagInputEmpty = string.IsNullOrWhiteSpace(TagInputText) ||
                                       TagInputText.Replace("#", "").Trim().Length == 0;

                if (!isTagInputEmpty)
                {
                    // Parse tags from input
                    var parsedTags = TagHelper.ParseTagsWithCategories(TagInputText).ToList();

                    // Create a new list to store tags with correct categories
                    var tagsWithCorrectCategories = new List<TagWithCategory>();

                    // For each parsed tag, check if it already exists in the tag cloud
                    foreach (var tag in parsedTags)
                    {
                        // If the tag already has a non-default category, keep it
                        if (!string.IsNullOrEmpty(tag.Category) && tag.Category != "Uncategorized")
                        {
                            tagsWithCorrectCategories.Add(tag);
                            continue;
                        }

                        // Otherwise, check if this tag exists in the tag cloud with a category
                        string existingCategory = _categoryService.GetTagCategory(tag.TagName);

                        // If it has a specific category in the tag cloud, use that
                        if (!string.IsNullOrEmpty(existingCategory) && existingCategory != "Uncategorized")
                        {
                            tagsWithCorrectCategories.Add(new TagWithCategory
                            {
                                TagName = tag.TagName,
                                Category = existingCategory
                            });
                        }
                        else
                        {
                            // Otherwise, keep the original (which is likely "Uncategorized")
                            tagsWithCorrectCategories.Add(tag);
                        }
                    }

                    // Update UI collections with the corrected tags
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        // Update FolderTags (for backward compatibility)
                        FolderTags.Clear();
                        foreach (var tag in tagsWithCorrectCategories)
                        {
                            FolderTags.Add(tag.TagName);
                        }

                        // Update TagDisplayItems
                        TagDisplayItems.Clear();
                        foreach (var tag in tagsWithCorrectCategories)
                        {
                            TagDisplayItems.Add(new TagDisplayInfo(tag.TagName, tag.Category));
                        }

                        OnPropertyChanged(nameof(TagDisplayItems));
                        OnPropertyChanged(nameof(FolderTags));
                        OnPropertyChanged(nameof(DisplayTagLine));
                    });

                    // Save tags with categories to file
                    await _tagService.SetTagsAndRatingForFolderAsync(
                        CurrentFolder.FolderPath,
                        tagsWithCorrectCategories,
                        Rating
                    );
                }
                else
                {
                    // If input is empty, just preserve existing tags (if any)
                    return;
                }

                // Clear input
                TagInputText = string.Empty;

                // Update tag cloud
                _tagCloud.InvalidateCache();

                UpdateStatus(isTagInputEmpty
                    ? "No new tags provided. Existing tags preserved."
                    : "Tags updated successfully.");

                var args = new TagsUpdatedEventArgs(CurrentFolder, FolderTags, Rating);
                TagsUpdated?.Invoke(this, args);
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error saving tags: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Error in SaveFolderTagsAsync: {ex.Message}");
                System.Diagnostics.Debug.WriteLine(ex.StackTrace);
            }
        }


        private void SaveRatingImmediately(int rating)
        {
            if (CurrentFolder == null) return;

            Rating = rating;

            Task.Run(async () =>
            {
                try
                {
                    await _tagService.SetTagsAndRatingForFolderAsync(
                        CurrentFolder.FolderPath,
                        new List<string>(FolderTags),
                        Rating
                    );

                    // Use the constructor instead of object initializer
                    var args = new TagsUpdatedEventArgs(CurrentFolder, FolderTags, Rating);
                    TagsUpdated?.Invoke(this, args);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error saving rating: {ex.Message}");
                }
            });
        }

        private void EditTags()
        {
            if (CurrentFolder == null) return;

            if (TagDisplayItems.Count > 0)
            {
                // Create a string representation including categories where applicable
                var tagStrings = new List<string>();

                foreach (var tagDisplay in TagDisplayItems)
                {
                    // Only include category if it's not "Uncategorized"
                    if (tagDisplay.Category != "Uncategorized")
                    {
                        tagStrings.Add($"#{tagDisplay.Category}::{tagDisplay.TagName}");
                    }
                    else
                    {
                        tagStrings.Add($"#{tagDisplay.TagName}");
                    }
                }

                TagInputText = string.Join(" ", tagStrings);
                UpdateStatus("Tags loaded for editing. Click 'Update' to save changes.");
            }
            else
            {
                TagInputText = string.Empty;
                UpdateStatus("No existing tags. Add tags using the # symbol as prefix. For categories, use Category::TagName format.");
            }
        }

        public async Task DeleteTagFromAllFoldersAsync(string tagToDelete, IEnumerable<string> folderPaths)
        {
            await _tagService.DeleteTagFromAllFoldersAsync(tagToDelete, folderPaths);

            // Reload metadata for current folder if needed
            if (CurrentFolder != null && folderPaths.Any(p => p.Equals(CurrentFolder.FolderPath, StringComparison.OrdinalIgnoreCase)))
            {
                await LoadFolderMetadataAsync(CurrentFolder);

                // Raise event to notify of tags update with the current tags
                var args = new TagsUpdatedEventArgs(CurrentFolder, FolderTags, Rating);
                TagsUpdated?.Invoke(this, args);
            }
            else
            {
                // If current folder wasn't affected, still notify but with empty tags
                var args = new TagsUpdatedEventArgs(CurrentFolder); // Uses defaults for tags (empty list) and rating (0)
                TagsUpdated?.Invoke(this, args);
            }
        }

        private async Task ShowTagCloud() // Changed to async Task (no lambda wrapper needed)
        {
            TagCloudRequested?.Invoke(this, EventArgs.Empty);
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

        private async Task ProcessBatchTagUpdate(
            List<FolderInfo> folders,
            HashSet<string> tagsToAdd,
            HashSet<string> tagsToRemove)
        {
            var progressDialog = new Views.ProgressDialog(
                "Updating Tags",
                $"Updating tags for {folders.Count} folders...");

            progressDialog.Owner = Application.Current.MainWindow;

            using (var cts = new System.Threading.CancellationTokenSource())
            {
                progressDialog.CancelRequested += (s, e) =>
                {
                    cts.Cancel();
                    UpdateStatus("Tag update cancelled.");
                };

                var updateTask = Task.Run(async () =>
                {
                    try
                    {
                        int total = folders.Count;
                        int processed = 0;

                        foreach (var folder in folders)
                        {
                            if (cts.Token.IsCancellationRequested)
                                break;

                            try
                            {
                                double progress = (double)processed / total;
                                progressDialog.UpdateProgress(progress,
                                    $"Updating folder {processed + 1} of {total}: {folder.Name}");

                                // Get current tags
                                var currentTags = await _tagService.GetTagsForFolderAsync(folder.FolderPath);
                                var updatedTags = new List<string>(currentTags);

                                // Remove specified tags
                                if (tagsToRemove.Count > 0)
                                {
                                    updatedTags.RemoveAll(tag =>
                                        tagsToRemove.Contains(tag, StringComparer.OrdinalIgnoreCase));
                                }

                                // Add new tags
                                foreach (var tag in tagsToAdd)
                                {
                                    if (!updatedTags.Any(t =>
                                        string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
                                    {
                                        updatedTags.Add(tag);
                                    }
                                }

                                // Get current rating
                                int rating = await _tagService.GetRatingForFolderAsync(folder.FolderPath);

                                // Save updated tags
                                await _tagService.SetTagsAndRatingForFolderAsync(
                                    folder.FolderPath,
                                    updatedTags,
                                    rating);

                                // Update folder object
                                folder.Tags = new ObservableCollection<string>(updatedTags);

                                await Task.Delay(10, cts.Token);
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine(
                                    $"Error updating tags for folder {folder.FolderPath}: {ex.Message}");
                            }

                            processed++;
                        }

                        progressDialog.UpdateProgress(1.0, "Tag update completed");

                        // Update tag cloud
                        _tagCloud.InvalidateCache();

                        return true;
                    }
                    catch (OperationCanceledException)
                    {
                        return false;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error in batch tag update: {ex.Message}");
                        return false;
                    }
                }, cts.Token);

                progressDialog.ShowDialog();

                if (progressDialog.IsCancelled && !cts.IsCancellationRequested)
                {
                    cts.Cancel();
                }

                bool success = await updateTask;

                UpdateStatus(success && !cts.IsCancellationRequested
                    ? $"Successfully updated tags for {folders.Count} folders"
                    : "Tag update cancelled");
            }
        }

        private List<string> GetAllFolderPaths()
        {
            // Try to get folders from various sources
            if (Application.Current?.MainWindow?.DataContext is MainViewModel mainViewModel)
            {
                // Get from main view model if possible
                return mainViewModel.GetAllIndexedFolderPaths();
            }

            // Otherwise return folders from the current context if available
            return new List<string>();
        }
        public string GetTagInputTooltip()
        {
            return "Enter tags with optional categories using format: #tag #category::tag";
        }



        #endregion

        #region Helper Methods

        private void UpdateStatus(string message)
        {
            StatusMessageChanged?.Invoke(this, message);
        }

        private void OnTagsUpdated(TagsUpdatedEventArgs e)
        {
            TagsUpdated?.Invoke(this, e);
        }

        #endregion
    }
}