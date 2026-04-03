using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using ImageFolderManager.Models;
using ImageFolderManager.Services;

namespace ImageFolderManager.ViewModels
{
    public class TagCloudViewModel : INotifyPropertyChanged
    {
        // Use ObservableCollection for UI binding
        private ObservableCollection<TagCloudItem> _tagItems = new ObservableCollection<TagCloudItem>();
        public ObservableCollection<TagCloudItem> TagItems
        {
            get => _tagItems;
            private set
            {
                if (_tagItems != value)
                {
                    _tagItems = value;
                    OnPropertyChanged();
                }
            }
        }
        // Categories collection for tabs
        private ObservableCollection<TagCategory> _categories = new ObservableCollection<TagCategory>();
        public ObservableCollection<TagCategory> Categories
        {
            get => _categories;
            private set
            {
                if (_categories != value)
                {
                    _categories = value;
                    OnPropertyChanged();
                }
            }
        }

        // Currently selected category
        private TagCategory _selectedCategory;
        public TagCategory SelectedCategory
        {
            get => _selectedCategory;
            set
            {
                if (_selectedCategory != value)
                {
                    _selectedCategory = value;
                    OnPropertyChanged();
                    UpdateTagItemsForSelectedCategory();
                }
            }
        }

        public event EventHandler<string> TagDeleted;
        // All tags grouped by category
        private Dictionary<string, List<TagCloudItem>> _tagsByCategory = new Dictionary<string, List<TagCloudItem>>();

        // Enhanced color palette with slightly brighter colors for better visibility
        private readonly List<SolidColorBrush> _tagColors = new List<SolidColorBrush>
        {
            new SolidColorBrush(Color.FromRgb(86, 156, 214)),    // Soft blue
            new SolidColorBrush(Color.FromRgb(156, 220, 254)),   // Light blue
            new SolidColorBrush(Color.FromRgb(78, 201, 176)),    // Teal
            new SolidColorBrush(Color.FromRgb(184, 215, 163)),   // Light green
            new SolidColorBrush(Color.FromRgb(214, 157, 133)),   // Light orange
            new SolidColorBrush(Color.FromRgb(209, 105, 105)),   // Light red
            new SolidColorBrush(Color.FromRgb(181, 206, 168)),   // Sage green
            new SolidColorBrush(Color.FromRgb(206, 145, 120)),   // Light brown
            new SolidColorBrush(Color.FromRgb(197, 134, 192)),   // Light purple
            new SolidColorBrush(Color.FromRgb(220, 220, 170))    // Light gold
        };

        // Thread-safe random for color selection
        private static readonly Random _random = new Random();
        private static readonly object _randomLock = new object();

        // Current list of used tags for quick lookups during updates
        private Dictionary<string, TagCloudItem> _currentTags = new Dictionary<string, TagCloudItem>(StringComparer.OrdinalIgnoreCase);

        // Cache for tag counts to avoid recalculation during small updates
        private Dictionary<string, TagCloudItemData> _cachedTagData = new Dictionary<string, TagCloudItemData>(StringComparer.OrdinalIgnoreCase);
        private bool _isFullUpdateNeeded = true;
        private int _lastFolderCount = 0;
        private readonly object _cacheSync = new object();
        private long _latestUpdateRequestId;
        
        // Dispatcher for UI thread updates
        private readonly Dispatcher _dispatcher;

        // Configuration
        private const int MAX_TAGS_TO_DISPLAY = 75;
        private const double MIN_FONT_SIZE = 12;
        private const double MAX_FONT_SIZE = 24;
        private const double TAG_COUNT_THRESHOLD = 0.25;

        // Default category name
        public const string DEFAULT_CATEGORY = "Uncategorized";

        // Tag management service reference
        private TagCategoryService _categoryService;

        public TagCloudViewModel(TagCategoryService categoryService = null)
        {
            // Store the dispatcher for thread-safe UI updates
            _dispatcher = Dispatcher.CurrentDispatcher;

            // Initialize category service
            _categoryService = categoryService ?? new TagCategoryService();

            // Freeze the brushes for better performance
            foreach (var brush in _tagColors)
            {
                brush.Freeze();
            }

            // Initialize with default category
            InitializeDefaultCategory();
        }

        private void InitializeDefaultCategory()
        {
            var defaultCategory = new TagCategory { Name = DEFAULT_CATEGORY, TagCount = 0 };
            Categories.Add(defaultCategory);
            SelectedCategory = defaultCategory;
            _tagsByCategory[DEFAULT_CATEGORY] = new List<TagCloudItem>();
        }

