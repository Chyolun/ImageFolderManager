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
using ImageFolderManager.Commands;
using ImageFolderManager.StateMachine;
using Timer = System.Threading.Timer;


namespace ImageFolderManager.Services
{
    /// <summary>
    /// Unified service that combines folder management and real-time indexing with optional command system support
    /// Maintains backward compatibility with existing interfaces
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

        // Command system events (optional)
        public event EventHandler<CommandExecutionEventArgs> CommandStarted;
        public event EventHandler<CommandExecutionEventArgs> CommandCompleted;
        public event EventHandler<CommandExecutionEventArgs> CommandFailed;

        #endregion

        #region Fields and Properties

        // Services
        private readonly FolderTagService _tagService = new FolderTagService();

        // Command system components (optional)
        private CommandSystemInitializer _commandSystem;
        private CommandExecutor _commandExecutor;
        private FolderStateMachine _stateMachine;
        private PathLockManager _pathLockManager;
        private bool _commandSystemEnabled = false;

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

        private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeOperations =
                 new ConcurrentDictionary<string, CancellationTokenSource>();
        private readonly SemaphoreSlim _eventProcessingSemaphore = new SemaphoreSlim(1, 1);

        private readonly ConcurrentDictionary<string, bool> _loadingStates =
                 new ConcurrentDictionary<string, bool>();

        // Configuration
        private string _rootDirectory;
        private bool _isIndexing = false;
        private bool _isDisposed = false;

        // Event throttling
        private const int EVENT_THROTTLE_MS = 500;
        private const int BATCH_PROCESSING_INTERVAL_MS = 1000;

        #endregion

        #region Public Properties

        /// <summary>
        /// Gets whether the service is currently indexing folders
        /// </summary>
        public bool IsIndexing
        {
            get
            {
                lock (_processingLock)
                {
                    return _isIndexing;
                }
            }
            private set
            {
                lock (_processingLock)
                {
                    _isIndexing = value;
                }
            }
        }

        /// <summary>
        /// Gets the currently monitored root directory
        /// </summary>
        public string RootDirectory => _rootDirectory;

        /// <summary>
        /// Gets the number of indexed folders
        /// </summary>
        public int IndexedFolderCount => _folderIndex.Count;

        /// <summary>
        /// Gets a collection of all indexed folder paths
        /// </summary>
        public IEnumerable<string> IndexedFolders => _folderIndex.Keys;

        /// <summary>
        /// Gets the command executor for advanced operations (if enabled)
        /// </summary>
        public CommandExecutor CommandExecutor => _commandExecutor;

        /// <summary>
        /// Gets the state machine for folder state tracking (if enabled)
        /// </summary>
        public FolderStateMachine StateMachine => _stateMachine;

        #endregion

        #region Constructor and Initialization

        public UnifiedFolderService(bool enableCommandSystem = false)
        {
            _commandSystemEnabled = enableCommandSystem;

            // Initialize command system if requested
            if (_commandSystemEnabled)
            {
                InitializeCommandSystem();
            }

            // Initialize event processing timer
            _eventProcessingTimer = new Timer(ProcessEventQueue, null,
                BATCH_PROCESSING_INTERVAL_MS, BATCH_PROCESSING_INTERVAL_MS);
        }

        /// <summary>
        /// Initialize the command system components (optional)
        /// </summary>
        private void InitializeCommandSystem()
        {
            try
            {
                _commandSystem = new CommandSystemInitializer();
                _commandSystem.Initialize();

                _commandExecutor = _commandSystem.CommandExecutor;
                _stateMachine = _commandSystem.StateMachine;
                _pathLockManager = _commandSystem.PathLockManager;

                // Subscribe to command events
                if (_commandExecutor != null)
                {
                    _commandExecutor.CommandStarted += OnCommandStarted;
                    _commandExecutor.CommandCompleted += OnCommandCompleted;
                    _commandExecutor.CommandFailed += OnCommandFailed;
                }

                Debug.WriteLine("Command system initialized successfully");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to initialize command system: {ex.Message}");
                _commandSystemEnabled = false;
            }
        }

        #endregion

        #region Command System Event Handlers (Optional)

