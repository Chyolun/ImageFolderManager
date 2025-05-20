using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ImageFolderManager.Models;

namespace ImageFolderManager.Services
{
    /// <summary>
    /// Unified service for folder management and monitoring
    /// </summary>
    public class FolderManagementService : IDisposable
    {
        #region Fields

        // Services
        private readonly FolderTagService _tagService = new FolderTagService();

        // Configuration parameters
        private const int MAX_CONCURRENT_WATCHERS = 100;
        private const int EVENT_PROCESSING_DELAY_MS = 300;
        private const int MAX_EVENTS_PER_BATCH = 20;
        private const int WATCHER_RESET_THRESHOLD = 5;

        // Cache paths
        private readonly string _thumbnailCachePath = Path.Combine(Path.GetTempPath(), "ImageFolderManager", "thumbnails");

        // Callback for file system events
        private Action<FolderInfo, FileSystemEventArgs, WatcherChangeTypes> _fileSystemEventCallback;

        // Track watched folders and their associated FileSystemWatcher instances
        private readonly Dictionary<string, WatcherInfo> _watchers = new Dictionary<string, WatcherInfo>(StringComparer.OrdinalIgnoreCase);

        // For handling event throttling and batching
        private readonly ConcurrentQueue<FileSystemEventBatch> _pendingEvents = new ConcurrentQueue<FileSystemEventBatch>();
        private readonly ConcurrentDictionary<string, FileSystemEventBatch> _activeBatches = new ConcurrentDictionary<string, FileSystemEventBatch>(StringComparer.OrdinalIgnoreCase);
        private readonly TimeSpan _eventProcessingDelay = TimeSpan.FromMilliseconds(EVENT_PROCESSING_DELAY_MS);

        // Synchronization objects
        private readonly SemaphoreSlim _processingLock = new SemaphoreSlim(1, 1);
        private readonly object _watcherLock = new object();
        private CancellationTokenSource _processingCancellation;
        private Task _processingTask;
        private bool _isDisposed;

        #endregion

        #region Nested Classes

        /// <summary>
        /// Represents information about a file system watcher, including error tracking
        /// </summary>
        private class WatcherInfo
        {
            public FileSystemWatcher Watcher { get; }
            public FolderInfo FolderInfo { get; }
            public int ErrorCount { get; set; }
            public DateTime LastReset { get; set; }

            public WatcherInfo(FileSystemWatcher watcher, FolderInfo folderInfo)
            {
                Watcher = watcher;
                FolderInfo = folderInfo;
                ErrorCount = 0;
                LastReset = DateTime.Now;
            }
        }

        /// <summary>
        /// Represents a batch of file system events for a specific folder
        /// </summary>
        private class FileSystemEventBatch
        {
            public string FolderPath { get; }
            public FolderInfo FolderInfo { get; }
            public ConcurrentDictionary<string, Tuple<FileSystemEventArgs, WatcherChangeTypes>> Events { get; }
            public DateTime CreationTime { get; }

            public FileSystemEventBatch(string folderPath, FolderInfo folderInfo)
            {
                FolderPath = folderPath;
                FolderInfo = folderInfo;
                Events = new ConcurrentDictionary<string, Tuple<FileSystemEventArgs, WatcherChangeTypes>>(StringComparer.OrdinalIgnoreCase);
                CreationTime = DateTime.Now;
            }
        }

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the FolderManagementService
        /// </summary>
        /// <param name="fileSystemEventCallback">Callback for file system events</param>
        public FolderManagementService(Action<FolderInfo, FileSystemEventArgs, WatcherChangeTypes> fileSystemEventCallback = null)
        {
            _fileSystemEventCallback = fileSystemEventCallback;
            _processingCancellation = new CancellationTokenSource();

            // Start the background event processing task if a callback is provided
            if (_fileSystemEventCallback != null)
            {
                _processingTask = Task.Run(ProcessEventsLoopAsync);

                // Setup application exit handler to ensure proper cleanup
                Application.Current.Exit += (s, e) => Dispose();
            }
        }

        #endregion

        #region Folder Loading and Creation Methods

        /// <summary>
        /// Loads a root folder and its immediate subfolders
        /// </summary>
        /// <param name="path">Path to the root folder</param>
        /// <returns>A FolderInfo object representing the root folder</returns>
        public async Task<FolderInfo> LoadRootFolderAsync(string path)
        {
            var root = await CreateFolderInfoWithoutImagesAsync(path);
            await LoadSubfoldersAsync(root);

            // Start watching the root folder
            WatchFolder(root);

            return root;
        }

