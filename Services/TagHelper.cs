using ImageFolderManager.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;

namespace ImageFolderManager.Services
{
    /// <summary>
    /// Enhanced helper class for tag-related operations with category support
    /// </summary>
    public static class TagHelper
    {
        private static readonly Regex InvalidTagCharacters = new Regex(@"[\\/*?""<>|]", RegexOptions.Compiled);
        private const int MaxTagLength = 50;
        private const string CategorySeparator = "::";

        #region Basic Tag Operations

        /// <summary>
        /// Parses a string containing tags separated by '#' with optional category information
        /// </summary>
        /// <param name="input">Input string containing tags (e.g., "#nature #animals #Category::specific")</param>
        /// <param name="removeDuplicates">Whether to remove duplicate tags</param>
        /// <returns>Collection of normalized tags</returns>
        public static IEnumerable<string> ParseTags(string input, bool removeDuplicates = true)
        {
            if (string.IsNullOrWhiteSpace(input))
                return Enumerable.Empty<string>();

            // Split by '#' and process each tag
            var tags = input.Split('#')
                .Skip(1) // Skip the first element as it's before the first '#'
                .Select(tag => tag.Trim()) // Trim spaces before and after
                .Where(tag => !string.IsNullOrWhiteSpace(tag)) // Remove empty entries
                .Select(tag => NormalizeTag(tag))
                .Where(tag => !string.IsNullOrWhiteSpace(tag));

            return removeDuplicates
                ? tags.Distinct(StringComparer.OrdinalIgnoreCase)
                : tags;
        }

        /// <summary>
        /// Parses tags with category information using '#' as separator
        /// </summary>
        /// <param name="input">Input string containing tags with optional categories (e.g., "#clothing::glasses # photon book #IP")</param>
        /// <param name="defaultCategory">Default category for tags without explicit category</param>
        /// <param name="removeDuplicates">Whether to remove duplicate tags</param>
        /// <returns>Collection of tags with category information</returns>
        public static IEnumerable<TagWithCategory> ParseTagsWithCategories(
            string input,
            string defaultCategory = "Uncategorized",
            bool removeDuplicates = true)
        {
            if (string.IsNullOrWhiteSpace(input))
                return Enumerable.Empty<TagWithCategory>();

            // Split by '#' and process each tag, ignoring spaces around '#'
            var tags = input.Split('#')
                .Skip(1) // Skip the first element as it's before the first '#'
                .Select(tag => tag.Trim()) // Trim spaces before and after each tag
                .Where(tag => !string.IsNullOrWhiteSpace(tag)) // Remove empty entries
                .Select(tag => ParseTagWithCategory(tag.Trim(), defaultCategory))
                .Where(tag => tag != null && !string.IsNullOrWhiteSpace(tag.TagName));

            if (removeDuplicates)
            {
                tags = tags.GroupBy(t => t.FullIdentifier, StringComparer.OrdinalIgnoreCase)
                          .Select(g => g.First());
            }

            return tags;
        }

        /// <summary>
        /// Parses a single tag that might contain category information
        /// </summary>
        /// <param name="tagString">Tag string (e.g., "Category::TagName" or "TagName")</param>
        /// <param name="defaultCategory">Default category if none specified</param>
        /// <returns>TagWithCategory object or null if invalid</returns>
        public static TagWithCategory ParseTagWithCategory(string tagString, string defaultCategory = "Uncategorized")
        {
            if (string.IsNullOrWhiteSpace(tagString))
                return null;

            // Remove leading '#' if present and trim whitespace
            tagString = tagString.Trim().TrimStart('#').Trim();

            // Check if tag contains category separator
            if (tagString.Contains(CategorySeparator))
            {
                var parts = tagString.Split(new[] { CategorySeparator }, 2, StringSplitOptions.None);
                if (parts.Length == 2)
                {
                    string category = NormalizeTag(parts[0].Trim());
                    string tagName = NormalizeTag(parts[1].Trim());

                    if (!string.IsNullOrWhiteSpace(category) && !string.IsNullOrWhiteSpace(tagName))
                    {
                        return new TagWithCategory
                        {
                            Category = category,
                            TagName = tagName
                        };
                    }
                }
            }

            // No category specified, use default
            string normalizedTag = NormalizeTag(tagString);
            if (!string.IsNullOrWhiteSpace(normalizedTag))
            {
                return new TagWithCategory
                {
                    Category = defaultCategory ?? "Uncategorized",
                    TagName = normalizedTag
                };
            }

            return null;
        }


