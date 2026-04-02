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

        // Command system components (optional)
        private CommandSystemInitializer _commandSystem;
        private CommandExecutor _commandExecutor;
        private FolderStateMachine _stateMachine;
        private PathLockManager _pathLockManager;
        private bool _commandSystemEnabled = false;
        private readonly FolderTagService _tagService;
        // Single file system watcher for the entire tree
        private FileSystemWatcher _rootWatcher;

        // Thread-safe folder index
        private readonly ConcurrentDictionary<string, FolderIndexEntry> _folderIndex =
            new ConcurrentDictionary<string, FolderIndexEntry>(StringComparer.OrdinalIgnoreCase);

        // Event processing
        private readonly ConcurrentDictionary<string, DateTime> _lastEventTime =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly object _processingLock = new object();
		private readonly FileSystemEventProcessor _eventProcessor;

		private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeOperations =
                 new ConcurrentDictionary<string, CancellationTokenSource>();
        private readonly SemaphoreSlim _eventProcessingSemaphore = new SemaphoreSlim(1, 1);

        private readonly ConcurrentDictionary<string, bool> _loadingStates =
                 new ConcurrentDictionary<string, bool>();

        // Configuration
        private string _rootDirectory;
        private bool _isIndexing = false;
        private bool _isDisposed = false;

        private HierarchicalNodeManager _nodeManager;
        private FolderOperationCoordinator _coordinator;

        private readonly ConcurrentDictionary<string, HashSet<string>> _folderNameIndex =
                new ConcurrentDictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly object _nameIndexLock = new object();

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

        public UnifiedFolderService(FolderTagService tagService, HierarchicalNodeManager nodeManager = null, bool enableCommandSystem = false)
        {
            _nodeManager = nodeManager ?? new HierarchicalNodeManager();
            _commandSystemEnabled = enableCommandSystem;
            _tagService = tagService ?? throw new ArgumentNullException(nameof(tagService));
            // Initialize command system if requested
            if (_commandSystemEnabled)
            {
                InitializeCommandSystem();
            }

			// Initialize event processing timer
			_eventProcessor = new FileSystemEventProcessor();
			_eventProcessor.EventsProcessed += HandleProcessedEventsAsync;
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
                            await AddFolderToIndexSafe(createCmd.CreatedPath);
                            FolderCreated?.Invoke(createCmd.CreatedPath);
                        }
                        break;

                    case FolderCommandType.Delete:
                        if (command is DeleteFolderCommand deleteCmd)
                        {
                            RemoveFolderFromIndexSafe(deleteCmd.FolderPath);
                            FolderDeleted?.Invoke(deleteCmd.FolderPath);
                        }
                        break;

                    case FolderCommandType.Move:
                        if (command is MoveFolderCommand moveCmd)
                        {
                            var actualDestination = string.IsNullOrWhiteSpace(moveCmd.ActualDestinationPath)
                                ? moveCmd.DestinationPath
                                : moveCmd.ActualDestinationPath;
                            RemoveFolderFromIndexSafe(moveCmd.SourcePath);
                            await AddFolderToIndexSafe(actualDestination);
                            FolderRenamed?.Invoke(moveCmd.SourcePath, actualDestination);
                        }
                        break;

                    case FolderCommandType.Rename:
                        if (command is RenameFolderCommand renameCmd)
                        {
                            RemoveFolderFromIndexSafe(renameCmd.OldPath);
                            await AddFolderToIndexSafe(renameCmd.NewPath);
                            FolderRenamed?.Invoke(renameCmd.OldPath, renameCmd.NewPath);
                        }
                        break;

                    case FolderCommandType.Copy:
                        if (command is CopyFolderCommand copyCmd)
                        {
                            var actualCopyDestination = string.IsNullOrWhiteSpace(copyCmd.ActualDestinationPath)
                                ? copyCmd.DestinationPath
                                : copyCmd.ActualDestinationPath;
                            await AddFolderToIndexSafe(actualCopyDestination);
                            FolderCreated?.Invoke(actualCopyDestination);
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
        /// Search folders by name or path containing the specified term.
        /// Uses a pre-built name index for O(hits) instead of O(n).
        /// Falls back to the path scan for path-only matches.
        /// </summary>
        public IEnumerable<string> SearchFolders(string searchTerm)
        {
            if (string.IsNullOrWhiteSpace(searchTerm))
                return Enumerable.Empty<string>();

            var normalizedSearchTerm = searchTerm.ToLowerInvariant();
            var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // ©¤©¤ Fast path: name index (folder name substring) ©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤
            foreach (var kvp in _folderNameIndex)
            {
                if (kvp.Key.Contains(normalizedSearchTerm))
                {
                    foreach (var path in kvp.Value)
                        results.Add(path);
                }
            }

            // ©¤©¤ Slow path: path substring (handles deep path matches) ©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤
            // Only needed when the term might appear in a parent-segment of the path
            // but NOT in the folder name itself.
            foreach (var folderPath in _folderIndex.Keys)
            {
                if (!results.Contains(folderPath) &&
                    folderPath.ToLowerInvariant().Contains(normalizedSearchTerm))
                {
                    results.Add(folderPath);
                }
            }

            return results;
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

                    await AddFolderToIndexSafe(newFolderPath);
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

                    RemoveFolderFromIndexSafe(folderPath);
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

                    RemoveFolderFromIndexSafe(sourcePath);
                    await AddFolderToIndexSafe(destinationPath);
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
        /// Copy a folder (uses command system if available)
        /// </summary>
        public async Task<bool> CopyFolderAsync(string sourcePath, string destinationPath)
        {
            if (_commandSystemEnabled && _commandExecutor != null)
            {
                var command = new CopyFolderCommand(sourcePath, destinationPath);
                var result = await _commandExecutor.ExecuteCommandAsync(command);
                return result.Success;
            }
            else
            {
                try
                {
                    if (!Directory.Exists(sourcePath))
                    {
                        return false;
                    }

                    if (Directory.Exists(destinationPath))
                    {
                        return false;
                    }

                    CopyDirectoryRecursive(sourcePath, destinationPath);
                    await AddFolderToIndexSafe(destinationPath);
                    FolderCreated?.Invoke(destinationPath);

                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error copying folder: {ex.Message}");
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

                    RemoveFolderFromIndexSafe(folderPath);
                    await AddFolderToIndexSafe(newPath);
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

        /// <summary>
        /// Refresh a specific folder in the index
        /// </summary>
        public async Task RefreshFolderAsync(string folderPath)
        {
            var normalizedPath = PathNormalizationService.GetCanonicalPath(folderPath);

            if (_folderIndex.TryGetValue(normalizedPath, out var existingEntry))
            {
                // Refresh existing entry
                var refreshedFolder = await CreateFolderInfoSafe(normalizedPath);
                if (refreshedFolder != null)
                {
                    existingEntry.FolderInfo = refreshedFolder;
                    existingEntry.LastAccessed = DateTime.Now;
                }
            }
            else
            {
                // Add new entry if not exists
                await AddFolderToIndexSafe(normalizedPath);
            }
        }

        /// <summary>
        /// Initialize coordinator (called from MainViewModel)
        /// </summary>
        public void InitializeCoordinator(FolderOperationCoordinator coordinator)
        {
            _coordinator = coordinator;
        }

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
				_eventProcessor.QueueEvent(e.FullPath, e.ChangeType);
			}
		}

		private void OnFileSystemRenamed(object sender, RenamedEventArgs e)
		{
			if (Directory.Exists(e.FullPath) || Directory.Exists(e.OldFullPath))
			{
				_eventProcessor.QueueRenameEvent(e.OldFullPath, e.FullPath);
			}
		}

		private async Task HandleProcessedEventsAsync(List<ProcessedFileSystemEvent> events)
		{
			foreach (var processedEvent in events)
			{
				try
				{
					await ProcessSingleEventAsync(processedEvent);
				}
				catch (Exception ex)
				{
					Debug.WriteLine($"Error processing event {processedEvent.EventType} for {processedEvent.NewPath}: {ex.Message}");
				}
			}
		}

		private async Task ProcessSingleEventAsync(ProcessedFileSystemEvent processedEvent)
		{
			switch (processedEvent.EventType)
			{
				case ProcessedEventType.Create:
					await HandleCreateEventAsync(processedEvent.NewPath);
					break;

				case ProcessedEventType.Delete:
					await HandleDeleteEventAsync(processedEvent.NewPath);
					break;

				case ProcessedEventType.Rename:
					await HandleRenameEventAsync(processedEvent.OldPath, processedEvent.NewPath);
					break;

				case ProcessedEventType.Change:
					await HandleChangeEventAsync(processedEvent.NewPath);
					break;
			}
		}

		private async Task HandleCreateEventAsync(string normalizedPath)
		{
		    
			await AddFolderToIndexSafe(normalizedPath);
			FolderCreated?.Invoke(normalizedPath);

			// Fire legacy event for backward compatibility
			var folderInfo = GetFolderInfoFromIndex(normalizedPath);
			var args = new FileSystemEventArgs(WatcherChangeTypes.Created,
				Path.GetDirectoryName(normalizedPath), Path.GetFileName(normalizedPath));
			FileSystemEvent?.Invoke(folderInfo, args, WatcherChangeTypes.Created);
		}

		private Task HandleDeleteEventAsync(string normalizedPath)
		{
			RemoveFolderFromIndexSafe(normalizedPath);
			FolderDeleted?.Invoke(normalizedPath);

			// Fire legacy event for backward compatibility
			var args = new FileSystemEventArgs(WatcherChangeTypes.Deleted,
				Path.GetDirectoryName(normalizedPath), Path.GetFileName(normalizedPath));
			FileSystemEvent?.Invoke(null, args, WatcherChangeTypes.Deleted);
			return Task.CompletedTask;
		}

		private async Task HandleRenameEventAsync(string normalizedOldPath, string normalizedNewPath)
		{

			RemoveFolderFromIndexSafe(normalizedOldPath);
			await AddFolderToIndexSafe(normalizedNewPath);
			FolderRenamed?.Invoke(normalizedOldPath, normalizedNewPath);
            // ADD: Notify coordinator if available
            if (_coordinator != null)
            {
                _ = Task.Run(async () =>
                {
                    await _coordinator.ExecuteFolderMoveAsync(normalizedOldPath, normalizedNewPath);
                });
            }
        }

		private async Task HandleChangeEventAsync(string normalizedPath)
        { 
			// For change events, just refresh the folder info if it exists
			if (_folderIndex.ContainsKey(normalizedPath))
			{
				await AddFolderToIndexSafe(normalizedPath); // Refresh existing entry
			}
		}

        // In AddFolderToIndexSafe(), after the _folderIndex.AddOrUpdate(...) call:
        private void AddToNameIndex(string folderPath)
        {
            string name = Path.GetFileName(folderPath)?.ToLowerInvariant();
            if (string.IsNullOrEmpty(name)) return;

            lock (_nameIndexLock)
            {
                var bucket = _folderNameIndex.GetOrAdd(name,
                    _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                lock (bucket) bucket.Add(folderPath);
            }
        }

        // In RemoveFolderFromIndexSafe(), after the _folderIndex.TryRemove(...) call:
        private void RemoveFromNameIndex(string folderPath)
        {
            string name = Path.GetFileName(folderPath)?.ToLowerInvariant();
            if (string.IsNullOrEmpty(name)) return;

            if (_folderNameIndex.TryGetValue(name, out var bucket))
            {
                lock (bucket) bucket.Remove(folderPath);
            }
        }

        // In RefreshIndexAsync(), after rebuilding _folderIndex, rebuild name index too:
        private void RebuildNameIndex()
        {
            _folderNameIndex.Clear();
            foreach (var path in _folderIndex.Keys)
                AddToNameIndex(path);
        }

        private async Task AddFolderToIndexSafe(string folderPath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
                return;

            var normalizedPath = PathService.NormalizePath(folderPath);

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
                    AddToNameIndex(normalizedPath);
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

            var normalizedPath = PathService.NormalizePath(folderPath);

            // Cancel any loading operation
            if (_activeOperations.TryRemove(normalizedPath, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }

            // Remove loading state
            _loadingStates.TryRemove(normalizedPath, out _);

            // Remove from index
            if (_folderIndex.TryRemove(normalizedPath, out _))
            {
                RemoveFromNameIndex(normalizedPath);
            }
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

                RebuildNameIndex();
                IndexRebuilt?.Invoke(folders.Select(f => f.FolderPath).ToList());
            }
            finally
            {
                IsIndexing = false;
            }
        }

        private static void CopyDirectoryRecursive(string sourceDir, string destinationDir)
        {
            var sourceInfo = new DirectoryInfo(sourceDir);
            if (!sourceInfo.Exists)
            {
                throw new DirectoryNotFoundException($"Source directory not found: {sourceDir}");
            }

            Directory.CreateDirectory(destinationDir);

            foreach (var file in sourceInfo.GetFiles())
            {
                var destinationFile = Path.Combine(destinationDir, file.Name);
                file.CopyTo(destinationFile, overwrite: false);
            }

            foreach (var subDirectory in sourceInfo.GetDirectories())
            {
                var nestedDestination = Path.Combine(destinationDir, subDirectory.Name);
                CopyDirectoryRecursive(subDirectory.FullName, nestedDestination);
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
			_eventProcessor?.Dispose();
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