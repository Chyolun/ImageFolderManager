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
    /// Service for managing folder tags and ratings
    /// </summary>
    public class FolderTagService
    {
        private const string TagFileName = ".folderTags";
        public bool EnableCaching { get; set; } = true;

        // Thread-safe cache using ConcurrentDictionary
        // Tuple stores: tag list, rating, file modification time
        private readonly ConcurrentDictionary<string, Tuple<List<string>, int, DateTime>> _tagCache
            = new ConcurrentDictionary<string, Tuple<List<string>, int, DateTime>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Clears the cache
        /// </summary>
        public void ClearCache() => _tagCache.Clear();

        /// <summary>
        /// Gets tags for a folder
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
                        return cachedTags;

                    // Load from file
                    var tagsAndRating = LoadTagsAndRatingFromFile(folderPath);
                    return tagsAndRating.Item1;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error reading tags from file: {ex.Message}");
                    return new List<string>();
                }
            });
        }

        /// <summary>
        /// Tries to get tags from cache
        /// </summary>
        private bool TryGetCachedTags(string folderPath, out List<string> tags)
        {
            tags = new List<string>();
            folderPath = PathService.NormalizePath(folderPath);

            if (!_tagCache.TryGetValue(folderPath, out var cachedData))
                return false;

            string tagFilePath = Path.Combine(folderPath, TagFileName);
            if (!File.Exists(tagFilePath))
                return false;

            DateTime lastWriteTime = File.GetLastWriteTime(tagFilePath);
            if (lastWriteTime > cachedData.Item3)
                return false;

            tags = new List<string>(cachedData.Item1);
            return true;
        }

        /// <summary>
        /// Loads tags and rating from file
        /// </summary>
        private Tuple<List<string>, int> LoadTagsAndRatingFromFile(string folderPath)
        {
            folderPath = PathService.NormalizePath(folderPath);
            string filePath = Path.Combine(folderPath, TagFileName);

            if (!File.Exists(filePath))
                return new Tuple<List<string>, int>(new List<string>(), 0);

            try
            {
                string content = File.ReadAllText(filePath);
                string[] parts = content.Split('|');
                List<string> tags = new List<string>();
                int rating = 0;

                if (parts.Length > 0)
                {
                    tags = parts[0].Split(new[] { '#' }, StringSplitOptions.RemoveEmptyEntries)
                               .Select(t => t.Trim())
                               .Where(t => !string.IsNullOrEmpty(t))
                               .ToList();
                }

                if (parts.Length > 1)
                {
                    int.TryParse(parts[1], out rating);
                }

                // Update cache if enabled
                if (EnableCaching)
                {
                    _tagCache[folderPath] = new Tuple<List<string>, int, DateTime>(
                        new List<string>(tags),
                        rating,
                        File.GetLastWriteTime(filePath)
                    );
                }

                return new Tuple<List<string>, int>(tags, rating);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error reading from tag file: {ex.Message}");
                return new Tuple<List<string>, int>(new List<string>(), 0);
            }
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
        /// Sets tags and rating for a folder
        /// </summary>
        public Task SetTagsAndRatingForFolderAsync(string folderPath, List<string> tags, int rating)
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
                        .Where(t => !string.IsNullOrWhiteSpace(t))
                        .Select(t => t.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    // Ensure rating is within valid range (0-5)
                    rating = Math.Max(0, Math.Min(5, rating));

                    string tagFilePath = Path.Combine(folderPath, TagFileName);
                    string content = string.Join("#", normalizedTags) + "|" + rating;

                    // Ensure directory exists
                    string directoryPath = Path.GetDirectoryName(tagFilePath);
                    if (!PathService.DirectoryExists(directoryPath))
                    {
                        Directory.CreateDirectory(directoryPath);
                    }

                    // Write to file
                    File.WriteAllText(tagFilePath, content);

                    // Update cache if enabled
                    if (EnableCaching)
                    {
                        _tagCache[folderPath] = new Tuple<List<string>, int, DateTime>(
                            new List<string>(normalizedTags),
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
        /// Renames tags across all folders
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

                // Get current tags
                var tags = await GetTagsForFolderAsync(normalizedPath);
                int rating = await GetRatingForFolderAsync(normalizedPath);

                // Check if the folder has the old tag
                int index = tags.FindIndex(t => t.Equals(oldTag, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    // Replace old tag with new tag
                    tags[index] = newTag;

                    // Deduplicate in case new tag already exists
                    tags = tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                    // Update the tags
                    await SetTagsAndRatingForFolderAsync(normalizedPath, tags, rating);
                }
            }

            // Clear cache after global tag rename
            if (EnableCaching)
            {
                ClearCache();
            }
        }
    }
}