        /// <summary>
        /// Formats a collection of tags into a hash-separated string
        /// </summary>
        public static string FormatTags(IEnumerable<string> tags)
        {
            if (tags == null || !tags.Any())
                return string.Empty;

            return string.Join(" ", tags.Select(tag => $"#{tag}"));
        }

        /// <summary>
        /// Formats a collection of tags with categories into a hash-separated string
        /// </summary>
        public static string FormatTagsWithCategories(IEnumerable<TagWithCategory> tags, bool includeCategory = false)
        {
            if (tags == null || !tags.Any())
                return string.Empty;

            if (includeCategory)
            {
                return string.Join(" ", tags.Select(tag =>
                    tag.Category == "Uncategorized" ? $"#{tag.TagName}" : $"#{tag.Category}{CategorySeparator}{tag.TagName}"));
            }
            else
            {
                return string.Join(" ", tags.Select(tag => $"#{tag.TagName}"));
            }
        }

        /// <summary>
        /// Normalizes a tag by removing invalid characters and trimming
        /// </summary>
        /// <param name="tag">Raw tag string</param>
        /// <returns>Normalized tag</returns>
        public static string NormalizeTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
                return string.Empty;

            // Remove leading/trailing whitespace and hash symbols
            tag = tag.Trim().TrimStart('#').Trim();

            // Remove invalid characters
            tag = InvalidTagCharacters.Replace(tag, "");

            // Limit length
            if (tag.Length > MaxTagLength)
            {
                tag = tag.Substring(0, MaxTagLength);
            }

            // Normalize consecutive whitespace to single spaces
            tag = Regex.Replace(tag, @"\s+", " ").Trim();

            return tag;
        }

        /// <summary>
        /// Validates if a tag string is valid
        /// </summary>
        /// <param name="tag">Tag to validate</param>
        /// <returns>True if valid, false otherwise</returns>
        public static bool IsValidTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
                return false;

