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
using System.Windows.Threading;
using ImageFolderManager.Models;
using Timer = System.Threading.Timer;

namespace ImageFolderManager.Services
{
    /// <summary>
    /// Unified service that combines folder management and real-time indexing
    /// Replaces both FolderManagementService and FileSystemIndexingService
    /// </summary>
    public class UnifiedFolderService : IDisposable
    {
        #region Events and Delegates

        public event Action<string> FolderCreated;
        public event Action<string> FolderDeleted;
        public event Action<string, string> FolderRenamed; // oldPath, newPath
        public event Action<List<string>> IndexRebuilt;

        // Legacy callback for existing MainViewModel integration
        public event Action<FolderInfo, FileSystemEventArgs, WatcherChangeTypes> FileSystemEvent;

        #endregion

        #region Fields and Properties

        // Services
        private readonly FolderTagService _tagService = new FolderTagService();

        // Single file system watcher for the entire tree
        private FileSystemWatcher _rootWatcher;

        // Thread-safe folder index
        private readonly ConcurrentDictionary<string, FolderIndexEntry> _folderIndex =
            new ConcurrentDictionary<string, FolderIndexEntry>(StringComparer.OrdinalIgnoreCase);

        // Event processing
        private readonly ConcurrentQueue<FileSystemEventData> _eventQueue = new ConcurrentQueue<FileSystemEventData>();
        private readonly ConcurrentDictionary<string, DateTime> _lastEventTime =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Timer _eventProcessingTimer;
        private readonly object _processingLock = new object();

        // Configuration
        private string _rootDirectory;
        private bool _isIndexing = false;
        private bool _isDisposed = false;
        private readonly TimeSpan _debounceInterval = TimeSpan.FromMilliseconds(200);
        private readonly Dispatcher _dispatcher;

        // Properties
        public IReadOnlyCollection<string> IndexedFolders => _folderIndex.Keys.ToList();
        public string RootDirectory => _rootDirectory;
        public bool IsIndexing => _isIndexing;
        public int IndexedFolderCount => _folderIndex.Count;

        #endregion

        #region Nested Classes

        private class FolderIndexEntry
        {
            public string FullPath { get; set; }
            public string Name { get; set; }
            public string ParentPath { get; set; }
            public DateTime LastModified { get; set; }
            public bool Exists { get; set; } = true;

            public FolderIndexEntry(string fullPath)
            {
                FullPath = PathService.NormalizePath(fullPath);
                Name = Path.GetFileName(FullPath);
                ParentPath = Path.GetDirectoryName(FullPath);
                LastModified = Directory.Exists(FullPath) ? Directory.GetLastWriteTime(FullPath) : DateTime.MinValue;
            }
        }

        private class FileSystemEventData
        {
            public WatcherChangeTypes ChangeType { get; set; }
            public string FullPath { get; set; }
            public string OldPath { get; set; } // For rename events
            public DateTime Timestamp { get; set; } = DateTime.Now;
        }

        #endregion

        #region Constructor

        public UnifiedFolderService()
        {
            _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

            // Initialize event processing timer (processes events every 100ms)
            _eventProcessingTimer = new Timer(ProcessEventQueue, null,
                TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));

            // Setup application exit handler
            if (Application.Current != null)
            {
                Application.Current.Exit += (s, e) => Dispose();
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Starts monitoring a root directory and builds initial index
        /// </summary>
        public async Task StartMonitoringAsync(string rootDirectory, bool buildInitialIndex = true)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(UnifiedFolderService));

            if (string.IsNullOrEmpty(rootDirectory) || !Directory.Exists(rootDirectory))
                throw new ArgumentException("Invalid root directory", nameof(rootDirectory));

            // Stop existing monitoring
            await StopMonitoringAsync();

            _rootDirectory = PathService.NormalizePath(rootDirectory);
            Debug.WriteLine($"Starting unified folder monitoring for: {_rootDirectory}");