        /// <summary>
        /// Updates the tag cloud based on folder data with category support
        /// </summary>
        public async Task UpdateTagCloudAsync(IEnumerable<FolderInfo> allFolders, CancellationToken cancellationToken = default)
        {
            if (allFolders == null)
                return;

            long updateRequestId = Interlocked.Increment(ref _latestUpdateRequestId);
            var folderSnapshot = CreateFolderSnapshot(allFolders);

            try
            {
                await Task.Run(async () =>
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (!IsLatestUpdateRequest(updateRequestId))
                            return;

                        // Determine if we need a full update
                        bool shouldPerformFullUpdate = ShouldPerformFullUpdate(folderSnapshot.Count);

                        // Get tag data with categories
                        var tagData = await GetTagDataWithCategoriesAsync(folderSnapshot, shouldPerformFullUpdate);

                        cancellationToken.ThrowIfCancellationRequested();
                        if (!IsLatestUpdateRequest(updateRequestId))
                            return;

                        // Create updated tag items with categories
                        var updatedTagsByCategory = await CreateCategorizedTagItemsAsync(tagData, cancellationToken);

                        if (cancellationToken.IsCancellationRequested || !IsLatestUpdateRequest(updateRequestId))
                            return;

                        // Update UI on dispatcher thread
                        await UpdateCategorizedUIAsync(updatedTagsByCategory, cancellationToken, updateRequestId);
                    }
                    catch (OperationCanceledException)
                    {
                        Debug.WriteLine("Tag cloud calculation was canceled");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error in tag cloud calculation: {ex.Message}");
                    }
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("Tag cloud update task was canceled");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error updating tag cloud: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets tag data with category information
        /// </summary>
        private Task<Dictionary<string, TagCloudItemData>> GetTagDataWithCategoriesAsync(
            IReadOnlyCollection<FolderInfo> allFolders, bool forceFullUpdate)
        {
            if (forceFullUpdate)
            {
                var tagData = new Dictionary<string, TagCloudItemData>(StringComparer.OrdinalIgnoreCase);

                // Get all folder tags with category information
                foreach (var folder in allFolders)
                {
                    if (folder?.Tags == null)
                        continue;

                    foreach (var tag in folder.Tags)
                    {
                        if (string.IsNullOrWhiteSpace(tag)) continue;

                        // Parse category from tag (if stored with category)
                        var (category, tagName) = ParseTagWithCategory(tag);

                        string key = $"{category}::{tagName}";

                        if (tagData.ContainsKey(key))
                        {
                            tagData[key].Count++;
                        }
                        else
                        {
                            tagData[key] = new TagCloudItemData
                            {
                                Tag = tagName,
                                Category = category,
                                Count = 1
                            };
                        }
                    }
                }

                lock (_cacheSync)
                {
                    _cachedTagData = CloneTagData(tagData);
                    _isFullUpdateNeeded = false;
                }

                Debug.WriteLine($"Performed full tag count with categories, found {tagData.Count} unique tags");
                return Task.FromResult(CloneTagData(tagData));
            }
            else
            {
                Debug.WriteLine("Using cached tag data");
                lock (_cacheSync)
                {
                    return Task.FromResult(CloneTagData(_cachedTagData));
                }
            }
        }

        /// <summary>
        /// Parse tag to extract category and tag name
        /// </summary>
        private (string category, string tagName) ParseTagWithCategory(string fullTag)
        {
            // Check if tag contains category separator
            if (fullTag.Contains("::"))
            {
                var parts = fullTag.Split(new[] { "::" }, 2, StringSplitOptions.None);
                return (parts[0], parts[1]);
            }

            // Check if we have stored category mapping for this tag
            string storedCategory = _categoryService.GetTagCategory(fullTag);
            if (!string.IsNullOrEmpty(storedCategory))
            {
                return (storedCategory, fullTag);
            }

            // Default to uncategorized
            return (DEFAULT_CATEGORY, fullTag);
        }

        /// <summary>
        /// Creates categorized tag items from tag data
        /// </summary>
        /// <summary>
        /// Creates categorized tag items from tag data
        /// </summary>
        private Task<Dictionary<string, List<TagCloudItem>>> CreateCategorizedTagItemsAsync(
            Dictionary<string, TagCloudItemData> allTagData, CancellationToken cancellationToken)
        {
            var result = new Dictionary<string, List<TagCloudItem>>();

            // Group by category
            var groupedByCategory = allTagData.Values.GroupBy(t => t.Category);

            foreach (var categoryGroup in groupedByCategory)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string category = categoryGroup.Key;
                var categoryTags = categoryGroup.OrderByDescending(t => t.Count)
                                               .Take(MAX_TAGS_TO_DISPLAY)
                                               .ToList();

                if (categoryTags.Count == 0) continue;

                // Calculate min/max for font scaling within this category
                int minCount = categoryTags.Min(t => t.Count);
                int maxCount = categoryTags.Max(t => t.Count);

                var tagItems = new List<TagCloudItem>();

                var currentTagsSnapshot = _currentTags;
                foreach (var tagDataItem in categoryTags)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    double fontSize = CalculateFontSize(tagDataItem.Count, minCount, maxCount);
                    string tagKey = $"{category}::{tagDataItem.Tag}";

                    var color = currentTagsSnapshot.TryGetValue(tagKey, out var existingItem)
                        ? existingItem.Color
                        : GetRandomColor();

                    // Always create a fresh item to avoid mutating UI-bound objects from worker threads.
                    var item = new TagCloudItem
                    {
                        Tag = tagDataItem.Tag,
                        Category = category,
                        Count = tagDataItem.Count,
                        FontSize = fontSize,
                        Color = color
                    };

                    tagItems.Add(item);
                }

                result[category] = tagItems;
            }

            return Task.FromResult(result);
        }

