using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
        private Dictionary<string, int> _cachedTagCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private bool _isFullUpdateNeeded = true;
        private int _lastFolderCount = 0;

        // Dispatcher for UI thread updates
        private readonly Dispatcher _dispatcher;

        // Configuration
        private const int MAX_TAGS_TO_DISPLAY = 75;
        private const double MIN_FONT_SIZE = 12;
        private const double MAX_FONT_SIZE = 24;
        private const double TAG_COUNT_THRESHOLD = 0.25; // Update cache if folder count changes by more than 25%

        public TagCloudViewModel()
        {
            // Store the dispatcher for thread-safe UI updates
            _dispatcher = Dispatcher.CurrentDispatcher;

            // Freeze the brushes for better performance
            foreach (var brush in _tagColors)
            {
                brush.Freeze();
            }
        }

        /// <summary>
        /// Updates the tag cloud based on folder data
        /// </summary>
        /// <param name="allFolders">Collection of folders to analyze for tags</param>
        /// <summary>
        /// Updates the tag cloud based on folder data
        /// </summary>
        /// <param name="allFolders">Collection of folders to analyze for tags</param>
        /// <param name="cancellationToken">Cancellation token for the operation</param>
        public async Task UpdateTagCloudAsync(IEnumerable<FolderInfo> allFolders, CancellationToken cancellationToken = default)
        {
            if (allFolders == null)
                return;

            try
            {
                // Use Task.Run to perform tag counting off the UI thread
                await Task.Run(async () =>
                {
                    try
                    {
                        // Check for cancellation
                        cancellationToken.ThrowIfCancellationRequested();

                        // Determine if we need a full update
                        bool shouldPerformFullUpdate = ShouldPerformFullUpdate(allFolders);

                        // Get tag counts
                        Dictionary<string, int> tagCounts = await GetTagCountsAsync(allFolders, shouldPerformFullUpdate);

                        // Check for cancellation again
                        cancellationToken.ThrowIfCancellationRequested();

                        // Create updated tag items
                        var updatedTags = await CreateTagItemsAsync(tagCounts, cancellationToken);

                        // Check for cancellation
                        if (cancellationToken.IsCancellationRequested)
                            return;

                        // Update UI on dispatcher thread
                        await UpdateUIAsync(updatedTags, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        // Task was canceled, this is expected
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
        /// Determines if a full tag count update is needed
        /// </summary>
        private bool ShouldPerformFullUpdate(IEnumerable<FolderInfo> allFolders)
        {
            int folderCount = allFolders.Count();
            bool forceFullUpdate = _isFullUpdateNeeded ||
                                 Math.Abs(folderCount - _lastFolderCount) / (double)Math.Max(1, _lastFolderCount) > TAG_COUNT_THRESHOLD;

            _lastFolderCount = folderCount;
            return forceFullUpdate;
        }

        /// <summary>
        /// Gets tag counts from folders, either by recounting or using cache
        /// </summary>
        private async Task<Dictionary<string, int>> GetTagCountsAsync(IEnumerable<FolderInfo> allFolders, bool forceFullUpdate)
        {
            if (forceFullUpdate)
            {
                // Get all folder tags
                var allFolderTags = allFolders.Select(f => f.Tags);

                // Use TagHelper to count tag frequency
                var tagCounts = TagHelper.CountTagFrequency(allFolderTags);

                // Update cache
                _cachedTagCounts = new Dictionary<string, int>(tagCounts, StringComparer.OrdinalIgnoreCase);
                _isFullUpdateNeeded = false;

                Debug.WriteLine($"Performed full tag count, found {tagCounts.Count} unique tags");
                return tagCounts;
            }
            else
            {
                // Use cached counts
                Debug.WriteLine("Using cached tag counts");
                return new Dictionary<string, int>(_cachedTagCounts, StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Creates TagCloudItem objects from tag counts
        /// </summary>
        private async Task<Dictionary<string, TagCloudItem>> CreateTagItemsAsync(
            Dictionary<string, int> tagCounts,
            CancellationToken cancellationToken)
        {
            // Sort tags by count and take top MAX_TAGS_TO_DISPLAY
            var sortedTags = tagCounts
                .OrderByDescending(pair => pair.Value)
                .Take(MAX_TAGS_TO_DISPLAY)
                .ToList();

            // Check for cancellation
            cancellationToken.ThrowIfCancellationRequested();

            // Calculate min/max for font scaling
            int minCount = sortedTags.Any() ? sortedTags.Min(t => t.Value) : 0;
            int maxCount = sortedTags.Any() ? sortedTags.Max(t => t.Value) : 0;

            // Create updated tags dictionary
            var updatedTags = new Dictionary<string, TagCloudItem>(StringComparer.OrdinalIgnoreCase);

            foreach (var tag in sortedTags)
            {
                // Check for cancellation periodically for responsiveness
                if (sortedTags.Count > 50 && updatedTags.Count % 50 == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                double fontSize = CalculateFontSize(tag.Value, minCount, maxCount);

                // Check if tag already exists
                if (_currentTags.TryGetValue(tag.Key, out var existingItem))
                {
                    // Update existing tag (keeping the same color)
                    existingItem.Count = tag.Value;
                    existingItem.FontSize = fontSize;
                    updatedTags[tag.Key] = existingItem;
                }
                else
                {
                    // Create new tag
                    updatedTags[tag.Key] = new TagCloudItem
                    {
                        Tag = tag.Key,
                        Count = tag.Value,
                        FontSize = fontSize,
                        Color = GetRandomColor()
                    };
                }
            }

            return updatedTags;
        }

        /// <summary>
        /// Updates the UI with the new tag items
        /// </summary>
        private async Task UpdateUIAsync(Dictionary<string, TagCloudItem> updatedTags, CancellationToken cancellationToken)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                // Check if we're still allowed to update
                if (!cancellationToken.IsCancellationRequested)
                {
                    UpdateTagItemsCollection(updatedTags);
                }
            }, DispatcherPriority.Background);
        }

        /// <summary>
        /// Updates the ObservableCollection with minimal changes
        /// </summary>
        private void UpdateTagItemsCollection(Dictionary<string, TagCloudItem> updatedTags)
        {
            try
            {
                // First approach: Check if there's a big difference in tags
                if (Math.Abs(_currentTags.Count - updatedTags.Count) > 10)
                {
                    // Many tags changed - more efficient to clear and rebuild
                    TagItems.Clear();

                    foreach (var tag in updatedTags.Values.OrderByDescending(t => t.Count))
                    {
                        TagItems.Add(tag);
                    }
                }
                else
                {
                    // Incremental update - remove items no longer present
                    var tagsToRemove = _currentTags.Keys.Except(updatedTags.Keys).ToList();
                    foreach (var tag in tagsToRemove)
                    {
                        var item = _currentTags[tag];
                        TagItems.Remove(item);
                    }

                    // Add new items, update existing properties for items already in collection
                    foreach (var tag in updatedTags.Values)
                    {
                        if (!_currentTags.ContainsKey(tag.Tag))
                        {
                            // Find insertion point to maintain sorted order
                            int index = 0;
                            while (index < TagItems.Count &&
                                  TagItems[index].Count >= tag.Count)
                            {
                                index++;
                            }

                            TagItems.Insert(index, tag);
                        }
                    }
                }

                // Update current tags reference
                _currentTags = new Dictionary<string, TagCloudItem>(updatedTags, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating TagItems collection: {ex.Message}");
            }
        }

        /// <summary>
        /// Forces a full recalculation on next update
        /// </summary>
        public void InvalidateCache()
        {
            _isFullUpdateNeeded = true;
            _cachedTagCounts.Clear();
        }

        /// <summary>
        /// Calculates the font size for a tag based on its frequency
        /// </summary>
        private double CalculateFontSize(int count, int minCount, int maxCount)
        {
            // Handle edge cases
            if (minCount == maxCount)
                return MIN_FONT_SIZE;

            if (minCount <= 0 || maxCount <= 0)
                return MIN_FONT_SIZE;

            // Use log scale for better distribution of sizes
            double logMin = Math.Log(minCount);
            double logMax = Math.Log(maxCount);
            double logCount = Math.Log(count);

            // Calculate size using logarithmic scaling
            return MIN_FONT_SIZE +
                  (logCount - logMin) * (MAX_FONT_SIZE - MIN_FONT_SIZE) / (logMax - logMin);
        }

        /// <summary>
        /// Gets a random color for a tag
        /// </summary>
        private SolidColorBrush GetRandomColor()
        {
            // Thread-safe random usage
            lock (_randomLock)
            {
                return _tagColors[_random.Next(_tagColors.Count)];
            }
        }

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion
    }
}