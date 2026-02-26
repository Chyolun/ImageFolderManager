using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ImageFolderManager.Services
{
    /// <summary>
    /// Enhanced service for managing folder tags and ratings with category support
    /// </summary>
    public class FolderTagService
    {
        private const string TagFileName = ".folderTags";
        private const string CategorySeparator = "::";
        public TagCategoryService CategoryService => _categoryService;

        public bool EnableCaching { get; set; } = true;

        // Thread-safe cache using ConcurrentDictionary
        // Tuple stores: tag list, rating, file modification time
        private readonly ConcurrentDictionary<string, Tuple<List<TagWithCategory>, int, DateTime>> _tagCache
            = new ConcurrentDictionary<string, Tuple<List<TagWithCategory>, int, DateTime>>(StringComparer.OrdinalIgnoreCase);

        private readonly TagCategoryService _categoryService;

        public FolderTagService(TagCategoryService categoryService = null)
        {
            _categoryService = categoryService ?? new TagCategoryService();
        }

        /// <summary>
        /// Clears the cache
        /// </summary>
        public void ClearCache() => _tagCache.Clear();

        /// <summary>
        /// Gets tags for a folder (returns simple tag names for backward compatibility)
        /// </summary>
        public Task<List<string>> GetTagsForFolderAsync(string folderPath)
        {
            // Normalize path to ensure consistent cache keys
            folderPath = PathService.NormalizePath(folderPath);

            if (string.IsNullOrEmpty(folderPath) || !PathService.DirectoryExists(folderPath))
                return Task.FromResult(new List<string>());

            return Task.Run(() =>
            {
                try
                {
                    // Check cache first if enabled
                    if (EnableCaching && TryGetCachedTags(folderPath, out var cachedTags))
                        return cachedTags.Select(t => t.TagName).ToList();

                    // Load from file
                    var tagsAndRating = LoadTagsAndRatingFromFile(folderPath);
                    return tagsAndRating.Item1.Select(t => t.TagName).ToList();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error reading tags from file: {ex.Message}");
                    return new List<string>();
                }
            });
        }

        /// <summary>
        /// Gets tags with categories for a folder
        /// </summary>
        public Task<List<TagWithCategory>> GetTagsWithCategoriesForFolderAsync(string folderPath)
        {
            // Normalize path to ensure consistent cache keys
            folderPath = PathService.NormalizePath(folderPath);

            if (string.IsNullOrEmpty(folderPath) || !PathService.DirectoryExists(folderPath))
                return Task.FromResult(new List<TagWithCategory>());

            return Task.Run(() =>
            {
                try
                {
                    // Check cache first if enabled
                    if (EnableCaching && TryGetCachedTags(folderPath, out var cachedTags))
                        return cachedTags;

                    // Load from file
                    var tagsAndRating = LoadTagsAndRatingFromFile(folderPath);
                    return tagsAndRating.Item1;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error reading tags from file: {ex.Message}");
                    return new List<TagWithCategory>();
                }
            });
        }
        /// <summary>
        /// Renames tags across all folders with explicit category preservation
        /// </summary>
        public async Task RenameTagAsync(string oldTag, string newTag, IEnumerable<string> folderPaths, string category = null)
        {
            if (string.IsNullOrWhiteSpace(oldTag) || string.IsNullOrWhiteSpace(newTag) || oldTag == newTag)
                return;

            oldTag = oldTag.Trim();
            newTag = newTag.Trim();

            // If no category specified, try to get the original tag's category
            if (string.IsNullOrEmpty(category))
            {
                category = _categoryService.GetTagCategory(oldTag);
            }

            foreach (var folderPath in folderPaths)
            {
                string normalizedPath = PathService.NormalizePath(folderPath);

                // Skip if directory doesn't exist
                if (!PathService.DirectoryExists(normalizedPath))
                    continue;

                // Get current tags with categories
                var tags = await GetTagsWithCategoriesForFolderAsync(normalizedPath);
                int rating = await GetRatingForFolderAsync(normalizedPath);

                // Check if the folder has the old tag and update it
                bool hasChanges = false;
                for (int i = 0; i < tags.Count; i++)
                {
                    if (tags[i].TagName.Equals(oldTag, StringComparison.OrdinalIgnoreCase))
                    {
                        // Create new tag with original category preserved
                        tags[i] = new TagWithCategory
                        {
                            TagName = newTag,
                            Category = string.IsNullOrEmpty(category) ? tags[i].Category : category
                        };
                        hasChanges = true;
                    }
                }

                if (hasChanges)
                {
                    // Remove any duplicates that might have been created
                    tags = tags.GroupBy(t => t.TagName, StringComparer.OrdinalIgnoreCase)
                              .Select(g => g.First())
                              .ToList();

                    // Update the tags
                    await SetTagsAndRatingForFolderAsync(normalizedPath, tags, rating);
                }
            }

            // Update category service mapping - preserve the category
            if (!string.IsNullOrEmpty(category))
            {
                _categoryService.SetTagCategory(newTag, category);
            }

            // Clear cache after global tag rename
            if (EnableCaching)
            {
                ClearCache();
            }
        }


        public async Task DeleteTagFromAllFoldersAsync(string tagToDelete, IEnumerable<string> folderPaths)
        {
            if (string.IsNullOrWhiteSpace(tagToDelete))
                return;

            tagToDelete = tagToDelete.Trim();

            foreach (var folderPath in folderPaths)
            {
                string normalizedPath = PathService.NormalizePath(folderPath);

                // Skip if directory doesn't exist
                if (!PathService.DirectoryExists(normalizedPath))
                    continue;

                // Get current tags with categories
                var tags = await GetTagsWithCategoriesForFolderAsync(normalizedPath);
                int rating = await GetRatingForFolderAsync(normalizedPath);

                // Check if the folder has the tag to delete
                int originalCount = tags.Count;
                tags.RemoveAll(t => t.TagName.Equals(tagToDelete, StringComparison.OrdinalIgnoreCase));

                // If we removed any tags, update the folder
                if (tags.Count < originalCount)
                {
                    await SetTagsAndRatingForFolderAsync(normalizedPath, tags, rating);
                }
            }

            // Clear the cache after a global tag operation
            if (EnableCaching)
            {
                ClearCache();
            }

            // No need to explicitly remove from TagCategoryService, 
            // as TagCategoryService has a CleanupUnusedTagMappings method that will 
            // be called during the next tag cloud update
        }

        /// <summary>
        /// Tries to get tags from cache
        /// </summary>
        private bool TryGetCachedTags(string folderPath, out List<TagWithCategory> tags)
        {
            tags = new List<TagWithCategory>();
            folderPath = PathService.NormalizePath(folderPath);

            if (!_tagCache.TryGetValue(folderPath, out var cachedData))
                return false;

            string tagFilePath = Path.Combine(folderPath, TagFileName);
            if (!File.Exists(tagFilePath))
                return false;

            DateTime lastWriteTime = File.GetLastWriteTime(tagFilePath);
            if (lastWriteTime > cachedData.Item3)
                return false;

            tags = new List<TagWithCategory>(cachedData.Item1);
            return true;
        }

        /// <summary>
        /// Loads tags and rating from file with enhanced category support
        /// </summary>
        private Tuple<List<TagWithCategory>, int> LoadTagsAndRatingFromFile(string folderPath)
        {
            folderPath = PathService.NormalizePath(folderPath);
            string filePath = Path.Combine(folderPath, TagFileName);

            if (!File.Exists(filePath))
                return new Tuple<List<TagWithCategory>, int>(new List<TagWithCategory>(), 0);

            try
            {
                string content = File.ReadAllText(filePath);
                string[] parts = content.Split('|');
                List<TagWithCategory> tags = new List<TagWithCategory>();
                int rating = 0;

                if (parts.Length > 0)
                {
                    var tagStrings = parts[0].Split(new[] { '#' }, StringSplitOptions.RemoveEmptyEntries)
                                           .Select(t => t.Trim())
                                           .Where(t => !string.IsNullOrEmpty(t))
                                           .ToList();

                    foreach (var tagString in tagStrings)
                    {
                        var tagWithCategory = ParseTagString(tagString);
                        if (tagWithCategory != null)
                        {
                            tags.Add(tagWithCategory);
                        }
                    }
                }

                if (parts.Length > 1)
                {
                    int.TryParse(parts[1], out rating);
                }

                // Update cache if enabled
                if (EnableCaching)
                {
                    _tagCache[folderPath] = new Tuple<List<TagWithCategory>, int, DateTime>(
                        new List<TagWithCategory>(tags),
                        rating,
                        File.GetLastWriteTime(filePath)
                    );
                }

                return new Tuple<List<TagWithCategory>, int>(tags, rating);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error reading from tag file: {ex.Message}");
                return new Tuple<List<TagWithCategory>, int>(new List<TagWithCategory>(), 0);
            }
        }

        /// <summary>
        /// Parses a tag string that might contain category information
        /// </summary>
        private TagWithCategory ParseTagString(string tagString)
        {
            if (string.IsNullOrWhiteSpace(tagString))
                return null;

            // Check if tag contains category separator
            if (tagString.Contains(CategorySeparator))
            {
                var parts = tagString.Split(new[] { CategorySeparator }, 2, StringSplitOptions.None);
                if (parts.Length == 2)
                {
                    var parsedTag = new TagWithCategory
                    {
                        TagName = parts[1].Trim(),
                        Category = parts[0].Trim()
                    };
                    if (!string.IsNullOrWhiteSpace(parsedTag.TagName) &&
                        !string.IsNullOrWhiteSpace(parsedTag.Category) &&
                        !parsedTag.Category.Equals("Uncategorized", StringComparison.OrdinalIgnoreCase))
                        {
                              _categoryService.SetTagCategory(parsedTag.TagName, parsedTag.Category);
                         }
                         return parsedTag;
                }
            }

            // Get category from category service or default to "Uncategorized"
            string category = _categoryService.GetTagCategory(tagString);
            return new TagWithCategory
            {
                TagName = tagString,
                Category = category
            };
        }

        /// <summary>
        /// Gets rating for a folder
        /// </summary>
        public Task<int> GetRatingForFolderAsync(string folderPath)
        {
            folderPath = PathService.NormalizePath(folderPath);

            if (string.IsNullOrEmpty(folderPath) || !PathService.DirectoryExists(folderPath))
                return Task.FromResult(0);

            return Task.Run(() =>
            {
                try
                {
                    // Check cache
                    if (EnableCaching && _tagCache.TryGetValue(folderPath, out var cachedData))
                    {
                        string tagFilePath = Path.Combine(folderPath, TagFileName);
                        if (File.Exists(tagFilePath))
                        {
                            DateTime lastWriteTime = File.GetLastWriteTime(tagFilePath);
                            if (lastWriteTime <= cachedData.Item3)
                            {
                                return cachedData.Item2; // Return cached rating
                            }
                        }
                    }

                    var tagsAndRating = LoadTagsAndRatingFromFile(folderPath);
                    return tagsAndRating.Item2;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error getting rating: {ex.Message}");
                    return 0;
                }
            });
        }

        /// <summary>
        /// Sets tags and rating for a folder (accepts simple tag names for backward compatibility)
        /// </summary>
        public Task SetTagsAndRatingForFolderAsync(string folderPath, List<string> tags, int rating)
        {
            var tagsWithCategories = tags
               .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => TagHelper.ParseTagWithCategory(tag))
                .Where(parsedTag => parsedTag != null && !string.IsNullOrWhiteSpace(parsedTag.TagName))
                .Select(parsedTag => new TagWithCategory
                {
                TagName = parsedTag.TagName,
                    Category = parsedTag.Category
                })
                .ToList();

            return SetTagsAndRatingForFolderAsync(folderPath, tagsWithCategories, rating);
        }

        /// <summary>
        /// Sets tags with categories and rating for a folder
        /// </summary>
        public Task SetTagsAndRatingForFolderAsync(string folderPath, List<TagWithCategory> tags, int rating)
        {
            folderPath = PathService.NormalizePath(folderPath);

            if (string.IsNullOrEmpty(folderPath) || !PathService.DirectoryExists(folderPath))
                return Task.CompletedTask;

            return Task.Run(() =>
            {
                try
                {
                    // Normalize tags - remove duplicates and empty tags
                    var normalizedTags = tags
                        .Where(t => !string.IsNullOrWhiteSpace(t?.TagName))
                        .GroupBy(t => t.TagName, StringComparer.OrdinalIgnoreCase)
                        .Select(g => g.First()) // Take first occurrence to remove duplicates
                        .ToList();

                    // Ensure rating is within valid range (0-5)
                    rating = Math.Max(0, Math.Min(5, rating));

                    string tagFilePath = Path.Combine(folderPath, TagFileName);

                    // Create content with category information
                    var tagStrings = normalizedTags.Select(t =>
                        string.IsNullOrWhiteSpace(t.Category) || t.Category == "Uncategorized"
                            ? t.TagName
                            : $"{t.Category}{CategorySeparator}{t.TagName}");

                    string content = string.Join("#", tagStrings) + "|" + rating;

                    // Ensure directory exists
                    string directoryPath = Path.GetDirectoryName(tagFilePath);
                    if (!PathService.DirectoryExists(directoryPath))
                    {
                        Directory.CreateDirectory(directoryPath);
                    }

                    // Write to file
                    File.WriteAllText(tagFilePath, content);

                    // Update category service with new mappings
                    foreach (var tag in normalizedTags)
                    {
                        if (!string.IsNullOrWhiteSpace(tag.Category) && tag.Category != "Uncategorized")
                        {
                            _categoryService.SetTagCategory(tag.TagName, tag.Category);
                        }
                    }

                    // Update cache if enabled
                    if (EnableCaching)
                    {
                        _tagCache[folderPath] = new Tuple<List<TagWithCategory>, int, DateTime>(
                            new List<TagWithCategory>(normalizedTags),
                            rating,
                            File.GetLastWriteTime(tagFilePath)
                        );
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error writing tags and rating: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Renames tags across all folders with category support
        /// </summary>
        public async Task RenameTagAsync(string oldTag, string newTag, IEnumerable<string> folderPaths)
        {
            if (string.IsNullOrWhiteSpace(oldTag) || string.IsNullOrWhiteSpace(newTag) || oldTag == newTag)
                return;

            oldTag = oldTag.Trim();
            newTag = newTag.Trim();

            foreach (var folderPath in folderPaths)
            {
                string normalizedPath = PathService.NormalizePath(folderPath);

                // Skip if directory doesn't exist
                if (!PathService.DirectoryExists(normalizedPath))
                    continue;

                // Get current tags with categories
                var tags = await GetTagsWithCategoriesForFolderAsync(normalizedPath);
                int rating = await GetRatingForFolderAsync(normalizedPath);

                // Check if the folder has the old tag and update it
                bool hasChanges = false;
                for (int i = 0; i < tags.Count; i++)
                {
                    if (tags[i].TagName.Equals(oldTag, StringComparison.OrdinalIgnoreCase))
                    {
                        // Preserve the category when renaming
                        tags[i] = new TagWithCategory
                        {
                            TagName = newTag,
                            Category = tags[i].Category
                        };
                        hasChanges = true;
                    }
                }

                if (hasChanges)
                {
                    // Remove any duplicates that might have been created
                    tags = tags.GroupBy(t => t.TagName, StringComparer.OrdinalIgnoreCase)
                              .Select(g => g.First())
                              .ToList();

                    // Update the tags
                    await SetTagsAndRatingForFolderAsync(normalizedPath, tags, rating);
                }
            }

            // Update category service mapping
            string oldCategory = _categoryService.GetTagCategory(oldTag);
            _categoryService.SetTagCategory(newTag, oldCategory);

            // Clear cache after global tag rename
            if (EnableCaching)
            {
                ClearCache();
            }
        }
    }

    /// <summary>
    /// Represents a tag with its category information
    /// </summary>
    public class TagWithCategory
    {
        public string TagName { get; set; }
        public string Category { get; set; } = "Uncategorized";

        public string FullIdentifier => $"{Category}::{TagName}";

        public override bool Equals(object obj)
        {
            if (obj is TagWithCategory other)
            {
                return string.Equals(TagName, other.TagName, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(Category, other.Category, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        public override int GetHashCode()
        {
            return (TagName?.ToLowerInvariant()?.GetHashCode() ?? 0) ^
                   (Category?.ToLowerInvariant()?.GetHashCode() ?? 0);
        }

        public override string ToString()
        {
            return string.IsNullOrWhiteSpace(Category) || Category == "Uncategorized"
                ? TagName
                : $"{Category}::{TagName}";
        }
    }
}