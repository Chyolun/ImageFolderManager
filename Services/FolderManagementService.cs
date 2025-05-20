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
using ImageFolderManager.Services;

namespace ImageFolderManager.Services
{
    /// <summary>
    /// Service for managing folders and file system events
    /// </summary>
    public class FolderManagementService : IDisposable
    {
        #region Fields

        // Services
        private readonly FolderTagService _tagService = new FolderTagService();

        // Configuration parameters
        private const int MAX_CONCURRENT_WATCHERS = 50;
        private const int EVENT_PROCESSING_DELAY_MS = 300;
        private const int MAX_EVENTS_PER_BATCH = 20;

        // Event callback
        private readonly Action<FolderInfo, FileSystemEventArgs, WatcherChangeTypes> _fileSystemEventCallback;

        // Track watched folders
        private readonly Dictionary<string, FileSystemWatcher> _watchers =
            new Dictionary<string, FileSystemWatcher>(StringComparer.OrdinalIgnoreCase);

        // Event processing
        private readonly ConcurrentQueue<FileSystemEventBatch> _pendingEvents =
            new ConcurrentQueue<FileSystemEventBatch>();
        private readonly ConcurrentDictionary<string, FileSystemEventBatch> _activeBatches =
            new ConcurrentDictionary<string, FileSystemEventBatch>(StringComparer.OrdinalIgnoreCase);
        private readonly TimeSpan _eventProcessingDelay = TimeSpan.FromMilliseconds(EVENT_PROCESSING_DELAY_MS);

        // Synchronization
        private readonly SemaphoreSlim _processingLock = new SemaphoreSlim(1, 1);
        private readonly object _watcherLock = new object();
        private CancellationTokenSource _processingCancellation;
        private Task _processingTask;
        private bool _isDisposed;

        #endregion

        #region Nested Classes

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
                Events = new ConcurrentDictionary<string, Tuple<FileSystemEventArgs, WatcherChangeTypes>>(
                    StringComparer.OrdinalIgnoreCase);
                CreationTime = DateTime.Now;
            }
        }

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the FolderManagementService
        /// </summary>
        /// <param name="fileSystemEventCallback">Callback for file system events</param>
        public FolderManagementService(
            Action<FolderInfo, FileSystemEventArgs, WatcherChangeTypes> fileSystemEventCallback = null)
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

        #region Folder Loading Methods

        /// <summary>
        /// Loads a root folder and its immediate subfolders
        /// </summary>
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
        /// Loads images for a folder
        /// </summary>
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
        private async Task TraverseDirectoriesAsync(
            string path,
            FolderInfo parent,
            List<FolderInfo> result,
            bool watchFolders)
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

                    // Setup event handlers
                    watcher.Created += (s, e) => SafelyHandleEvent(folder, e, WatcherChangeTypes.Created);
                    watcher.Deleted += (s, e) => SafelyHandleEvent(folder, e, WatcherChangeTypes.Deleted);
                    watcher.Renamed += (s, e) => SafelyHandleEvent(folder, e, WatcherChangeTypes.Renamed);
                    watcher.Changed += (s, e) => SafelyHandleEvent(folder, e, WatcherChangeTypes.Changed);
                    watcher.Error += (s, e) => HandleWatcherError(normalizedPath, e);

                    // Store the watcher
                    _watchers[normalizedPath] = watcher;
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

            // Try to recreate the watcher if needed
            lock (_watcherLock)
            {
                if (_watchers.TryGetValue(folderPath, out var watcher))
                {
                    try
                    {
                        // Dispose the current watcher
                        watcher.EnableRaisingEvents = false;
                        watcher.Dispose();
                        _watchers.Remove(folderPath);

                        // Only recreate if folder still exists
                        if (Directory.Exists(folderPath))
                        {
                            var folderInfo = new FolderInfo { FolderPath = folderPath };
                            WatchFolder(folderInfo);
                        }
                    }
                    catch (Exception resetEx)
                    {
                        Debug.WriteLine($"Error resetting watcher: {resetEx.Message}");
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
        public void UnwatchFolder(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath))
                return;

            string normalizedPath = PathService.NormalizePath(folderPath);

            lock (_watcherLock)
            {
                if (_isDisposed) return;

                if (_watchers.TryGetValue(normalizedPath, out var watcher))
                {
                    try
                    {
                        watcher.EnableRaisingEvents = false;
                        watcher.Dispose();
                        _watchers.Remove(normalizedPath);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error disposing watcher: {ex.Message}");
                    }
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

                foreach (var watcher in _watchers.Values)
                {
                    try
                    {
                        watcher.EnableRaisingEvents = false;
                        watcher.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error disposing watcher: {ex.Message}");
                    }
                }

                _watchers.Clear();
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
                foreach (var watcher in _watchers.Values)
                {
                    try
                    {
                        watcher.EnableRaisingEvents = false;
                        watcher.Dispose();
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