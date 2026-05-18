using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace ImageFolderManager.Services
{
    /// <summary>
    /// Service for managing tag categories and their mappings
    /// </summary>
    public class TagCategoryService
    {
        private readonly string _baseStorageDirectory;
        private string _categoryMappingFilePath;
        private string _categoriesFilePath;
        private Dictionary<string, string> _tagCategoryMappings;
        private HashSet<string> _categories;
        private readonly object _syncRoot = new object();
        private string _currentStorageDirectory;
        private string _currentRootScope;


        public TagCategoryService(string storageDirectory = null)
        {
            _baseStorageDirectory = string.IsNullOrWhiteSpace(storageDirectory)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ImageFolderManager")
                : storageDirectory;

            ConfigureStorageDirectory(_baseStorageDirectory);
        }

        public string CurrentStorageDirectory
        {
            get
            {
                lock (_syncRoot)
                {
                    return _currentStorageDirectory;
                }
            }
        }

        public string CurrentRootScope
        {
            get
            {
                lock (_syncRoot)
                {
                    return _currentRootScope;
                }
            }
        }

        public void SetRootDirectoryScope(string rootDirectory)
        {
            string normalizedRoot = string.IsNullOrWhiteSpace(rootDirectory)
                ? string.Empty
                : PathService.NormalizePath(rootDirectory);
            string targetDirectory = string.IsNullOrWhiteSpace(normalizedRoot)
                ? _baseStorageDirectory
                : BuildRootScopedStorageDirectory(normalizedRoot);

            lock (_syncRoot)
            {
                if (string.Equals(_currentStorageDirectory, targetDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    _currentRootScope = normalizedRoot;
                    return;
                }

                ConfigureStorageDirectory(targetDirectory);
                _currentRootScope = normalizedRoot;
            }
        }

        /// <summary>
        /// Gets the category for a specific tag
        /// </summary>
        public string GetTagCategory(string tag)
        {
            lock (_syncRoot)
            {
                if (string.IsNullOrWhiteSpace(tag))
                    return "Uncategorized";

                return _tagCategoryMappings.ContainsKey(tag)
                    ? _tagCategoryMappings[tag]
                    : "Uncategorized";
            }
        }

        /// <summary>
        /// Sets the category for a specific tag
        /// </summary>
        public void SetTagCategory(string tag, string category)
        {
            lock (_syncRoot)
            {
                if (string.IsNullOrWhiteSpace(tag))
                    return;

                if (string.IsNullOrWhiteSpace(category))
                    category = "Uncategorized";

                _tagCategoryMappings[tag] = category;

                // Ensure category exists
                if (!_categories.Contains(category))
                {
                    AddCategory(category);
                }

                SaveMappings();
            }
        }

        /// <summary>
        /// Adds a new category
        /// </summary>
        public void AddCategory(string categoryName)
        {
            lock (_syncRoot)
            {
                if (string.IsNullOrWhiteSpace(categoryName))
                    return;

                if (!_categories.Contains(categoryName))
                {
                    _categories.Add(categoryName);
                    SaveCategories();
                }
            }
        }



        /// <summary>
        /// Removes a category and reassigns its tags to "Uncategorized"
        /// </summary>
        public void RemoveCategory(string categoryName)
        {
            lock (_syncRoot)
            {
                if (string.IsNullOrWhiteSpace(categoryName) ||
                    categoryName.Equals("Uncategorized", StringComparison.OrdinalIgnoreCase))
                    return;

                // Reassign all tags in this category to "Uncategorized"
                var tagsToReassign = _tagCategoryMappings
                    .Where(kvp => kvp.Value.Equals(categoryName, StringComparison.OrdinalIgnoreCase))
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var tag in tagsToReassign)
                {
                    _tagCategoryMappings[tag] = "Uncategorized";
                }

                _categories.Remove(categoryName);
                SaveMappings();
                SaveCategories();
            }
        }

        /// <summary>
        /// Renames a category
        /// </summary>
        public void RenameCategory(string oldName, string newName)
        {
            lock (_syncRoot)
            {
                if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName))
                    return;

                if (oldName.Equals(newName, StringComparison.OrdinalIgnoreCase))
                    return;

                // Don't allow renaming "Uncategorized"
                if (oldName.Equals("Uncategorized", StringComparison.OrdinalIgnoreCase))
                    return;

                // Update all tag mappings
                var tagsToUpdate = _tagCategoryMappings
                    .Where(kvp => kvp.Value.Equals(oldName, StringComparison.OrdinalIgnoreCase))
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var tag in tagsToUpdate)
                {
                    _tagCategoryMappings[tag] = newName;
                }

                // Update categories
                _categories.Remove(oldName);
                _categories.Add(newName);

                SaveMappings();
                SaveCategories();
            }
        }

        /// <summary>
        /// Gets all available categories
        /// </summary>
        public List<string> GetAllCategories()
        {
            lock (_syncRoot)
            {
                return _categories.OrderBy(c => c == "Uncategorized" ? 0 : 1)
                                 .ThenBy(c => c)
                                 .ToList();
            }
        }

        /// <summary>
        /// Gets all tags in a specific category
        /// </summary>
        public List<string> GetTagsInCategory(string categoryName)
        {
            lock (_syncRoot)
            {
                if (string.IsNullOrWhiteSpace(categoryName))
                    return new List<string>();

                return _tagCategoryMappings
                    .Where(kvp => kvp.Value.Equals(categoryName, StringComparison.OrdinalIgnoreCase))
                    .Select(kvp => kvp.Key)
                    .OrderBy(tag => tag)
                    .ToList();
            }
        }

        /// <summary>
        /// Moves multiple tags to a new category
        /// </summary>
        public void MoveTagsToCategory(IEnumerable<string> tags, string newCategory)
        {
            lock (_syncRoot)
            {
                if (tags == null || string.IsNullOrWhiteSpace(newCategory))
                    return;

                bool hasChanges = false;

                foreach (var tag in tags)
                {
                    if (string.IsNullOrWhiteSpace(tag))
                        continue;

                    _tagCategoryMappings[tag] = newCategory;
                    hasChanges = true;
                }

                if (!hasChanges)
                    return;

                // Ensure category exists and persist once for batch operation
                if (!_categories.Contains(newCategory))
                {
                    AddCategory(newCategory);
                }

                SaveMappings();
            }
        }

        /// <summary>
        /// Cleans up mappings for tags that no longer exist
        /// </summary>
        public void CleanupUnusedTagMappings(IEnumerable<string> existingTags)
        {
            lock (_syncRoot)
            {
                if (existingTags == null)
                    return;

                var existingTagsSet = new HashSet<string>(existingTags, StringComparer.OrdinalIgnoreCase);
                var mappingsToRemove = _tagCategoryMappings.Keys
                    .Where(tag => !existingTagsSet.Contains(tag))
                    .ToList();

                bool hasChanges = false;
                foreach (var tag in mappingsToRemove)
                {
                    _tagCategoryMappings.Remove(tag);
                    hasChanges = true;
                }

                if (hasChanges)
                {
                    SaveMappings();
                }
            }
        }

        #region Private Methods

        private void ConfigureStorageDirectory(string storageDirectory)
        {
            if (string.IsNullOrWhiteSpace(storageDirectory))
                throw new ArgumentException("Storage directory cannot be empty.", nameof(storageDirectory));

            if (!Directory.Exists(storageDirectory))
            {
                Directory.CreateDirectory(storageDirectory);
            }

            _currentStorageDirectory = storageDirectory;
            _categoryMappingFilePath = Path.Combine(storageDirectory, "tagCategories.json");
            _categoriesFilePath = Path.Combine(storageDirectory, "categories.json");

            LoadMappings();
            LoadCategories();
        }

        private string BuildRootScopedStorageDirectory(string normalizedRoot)
        {
            string safeRootName = GetSafeRootName(normalizedRoot);
            string rootHash = ComputeStableHash(normalizedRoot);
            return Path.Combine(_baseStorageDirectory, "RootScopes", $"{safeRootName}_{rootHash}");
        }

        private static string GetSafeRootName(string normalizedRoot)
        {
            string rootName = Path.GetFileName(normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(rootName))
            {
                rootName = "root";
            }

            var invalidChars = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(rootName.Length);
            foreach (char character in rootName)
            {
                builder.Append(invalidChars.Contains(character) ? '_' : character);
            }

            return builder.ToString();
        }

        private static string ComputeStableHash(string value)
        {
            using var sha256 = SHA256.Create();
            byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
            return string.Concat(bytes.Take(8).Select(b => b.ToString("x2")));
        }

        private void LoadMappings()
        {
            try
            {
                if (File.Exists(_categoryMappingFilePath))
                {
                    string json = File.ReadAllText(_categoryMappingFilePath);
                    var deserializedMappings = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);

                    _tagCategoryMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (deserializedMappings != null)
                    {
                        foreach (var kvp in deserializedMappings)
                        {
                            _tagCategoryMappings[kvp.Key] = kvp.Value;
                        }
                    }
                }
                else
                {
                    _tagCategoryMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading tag category mappings: {ex.Message}");
                _tagCategoryMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private void SaveMappings()
        {
            try
            {
                string json = JsonConvert.SerializeObject(_tagCategoryMappings, Formatting.Indented);
                File.WriteAllText(_categoryMappingFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving tag category mappings: {ex.Message}");
            }
        }

        public Dictionary<string, string> GetExistingTagCategories(IEnumerable<string> tagNames)
        {
            lock (_syncRoot)
            {
                var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (tagNames == null)
                    return result;

                foreach (var tagName in tagNames)
                {
                    if (string.IsNullOrWhiteSpace(tagName))
                        continue;

                    string category = GetTagCategory(tagName);
                    result[tagName] = category;
                }

                return result;
            }
        }

        private void LoadCategories()
        {
            try
            {
                if (File.Exists(_categoriesFilePath))
                {
                    string json = File.ReadAllText(_categoriesFilePath);
                    var categories = JsonConvert.DeserializeObject<List<string>>(json) ?? new List<string>();
                    _categories = new HashSet<string>(categories, StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    _categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                // Ensure "Uncategorized" always exists
                if (!_categories.Contains("Uncategorized"))
                {
                    _categories.Add("Uncategorized");
                    SaveCategories();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading categories: {ex.Message}");
                _categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Uncategorized" };
                SaveCategories();
            }
        }

        private void SaveCategories()
        {
            try
            {
                var categoryList = _categories.ToList();
                string json = JsonConvert.SerializeObject(categoryList, Formatting.Indented);
                File.WriteAllText(_categoriesFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving categories: {ex.Message}");
            }
        }

        #endregion
    }
}