        private void OnCommandStarted(object sender, CommandExecutionEventArgs e)
        {
            Debug.WriteLine($"Command started: {e.Command.CommandType} - {e.Command.CommandId}");
            CommandStarted?.Invoke(this, e);
        }

        private void OnCommandCompleted(object sender, CommandExecutionEventArgs e)
        {
            Debug.WriteLine($"Command completed: {e.Command.CommandType} - {e.Command.CommandId}");

            // Update local index based on command type
            _ = Task.Run(async () => await UpdateIndexAfterCommand(e.Command));

            CommandCompleted?.Invoke(this, e);
        }

        private void OnCommandFailed(object sender, CommandExecutionEventArgs e)
        {
            Debug.WriteLine($"Command failed: {e.Command.CommandType} - {e.Command.CommandId}: {e.Result?.Message}");
            CommandFailed?.Invoke(this, e);
        }

        /// <summary>
        /// Update the folder index after a command executes
        /// </summary>
        private async Task UpdateIndexAfterCommand(IFolderCommand command)
        {
            try
            {
                switch (command.CommandType)
                {
                    case FolderCommandType.Create:
                        if (command is CreateFolderCommand createCmd)
                        {
                            await AddFolderToIndex(createCmd.CreatedPath);
                            FolderCreated?.Invoke(createCmd.CreatedPath);
                        }
                        break;

                    case FolderCommandType.Delete:
                        if (command is DeleteFolderCommand deleteCmd)
                        {
                            RemoveFolderFromIndex(deleteCmd.FolderPath);
                            FolderDeleted?.Invoke(deleteCmd.FolderPath);
                        }
                        break;

                    case FolderCommandType.Move:
                        if (command is MoveFolderCommand moveCmd)
                        {
                            RemoveFolderFromIndex(moveCmd.SourcePath);
                            await AddFolderToIndex(moveCmd.DestinationPath);
                            FolderRenamed?.Invoke(moveCmd.SourcePath, moveCmd.DestinationPath);
                        }
                        break;

                    case FolderCommandType.Rename:
                        if (command is RenameFolderCommand renameCmd)
                        {
                            RemoveFolderFromIndex(renameCmd.OldPath);
                            await AddFolderToIndex(renameCmd.NewPath);
                            FolderRenamed?.Invoke(renameCmd.OldPath, renameCmd.NewPath);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error updating index after command: {ex.Message}");
            }
        }

        #endregion

        #region Legacy Methods (Backward Compatibility)

        /// <summary>
        /// Start monitoring a directory for changes
        /// </summary>
        public async Task StartMonitoringAsync(string rootDirectory)
        {
            if (_isDisposed) return;

            await StopMonitoringAsync();

            _rootDirectory = PathService.NormalizePath(rootDirectory);

            if (string.IsNullOrEmpty(_rootDirectory) || !Directory.Exists(_rootDirectory))
            {
                throw new DirectoryNotFoundException($"Directory not found: {_rootDirectory}");
            }

            await RefreshIndexAsync();
            StartFileSystemWatcher();

            Debug.WriteLine($"Started monitoring: {_rootDirectory}");
        }

        /// <summary>
        /// Stop monitoring the current directory
        /// </summary>
        public async Task StopMonitoringAsync()
        {
            if (_rootWatcher != null)
            {
                _rootWatcher.EnableRaisingEvents = false;
                _rootWatcher.Dispose();
                _rootWatcher = null;
            }

            _folderIndex.Clear();
         
            _lastEventTime.Clear();

            _rootDirectory = null;

            await Task.CompletedTask;
            Debug.WriteLine("Stopped monitoring");
        }

        /// <summary>
        /// Load folders recursively from a directory
        /// </summary>
        public async Task<List<FolderInfo>> LoadFoldersRecursivelyAsync(string rootPath)
        {
            if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath))
                return new List<FolderInfo>();

            var folders = new List<FolderInfo>();

            try
            {
                IsIndexing = true;

                await Task.Run(() =>
                {
                    var directories = Directory.GetDirectories(rootPath, "*", SearchOption.AllDirectories)
                        .Concat(new[] { rootPath })
                        .OrderBy(d => d);

                    foreach (var directory in directories)
                    {
                        try
                        {
                            var folderInfo = CreateFolderInfo(directory);
                            if (folderInfo != null)
                            {
                                folders.Add(folderInfo);
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error loading folder {directory}: {ex.Message}");
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in LoadFoldersRecursivelyAsync: {ex.Message}");
            }
            finally
            {
                IsIndexing = false;
            }

            return folders;
        }

        /// <summary>
        /// Creates a FolderInfo without loading images (for performance)
        /// </summary>
        public async Task<FolderInfo> CreateFolderInfoWithoutImagesAsync(string folderPath)
        {
            return await Task.Run(() => CreateFolderInfo(folderPath));
        }

        /// <summary>
        /// Search folders by name or path containing the specified term
        /// </summary>
        public IEnumerable<string> SearchFolders(string searchTerm)
        {
            if (string.IsNullOrWhiteSpace(searchTerm))
                return Enumerable.Empty<string>();

            var normalizedSearchTerm = searchTerm.ToLowerInvariant();

            return _folderIndex.Keys.Where(folderPath =>
            {
                var folderName = Path.GetFileName(folderPath)?.ToLowerInvariant() ?? "";
                var fullPath = folderPath.ToLowerInvariant();

                return folderName.Contains(normalizedSearchTerm) ||
                       fullPath.Contains(normalizedSearchTerm);
            });
        }

        #endregion

        #region Enhanced Methods (Using Command System if Available)

        /// <summary>
        /// Create a new folder (uses command system if available)
        /// </summary>
        public async Task<bool> CreateFolderAsync(string parentPath, string folderName)
        {
            if (_commandSystemEnabled && _commandExecutor != null)
            {
                var command = new CreateFolderCommand(parentPath, folderName);
                var result = await _commandExecutor.ExecuteCommandAsync(command);
                return result.Success;
            }
            else
            {
                // Fallback to direct operation
                try
                {
                    var newFolderPath = Path.Combine(parentPath, folderName);
                    Directory.CreateDirectory(newFolderPath);

                    await AddFolderToIndex(newFolderPath);
                    FolderCreated?.Invoke(newFolderPath);

                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error creating folder: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// Delete a folder (uses command system if available)
        /// </summary>
        public async Task<bool> DeleteFolderAsync(string folderPath, bool useRecycleBin = true)
        {
            if (_commandSystemEnabled && _commandExecutor != null)
            {
                var command = new DeleteFolderCommand(folderPath, useRecycleBin);
                var result = await _commandExecutor.ExecuteCommandAsync(command);
                return result.Success;
            }
            else
            {
                // Fallback to direct operation
                try
                {
                    if (useRecycleBin)
                    {
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                            folderPath,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    }
                    else
                    {
                        Directory.Delete(folderPath, true);
                    }

                    RemoveFolderFromIndex(folderPath);
                    FolderDeleted?.Invoke(folderPath);

                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error deleting folder: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// Move a folder (uses command system if available)
        /// </summary>
        public async Task<bool> MoveFolderAsync(string sourcePath, string destinationPath)
        {
            if (_commandSystemEnabled && _commandExecutor != null)
            {
                var command = new MoveFolderCommand(sourcePath, destinationPath);
                var result = await _commandExecutor.ExecuteCommandAsync(command);
                return result.Success;
            }
            else
            {
                // Fallback to direct operation
                try
                {
                    Directory.Move(sourcePath, destinationPath);

                    RemoveFolderFromIndex(sourcePath);
                    await AddFolderToIndex(destinationPath);
                    FolderRenamed?.Invoke(sourcePath, destinationPath);

                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error moving folder: {ex.Message}");
                    return false;
                }
            }


        }

        /// <summary>
        /// Rename a folder (uses command system if available)
        /// </summary>
        public async Task<bool> RenameFolderAsync(string folderPath, string newName)
        {
            if (_commandSystemEnabled && _commandExecutor != null)
            {
                var command = new RenameFolderCommand(folderPath, newName);
                var result = await _commandExecutor.ExecuteCommandAsync(command);
                return result.Success;
            }
            else
            {
                // Fallback to direct operation
                try
                {
                    var newPath = Path.Combine(Path.GetDirectoryName(folderPath), newName);
                    Directory.Move(folderPath, newPath);

                    RemoveFolderFromIndex(folderPath);
                    await AddFolderToIndex(newPath);
                    FolderRenamed?.Invoke(folderPath, newPath);

                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error renaming folder: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// Check if a folder is currently locked by the command system
        /// </summary>
        public bool IsFolderLocked(string folderPath)
        {
            return _commandSystemEnabled && _pathLockManager?.IsPathLocked(folderPath) == true;
        }

        /// <summary>
        /// Get the current state of a folder
        /// </summary>
        public FolderState GetFolderState(string folderPath)
        {
            return _commandSystemEnabled && _stateMachine != null
                ? _stateMachine.GetFolderState(folderPath)
                : FolderState.Available;
        }

        #endregion

        #region Helper Methods

        private void StartFileSystemWatcher()
        {
            if (string.IsNullOrEmpty(_rootDirectory)) return;

            _rootWatcher = new FileSystemWatcher(_rootDirectory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };

            _rootWatcher.Created += OnFileSystemEvent;
            _rootWatcher.Deleted += OnFileSystemEvent;
            _rootWatcher.Renamed += OnFileSystemRenamed;
        }

        private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
        {
            if (Directory.Exists(e.FullPath) || e.ChangeType == WatcherChangeTypes.Deleted)
            {
                QueueFileSystemEvent(e.FullPath, e.ChangeType);
            }
        }

        private void OnFileSystemRenamed(object sender, RenamedEventArgs e)
        {
            if (Directory.Exists(e.FullPath) || Directory.Exists(e.OldFullPath))
            {
                QueueFileSystemEvent(e.OldFullPath, WatcherChangeTypes.Deleted);
                QueueFileSystemEvent(e.FullPath, WatcherChangeTypes.Created);
            }
        }

        private void QueueFileSystemEvent(string path, WatcherChangeTypes changeType)
        {
            var eventData = new FileSystemEventData
            {
                Path = PathService.NormalizePath(path),
                ChangeType = changeType,
                Timestamp = DateTime.Now
            };

            _eventQueue.Enqueue(eventData);
        }

        private async void ProcessEventQueue(object state)
        {
            if (!await _eventProcessingSemaphore.WaitAsync(100))
                return; // Skip if already processing

            try
            {
                var eventsToProcess = new List<FileSystemEventData>();
                var eventsByPath = new Dictionary<string, FileSystemEventData>(StringComparer.OrdinalIgnoreCase);

                // Dequeue and deduplicate events by path
                while (_eventQueue.TryDequeue(out var eventData))
                {
                    var normalizedPath = PathService.CanonicalizePathForIndex(eventData.Path);
                    var timeSinceLastEvent = DateTime.Now - (_lastEventTime.GetOrAdd(normalizedPath, DateTime.MinValue));

                    if (timeSinceLastEvent.TotalMilliseconds >= EVENT_THROTTLE_MS)
                    {
                        // Keep only the most recent event per path
                        eventsByPath[normalizedPath] = eventData;
                        _lastEventTime[normalizedPath] = DateTime.Now;
                    }
                }

                eventsToProcess.AddRange(eventsByPath.Values);

                // Process events sequentially to avoid conflicts
                foreach (var eventData in eventsToProcess.OrderBy(e => e.Timestamp))
                {
                    await ProcessSingleEventSafe(eventData);
                }
            }
            finally
            {
                _eventProcessingSemaphore.Release();
            }
        }

        private async Task ProcessSingleEventSafe(FileSystemEventData eventData)
        {
            var normalizedPath = PathService.CanonicalizePathForIndex(eventData.Path);

            // Cancel any existing operation for this path
            if (_activeOperations.TryGetValue(normalizedPath, out var existingCts))
            {
                existingCts.Cancel();
                _activeOperations.TryRemove(normalizedPath, out _);
            }

            // Create new cancellation token for this operation
            var cts = new CancellationTokenSource();
            _activeOperations[normalizedPath] = cts;

            try
            {
                switch (eventData.ChangeType)
                {
                    case WatcherChangeTypes.Created:
                        await AddFolderToIndexSafe(normalizedPath, cts.Token);
                        break;

                    case WatcherChangeTypes.Deleted:
                        RemoveFolderFromIndexSafe(normalizedPath);
                        break;
                }

                // Fire legacy event for backward compatibility
                if (!cts.Token.IsCancellationRequested)
                {
                    var folderInfo = GetFolderInfoFromIndex(normalizedPath);
                    var args = new FileSystemEventArgs(eventData.ChangeType,
                        Path.GetDirectoryName(eventData.Path), Path.GetFileName(eventData.Path));
                    FileSystemEvent?.Invoke(folderInfo, args, eventData.ChangeType);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when operation is cancelled
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error processing filesystem event for {normalizedPath}: {ex.Message}");
            }
            finally
            {
                _activeOperations.TryRemove(normalizedPath, out _);
                cts.Dispose();
            }
        }

        private async Task AddFolderToIndexSafe(string folderPath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
                return;

            var normalizedPath = PathService.CanonicalizePathForIndex(folderPath);

            // Check if already loading
            if (!_loadingStates.TryAdd(normalizedPath, true))
                return; // Already being loaded

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var folderInfo = await CreateFolderInfoSafe(normalizedPath, cancellationToken);

                if (folderInfo != null && !cancellationToken.IsCancellationRequested)
                {
                    var indexEntry = new FolderIndexEntry
                    {
                        FolderInfo = folderInfo,
                        LastAccessed = DateTime.Now,
                        IsMonitored = true
                    };

                    _folderIndex.AddOrUpdate(normalizedPath, indexEntry, (key, existing) => indexEntry);
                }
            }
            finally
            {
                _loadingStates.TryRemove(normalizedPath, out _);
            }
        }

        // Add safe folder removal method:
        private void RemoveFolderFromIndexSafe(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)) return;

            var normalizedPath = PathService.CanonicalizePathForIndex(folderPath);

            // Cancel any loading operation
            if (_activeOperations.TryRemove(normalizedPath, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }

            // Remove loading state
            _loadingStates.TryRemove(normalizedPath, out _);

            // Remove from index
            _folderIndex.TryRemove(normalizedPath, out _);
        }

        // Add safe folder info creation:
        private async Task<FolderInfo> CreateFolderInfoSafe(string folderPath, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!Directory.Exists(folderPath) || cancellationToken.IsCancellationRequested)
                    return null;

                var dirInfo = new DirectoryInfo(folderPath);
                var folderInfo = new FolderInfo
                {
                    FolderPath = folderPath,
                    Tags = new ObservableCollection<string>(),
                    IsLoading = true // Set loading state initially
                };

                // Load metadata asynchronously
                try
                {
                    var tags = await _tagService.GetTagsForFolderAsync(folderPath);
                    var rating = await _tagService.GetRatingForFolderAsync(folderPath);

                    if (!cancellationToken.IsCancellationRequested)
                    {
                        // Update on UI thread if available
                        if (Application.Current?.Dispatcher != null)
                        {
                            await Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                folderInfo.Tags.Clear();
                                foreach (var tag in tags)
                                {
                                    folderInfo.Tags.Add(tag);
                                }
                                folderInfo.Rating = rating;
                                folderInfo.IsLoading = false; // Clear loading state
                            }, DispatcherPriority.Background, cancellationToken);
                        }
                        else
                        {
                            // Direct update if no dispatcher
                            folderInfo.Tags.Clear();
                            foreach (var tag in tags)
                            {
                                folderInfo.Tags.Add(tag);
                            }
                            folderInfo.Rating = rating;
                            folderInfo.IsLoading = false;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    folderInfo.IsLoading = false;
                    throw;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error loading metadata for {folderPath}: {ex.Message}");
                    folderInfo.IsLoading = false;
                }

                return folderInfo;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error creating FolderInfo for {folderPath}: {ex.Message}");
                return null;
            }
        }


        private async Task AddFolderToIndex(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
                return;

            var normalizedPath = PathService.CanonicalizePathForIndex(folderPath);

            // Add validation to prevent corruption
            if (string.IsNullOrEmpty(normalizedPath))
                return;

            var folderInfo = CreateFolderInfo(normalizedPath);

            if (folderInfo != null)
            {
                var indexEntry = new FolderIndexEntry
                {
                    FolderInfo = folderInfo,
                    LastAccessed = DateTime.Now,
                    IsMonitored = true
                };


               // Use atomic update to prevent race conditions
                _folderIndex.AddOrUpdate(normalizedPath, indexEntry, (key, existing) =>
                {
                    // Preserve loading state if exists
                    if (existing?.FolderInfo?.IsLoading == true && folderInfo.IsLoading == false)
                    {
                        folderInfo.IsLoading = false;
                    }
                    return indexEntry;
                });
            }
        }

        private void RemoveFolderFromIndex(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)) return;

            var normalizedPath = PathService.CanonicalizePathForIndex(folderPath);

            // Add validation to prevent corruption
            if (string.IsNullOrEmpty(normalizedPath))
                return;
            _folderIndex.TryRemove(normalizedPath, out _);
        }

        private FolderInfo GetFolderInfoFromIndex(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)) return null;

            var normalizedPath = PathService.CanonicalizePathForIndex(folderPath);
                       
            return _folderIndex.TryGetValue(normalizedPath, out var indexEntry) ? indexEntry.FolderInfo : null;
        }

        private FolderInfo CreateFolderInfo(string folderPath)
        {
            try
            {
                if (!Directory.Exists(folderPath)) return null;

                var dirInfo = new DirectoryInfo(folderPath);
                var folderInfo = new FolderInfo
                {

                    FolderPath = folderPath,

                    Tags = new ObservableCollection<string>()
                };

                // Load tags asynchronously if needed
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var tags = await _tagService.GetTagsForFolderAsync(folderPath);
                        var rating = await _tagService.GetRatingForFolderAsync(folderPath);

                        Application.Current?.Dispatcher.Invoke(() =>
                        {
                            folderInfo.Tags.Clear();
                            foreach (var tag in tags)
                            {
                                folderInfo.Tags.Add(tag);
                            }
                            folderInfo.Rating = rating;
                        });
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error loading tags for {folderPath}: {ex.Message}");
                    }
                });

                return folderInfo;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error creating FolderInfo for {folderPath}: {ex.Message}");
                return null;
            }
        }

        private async Task RefreshIndexAsync()
        {
            if (string.IsNullOrEmpty(_rootDirectory)) return;

            try
            {
                IsIndexing = true;
                _folderIndex.Clear();

                var folders = await LoadFoldersRecursivelyAsync(_rootDirectory);

                foreach (var folder in folders)
                {
                    var indexEntry = new FolderIndexEntry
                    {
                        FolderInfo = folder,
                        LastAccessed = DateTime.Now,
                        IsMonitored = true
                    };

                    _folderIndex.AddOrUpdate(folder.FolderPath, indexEntry, (key, existing) => indexEntry);
                }

                IndexRebuilt?.Invoke(folders.Select(f => f.FolderPath).ToList());
            }
            finally
            {
                IsIndexing = false;
            }
        }

        #endregion

        #region IDisposable Implementation

        public void Dispose()
        {
            if (_isDisposed) return;

            _isDisposed = true;

            // Cancel all active operations
            foreach (var kvp in _activeOperations)
            {
                try
                {
                    kvp.Value.Cancel();
                    kvp.Value.Dispose();
                }
                catch { }
            }
            _activeOperations.Clear();

            // Clear loading states
            _loadingStates.Clear();

            // Dispose resources
            _eventProcessingTimer?.Dispose();
            _rootWatcher?.Dispose();
            _commandSystem?.Dispose();
            _eventProcessingSemaphore?.Dispose();

            GC.SuppressFinalize(this);
        }

        #endregion

        #region Private Classes

        private class FileSystemEventData
        {
            public string Path { get; set; }
            public WatcherChangeTypes ChangeType { get; set; }
            public DateTime Timestamp { get; set; }
        }

        private class FolderIndexEntry
        {
            public FolderInfo FolderInfo { get; set; }
            public DateTime LastAccessed { get; set; }
            public bool IsMonitored { get; set; }
        }

        #endregion
    }
}