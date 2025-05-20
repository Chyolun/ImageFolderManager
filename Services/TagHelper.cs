using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;

namespace ImageFolderManager.Services
{
    /// <summary>
    /// Comprehensive helper class for tag-related operations throughout the application
    /// </summary>
    public static class TagHelper
    {
        private static readonly Regex InvalidTagCharacters = new Regex(@"[\\/:*?""<>|]", RegexOptions.Compiled);
        private const int MaxTagLength = 50;

        #region Basic Tag Operations

        /// <summary>
        /// Parses a string containing hash-separated tags into a collection of normalized tags
        /// </summary>
        /// <param name="input">The input string containing tags (e.g., "#nature #animals #photography")</param>
        /// <param name="removeDuplicates">Whether to remove duplicate tags (case-insensitive)</param>
        /// <returns>A collection of parsed and normalized tags</returns>
        public static IEnumerable<string> ParseTags(string input, bool removeDuplicates = true)
        {
            if (string.IsNullOrWhiteSpace(input))
                return Enumerable.Empty<string>();

            var tags = input.Split(new[] { '#', ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(tag => NormalizeTag(tag))
                .Where(tag => !string.IsNullOrWhiteSpace(tag));

            return removeDuplicates
                ? tags.Distinct(StringComparer.OrdinalIgnoreCase)
                : tags;
        }

        /// <summary>
        /// Formats a collection of tags into a hash-separated string
        /// </summary>
        /// <param name="tags">The collection of tags</param>
        /// <returns>A formatted string (e.g., "#nature #animals #photography")</returns>
        public static string FormatTags(IEnumerable<string> tags)
        {
            if (tags == null || !tags.Any())
                return string.Empty;

            return string.Join(" ", tags.Select(tag => $"#{tag}"));
        }

        /// <summary>
        /// Normalizes a tag by trimming whitespace and removing invalid characters
        /// </summary>
        /// <param name="tag">The tag to normalize</param>
        /// <returns>A normalized tag</returns>
        public static string NormalizeTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
                return string.Empty;

            // Remove leading/trailing whitespace and any leading '#' character
            tag = tag.Trim().TrimStart('#');

            // Remove invalid characters
            tag = InvalidTagCharacters.Replace(tag, "");

            // Truncate if too long
            if (tag.Length > MaxTagLength)
                tag = tag.Substring(0, MaxTagLength);

            return tag;
        }

        /// <summary>
        /// Checks if a tag is valid (not empty and contains only valid characters)
        /// </summary>
        /// <param name="tag">The tag to validate</param>
        /// <returns>True if the tag is valid, false otherwise</returns>
        public static bool IsValidTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
                return false;

            tag = NormalizeTag(tag);
            return !string.IsNullOrWhiteSpace(tag) && !InvalidTagCharacters.IsMatch(tag);
        }

        #endregion

        #region Tag Collections Operations

        /// <summary>
        /// Merges two sets of tags, removing duplicates (case-insensitive)
        /// </summary>
        /// <param name="tags1">First set of tags</param>
        /// <param name="tags2">Second set of tags</param>
        /// <returns>Merged collection of tags without duplicates</returns>
        public static IEnumerable<string> MergeTags(IEnumerable<string> tags1, IEnumerable<string> tags2)
        {
            if (tags1 == null) tags1 = Enumerable.Empty<string>();
            if (tags2 == null) tags2 = Enumerable.Empty<string>();

            return tags1.Union(tags2, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Removes specified tags from a source collection (case-insensitive)
        /// </summary>
        /// <param name="sourceTags">Source collection of tags</param>
        /// <param name="tagsToRemove">Tags to remove</param>
        /// <returns>Collection with specified tags removed</returns>
        public static IEnumerable<string> RemoveTags(IEnumerable<string> sourceTags, IEnumerable<string> tagsToRemove)
        {
            if (sourceTags == null) return Enumerable.Empty<string>();
            if (tagsToRemove == null || !tagsToRemove.Any()) return sourceTags;

            return sourceTags.Except(tagsToRemove, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Finds common tags among multiple collections (case-insensitive)
        /// </summary>
        /// <param name="tagCollections">Multiple collections of tags</param>
        /// <returns>Collection of tags common to all input collections</returns>
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
        /// Updates an ObservableCollection of tags from a string input
        /// </summary>
        /// <param name="targetCollection">The collection to update</param>
        /// <param name="tagsInput">String containing tags separated by # symbols</param>
        /// <returns>True if the collection was modified</returns>
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

        #region Search and Analysis

        /// <summary>
        /// Parses search criteria for tags from a search string (terms starting with #)
        /// </summary>
        /// <param name="searchText">The search text to parse</param>
        /// <returns>Collection of tag search terms</returns>
        public static IEnumerable<string> ParseTagSearchTerms(string searchText)
        {
            if (string.IsNullOrWhiteSpace(searchText))
                return Enumerable.Empty<string>();

            return searchText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(term => term.StartsWith("#") && term.Length > 1)
                .Select(term => term.Substring(1).ToLowerInvariant()) // Remove # prefix and convert to lowercase
                .Where(term => !string.IsNullOrWhiteSpace(term));
        }

        /// <summary>
        /// Creates a predicate function to test if a folder matches tag search criteria
        /// </summary>
        /// <param name="tagSearchTerms">Collection of tag search terms</param>
        /// <returns>A predicate function that tests if a folder's tags match the search criteria</returns>
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
        /// Counts tag frequency across multiple collections
        /// </summary>
        /// <param name="tagCollections">Multiple collections of tags</param>
        /// <returns>Dictionary mapping tags to their frequency counts</returns>
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

        #endregion

        #region UI Helpers

        /// <summary>
        /// Suggests related tags based on current tags and a dictionary of tag frequencies
        /// </summary>
        /// <param name="currentTags">Current tags</param>
        /// <param name="tagFrequencies">Dictionary mapping tags to their frequencies</param>
        /// <param name="maxSuggestions">Maximum number of suggestions to return</param>
        /// <returns>Collection of suggested tags</returns>
        public static IEnumerable<string> SuggestRelatedTags(
            IEnumerable<string> currentTags,
            Dictionary<string, int> tagFrequencies,
            int maxSuggestions = 5)
        {
            if (currentTags == null || !currentTags.Any() || tagFrequencies == null || !tagFrequencies.Any())
                return Enumerable.Empty<string>();

            // Create a set of current tags to exclude from suggestions
            var currentTagSet = new HashSet<string>(currentTags, StringComparer.OrdinalIgnoreCase);

            // Filter out current tags and sort by frequency
            return tagFrequencies
                .Where(kvp => !currentTagSet.Contains(kvp.Key))
                .OrderByDescending(kvp => kvp.Value)
                .Take(maxSuggestions)
                .Select(kvp => kvp.Key);
        }

        /// <summary>
        /// Creates display text for a collection of tags
        /// </summary>
        /// <param name="tags">Collection of tags</param>
        /// <param name="prefix">Whether to include # prefix</param>
        /// <param name="maxTags">Maximum number of tags to include (0 for all)</param>
        /// <returns>Formatted string for display</returns>
        public static string CreateTagDisplayText(IEnumerable<string> tags, bool prefix = true, int maxTags = 0)
        {
            if (tags == null || !tags.Any())
                return string.Empty;

            var tagList = tags.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();

            if (maxTags > 0 && tagList.Count > maxTags)
            {
                // Truncate list and add indication
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
    }
}