            try
            {
                // Build initial index if requested
                if (buildInitialIndex)
                {
                    await BuildInitialIndexAsync();
                }

                // Setup single file system watcher for entire tree
                SetupFileSystemWatcher();

                Debug.WriteLine($"Unified folder monitoring started. Indexed {_folderIndex.Count} folders.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error starting unified folder monitoring: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Stops monitoring and clears the index
        /// </summary>
        public async Task StopMonitoringAsync()
        {
            if (_rootWatcher != null)
            {
                try
                {
                    _rootWatcher.EnableRaisingEvents = false;
                    _rootWatcher.Dispose();
                    _rootWatcher = null;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error stopping file system watcher: {ex.Message}");
                }
            }

            // Clear the index and events
            _folderIndex.Clear();
            _lastEventTime.Clear();
            while (_eventQueue.TryDequeue(out _)) { }

            _rootDirectory = null;
            Debug.WriteLine("Unified folder monitoring stopped.");
        }

        /// <summary>
        /// Rebuilds the entire folder index
        /// </summary>
        public async Task RebuildIndexAsync()
        {
            if (_isIndexing || string.IsNullOrEmpty(_rootDirectory))
                return;

            Debug.WriteLine("Forcing index rebuild...");
            _folderIndex.Clear();
            await BuildInitialIndexAsync();
        }

        #endregion

        #region Folder Management Methods (from FolderManagementService)

        /// <summary>
        /// Creates a FolderInfo without loading images
        /// </summary>
        public async Task<FolderInfo> CreateFolderInfoWithoutImagesAsync(string path, bool loadImages = false)
        {
            var folder = new FolderInfo
            {
                FolderPath = PathService.NormalizePath(path),
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
        /// Loads all folders recursively from a root path
        /// </summary>
        public async Task<List<FolderInfo>> LoadFoldersRecursivelyAsync(string rootPath, bool watchFolders = false)
        {
            // Disable caching during bulk operations
            bool originalCachingSetting = _tagService.EnableCaching;
            _tagService.EnableCaching = false;

            var result = new List<FolderInfo>();

            try
            {
                _tagService.ClearCache();
                await TraverseDirectoriesAsync(rootPath, null, result);
            }
            finally
            {
                _tagService.EnableCaching = originalCachingSetting;
            }

            return result;
        }

        private async Task TraverseDirectoriesAsync(string path, FolderInfo parent, List<FolderInfo> result)
        {
            if (!PathService.DirectoryExists(path))
                return;

            try
            {
                var folder = new FolderInfo
                {
                    FolderPath = PathService.NormalizePath(path),
                    Parent = parent,
                    Children = new ObservableCollection<FolderInfo>(),
                    Images = new ObservableCollection<ImageInfo>(),
                    Tags = new ObservableCollection<string>(await _tagService.GetTagsForFolderAsync(path)),
                    Rating = await _tagService.GetRatingForFolderAsync(path)
                };

                result.Add(folder);

                // Process subdirectories
                try
                {
                    var subDirectories = Directory.GetDirectories(path);
                    foreach (var subDir in subDirectories)
                    {
                        await TraverseDirectoriesAsync(subDir, folder, result);
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // Skip directories we can't access
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error processing directory {path}: {ex.Message}");
            }
        }

        #endregion

        #region Search and Query Methods (from FileSystemIndexingService)

        /// <summary>
        /// Searches for folders matching a pattern
        /// </summary>
        public List<string> SearchFolders(string searchPattern)
        {
            if (string.IsNullOrWhiteSpace(searchPattern))
                return _folderIndex.Keys.ToList();

            var pattern = searchPattern.ToLowerInvariant();

            return _folderIndex.Values
                .Where(entry => entry.Name.ToLowerInvariant().Contains(pattern) ||
                               entry.FullPath.ToLowerInvariant().Contains(pattern))
                .Select(entry => entry.FullPath)
                .ToList();
        }

        /// <summary>
        /// Gets child folders of a parent path
        /// </summary>
        public List<string> GetChildFolders(string parentPath)
        {
            var normalizedParent = PathService.NormalizePath(parentPath);

            return _folderIndex.Values
                .Where(entry => string.Equals(entry.ParentPath, normalizedParent, StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.FullPath)
                .ToList();
        }

        /// <summary>
        /// Checks if a folder is indexed
        /// </summary>
        public bool IsFolderIndexed(string folderPath)
        {
            var normalizedPath = PathService.NormalizePath(folderPath);
            return _folderIndex.ContainsKey(normalizedPath);
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Builds the initial folder index
        /// </summary>
        private async Task BuildInitialIndexAsync()
        {
            if (_isIndexing)
                return;

            _isIndexing = true;

            try
            {
                Debug.WriteLine("Building initial folder index...");

                var allFolders = new List<string>();

                await Task.Run(() =>
                {
                    try
                    {
                        ScanDirectoryRecursive(_rootDirectory, allFolders);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error during initial index build: {ex.Message}");
                    }
                });

                // Add all folders to index
                foreach (var folderPath in allFolders)
                {
                    var entry = new FolderIndexEntry(folderPath);
                    _folderIndex.TryAdd(entry.FullPath, entry);
                }

                Debug.WriteLine($"Initial index built. {allFolders.Count} folders indexed.");

                // Notify listeners
                _dispatcher.BeginInvoke(() =>
                {
                    IndexRebuilt?.Invoke(allFolders);
                });
            }
            finally
            {
                _isIndexing = false;
            }
        }

        /// <summary>
        /// Recursively scans directories
        /// </summary>
        private void ScanDirectoryRecursive(string directoryPath, List<string> allFolders)
        {
            try
            {
                allFolders.Add(directoryPath);

                var subdirectories = Directory.GetDirectories(directoryPath);
                foreach (var subdirectory in subdirectories)
                {
                    try
                    {
                        ScanDirectoryRecursive(subdirectory, allFolders);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        Debug.WriteLine($"Skipping inaccessible directory: {subdirectory}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error scanning directory {subdirectory}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error scanning directory {directoryPath}: {ex.Message}");
            }
        }

        /// <summary>
        /// Sets up the unified file system watcher
        /// </summary>
        private void SetupFileSystemWatcher()
        {
            try
            {
                _rootWatcher = new FileSystemWatcher
                {
                    Path = _rootDirectory,
                    IncludeSubdirectories = true, // Monitor entire tree with single watcher
                    NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName |
                                  NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                    Filter = "*.*",
                    EnableRaisingEvents = false
                };

                // Subscribe to events
                _rootWatcher.Created += OnFileSystemEvent;
                _rootWatcher.Deleted += OnFileSystemEvent;
                _rootWatcher.Renamed += OnFileSystemRenamed;
                _rootWatcher.Changed += OnFileSystemEvent;
                _rootWatcher.Error += OnWatcherError;

                // Start monitoring
                _rootWatcher.EnableRaisingEvents = true;

                Debug.WriteLine("Unified file system watcher setup completed");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error setting up unified file system watcher: {ex.Message}");
                throw;
            }
        }

        #endregion

        #region Event Handlers

        private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
        {
            if (IsDirectory(e.FullPath) || WasDirectory(e.FullPath))
            {
                QueueEvent(new FileSystemEventData
                {
                    ChangeType = e.ChangeType,
                    FullPath = e.FullPath
                });
            }
        }

        private void OnFileSystemRenamed(object sender, RenamedEventArgs e)
        {
            if (IsDirectory(e.FullPath) || WasDirectory(e.OldFullPath))
            {
                QueueEvent(new FileSystemEventData
                {
                    ChangeType = WatcherChangeTypes.Renamed,
                    FullPath = e.FullPath,
                    OldPath = e.OldFullPath
                });
            }
        }

        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            Exception ex = e.GetException();
            Debug.WriteLine($"Unified file system watcher error: {ex.Message}");

            // Try to restart the watcher
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1000);

                    if (!_isDisposed && !string.IsNullOrEmpty(_rootDirectory))
                    {
                        SetupFileSystemWatcher();
                        Debug.WriteLine("Unified file system watcher restarted after error");
                    }
                }
                catch (Exception restartEx)
                {
                    Debug.WriteLine($"Error restarting unified file system watcher: {restartEx.Message}");
                }
            });
        }

        private void QueueEvent(FileSystemEventData eventData)
        {
            // Debounce rapid events for the same path
            if (ShouldDebounceEvent(eventData.FullPath))
                return;

            _lastEventTime[eventData.FullPath] = DateTime.Now;
            _eventQueue.Enqueue(eventData);
        }

        private bool ShouldDebounceEvent(string path)
        {
            if (_lastEventTime.TryGetValue(path, out var lastTime))
            {
                var timeSince = DateTime.Now - lastTime;
                if (timeSince < _debounceInterval)
                {
                    Debug.WriteLine($"Debouncing event for {path}, time since last event: {timeSince.TotalMilliseconds}ms");
                    return true;
                }
            }
            return false;
        }

        private void ProcessEventQueue(object state)
        {
            if (_isDisposed || !Monitor.TryEnter(_processingLock))
                return;

            try
            {
                var eventsProcessed = 0;
                const int maxEventsPerBatch = 50;

                while (_eventQueue.TryDequeue(out var eventData) && eventsProcessed < maxEventsPerBatch)
                {
                    try
                    {
                        ProcessSingleEvent(eventData);
                        eventsProcessed++;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error processing unified file system event: {ex.Message}");
                    }
                }
            }
            finally
            {
                Monitor.Exit(_processingLock);
            }
        }

        private void ProcessSingleEvent(FileSystemEventData eventData)
        {
            var normalizedPath = PathService.NormalizePath(eventData.FullPath);

            try
            {
                switch (eventData.ChangeType)
                {
                    case WatcherChangeTypes.Created:
                        HandleFolderCreated(normalizedPath, eventData);
                        break;

                    case WatcherChangeTypes.Deleted:
                        HandleFolderDeleted(normalizedPath, eventData);
                        break;

                    case WatcherChangeTypes.Renamed:
                        if (!string.IsNullOrEmpty(eventData.OldPath))
                        {
                            var normalizedOldPath = PathService.NormalizePath(eventData.OldPath);
                            HandleFolderRenamed(normalizedOldPath, normalizedPath, eventData);
                        }
                        break;

                    case WatcherChangeTypes.Changed:
                        // For legacy compatibility, fire the FileSystemEvent
                        FireLegacyFileSystemEvent(normalizedPath, eventData);
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error processing event {eventData.ChangeType} for {normalizedPath}: {ex.Message}");
            }
        }

        private void HandleFolderCreated(string folderPath, FileSystemEventData eventData)
        {
            if (!Directory.Exists(folderPath))
                return;

            // Add to index
            var entry = new FolderIndexEntry(folderPath);
            if (_folderIndex.TryAdd(entry.FullPath, entry))
            {
                Debug.WriteLine($"Folder added to unified index: {folderPath}");

                // Fire events on UI thread
                _dispatcher.BeginInvoke(() =>
                {
                    FolderCreated?.Invoke(folderPath);
                    FireLegacyFileSystemEvent(folderPath, eventData);
                });

                // Scan for subdirectories that might have been created
                Task.Run(() =>
                {
                    try
                    {
                        var subdirectories = new List<string>();
                        ScanDirectoryRecursive(folderPath, subdirectories);

                        foreach (var subdir in subdirectories.Skip(1)) // Skip the folder itself
                        {
                            var subdirEntry = new FolderIndexEntry(subdir);
                            if (_folderIndex.TryAdd(subdirEntry.FullPath, subdirEntry))
                            {
                                _dispatcher.BeginInvoke(() =>
                                {
                                    FolderCreated?.Invoke(subdir);
                                });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error scanning subdirectories: {ex.Message}");
                    }
                });
            }
        }

        private void HandleFolderDeleted(string folderPath, FileSystemEventData eventData)
        {
            // Remove from index
            if (_folderIndex.TryRemove(folderPath, out var removedEntry))
            {
                Debug.WriteLine($"Folder removed from unified index: {folderPath}");

                _dispatcher.BeginInvoke(() =>
                {
                    FolderDeleted?.Invoke(folderPath);
                    FireLegacyFileSystemEvent(folderPath, eventData);
                });
            }

            // Remove subdirectories
            var subfolders = _folderIndex.Keys
                .Where(path => PathService.IsPathWithin(folderPath, path))
                .ToList();

            foreach (var subfolder in subfolders)
            {
                if (_folderIndex.TryRemove(subfolder, out _))
                {
                    Debug.WriteLine($"Subfolder removed from unified index: {subfolder}");

                    _dispatcher.BeginInvoke(() =>
                    {
                        FolderDeleted?.Invoke(subfolder);
                    });
                }
            }
        }

        private void HandleFolderRenamed(string oldPath, string newPath, FileSystemEventData eventData)
        {
            // Update index
            if (_folderIndex.TryRemove(oldPath, out var oldEntry))
            {
                var newEntry = new FolderIndexEntry(newPath);
                _folderIndex.TryAdd(newEntry.FullPath, newEntry);

                Debug.WriteLine($"Folder renamed in unified index: {oldPath} -> {newPath}");

                _dispatcher.BeginInvoke(() =>
                {
                    FolderRenamed?.Invoke(oldPath, newPath);
                    FireLegacyFileSystemEvent(newPath, eventData);
                });

                // Update subdirectories
                var subfolders = _folderIndex.Keys
                    .Where(path => PathService.IsPathWithin(oldPath, path))
                    .ToList();

                foreach (var subfolderOldPath in subfolders)
                {
                    if (_folderIndex.TryRemove(subfolderOldPath, out var subfolderEntry))
                    {
                        var subfolderNewPath = newPath + subfolderOldPath.Substring(oldPath.Length);
                        var newSubfolderEntry = new FolderIndexEntry(subfolderNewPath);
                        _folderIndex.TryAdd(newSubfolderEntry.FullPath, newSubfolderEntry);

                        _dispatcher.BeginInvoke(() =>
                        {
                            FolderRenamed?.Invoke(subfolderOldPath, subfolderNewPath);
                        });
                    }
                }
            }
        }

        private void FireLegacyFileSystemEvent(string folderPath, FileSystemEventData eventData)
        {
            try
            {
                // Create a fake FolderInfo for legacy compatibility
                var folderInfo = new FolderInfo { FolderPath = folderPath };
                var args = new FileSystemEventArgs(eventData.ChangeType,
                    Path.GetDirectoryName(folderPath), Path.GetFileName(folderPath));

                FileSystemEvent?.Invoke(folderInfo, args, eventData.ChangeType);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error firing legacy file system event: {ex.Message}");
            }
        }

        #endregion

        #region Helper Methods

        private bool IsDirectory(string path)
        {
            try
            {
                return Directory.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        private bool WasDirectory(string path)
        {
            // Check if path was in our index (indicating it was a directory)
            return _folderIndex.ContainsKey(PathService.NormalizePath(path));
        }

        #endregion

        #region IDisposable Implementation

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;

            try
            {
                // Stop monitoring
                StopMonitoringAsync().Wait(1000);

                // Dispose timer
                _eventProcessingTimer?.Dispose();

                Debug.WriteLine("UnifiedFolderService disposed");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error disposing UnifiedFolderService: {ex.Message}");
            }
        }

        #endregion
    }
}