        /// <summary>
        /// Updates the categorized UI
        /// </summary>
        private async Task UpdateCategorizedUIAsync(
            Dictionary<string, List<TagCloudItem>> updatedTagsByCategory,
            CancellationToken cancellationToken,
            long updateRequestId)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (!cancellationToken.IsCancellationRequested &&
                    IsLatestUpdateRequest(updateRequestId))
                {
                    UpdateCategoriesAndTags(updatedTagsByCategory);
                }
            }, DispatcherPriority.Background);
        }

        /// <summary>
        /// Updates categories and tag collections
        /// </summary>
        private void UpdateCategoriesAndTags(Dictionary<string, List<TagCloudItem>> updatedTagsByCategory)
        {
            try
            {
                // Store current tags for quick lookup
                var newCurrentTags = new Dictionary<string, TagCloudItem>(StringComparer.OrdinalIgnoreCase);

                // Update tags by category
                _tagsByCategory.Clear();
                foreach (var kvp in updatedTagsByCategory)
                {
                    _tagsByCategory[kvp.Key] = kvp.Value;

                    // Add to current tags lookup
                    foreach (var tag in kvp.Value)
                    {
                        newCurrentTags[$"{tag.Category}::{tag.Tag}"] = tag;
                    }
                }

                _currentTags = newCurrentTags;

                // Update categories
                UpdateCategoriesCollection();

                // Update tag items for selected category
                UpdateTagItemsForSelectedCategory();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error updating categorized UI: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates the categories collection
        /// </summary>
        private void UpdateCategoriesCollection()
        {
            var currentCategoryNames = Categories.Select(c => c.Name).ToHashSet();
            var newCategoryNames = _tagsByCategory.Keys.ToHashSet();

            // Remove categories that no longer exist
            var categoriesToRemove = Categories.Where(c => !newCategoryNames.Contains(c.Name)).ToList();
            foreach (var category in categoriesToRemove)
            {
                Categories.Remove(category);
            }

            // Add new categories
            foreach (var categoryName in newCategoryNames)
            {
                if (!currentCategoryNames.Contains(categoryName))
                {
                    var newCategory = new TagCategory
                    {
                        Name = categoryName,
                        TagCount = _tagsByCategory.ContainsKey(categoryName) ? _tagsByCategory[categoryName].Count : 0
                    };
                    Categories.Add(newCategory);
                }
            }

            // Update tag counts for existing categories
            foreach (var category in Categories)
            {
                int tagCount = _tagsByCategory.ContainsKey(category.Name) ? _tagsByCategory[category.Name].Count : 0;
                category.TagCount = tagCount;
            }

            // Ensure we have a selected category
            if (SelectedCategory == null || !Categories.Contains(SelectedCategory))
            {
                SelectedCategory = Categories.FirstOrDefault();
            }
        }

        public Task DeleteTagAsync(string tagName)
        {
            if (string.IsNullOrWhiteSpace(tagName))
                return Task.CompletedTask;

            // Find the tag in our collections
            var tagsToRemove = new List<TagCloudItem>();

            // Search all categories for this tag
            foreach (var categoryPair in _tagsByCategory)
            {
                var tagInCategory = categoryPair.Value.FirstOrDefault(t =>
                    t.Tag.Equals(tagName, StringComparison.OrdinalIgnoreCase));

                if (tagInCategory != null)
                {
                    tagsToRemove.Add(tagInCategory);
                }
            }

            // Remove tags from collections
            foreach (var tag in tagsToRemove)
            {
                TagItems.Remove(tag);

                if (_tagsByCategory.TryGetValue(tag.Category, out var categoryTags))
                {
                    categoryTags.Remove(tag);
                }
            }

            // Notify UI that tags have been removed
            OnPropertyChanged(nameof(TagItems));

            // If you have a TagDeleted event, raise it here
            TagDeleted?.Invoke(this, tagName);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Updates tag items for the currently selected category
        /// </summary>
        private void UpdateTagItemsForSelectedCategory()
        {
            TagItems.Clear();

            if (SelectedCategory != null && _tagsByCategory.ContainsKey(SelectedCategory.Name))
            {
                var categoryTags = _tagsByCategory[SelectedCategory.Name];
                foreach (var tag in categoryTags.OrderByDescending(t => t.Count))
                {
                    TagItems.Add(tag);
                }
            }
        }

        /// <summary>
        /// Adds a new category
        /// </summary>
        public void AddCategory(string categoryName)
        {
            if (string.IsNullOrWhiteSpace(categoryName) ||
                Categories.Any(c => c.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase)))
                return;

            var newCategory = new TagCategory { Name = categoryName, TagCount = 0 };
            Categories.Add(newCategory);
            _tagsByCategory[categoryName] = new List<TagCloudItem>();

            // Save category to persistent storage
            _categoryService.AddCategory(categoryName);
        }

        /// <summary>
        /// Renames a category
        /// </summary>
        public void RenameCategory(string oldName, string newName)
        {
            if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName))
                return;

            if (oldName.Equals(newName, StringComparison.OrdinalIgnoreCase))
                return;

            // Don't allow renaming "Uncategorized"
            if (oldName.Equals("Uncategorized", StringComparison.OrdinalIgnoreCase))
                return;

            // Call the category service to rename the category
            _categoryService.RenameCategory(oldName, newName);

            // Update the category in our collections
            var category = Categories.FirstOrDefault(c => c.Name.Equals(oldName, StringComparison.OrdinalIgnoreCase));
            if (category != null)
            {
                category.Name = newName;
            }

            // Update tags to use the new category name
            if (_tagsByCategory.TryGetValue(oldName, out var tags))
            {
                _tagsByCategory.Remove(oldName);
                _tagsByCategory[newName] = tags;

                foreach (var tag in tags)
                {
                    tag.Category = newName;
                }
            }

            // If the selected category was renamed, update it
            if (SelectedCategory?.Name.Equals(oldName, StringComparison.OrdinalIgnoreCase) == true)
            {
                OnPropertyChanged(nameof(SelectedCategory));
            }

            // Refresh the display
            UpdateTagItemsForSelectedCategory();
        }

        /// <summary>
        /// Deletes a category and moves its tags to "Uncategorized"
        /// </summary>
        public void DeleteCategory(string categoryName)
        {
            if (string.IsNullOrWhiteSpace(categoryName))
                return;

            // Don't allow deleting "Uncategorized"
            if (categoryName.Equals("Uncategorized", StringComparison.OrdinalIgnoreCase))
                return;

            // Call the category service to delete the category
            _categoryService.RemoveCategory(categoryName);

            // Move tags to "Uncategorized"
            if (_tagsByCategory.TryGetValue(categoryName, out var tagsToMove))
            {
                // Get or create "Uncategorized" category
                if (!_tagsByCategory.TryGetValue("Uncategorized", out var uncategorizedTags))
                {
                    uncategorizedTags = new List<TagCloudItem>();
                    _tagsByCategory["Uncategorized"] = uncategorizedTags;
                }

                // Update the category for each tag and add to "Uncategorized"
                foreach (var tag in tagsToMove)
                {
                    tag.Category = "Uncategorized";
                    uncategorizedTags.Add(tag);
                }

                // Remove the old category
                _tagsByCategory.Remove(categoryName);
            }

            // Remove the category from the categories collection
            var categoryToRemove = Categories.FirstOrDefault(c => c.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase));
            if (categoryToRemove != null)
            {
                Categories.Remove(categoryToRemove);

                // If the deleted category was selected, select "Uncategorized"
                if (SelectedCategory == categoryToRemove)
                {
                    SelectedCategory = Categories.FirstOrDefault(c => c.Name.Equals("Uncategorized", StringComparison.OrdinalIgnoreCase));
                }
            }

            // Refresh the display
            UpdateTagItemsForSelectedCategory();
        }


        /// <summary>
        /// Moves a tag to a different category
        /// </summary>
        public Task MoveTagToCategoryAsync(TagCloudItem tag, string newCategory)
        {
            if (tag == null || string.IsNullOrWhiteSpace(newCategory))
                return Task.CompletedTask;

            string oldCategory = tag.Category;
            if (oldCategory.Equals(newCategory, StringComparison.OrdinalIgnoreCase))
                return Task.CompletedTask;

            // Update tag category
            tag.Category = newCategory;

            // Update category service mapping
            _categoryService.SetTagCategory(tag.Tag, newCategory);

            // Move tag between category collections
            if (_tagsByCategory.ContainsKey(oldCategory))
            {
                _tagsByCategory[oldCategory].Remove(tag);
            }

            if (!_tagsByCategory.ContainsKey(newCategory))
            {
                _tagsByCategory[newCategory] = new List<TagCloudItem>();
                AddCategory(newCategory);
            }

            _tagsByCategory[newCategory].Add(tag);

            // Update category counts
            UpdateCategoriesCollection();

            // Refresh display if needed
            if (SelectedCategory?.Name == oldCategory || SelectedCategory?.Name == newCategory)
            {
                UpdateTagItemsForSelectedCategory();
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Gets all tags in a specific category
        /// </summary>
        public List<TagCloudItem> GetTagsInCategory(string categoryName)
        {
            return _tagsByCategory.ContainsKey(categoryName)
                ? new List<TagCloudItem>(_tagsByCategory[categoryName])
                : new List<TagCloudItem>();
        }

        #region Existing Methods (updated for category support)

        private bool ShouldPerformFullUpdate(int folderCount)
        {
            lock (_cacheSync)
            {
                bool forceFullUpdate = _isFullUpdateNeeded ||
                                     Math.Abs(folderCount - _lastFolderCount) / (double)Math.Max(1, _lastFolderCount) > TAG_COUNT_THRESHOLD;

                _lastFolderCount = folderCount;
                return forceFullUpdate;
            }
        }

        /// <summary>
        /// Invalidates the tag cloud cache to force a full refresh
        /// </summary>
        public void InvalidateCache()
        {
            lock (_cacheSync)
            {
                _isFullUpdateNeeded = true;
            }
        }

        private double CalculateFontSize(int count, int minCount, int maxCount)
        {
            if (minCount == maxCount)
                return MIN_FONT_SIZE;

            if (minCount <= 0 || maxCount <= 0)
                return MIN_FONT_SIZE;

            double logMin = Math.Log(minCount);
            double logMax = Math.Log(maxCount);
            double logCount = Math.Log(count);

            return MIN_FONT_SIZE +
                  (logCount - logMin) * (MAX_FONT_SIZE - MIN_FONT_SIZE) / (logMax - logMin);
        }

        private SolidColorBrush GetRandomColor()
        {
            lock (_randomLock)
            {
                return _tagColors[_random.Next(_tagColors.Count)];
            }
        }

        private bool IsLatestUpdateRequest(long updateRequestId)
            => updateRequestId == Volatile.Read(ref _latestUpdateRequestId);

        private static Dictionary<string, TagCloudItemData> CloneTagData(
            Dictionary<string, TagCloudItemData> source)
        {
            var clone = new Dictionary<string, TagCloudItemData>(StringComparer.OrdinalIgnoreCase);
            if (source == null)
                return clone;

            foreach (var kvp in source)
            {
                if (kvp.Value == null)
                    continue;

                clone[kvp.Key] = new TagCloudItemData
                {
                    Tag = kvp.Value.Tag,
                    Category = kvp.Value.Category,
                    Count = kvp.Value.Count
                };
            }

            return clone;
        }

        private static List<FolderInfo> CreateFolderSnapshot(IEnumerable<FolderInfo> allFolders)
        {
            if (allFolders == null)
                return new List<FolderInfo>();

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    return allFolders.Where(f => f != null).ToList();
                }
                catch (InvalidOperationException) when (attempt < 2)
                {
                    Thread.Yield();
                }
            }

            return new List<FolderInfo>();
        }

        #endregion

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion
    }

    /// <summary>
    /// Helper class to store tag data during processing
    /// </summary>
    internal class TagCloudItemData
    {
        public string Tag { get; set; }
        public string Category { get; set; }
        public int Count { get; set; }
    }
}