        /// <summary>
        /// Loads the immediate subfolders of a parent folder
        /// </summary>
        /// <param name="parent">Parent folder</param>
        public async Task LoadSubfoldersAsync(FolderInfo parent)
        {
            try
            {
                var subDirs = Directory.GetDirectories(parent.FolderPath);
                foreach (var dir in subDirs)
                {
                    var child = await CreateFolderInfoWithoutImagesAsync(dir);
                    child.Parent = parent;
                    parent.Children.Add(child);

                    // Start watching each subfolder
                    WatchFolder(child);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading folder '{parent.FolderPath}': {ex.Message}");
            }
        }

        /// <summary>
        /// Creates a FolderInfo object without loading images
        /// </summary>
        /// <param name="path">Folder path</param>
        /// <param name="loadImages">Whether to load images in the background</param>
        /// <returns>A FolderInfo object</returns>
        public async Task<FolderInfo> CreateFolderInfoWithoutImagesAsync(string path, bool loadImages = false)
        {
            var folder = new FolderInfo
            {
                FolderPath = path,
                Children = new ObservableCollection<FolderInfo>(),
                Images = new ObservableCollection<ImageInfo>(),
                Tags = new ObservableCollection<string>(await _tagService.GetTagsForFolderAsync(path)),
                Rating = await _tagService.GetRatingForFolderAsync(path)
            };

            if (loadImages)
            {
                _ = LoadImagesAsync(folder);
            }

            return folder;
        }

        /// <summary>
        /// Loads images for a folder in the background
        /// </summary>
        /// <param name="folder">The folder to load images for</param>
        public async Task LoadImagesAsync(FolderInfo folder)
        {
            var supportedExtensions = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" };

            if (!Directory.Exists(folder.FolderPath)) return;

            var images = new List<ImageInfo>();

            try
            {
                foreach (var file in Directory.GetFiles(folder.FolderPath))
                {
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (Array.Exists(supportedExtensions, e => e == ext))
                    {
                        var imageInfo = new ImageInfo { FilePath = file };
                        await imageInfo.LoadThumbnailAsync();
                        images.Add(imageInfo);
                    }
                }

                // Update UI on the main thread
                Application.Current.Dispatcher.Invoke(() =>
                {
                    folder.Images.Clear();
                    foreach (var img in images)
                    {
                        folder.Images.Add(img);
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading images for {folder.FolderPath}: {ex.Message}");
            }
        }

        /// <summary>
        /// Recursively loads all folders under a root path
        /// </summary>
        /// <param name="rootPath">Root path to start loading from</param>
        /// <param name="watchFolders">Whether to watch loaded folders for changes</param>
        /// <returns>A list of all loaded folders</returns>
        public async Task<List<FolderInfo>> LoadFoldersRecursivelyAsync(string rootPath, bool watchFolders = false)
        {
            // Before starting a recursive scan, disable caching in the tag service
            bool originalCachingSetting = _tagService.EnableCaching;
            _tagService.EnableCaching = false;

            var result = new List<FolderInfo>();

            try
            {
                // Clear any existing cache
                _tagService.ClearCache();

                await TraverseDirectoriesAsync(rootPath, null, result, watchFolders);
            }
            finally
            {
                // Restore original caching setting
                _tagService.EnableCaching = originalCachingSetting;
            }

            return result;
        }

        /// <summary>
        /// Recursively traverses a directory structure
        /// </summary>
        private async Task TraverseDirectoriesAsync(string path, FolderInfo parent, List<FolderInfo> result, bool watchFolders)
        {
            if (!PathService.DirectoryExists(path))
            {
                return;
            }

            try
            {
                // Create the folder info
                var folder = new FolderInfo
                {
                    FolderPath = path,
                    Parent = parent,
                    Children = new ObservableCollection<FolderInfo>(),
                    Images = new ObservableCollection<ImageInfo>(),
                    Tags = new ObservableCollection<string>(await _tagService.GetTagsForFolderAsync(path)),
                    Rating = await _tagService.GetRatingForFolderAsync(path)
                };

                // Add to results
                result.Add(folder);

                // Watch folder if requested
                if (watchFolders)
                {
                    WatchFolder(folder);
                }

                // Process subdirectories
                string[] subDirectories;
                try
                {
                    subDirectories = Directory.GetDirectories(path);            
                }
                catch (UnauthorizedAccessException)
                {
                    // Skip directories we can't access
                    return;
                }

                // Process each subdirectory
                foreach (var subDir in subDirectories)
                {
                    await TraverseDirectoriesAsync(subDir, folder, result, watchFolders);
                }
            }
            catch (Exception ex)
            {
                // Log error but continue processing other directories
                Debug.WriteLine($"Error processing directory {path}: {ex.Message}");
            }
        }

        #endregion

        #region FileSystemWatcher Methods

        /// <summary>
        /// Starts watching a folder for file system changes
        /// </summary>
        /// <param name="folder">The folder to watch</param>
        public void WatchFolder(FolderInfo folder)
        {
            if (_fileSystemEventCallback == null)
                return; // No callback, no need to watch

            if (folder == null || string.IsNullOrEmpty(folder.FolderPath) ||
                !PathService.DirectoryExists(folder.FolderPath))
                return;

            string normalizedPath = PathService.NormalizePath(folder.FolderPath);

            lock (_watcherLock)
            {
                if (_isDisposed) return;

                // Check if we're already watching this folder
                if (_watchers.ContainsKey(normalizedPath))
                    return;

                // Enforce maximum watchers limit
                if (_watchers.Count >= MAX_CONCURRENT_WATCHERS)
                {
                    Debug.WriteLine($"Maximum number of watchers ({MAX_CONCURRENT_WATCHERS}) reached, not watching: {normalizedPath}");
                    return;
                }

                try
                {
                    var watcher = new FileSystemWatcher
                    {
                        Path = normalizedPath,
                        NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName |
                                    NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                        Filter = "*.*",
                        IncludeSubdirectories = false,
                        EnableRaisingEvents = true
                    };

                    // Setup event handlers with error handling
                    watcher.Created += (s, e) => SafelyHandleEvent(folder, e, WatcherChangeTypes.Created);
                    watcher.Deleted += (s, e) => SafelyHandleEvent(folder, e, WatcherChangeTypes.Deleted);
                    watcher.Renamed += (s, e) => SafelyHandleEvent(folder, e, WatcherChangeTypes.Renamed);
                    watcher.Changed += (s, e) => SafelyHandleEvent(folder, e, WatcherChangeTypes.Changed);
                    watcher.Error += (s, e) => HandleWatcherError(normalizedPath, e);

                    // Store the watcher
                    _watchers[normalizedPath] = new WatcherInfo(watcher, folder);

                    Debug.WriteLine($"Started watching folder: {normalizedPath}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error setting up watcher for {normalizedPath}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Safely handles a file system event by adding it to the event queue
        /// </summary>
        private void SafelyHandleEvent(FolderInfo folder, FileSystemEventArgs e, WatcherChangeTypes changeType)
        {
            try
            {
                string folderPath = folder.FolderPath;
                string filePath = e.FullPath;

                // Get or create batch for this folder
                var batch = _activeBatches.GetOrAdd(folderPath, _ => new FileSystemEventBatch(folderPath, folder));

                // Add or update event in batch
                batch.Events[filePath] = new Tuple<FileSystemEventArgs, WatcherChangeTypes>(e, changeType);

                // If this batch is not in the queue yet and it's the first event, add it
                if (batch.Events.Count == 1)
                {
                    _pendingEvents.Enqueue(batch);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error handling file system event: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles errors in the FileSystemWatcher
        /// </summary>
        private void HandleWatcherError(string folderPath, ErrorEventArgs e)
        {
            Exception ex = e.GetException();
            Debug.WriteLine($"FileSystemWatcher error for {folderPath}: {ex.Message}");

            lock (_watcherLock)
            {
                if (_watchers.TryGetValue(folderPath, out var watcherInfo))
                {
                    // Increment error count
                    watcherInfo.ErrorCount++;

                    // If we've hit threshold, try to reset watcher
                    if (watcherInfo.ErrorCount >= WATCHER_RESET_THRESHOLD)
                    {
                        // Only reset if last reset was more than 30 seconds ago
                        if ((DateTime.Now - watcherInfo.LastReset).TotalSeconds > 30)
                        {
                            Debug.WriteLine($"Resetting watcher for {folderPath} after {watcherInfo.ErrorCount} errors");

                            try
                            {
                                // Dispose and recreate watcher
                                var oldWatcher = watcherInfo.Watcher;
                                oldWatcher.EnableRaisingEvents = false;
                                oldWatcher.Dispose();

                                // Only recreate if folder still exists
                                if (Directory.Exists(folderPath))
                                {
                                    WatchFolder(watcherInfo.FolderInfo);
                                }
                                else
                                {
                                    _watchers.Remove(folderPath);
                                }
                            }
                            catch (Exception resetEx)
                            {
                                Debug.WriteLine($"Error resetting watcher: {resetEx.Message}");
                            }
                            finally
                            {
                                watcherInfo.ErrorCount = 0;
                                watcherInfo.LastReset = DateTime.Now;
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Processes events in a continuous loop
        /// </summary>
        private async Task ProcessEventsLoopAsync()
        {
            while (!_processingCancellation.IsCancellationRequested)
            {
                try
                {
                    // Wait for delay to batch events
                    await Task.Delay(_eventProcessingDelay, _processingCancellation.Token)
                        .ConfigureAwait(false);

                    // Process batches of events
                    await ProcessPendingEventsAsync()
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Normal cancellation
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error in event processing loop: {ex.Message}");

                    // Wait before continuing to avoid tight loop in error cases
                    await Task.Delay(1000, _processingCancellation.Token)
                        .ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Processes all pending file system events
        /// </summary>
        private async Task ProcessPendingEventsAsync()
        {
            // Use lock to ensure only one processing operation at a time
            if (!await _processingLock.WaitAsync(0).ConfigureAwait(false))
                return;

            try
            {
                // Process up to 10 batches at a time
                int batchCount = 0;
                var processedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                while (batchCount < 10 && _pendingEvents.TryDequeue(out var batch))
                {
                    batchCount++;
                    string folderPath = batch.FolderPath;

                    // Skip if already processed this folder in this cycle
                    if (processedFolders.Contains(folderPath))
                        continue;

                    processedFolders.Add(folderPath);

                    // Remove from active batches
                    _activeBatches.TryRemove(folderPath, out _);

                    // Skip if folder doesn't exist anymore or has too many events
                    if (!Directory.Exists(folderPath) || batch.Events.Count > 100)
                    {
                        Debug.WriteLine($"Skipping batch for {folderPath}: " +
                            (!Directory.Exists(folderPath) ? "Folder no longer exists" : $"Too many events ({batch.Events.Count})"));
                        continue;
                    }

                    // Process a limited number of events per batch
                    var events = batch.Events.Values.Take(MAX_EVENTS_PER_BATCH).ToList();

                    // Process events on UI thread
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        foreach (var eventItem in events)
                        {
                            try
                            {
                                _fileSystemEventCallback?.Invoke(batch.FolderInfo, eventItem.Item1, eventItem.Item2);
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"Error in event callback: {ex.Message}");
                            }
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error processing events: {ex.Message}");
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Stops watching a folder
        /// </summary>
        /// <param name="folderPath">Path of the folder to stop watching</param>
        public void UnwatchFolder(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath))
                return;

            string normalizedPath = PathService.NormalizePath(folderPath);

            lock (_watcherLock)
            {
                if (_isDisposed) return;

                if (_watchers.TryGetValue(normalizedPath, out var watcherInfo))
                {
                    try
                    {
                        watcherInfo.Watcher.EnableRaisingEvents = false;
                        watcherInfo.Watcher.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error disposing watcher: {ex.Message}");
                    }
                    finally
                    {
                        _watchers.Remove(normalizedPath);
                    }
                }

                // Also unwatch any subfolders
                var subfoldersToUnwatch = _watchers.Keys
                    .Where(path => PathService.IsPathWithin(normalizedPath, path))
                    .ToList();

                foreach (var subPath in subfoldersToUnwatch)
                {
                    UnwatchFolder(subPath);
                }
            }
        }


        /// <summary>
        /// Stops watching all folders
        /// </summary>
        public void UnwatchAllFolders()
        {
            lock (_watcherLock)
            {
                if (_isDisposed) return;

                foreach (var watcherInfo in _watchers.Values)
                {
                    try
                    {
                        watcherInfo.Watcher.EnableRaisingEvents = false;
                        watcherInfo.Watcher.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error disposing watcher: {ex.Message}");
                    }
                }

                _watchers.Clear();
            }
        }

        /// <summary>
        /// Returns a list of currently watched folders
        /// </summary>
        public List<string> GetWatchedFolders()
        {
            lock (_watcherLock)
            {
                return _watchers.Keys.ToList();
            }
        }

        #endregion

        #region IDisposable Implementation

        /// <summary>
        /// Disposes resources used by the service
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed)
                return;

            lock (_watcherLock)
            {
                _isDisposed = true;

                // Cancel the processing task
                try
                {
                    _processingCancellation?.Cancel();
                    _processingTask?.Wait(1000);
                }
                catch { /* Ignore exceptions during shutdown */ }

                // Dispose all watchers
                foreach (var watcherInfo in _watchers.Values)
                {
                    try
                    {
                        watcherInfo.Watcher.EnableRaisingEvents = false;
                        watcherInfo.Watcher.Dispose();
                    }
                    catch { /* Ignore exceptions during shutdown */ }
                }

                _watchers.Clear();
                _activeBatches.Clear();

                // Dispose synchronization objects
                _processingLock?.Dispose();
                _processingCancellation?.Dispose();

                GC.SuppressFinalize(this);
            }
        }

        #endregion
    }
}