            var normalized = NormalizeTag(tag);
            return !string.IsNullOrWhiteSpace(normalized) &&
                   normalized.Length <= MaxTagLength;
        }

        /// <summary>
        /// Formats tags for display, ensuring proper '#' prefixes
        /// </summary>
        /// <param name="tags">Collection of tags</param>
        /// <returns>Formatted string with '#' separators</returns>
        public static string FormatTagsForDisplay(IEnumerable<string> tags)
        {
            if (tags == null || !tags.Any())
                return string.Empty;

            return string.Join(" ", tags.Select(tag => $"#{tag}"));
        }

        /// <summary>
        /// Formats tags with categories for display
        /// </summary>
        /// <param name="tags">Collection of tags with categories</param>
        /// <returns>Formatted string with '#' separators</returns>
        public static string FormatTagsWithCategoriesForDisplay(IEnumerable<TagWithCategory> tags)
        {
            if (tags == null || !tags.Any())
                return string.Empty;

            return string.Join(" ", tags.Select(tag => $"#{tag.FullIdentifier}"));
        }

        /// <summary>
        /// Updates an ObservableCollection of tags from a string input using new parsing logic
        /// </summary>
        public static bool UpdateObservableCollection(ObservableCollection<string> targetCollection, string tagsInput)
        {
            if (targetCollection == null)
                throw new ArgumentNullException(nameof(targetCollection));

            var newTags = ParseTags(tagsInput).ToList();

            // Check if collections are different
            bool isDifferent = targetCollection.Count != newTags.Count ||
                               !targetCollection.All(t => newTags.Contains(t, StringComparer.OrdinalIgnoreCase));

            if (isDifferent)
            {
                targetCollection.Clear();
                foreach (var tag in newTags)
                {
                    targetCollection.Add(tag);
                }
                return true;
            }

            return false;
        }


        #endregion

        #region Tag Collection Operations

        /// <summary>
        /// Modifies a tag collection (merge, remove, or replace) with category support
        /// </summary>
        public static IEnumerable<TagWithCategory> ModifyTagCollectionWithCategories(
            IEnumerable<TagWithCategory> sourceTags,
            IEnumerable<TagWithCategory> modifierTags,
            TagOperation operation)
        {
            if (sourceTags == null) sourceTags = Enumerable.Empty<TagWithCategory>();
            if (modifierTags == null || !modifierTags.Any())
                return operation == TagOperation.Remove ? sourceTags : sourceTags;

            var sourceList = sourceTags.ToList();
            var modifierList = modifierTags.ToList();

            switch (operation)
            {
                case TagOperation.Add:
                    var result = new List<TagWithCategory>(sourceList);
                    foreach (var modifierTag in modifierList)
                    {
                        if (!result.Any(t => t.TagName.Equals(modifierTag.TagName, StringComparison.OrdinalIgnoreCase)))
                        {
                            result.Add(modifierTag);
                        }
                    }
                    return result;

                case TagOperation.Remove:
                    return sourceList.Where(sourceTag =>
                        !modifierList.Any(modifierTag =>
                            sourceTag.TagName.Equals(modifierTag.TagName, StringComparison.OrdinalIgnoreCase)));

                case TagOperation.Replace:
                    return modifierList;

                case TagOperation.Intersect:
                    return sourceList.Where(sourceTag =>
                        modifierList.Any(modifierTag =>
                            sourceTag.TagName.Equals(modifierTag.TagName, StringComparison.OrdinalIgnoreCase)));

                default:
                    return sourceTags;
            }
        }

        /// <summary>
        /// Modifies a tag collection (legacy method for backward compatibility)
        /// </summary>
        public static IEnumerable<string> ModifyTagCollection(
            IEnumerable<string> sourceTags,
            IEnumerable<string> modifierTags,
            TagOperation operation)
        {
            if (sourceTags == null) sourceTags = Enumerable.Empty<string>();
            if (modifierTags == null || !modifierTags.Any())
                return operation == TagOperation.Remove ? sourceTags : sourceTags;

            switch (operation)
            {
                case TagOperation.Add:
                    return sourceTags.Union(modifierTags, StringComparer.OrdinalIgnoreCase);
                case TagOperation.Remove:
                    return sourceTags.Except(modifierTags, StringComparer.OrdinalIgnoreCase);
                case TagOperation.Replace:
                    return modifierTags;
                case TagOperation.Intersect:
                    return sourceTags.Intersect(modifierTags, StringComparer.OrdinalIgnoreCase);
                default:
                    return sourceTags;
            }
        }

        /// <summary>
        /// Finds common tags among multiple collections with category awareness
        /// </summary>
        public static IEnumerable<TagWithCategory> FindCommonTagsWithCategories(IEnumerable<IEnumerable<TagWithCategory>> tagCollections)
        {
            if (tagCollections == null || !tagCollections.Any())
                return Enumerable.Empty<TagWithCategory>();

            var collections = tagCollections.ToList();
            if (collections.Count == 0)
                return Enumerable.Empty<TagWithCategory>();

            // Start with the first collection
            var commonTags = new Dictionary<string, TagWithCategory>(StringComparer.OrdinalIgnoreCase);
            foreach (var tag in collections.First())
            {
                commonTags[tag.TagName] = tag;
            }

            // Intersect with all other collections
            foreach (var collection in collections.Skip(1))
            {
                var collectionTagNames = new HashSet<string>(
                    collection.Select(t => t.TagName),
                    StringComparer.OrdinalIgnoreCase);

                var tagsToRemove = commonTags.Keys
                    .Where(tagName => !collectionTagNames.Contains(tagName))
                    .ToList();

                foreach (var tagName in tagsToRemove)
                {
                    commonTags.Remove(tagName);
                }

                // Early exit if no common tags left
                if (commonTags.Count == 0)
                    break;
            }

            return commonTags.Values;
        }

        /// <summary>
        /// Finds common tags among multiple collections (legacy method)
        /// </summary>
        public static IEnumerable<string> FindCommonTags(IEnumerable<IEnumerable<string>> tagCollections)
        {
            if (tagCollections == null || !tagCollections.Any())
                return Enumerable.Empty<string>();

            var collections = tagCollections.ToList();

            // Start with the first collection
            var commonTags = new HashSet<string>(
                collections.First().Select(t => t.ToLowerInvariant()),
                StringComparer.OrdinalIgnoreCase);

            // Intersect with all other collections
            foreach (var collection in collections.Skip(1))
            {
                var collectionSet = new HashSet<string>(
                    collection.Select(t => t.ToLowerInvariant()),
                    StringComparer.OrdinalIgnoreCase);

                commonTags.IntersectWith(collectionSet);

                // Early exit if no common tags left
                if (commonTags.Count == 0)
                    break;
            }

            return commonTags;
        }

        /// <summary>
        /// Updates an ObservableCollection of tags from a string input with category support
        /// </summary>
        public static bool UpdateObservableCollectionWithCategories(
            ObservableCollection<TagWithCategory> targetCollection,
            string tagsInput,
            string defaultCategory = "Uncategorized")
        {
            if (targetCollection == null)
                throw new ArgumentNullException(nameof(targetCollection));

            var newTags = ParseTagsWithCategories(tagsInput, defaultCategory).ToList();

            // Check if collections are different
            bool isDifferent = targetCollection.Count != newTags.Count ||
                               !targetCollection.All(existingTag =>
                                   newTags.Any(newTag => newTag.Equals(existingTag)));

            if (isDifferent)
            {
                targetCollection.Clear();
                foreach (var tag in newTags)
                {
                    targetCollection.Add(tag);
                }
                return true;
            }

            return false;
        }


        #endregion

        #region Search and Analysis

        /// <summary>
        /// Parses search criteria for tags from a search string (terms starting with #)
        /// Uses space separation for search terms to maintain compatibility
        /// </summary>
        public static IEnumerable<string> ParseTagSearchTerms(string searchText)
        {
            if (string.IsNullOrWhiteSpace(searchText))
                return Enumerable.Empty<string>();

            return searchText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(term => term.StartsWith("#") && term.Length > 1)
                .Select(term => term.Substring(1).ToLowerInvariant())
                .Where(term => !string.IsNullOrWhiteSpace(term));
        }

        /// <summary>
        /// Creates a predicate function to test if a folder matches tag search criteria with category support
        /// </summary>
        public static Func<IEnumerable<TagWithCategory>, bool> CreateTagSearchPredicateWithCategories(IEnumerable<string> tagSearchTerms)
        {
            if (tagSearchTerms == null || !tagSearchTerms.Any())
                return _ => true; // No search terms means all folders match

            var terms = tagSearchTerms.ToList();

            return (folderTags) =>
            {
                if (folderTags == null)
                    return false;

                var normalizedFolderTags = folderTags.Select(t => t.TagName.ToLowerInvariant()).ToList();
                var normalizedFullTags = folderTags.Select(t => t.FullIdentifier.ToLowerInvariant()).ToList();

                // All search terms must match (AND logic)
                return terms.All(term =>
                    normalizedFolderTags.Any(tag => tag.Contains(term)) ||
                    normalizedFullTags.Any(tag => tag.Contains(term))
                );
            };
        }

        /// <summary>
        /// Creates a predicate function to test if a folder matches tag search criteria (legacy method)
        /// </summary>
        public static Func<IEnumerable<string>, bool> CreateTagSearchPredicate(IEnumerable<string> tagSearchTerms)
        {
            if (tagSearchTerms == null || !tagSearchTerms.Any())
                return _ => true; // No search terms means all folders match

            var terms = tagSearchTerms.ToList();

            return (folderTags) =>
            {
                if (folderTags == null)
                    return false;

                var normalizedFolderTags = folderTags.Select(t => t.ToLowerInvariant());

                return terms.Any(searchTerm =>
                    normalizedFolderTags.Any(tag => tag.Contains(searchTerm)));
            };
        }

        /// <summary>
        /// Counts tag frequency across multiple collections with category support
        /// </summary>
        public static Dictionary<string, TagFrequencyData> CountTagFrequencyWithCategories(IEnumerable<IEnumerable<TagWithCategory>> tagCollections)
        {
            var tagCounts = new Dictionary<string, TagFrequencyData>(StringComparer.OrdinalIgnoreCase);

            if (tagCollections == null)
                return tagCounts;

            foreach (var collection in tagCollections)
            {
                if (collection == null) continue;

                foreach (var tag in collection)
                {
                    if (string.IsNullOrWhiteSpace(tag?.TagName)) continue;

                    string normalizedTag = NormalizeTag(tag.TagName);
                    if (string.IsNullOrWhiteSpace(normalizedTag)) continue;

                    if (tagCounts.ContainsKey(normalizedTag))
                    {
                        tagCounts[normalizedTag].Count++;
                    }
                    else
                    {
                        tagCounts[normalizedTag] = new TagFrequencyData
                        {
                            TagName = normalizedTag,
                            Category = tag.Category,
                            Count = 1
                        };
                    }
                }
            }

            return tagCounts;
        }

        /// <summary>
        /// Counts tag frequency across multiple collections (legacy method)
        /// </summary>
        public static Dictionary<string, int> CountTagFrequency(IEnumerable<IEnumerable<string>> tagCollections)
        {
            var tagCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            if (tagCollections == null)
                return tagCounts;

            foreach (var collection in tagCollections)
            {
                if (collection == null) continue;

                foreach (var tag in collection)
                {
                    if (string.IsNullOrWhiteSpace(tag)) continue;

                    string normalizedTag = NormalizeTag(tag);
                    if (string.IsNullOrWhiteSpace(normalizedTag)) continue;

                    if (tagCounts.ContainsKey(normalizedTag))
                        tagCounts[normalizedTag]++;
                    else
                        tagCounts[normalizedTag] = 1;
                }
            }

            return tagCounts;
        }


        /// <summary>
        /// Extracts unique categories from a collection of tags with categories
        /// </summary>
        public static IEnumerable<string> ExtractCategories(IEnumerable<TagWithCategory> tags)
        {
            if (tags == null)
                return Enumerable.Empty<string>();

            return tags.Where(t => !string.IsNullOrWhiteSpace(t.Category))
                      .Select(t => t.Category)
                      .Distinct(StringComparer.OrdinalIgnoreCase)
                      .OrderBy(c => c);
        }

        /// <summary>
        /// Groups tags by their categories
        /// </summary>
        public static Dictionary<string, List<TagWithCategory>> GroupTagsByCategory(IEnumerable<TagWithCategory> tags)
        {
            if (tags == null)
                return new Dictionary<string, List<TagWithCategory>>();

            return tags.GroupBy(t => t.Category ?? "Uncategorized", StringComparer.OrdinalIgnoreCase)
                      .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Gets all tag names from a collection of tags with categories
        /// </summary>
        public static IEnumerable<string> GetAllTagNames(IEnumerable<TagWithCategory> tags)
        {
            return tags?.Select(t => t.TagName) ?? Enumerable.Empty<string>();
        }

        /// <summary>
        /// Gets all full identifiers from a collection of tags with categories
        /// </summary>
        public static IEnumerable<string> GetAllFullIdentifiers(IEnumerable<TagWithCategory> tags)
        {
            return tags?.Select(t => t.FullIdentifier) ?? Enumerable.Empty<string>();
        }

        #endregion

        #region UI Helpers

        /// <summary>
        /// Creates display text for a collection of tags with category information
        /// </summary>
        public static string CreateTagDisplayTextWithCategories(
            IEnumerable<TagWithCategory> tags,
            bool includeCategory = false,
            bool prefix = true,
            int maxTags = 0)
        {
            if (tags == null || !tags.Any())
                return string.Empty;

            var tagList = tags.Where(t => !string.IsNullOrWhiteSpace(t?.TagName)).ToList();

            if (maxTags > 0 && tagList.Count > maxTags)
            {
                tagList = tagList.Take(maxTags).ToList();
                string truncatedText = CreateTagDisplayTextWithCategories(tagList, includeCategory, prefix, 0);
                return $"{truncatedText} +{tagList.Count - maxTags} more";
            }

            if (includeCategory)
            {
                return prefix
                    ? string.Join(" ", tagList.Select(t =>
                        t.Category == "Uncategorized" ? $"#{t.TagName}" : $"#{t.Category}{CategorySeparator}{t.TagName}"))
                    : string.Join(" ", tagList.Select(t =>
                        t.Category == "Uncategorized" ? t.TagName : $"{t.Category}{CategorySeparator}{t.TagName}"));
            }
            else
            {
                return prefix
                    ? string.Join(" ", tagList.Select(t => $"#{t.TagName}"))
                    : string.Join(" ", tagList.Select(t => t.TagName));
            }
        }

        /// <summary>
        /// Creates display text for a collection of tags (legacy method)
        /// </summary>
        public static string CreateTagDisplayText(IEnumerable<string> tags, bool prefix = true, int maxTags = 0)
        {
            if (tags == null || !tags.Any())
                return string.Empty;

            var tagList = tags.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();

            if (maxTags > 0 && tagList.Count > maxTags)
            {
                tagList = tagList.Take(maxTags).ToList();

                if (prefix)
                    return string.Join(" ", tagList.Select(t => $"#{t}")) + $" +{tagList.Count - maxTags} more";
                else
                    return string.Join(" ", tagList) + $" +{tagList.Count - maxTags} more";
            }

            return prefix
                ? string.Join(" ", tagList.Select(t => $"#{t}"))
                : string.Join(" ", tagList);
        }

        #endregion

        #region Migration Helpers

        /// <summary>
        /// Migrates legacy tags to the new category system
        /// </summary>
        public static List<TagWithCategory> MigrateLegacyTags(
            IEnumerable<string> legacyTags,
            string defaultCategory = "Uncategorized")
        {
            if (legacyTags == null)
                return new List<TagWithCategory>();

            return legacyTags
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => new TagWithCategory
                {
                    TagName = NormalizeTag(tag),
                    Category = defaultCategory
                })
                .Where(tag => !string.IsNullOrWhiteSpace(tag.TagName))
                .ToList();
        }

        #endregion
    }

    /// <summary>
    /// Data structure for tag frequency information with categories
    /// </summary>
    public class TagFrequencyData
    {
        public string TagName { get; set; }
        public string Category { get; set; }
        public int Count { get; set; }
    }

    /// <summary>
    /// Tag operation enum
    /// </summary>
    public enum TagOperation
    {
        Add,
        Remove,
        Replace,
        Intersect
    }
    

}