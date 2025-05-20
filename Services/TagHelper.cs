using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;

namespace ImageFolderManager.Services
{
    /// <summary>
    /// Helper class for tag-related operations
    /// </summary>
    public static class TagHelper
    {
        private static readonly Regex InvalidTagCharacters = new Regex(@"[\\/:*?""<>|]", RegexOptions.Compiled);
        private const int MaxTagLength = 50;

        #region Basic Tag Operations

        /// <summary>
        /// Parses a string containing tags
        /// </summary>
        /// <param name="input">Input string containing tags (e.g., "#nature #animals")</param>
        /// <param name="removeDuplicates">Whether to remove duplicate tags</param>
        /// <returns>Collection of normalized tags</returns>
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
        public static string FormatTags(IEnumerable<string> tags)
        {
            if (tags == null || !tags.Any())
                return string.Empty;

            return string.Join(" ", tags.Select(tag => $"#{tag}"));
        }

        /// <summary>
        /// Normalizes a tag by trimming whitespace and removing invalid characters
        /// </summary>
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
        /// Checks if a tag is valid
        /// </summary>
        public static bool IsValidTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
                return false;

            tag = NormalizeTag(tag);
            return !string.IsNullOrWhiteSpace(tag) && !InvalidTagCharacters.IsMatch(tag);
        }

        #endregion

        #region Tag Collection Operations

        /// <summary>
        /// Modifies a tag collection (merge, remove, or replace)
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
        /// Finds common tags among multiple collections
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
        /// Updates an ObservableCollection of tags from a string input
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

        #region Search and Analysis

        /// <summary>
        /// Parses search criteria for tags from a search string (terms starting with #)
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
        /// Creates a predicate function to test if a folder matches tag search criteria
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
        /// Counts tag frequency across multiple collections
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

        #endregion

        #region UI Helpers

        /// <summary>
        /// Creates display text for a collection of tags
        /// </summary>
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