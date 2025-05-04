using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    /// Service for monitoring file system changes in folders with advanced event batching and throttling
    /// </summary>
    public class FileSystemWatcherService : IDisposable
    {
        // Configuration parameters
        private const int MAX_CONCURRENT_WATCHERS = 100;
        private const int EVENT_PROCESSING_DELAY_MS = 300;
        private const int MAX_EVENTS_PER_BATCH = 20;
        private const int WATCHER_RESET_THRESHOLD = 5;

        // Track watched folders and their associated FileSystemWatcher instances
        private readonly Dictionary<string, WatcherInfo> _watchers = new Dictionary<string, WatcherInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly FolderService _folderService;
        private readonly Action<FolderInfo, FileSystemEventArgs, WatcherChangeTypes> _callbackAction;

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

        /// <summary>
        /// Initializes a new instance of the FileSystemWatcherService
        /// </summary>
        /// <param name="folderService">Service for folder operations</param>
        /// <param name="callbackAction">Action to call when file system events occur</param>
        public FileSystemWatcherService(FolderService folderService, Action<FolderInfo, FileSystemEventArgs, WatcherChangeTypes> callbackAction)
        {
            _folderService = folderService ?? throw new ArgumentNullException(nameof(folderService));
            _callbackAction = callbackAction ?? throw new ArgumentNullException(nameof(callbackAction));
            _processingCancellation = new CancellationTokenSource();

            // Start the background event processing task
            _processingTask = Task.Run(ProcessEventsLoopAsync);

            // Setup application exit handler to ensure proper cleanup
            Application.Current.Exit += (s, e) => Dispose();
        }

        /// <summary>
        /// Starts watching a folder for file system changes
        /// </summary>
        /// <param name="folder">The folder to watch</param>
        public void WatchFolder(FolderInfo folder)
        {
            if (folder == null || string.IsNullOrEmpty(folder.FolderPath) || !Directory.Exists(folder.FolderPath))
                return;

            string normalizedPath = NormalizePath(folder.FolderPath);

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
                    await Task.Delay(_eventProcessingDelay, _processingCancellation.Token);

                    // Process batches of events
                    await ProcessPendingEventsAsync();
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
                    await Task.Delay(1000, _processingCancellation.Token);
                }
            }
        }

        /// <summary>
        /// Processes all pending file system events
        /// </summary>
        private async Task ProcessPendingEventsAsync()
        {
            // Use lock to ensure only one processing operation at a time
            if (!await _processingLock.WaitAsync(0))
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
                                _callbackAction?.Invoke(batch.FolderInfo, eventItem.Item1, eventItem.Item2);
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

            string normalizedPath = NormalizePath(folderPath);

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
                    .Where(path => path.StartsWith(normalizedPath + Path.DirectorySeparatorChar))
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
        /// Normalizes a path for consistent comparison
        /// </summary>
        private string NormalizePath(string path)
        {
            return path?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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
    }
}