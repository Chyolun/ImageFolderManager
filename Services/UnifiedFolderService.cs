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
    /// Unified service that combines folder management and real-time indexing with command pattern integration
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

        // Command system events
        public event EventHandler<CommandExecutionEventArgs> CommandStarted;
        public event EventHandler<CommandExecutionEventArgs> CommandCompleted;
        public event EventHandler<CommandExecutionEventArgs> CommandFailed;

        #endregion

        #region Fields and Properties

        // Services
        private readonly FolderTagService _tagService = new FolderTagService();

        // Command system components
        private CommandSystemInitializer _commandSystem;
        private CommandExecutor _commandExecutor;
        private FolderStateMachine _stateMachine;
        private PathLockManager _pathLockManager;

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
        /// Gets the command executor for advanced operations
        /// </summary>
        public CommandExecutor CommandExecutor => _commandExecutor;

        /// <summary>
        /// Gets the state machine for folder state tracking
        /// </summary>
        public FolderStateMachine StateMachine => _stateMachine;

        #endregion

        #region Constructor and Initialization

        public UnifiedFolderService()
        {
            // Initialize command system
            InitializeCommandSystem();

            // Initialize event processing timer
            _eventProcessingTimer = new Timer(ProcessEventQueue, null,
                BATCH_PROCESSING_INTERVAL_MS, BATCH_PROCESSING_INTERVAL_MS);
        }

        /// <summary>
        /// Initialize the command system components
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
                _commandExecutor.CommandStarted += OnCommandStarted;
                _commandExecutor.CommandCompleted += OnCommandCompleted;
                _commandExecutor.CommandFailed += OnCommandFailed;

                Debug.WriteLine("Command system initialized successfully");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to initialize command system: {ex.Message}");
                throw;
            }
        }

        #endregion

        #region Command System Event Handlers

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

                    case FolderCommandType.BatchMove:
                    case FolderCommandType.BatchCopy:
                    case FolderCommandType.BatchDelete:
                        // For batch operations, trigger a full refresh
                        await RefreshIndexAsync();
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error updating index after command: {ex.Message}");
            }
        }

        #endregion

        #region Command-Based Folder Operations

        /// <summary>
        /// Create a new folder using the command system
        /// </summary>
        public async Task<CommandResult> CreateFolderAsync(string parentPath, string folderName, CancellationToken cancellationToken = default)
        {
            var command = new CreateFolderCommand(parentPath, folderName);
            return await _commandExecutor.ExecuteCommandAsync(command, cancellationToken);
        }

        /// <summary>
        /// Delete a folder using the command system
        /// </summary>
        public async Task<CommandResult> DeleteFolderAsync(string folderPath, bool useRecycleBin = true, CancellationToken cancellationToken = default)
        {
            var command = new DeleteFolderCommand(folderPath, useRecycleBin);
            return await _commandExecutor.ExecuteCommandAsync(command, cancellationToken);
        }

        /// <summary>
        /// Move a folder using the command system
        /// </summary>
        public async Task<CommandResult> MoveFolderAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
        {
            var command = new MoveFolderCommand(sourcePath, destinationPath);
            return await _commandExecutor.ExecuteCommandAsync(command, cancellationToken);
        }

        /// <summary>
        /// Rename a folder using the command system
        /// </summary>
        public async Task<CommandResult> RenameFolderAsync(string folderPath, string newName, CancellationToken cancellationToken = default)
        {
            var command = new RenameFolderCommand(folderPath, newName);
            return await _commandExecutor.ExecuteCommandAsync(command, cancellationToken);
        }

        /// <summary>
        /// Copy a folder using the command system
        /// </summary>
        public async Task<CommandResult> CopyFolderAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
        {
            var command = new CopyFolderCommand(sourcePath, destinationPath);
            return await _commandExecutor.ExecuteCommandAsync(command, cancellationToken);
        }

        /// <summary>
        /// Execute multiple folder operations as a batch
        /// </summary>
        public async Task<CommandResult> ExecuteBatchOperationAsync(IEnumerable<IFolderCommand> commands, CancellationToken cancellationToken = default)
        {
            var batchCommand = new BatchOperationCommand(commands);
            return await _commandExecutor.ExecuteCommandAsync(batchCommand, cancellationToken);
        }

        /// <summary>
        /// Check if a folder is currently locked by the command system
        /// </summary>
        public bool IsFolderLocked(string folderPath)
        {
            return _pathLockManager?.IsPathLocked(folderPath) ?? false;
        }

        /// <summary>
        /// Get the current state of a folder
        /// </summary>
        public FolderState GetFolderState(string folderPath)
        {
            return _stateMachine?.GetFolderState(folderPath) ?? FolderState.Available;
        }

        #endregion

        #region Existing Methods (Legacy Support)

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

        private void ProcessEventQueue(object state)
        {
            var eventsToProcess = new List<FileSystemEventData>();

            // Dequeue all pending events
            while (_eventQueue.TryDequeue(out var eventData))
            {
                var timeSinceLastEvent = DateTime.Now - (_lastEventTime.GetOrAdd(eventData.Path, DateTime.MinValue));

                if (timeSinceLastEvent.TotalMilliseconds >= EVENT_THROTTLE_MS)
                {
                    eventsToProcess.Add(eventData);
                    _lastEventTime[eventData.Path] = DateTime.Now;
                }
            }

            // Process deduplicated events
            foreach (var eventData in eventsToProcess.GroupBy(e => e.Path).Select(g => g.OrderByDescending(e => e.Timestamp).First()))
            {
                ProcessSingleEvent(eventData);
            }
        }

        private void ProcessSingleEvent(FileSystemEventData eventData)
        {
            try
            {
                switch (eventData.ChangeType)
                {
                    case WatcherChangeTypes.Created:
                        _ = Task.Run(async () => await AddFolderToIndex(eventData.Path));
                        break;

                    case WatcherChangeTypes.Deleted:
                        RemoveFolderFromIndex(eventData.Path);
                        break;
                }

                // Fire legacy event for backward compatibility
                var folderInfo = GetFolderInfoFromIndex(eventData.Path);
                var args = new FileSystemEventArgs(eventData.ChangeType, Path.GetDirectoryName(eventData.Path), Path.GetFileName(eventData.Path));
                FileSystemEvent?.Invoke(folderInfo, args, eventData.ChangeType);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error processing file system event: {ex.Message}");
            }
        }

        private async Task AddFolderToIndex(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
                return;

            var normalizedPath = PathService.NormalizePath(folderPath);
            var folderInfo = CreateFolderInfo(normalizedPath);

            if (folderInfo != null)
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

        private void RemoveFolderFromIndex(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)) return;

            var normalizedPath = PathService.NormalizePath(folderPath);
            _folderIndex.TryRemove(normalizedPath, out _);
        }

        private FolderInfo GetFolderInfoFromIndex(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath)) return null;

            var normalizedPath = PathService.NormalizePath(folderPath);
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

            _eventProcessingTimer?.Dispose();
            _rootWatcher?.Dispose();
            _commandSystem?.Dispose();

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