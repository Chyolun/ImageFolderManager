using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
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
        private readonly IDialogService _dialogService;
       
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
        private FolderOperationCoordinator _coordinator;
        private readonly object _metadataLoadSync = new object();
        private long _metadataLoadRequestId;

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

        public TagManagementViewModel(
            FolderTagService tagService,
            TagCloudViewModel tagCloud,
            FolderOperationCoordinator coordinator = null,
            IDialogService dialogService = null)
        {
            _tagService = tagService ?? throw new ArgumentNullException(nameof(tagService));
            _tagCloud = tagCloud ?? throw new ArgumentNullException(nameof(tagCloud));
            _coordinator = coordinator;
            _dialogService = dialogService ?? new WpfDialogService();
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

        internal long BeginMetadataLoadRequest(FolderInfo folder)
        {
            lock (_metadataLoadSync)
            {
                _metadataLoadRequestId++;
                CurrentFolder = folder;
                return _metadataLoadRequestId;
            }
        }

        internal bool IsMetadataLoadRequestCurrent(long requestId, FolderInfo folder)
        {
            lock (_metadataLoadSync)
            {
                if (folder == null)
                    return false;

                if (requestId != _metadataLoadRequestId)
                    return false;

                return CurrentFolder != null &&
                       PathService.PathsEqual(CurrentFolder.FolderPath, folder.FolderPath);
            }
        }

        public async Task LoadFolderMetadataAsync(FolderInfo folder, CancellationToken cancellationToken = default)
        {
            if (folder == null)
            {
                System.Diagnostics.Debug.WriteLine("LoadFolderMetadataAsync called with null folder");
                return;
            }

            long requestId = BeginMetadataLoadRequest(folder);
            System.Diagnostics.Debug.WriteLine($"Loading metadata for folder: {folder.FolderPath}");

            try
            {
                // Load tags and rating from file
                int rating = await _tagService.GetRatingForFolderAsync(folder.FolderPath);
                cancellationToken.ThrowIfCancellationRequested();

                var tagsWithCategoriesTask = _tagService.GetTagsWithCategoriesForFolderAsync(folder.FolderPath);
                await tagsWithCategoriesTask;

                var tagsWithCategories = tagsWithCategoriesTask.Result;
                cancellationToken.ThrowIfCancellationRequested();

                System.Diagnostics.Debug.WriteLine($"Loaded {tagsWithCategories.Count} categorized tags for folder: {folder.Name}");

                // Update UI on the dispatcher thread
                var dispatcher = System.Windows.Application.Current?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
                await dispatcher.InvokeAsync(() =>
                {
                    if (cancellationToken.IsCancellationRequested || !IsMetadataLoadRequestCurrent(requestId, folder))
                        return;

                    Rating = rating;

                    // Update tags collection
                    var uniqueTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var tag in tagsWithCategories.Where(t => t != null && !string.IsNullOrWhiteSpace(t.TagName)))
                    {
                        uniqueTags.Add(tag.ToString());
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

                    folder.Tags = new ObservableCollection<string>(ToSimpleTagNames(tagsWithCategories));
                    folder.CategorizedTags = new ObservableCollection<TagWithCategory>(
                        tagsWithCategories.Select(tag => new TagWithCategory
                        {
                            TagName = tag.TagName,
                            Category = tag.Category
                        }));
                    folder.Rating = rating;

                    // Explicitly notify that these properties changed
                    OnPropertyChanged(nameof(TagDisplayItems));
                    OnPropertyChanged(nameof(FolderTags));
                    OnPropertyChanged(nameof(DisplayTagLine));

                }, System.Windows.Threading.DispatcherPriority.Background, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine($"Load folder metadata canceled for: {folder.FolderPath}");
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
                    folders.Select(GetStoredTagIdentifiers)).ToList();

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
                _dialogService.Show($"Error during batch tag operation: {ex.Message}",
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
                var targetFolderPaths = ToFolderPathList(folderPaths ?? GetAllFolderPaths());

                if (targetFolderPaths.Count == 0)
                {
                    _dialogService.Show(
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
                await _tagService.RenameTagAsync(oldTag, tagNameOnly, targetFolderPaths, category);

                // Update tag cloud
                _tagCloud.InvalidateCache();
                await RefreshTagCloudFromFolderPathsAsync(targetFolderPaths);

                // If current folder has the renamed tag, refresh
                if (CurrentFolder != null &&
                    (CurrentFolder.CategorizedTags ?? Enumerable.Empty<TagWithCategory>()).Any(tag =>
                        tag != null &&
                        tag.TagName.Equals(oldTag, StringComparison.OrdinalIgnoreCase)))
                {
                    await LoadFolderMetadataAsync(CurrentFolder);
                }
            }
            catch (Exception ex)
            {
                _dialogService.Show(
                    $"Error renaming tag: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        public async Task MoveTagToCategoryAsync(
            string tagName,
            string oldCategory,
            string newCategory,
            IEnumerable<string> folderPaths = null)
        {
            if (string.IsNullOrWhiteSpace(tagName) || string.IsNullOrWhiteSpace(newCategory))
                return;

            try
            {
                var targetFolderPaths = ToFolderPathList(folderPaths ?? GetAllFolderPaths());
                await _tagService.MoveTagToCategoryAsync(tagName, oldCategory, newCategory, targetFolderPaths);

                _tagCloud.InvalidateCache();
                await RefreshTagCloudFromFolderPathsAsync(targetFolderPaths);

                if (CurrentFolder != null)
                {
                    await LoadFolderMetadataAsync(CurrentFolder);
                }
            }
            catch (Exception ex)
            {
                _dialogService.Show(
                    $"Error moving tag to category: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        public async Task RenameCategoryAsync(
            string oldCategory,
            string newCategory,
            IEnumerable<string> folderPaths = null)
        {
            if (string.IsNullOrWhiteSpace(oldCategory) || string.IsNullOrWhiteSpace(newCategory))
                return;

            try
            {
                var targetFolderPaths = ToFolderPathList(folderPaths ?? GetAllFolderPaths());
                await _tagService.RenameCategoryAsync(oldCategory, newCategory, targetFolderPaths);

                _tagCloud.InvalidateCache();
                await RefreshTagCloudFromFolderPathsAsync(targetFolderPaths);

                if (CurrentFolder != null)
                {
                    await LoadFolderMetadataAsync(CurrentFolder);
                }
            }
            catch (Exception ex)
            {
                _dialogService.Show(
                    $"Error renaming category: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        public async Task DeleteCategoryAsync(string categoryName, IEnumerable<string> folderPaths = null)
        {
            if (string.IsNullOrWhiteSpace(categoryName) ||
                categoryName.Equals("Uncategorized", StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                var targetFolderPaths = ToFolderPathList(folderPaths ?? GetAllFolderPaths());
                await _tagService.DeleteCategoryAsync(categoryName, targetFolderPaths);

                _tagCloud.InvalidateCache();
                await RefreshTagCloudFromFolderPathsAsync(targetFolderPaths);

                if (CurrentFolder != null)
                {
                    await LoadFolderMetadataAsync(CurrentFolder);
                }
            }
            catch (Exception ex)
            {
                _dialogService.Show(
                    $"Error deleting category: {ex.Message}",
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
                return;
            }

            await RefreshTagCloudFromFolderPathsAsync(GetAllFolderPaths());
        }

        #endregion

        #region Private Methods

        private async Task RefreshTagCloudFromFolderPathsAsync(IEnumerable<string> folderPaths)
        {
            var folderPathList = ToFolderPathList(folderPaths);
            if (folderPathList.Count == 0)
                return;

            var folders = new List<FolderInfo>();
            foreach (var folderPath in folderPathList)
            {
                var tagsWithCategories = await _tagService.GetTagsWithCategoriesForFolderAsync(folderPath);
                int rating = await _tagService.GetRatingForFolderAsync(folderPath);

                var validTags = (tagsWithCategories ?? new List<TagWithCategory>())
                    .Where(tag => tag != null && !string.IsNullOrWhiteSpace(tag.TagName))
                    .Select(tag => new TagWithCategory
                    {
                        TagName = tag.TagName,
                        Category = tag.Category
                    })
                    .ToList();

                folders.Add(new FolderInfo(folderPath)
                {
                    Tags = new ObservableCollection<string>(
                        validTags
                            .Select(tag => tag.TagName)
                            .Distinct(StringComparer.OrdinalIgnoreCase)),
                    CategorizedTags = new ObservableCollection<TagWithCategory>(validTags),
                    Rating = rating
                });
            }

            await _tagCloud.UpdateTagCloudAsync(folders);
        }

        private static List<string> ToFolderPathList(IEnumerable<string> folderPaths)
        {
            return (folderPaths ?? Enumerable.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<TagWithCategory> ParseStoredTags(IEnumerable<string> tags)
        {
            return (tags ?? Enumerable.Empty<string>())
                .Select(tag => TagHelper.ParseTagWithCategory(tag))
                .Where(tag => tag != null && !string.IsNullOrWhiteSpace(tag.TagName))
                .ToList();
        }

        private static List<string> ToSimpleTagNames(IEnumerable<TagWithCategory> tags)
        {
            return (tags ?? Enumerable.Empty<TagWithCategory>())
                .Where(tag => tag != null && !string.IsNullOrWhiteSpace(tag.TagName))
                .Select(tag => tag.TagName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<string> ToStoredTagIdentifiers(IEnumerable<TagWithCategory> tags)
        {
            return (tags ?? Enumerable.Empty<TagWithCategory>())
                .Where(tag => tag != null && !string.IsNullOrWhiteSpace(tag.TagName))
                .Select(tag => tag.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<string> GetStoredTagIdentifiers(FolderInfo folder)
        {
            if (folder?.CategorizedTags != null && folder.CategorizedTags.Count > 0)
                return ToStoredTagIdentifiers(folder.CategorizedTags);

            return folder?.Tags?
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
                ?? new List<string>();
        }

        private List<string> ResolveInputTagsForCurrentFolder(string tagsInput)
        {
            var parsedTags = TagHelper.ParseTagsWithCategories(tagsInput).ToList();
            if (parsedTags.Count == 0)
                return new List<string>();

            var existingCategorizedTags = CurrentFolder?.CategorizedTags?
                .Where(tag => tag != null && !string.IsNullOrWhiteSpace(tag.TagName))
                .ToList()
                ?? new List<TagWithCategory>();

            var resolvedTags = parsedTags.Select(parsedTag =>
            {
                if (parsedTag == null || string.IsNullOrWhiteSpace(parsedTag.TagName))
                    return null;

                bool isUncategorized = string.Equals(
                    parsedTag.Category,
                    "Uncategorized",
                    StringComparison.OrdinalIgnoreCase);

                if (!isUncategorized)
                    return parsedTag;

                string existingCategory = ResolveExistingCategoryForTag(parsedTag.TagName, existingCategorizedTags);
                if (!string.IsNullOrWhiteSpace(existingCategory))
                {
                    return new TagWithCategory
                    {
                        TagName = parsedTag.TagName,
                        Category = existingCategory
                    };
                }

                return parsedTag;
            });

            return ToStoredTagIdentifiers(resolvedTags);
        }

        private string ResolveExistingCategoryForTag(
            string tagName,
            IEnumerable<TagWithCategory> currentFolderTags)
        {
            if (string.IsNullOrWhiteSpace(tagName))
                return null;

            string mappedCategory = _categoryService.GetTagCategory(tagName);
            if (!IsDefaultCategory(mappedCategory))
                return mappedCategory;

            var matchedCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var existing in currentFolderTags ?? Enumerable.Empty<TagWithCategory>())
            {
                if (existing == null ||
                    string.IsNullOrWhiteSpace(existing.TagName) ||
                    string.IsNullOrWhiteSpace(existing.Category) ||
                    IsDefaultCategory(existing.Category) ||
                    !existing.TagName.Equals(tagName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                matchedCategories.Add(existing.Category);
            }

            foreach (var category in _tagCloud.Categories ?? Enumerable.Empty<TagCategory>())
            {
                if (category == null || IsDefaultCategory(category.Name))
                    continue;

                bool containsTag = _tagCloud.GetTagsInCategory(category.Name)
                    .Any(tag => tag.Tag.Equals(tagName, StringComparison.OrdinalIgnoreCase));

                if (containsTag)
                {
                    matchedCategories.Add(category.Name);
                }
            }

            return matchedCategories.Count == 1 ? matchedCategories.First() : null;
        }

        private static bool IsDefaultCategory(string category)
            => string.IsNullOrWhiteSpace(category) ||
               category.Equals("Uncategorized", StringComparison.OrdinalIgnoreCase);

        private static bool MatchesTagOperation(TagWithCategory tag, string tagExpression)
        {
            if (tag == null || string.IsNullOrWhiteSpace(tag.TagName) || string.IsNullOrWhiteSpace(tagExpression))
                return false;

            bool categorySpecified = tagExpression.Contains("::", StringComparison.Ordinal);
            var parsed = TagHelper.ParseTagWithCategory(tagExpression);
            if (parsed == null || string.IsNullOrWhiteSpace(parsed.TagName))
                return false;

            if (!tag.TagName.Equals(parsed.TagName, StringComparison.OrdinalIgnoreCase))
                return false;

            return !categorySpecified ||
                   string.Equals(tag.Category, parsed.Category, StringComparison.OrdinalIgnoreCase);
        }

        private static TagsUpdatedEventArgs CreateTagsUpdatedEventArgs(
            FolderInfo folder,
            IEnumerable<string> storedTags,
            int rating)
        {
            var categorizedTags = ParseStoredTags(storedTags);
            return new TagsUpdatedEventArgs(folder, ToSimpleTagNames(categorizedTags), rating)
            {
                CategorizedTags = categorizedTags
            };
        }

        private async Task SaveFolderTagsAsync()
        {
            if (CurrentFolder == null) return;

            try
            {
                var oldTags = new List<string>(FolderTags);

                bool isTagInputEmpty = string.IsNullOrWhiteSpace(TagInputText) ||
                                      TagInputText.Replace("#", "").Trim().Length == 0;

                if (!isTagInputEmpty)
                {
                    var resolvedTags = ResolveInputTagsForCurrentFolder(TagInputText);
                    bool tagsChanged = FolderTags.Count != resolvedTags.Count ||
                                       !FolderTags.All(tag => resolvedTags.Contains(tag, StringComparer.OrdinalIgnoreCase));

                    if (tagsChanged)
                    {
                        FolderTags.Clear();
                        foreach (var tag in resolvedTags)
                        {
                            FolderTags.Add(tag);
                        }
                    }

                    if (tagsChanged)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"Tags changed from: {string.Join(", ", oldTags)} to: {string.Join(", ", FolderTags)}");
                    }
                }

                var tags = new List<string>(FolderTags);

                if (_coordinator != null)
                {
                    // Use coordinated operation
                    var result = await _coordinator.ExecuteTagUpdateAsync(CurrentFolder.FolderPath, tags, Rating);

                    if (result.Success)
                    {
                        // Clear input after successful save
                        TagInputText = string.Empty;

                        // Notify UI of successful update
                        var args = CreateTagsUpdatedEventArgs(CurrentFolder, FolderTags, Rating);
                        TagsUpdated?.Invoke(this, args);

                        UpdateStatus(isTagInputEmpty
                            ? "No new tags provided. Existing tags preserved."
                            : "Tags updated successfully.");
                    }
                    else
                    {
                        UpdateStatus($"Failed to save tags: {result.Message}");
                    }
                }
                else
                {
                    // Fallback to direct service call (backward compatibility)
                    await _tagService.SetTagsAndRatingForFolderAsync(
                        CurrentFolder.FolderPath,
                        tags,
                        Rating
                    );

                    TagInputText = string.Empty;
                    _tagCloud.InvalidateCache();

                    UpdateStatus(isTagInputEmpty
                        ? "No new tags provided. Existing tags preserved."
                        : "Tags updated successfully.");

                    var args = CreateTagsUpdatedEventArgs(CurrentFolder, FolderTags, Rating);
                    TagsUpdated?.Invoke(this, args);
                }
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error saving tags: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Error in SaveFolderTagsAsync: {ex.Message}");
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
                    var tags = new List<string>(FolderTags);

                    if (_coordinator != null)
                    {
                        // Use coordinated operation
                        var result = await _coordinator.ExecuteTagUpdateAsync(CurrentFolder.FolderPath, tags, Rating);

                        if (result.Success)
                        {
                            var args = CreateTagsUpdatedEventArgs(CurrentFolder, FolderTags, Rating);
                            TagsUpdated?.Invoke(this, args);
                        }
                    }
                    else
                    {
                        // Fallback to direct service call
                        await _tagService.SetTagsAndRatingForFolderAsync(
                            CurrentFolder.FolderPath,
                            tags,
                            Rating
                        );

                        var args = CreateTagsUpdatedEventArgs(CurrentFolder, FolderTags, Rating);
                        TagsUpdated?.Invoke(this, args);
                    }
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
            if (CurrentFolder != null &&
                folderPaths != null &&
                folderPaths.Any(p => p.Equals(CurrentFolder.FolderPath, StringComparison.OrdinalIgnoreCase)))
            {
                await LoadFolderMetadataAsync(CurrentFolder);

                // Raise event to notify of tags update with the current tags
                var args = CreateTagsUpdatedEventArgs(CurrentFolder, FolderTags, Rating);
                TagsUpdated?.Invoke(this, args);
            }
            else
            {
                // If current folder wasn't affected, avoid pushing an empty tag snapshot onto it.
                var args = new TagsUpdatedEventArgs();
                TagsUpdated?.Invoke(this, args);
            }
        }

        private Task ShowTagCloud()
        {
            TagCloudRequested?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
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

                                // Get current tags with categories so we do not strip category data during batch edits.
                                var currentTags = await _tagService.GetTagsWithCategoriesForFolderAsync(folder.FolderPath);
                                var updatedTags = currentTags
                                    .Where(tag => tag != null && !string.IsNullOrWhiteSpace(tag.TagName))
                                    .Select(tag => new TagWithCategory
                                    {
                                        TagName = tag.TagName,
                                        Category = tag.Category
                                    })
                                    .ToList();

                                // Remove specified tags
                                if (tagsToRemove.Count > 0)
                                {
                                    updatedTags.RemoveAll(tag =>
                                        tagsToRemove.Any(tagExpression => MatchesTagOperation(tag, tagExpression)));
                                }

                                // Add new tags
                                foreach (var tag in tagsToAdd)
                                {
                                    var parsedTag = TagHelper.ParseTagWithCategory(tag);
                                    if (parsedTag == null || string.IsNullOrWhiteSpace(parsedTag.TagName))
                                        continue;

                                    if (!updatedTags.Any(existing =>
                                        string.Equals(existing.TagName, parsedTag.TagName, StringComparison.OrdinalIgnoreCase) &&
                                        string.Equals(existing.Category, parsedTag.Category, StringComparison.OrdinalIgnoreCase)))
                                    {
                                        updatedTags.Add(new TagWithCategory
                                        {
                                            TagName = parsedTag.TagName,
                                            Category = parsedTag.Category
                                        });
                                    }
                                }

                                // Get current rating
                                int rating = await _tagService.GetRatingForFolderAsync(folder.FolderPath);

                                var storedTags = ToStoredTagIdentifiers(updatedTags);

                                // Save updated tags
                                if (_coordinator != null)
                                {
                                    var result = await _coordinator.ExecuteTagUpdateAsync(folder.FolderPath, storedTags, rating);
                                    if (!result.Success)
                                    {
                                        throw new InvalidOperationException(result.Message);
                                    }
                                }
                                else
                                {
                                    await _tagService.SetTagsAndRatingForFolderAsync(
                                        folder.FolderPath,
                                        updatedTags,
                                        rating);
                                }

                                // Update folder object
                                folder.Tags = new ObservableCollection<string>(ToSimpleTagNames(updatedTags));
                                folder.CategorizedTags = new ObservableCollection<TagWithCategory>(updatedTags);

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

                if (success && !cts.IsCancellationRequested)
                {
                    await _tagCloud.ApplyFolderUpdatesAsync(folders);

                    if (CurrentFolder != null &&
                        folders.Any(folder => PathService.PathsEqual(folder.FolderPath, CurrentFolder.FolderPath)))
                    {
                        await LoadFolderMetadataAsync(CurrentFolder);
                        var args = CreateTagsUpdatedEventArgs(CurrentFolder, FolderTags, Rating);
                        TagsUpdated?.Invoke(this, args);
                    }
                }

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
