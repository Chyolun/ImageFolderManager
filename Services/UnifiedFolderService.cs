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
using ImageFolderManager.Services;
using Timer = System.Threading.Timer;

namespace ImageFolderManager.Services
{
    /// <summary>
    /// Unified service that combines folder management and real-time indexing with Command pattern integration
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
        public event EventHandler<CommandExecutionEventArgs> CommandExecuted;
        public event EventHandler<FolderStateChangedEventArgs> FolderStateChanged;

        #endregion

        #region Fields and Properties

        // Services
        private readonly FolderTagService _tagService = new FolderTagService();

        // Command System Integration
        private readonly CommandSystemInitializer _commandSystem;
        private CommandExecutor _commandExecutor;
        private FolderStateMachine _stateMachine;
        private PathLockManager _pathLockManager;
        private ExceptionHandlingService _exceptionService;

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

        // Command System Properties
        public CommandExecutor CommandExecutor => _commandExecutor;
        public FolderStateMachine StateMachine => _stateMachine;
        public bool IsCommandSystemEnabled => _commandSystem != null && _commandExecutor != null;

        #endregion

        #region Constructor

        public UnifiedFolderService()
        {
            _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

            // Initialize command system
            _commandSystem = new CommandSystemInitializer();
            try
            {
                _commandSystem.Initialize();
                _commandExecutor = _commandSystem.CommandExecutor;
                _stateMachine = _commandSystem.StateMachine;
                _pathLockManager = _commandSystem.PathLockManager;
                _exceptionService = _commandSystem.ExceptionService;

                // Subscribe to command system events
                if (_commandExecutor != null)
                {
                    _commandExecutor.CommandStarted += OnCommandStarted;
                    _commandExecutor.CommandCompleted += OnCommandCompleted;
                    _commandExecutor.CommandFailed += OnCommandFailed;
                }

                if (_stateMachine != null)
                {
                    _stateMachine.StateChanged += OnFolderStateChanged;
                }

                Debug.WriteLine("Command system integration initialized successfully");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to initialize command system: {ex.Message}");
                // Continue without command system if initialization fails
                _commandSystem?.Dispose();
                _commandSystem = null;
            }

            // Initialize timer for delayed event processing
            _eventProcessingTimer = new Timer(ProcessQueuedEvents, null, _debounceInterval, _debounceInterval);
        }

        #endregion

        #region Command System Event Handlers

        private void OnCommandStarted(object sender, CommandExecutionEventArgs e)
        {
            Debug.WriteLine($"Command started: {e.Command.CommandId} ({e.Command.CommandType})");
            CommandExecuted?.Invoke(this, e);
        }

        private void OnCommandCompleted(object sender, CommandExecutionEventArgs e)
        {
            Debug.WriteLine($"Command completed: {e.Command.CommandId} - {e.Result?.Message}");
            CommandExecuted?.Invoke(this, e);
        }

        private void OnCommandFailed(object sender, CommandExecutionEventArgs e)
        {
            Debug.WriteLine($"Command failed: {e.Command.CommandId} - {e.Result?.Message}");
            _exceptionService?.LogException("CommandExecution", e.Result?.Exception,
                $"Command {e.Command.CommandId} failed");
            CommandExecuted?.Invoke(this, e);
        }

        private void OnFolderStateChanged(object sender, FolderStateChangedEventArgs e)
        {
            Debug.WriteLine($"Folder state changed: {e.Path} {e.OldState} → {e.NewState}");
            FolderStateChanged?.Invoke(this, e);
        }

        #endregion

        #region Monitoring and Indexing

        /// <summary>
        /// Starts monitoring and builds initial index with state machine integration
        /// </summary>
        public async Task StartMonitoringAsync(string rootPath)
        {
            if (_isDisposed || string.IsNullOrEmpty(rootPath))
                return;

            if (!Directory.Exists(rootPath))
                throw new DirectoryNotFoundException($"Root directory not found: {rootPath}");

            try
            {
                // Stop any existing monitoring
                await StopMonitoringAsync();

                _rootDirectory = PathService.NormalizePath(rootPath);
                Debug.WriteLine($"Starting unified folder monitoring for: {_rootDirectory}");

                // Initialize state machine for root directory if available
                if (_stateMachine != null)
                {
                    await _stateMachine.TransitionStateAsync(_rootDirectory, FolderState.Monitoring);
                }

                // Build initial index
                await BuildInitialIndexAsync();

                // Set up file system watcher
                SetupFileSystemWatcher();

                Debug.WriteLine($"Unified folder monitoring started. Indexed {_folderIndex.Count} folders.");
            }
            catch (Exception ex)
            {
                _exceptionService?.LogException("MonitoringStartup", ex, $"Failed to start monitoring for {rootPath}");
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

            // Clear state machine states if available
            if (_stateMachine != null)
            {
                await _stateMachine.ClearAllStatesAsync();
            }

            _rootDirectory = null;
            Debug.WriteLine("Unified folder monitoring stopped.");
        }

        private async Task BuildInitialIndexAsync()
        {
            if (string.IsNullOrEmpty(_rootDirectory))
                return;

            _isIndexing = true;
            try
            {
                var directories = new List<string>();
                await Task.Run(() => ScanDirectoryRecursive(_rootDirectory, directories));

                foreach (var directory in directories)
                {
                    var entry = new FolderIndexEntry(directory);
                    _folderIndex.TryAdd(entry.FullPath, entry);

                    // Set initial state in state machine
                    if (_stateMachine != null)
                    {
                        await _stateMachine.TransitionStateAsync(directory, FolderState.Available);
                    }
                }

                IndexRebuilt?.Invoke(directories);
            }
            finally
            {
                _isIndexing = false;
            }
        }

        #endregion

        #region Enhanced Folder Operations with Command Pattern

        /// <summary>
        /// Create a folder using the command pattern
        /// </summary>
        public async Task<CommandResult> CreateFolderAsync(string parentPath, string folderName, CancellationToken cancellationToken = default)
        {
            if (!IsCommandSystemEnabled)
            {
                // Fallback to legacy creation
                return await CreateFolderLegacyAsync(parentPath, folderName);
            }

            try
            {
                var command = new CreateFolderCommand(parentPath, folderName);
                return await _commandExecutor.ExecuteCommandAsync(command, cancellationToken);
            }
            catch (Exception ex)
            {
                _exceptionService?.LogException("CreateFolder", ex, $"Failed to create folder {folderName} in {parentPath}");
                return CommandResult.CreateFailure($"Failed to create folder: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Delete a folder using the command pattern
        /// </summary>
        public async Task<CommandResult> DeleteFolderAsync(string folderPath, bool useRecycleBin = true, CancellationToken cancellationToken = default)
        {
            if (!IsCommandSystemEnabled)
            {
                // Fallback to legacy deletion
                return await DeleteFolderLegacyAsync(folderPath, useRecycleBin);
            }

            try
            {
                var command = new DeleteFolderCommand(folderPath, useRecycleBin);
                return await _commandExecutor.ExecuteCommandAsync(command, cancellationToken);
            }
            catch (Exception ex)
            {
                _exceptionService?.LogException("DeleteFolder", ex, $"Failed to delete folder {folderPath}");
                return CommandResult.CreateFailure($"Failed to delete folder: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Rename a folder using the command pattern
        /// </summary>
        public async Task<CommandResult> RenameFolderAsync(string folderPath, string newName, CancellationToken cancellationToken = default)
        {
            if (!IsCommandSystemEnabled)
            {
                // Fallback to legacy rename
                return await RenameFolderLegacyAsync(folderPath, newName);
            }

            try
            {
                var command = new RenameFolderCommand(folderPath, newName);
                return await _commandExecutor.ExecuteCommandAsync(command, cancellationToken);
            }
            catch (Exception ex)
            {
                _exceptionService?.LogException("RenameFolder", ex, $"Failed to rename folder {folderPath} to {newName}");
                return CommandResult.CreateFailure($"Failed to rename folder: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Move a folder using the command pattern
        /// </summary>
        public async Task<CommandResult> MoveFolderAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
        {
            if (!IsCommandSystemEnabled)
            {
                // Fallback to legacy move
                return await MoveFolderLegacyAsync(sourcePath, destinationPath);
            }

            try
            {
                var command = new MoveFolderCommand(sourcePath, destinationPath);
                return await _commandExecutor.ExecuteCommandAsync(command, cancellationToken);
            }
            catch (Exception ex)
            {
                _exceptionService?.LogException("MoveFolder", ex, $"Failed to move folder from {sourcePath} to {destinationPath}");
                return CommandResult.CreateFailure($"Failed to move folder: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Copy a folder using the command pattern
        /// </summary>
        public async Task<CommandResult> CopyFolderAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
        {
            if (!IsCommandSystemEnabled)
            {
                // Fallback to legacy copy
                return await CopyFolderLegacyAsync(sourcePath, destinationPath);
            }

            try
            {
                var command = new CopyFolderCommand(sourcePath, destinationPath);
                return await _commandExecutor.ExecuteCommandAsync(command, cancellationToken);
            }
            catch (Exception ex)
            {
                _exceptionService?.LogException("CopyFolder", ex, $"Failed to copy folder from {sourcePath} to {destinationPath}");
                return CommandResult.CreateFailure($"Failed to copy folder: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Undo the last operation
        /// </summary>
        public async Task<CommandResult> UndoLastOperationAsync(CancellationToken cancellationToken = default)
        {
            if (!IsCommandSystemEnabled)
            {
                return CommandResult.CreateFailure("Command system not available for undo operations");
            }

            try
            {
                return await _commandExecutor.UndoLastCommandAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _exceptionService?.LogException("UndoOperation", ex, "Failed to undo last operation");
                return CommandResult.CreateFailure($"Failed to undo: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Get folder state information
        /// </summary>
        public FolderState GetFolderState(string folderPath)
        {
            if (!IsCommandSystemEnabled || string.IsNullOrEmpty(folderPath))
                return FolderState.Available;

            return _stateMachine.GetFolderState(folderPath);
        }

        /// <summary>
        /// Check if folder operations are available for the given path
        /// </summary>
        public bool CanOperateOnFolder(string folderPath)
        {
            if (!IsCommandSystemEnabled)
                return true; // Legacy mode allows all operations

            var state = GetFolderState(folderPath);
            return state == FolderState.Available || state == FolderState.Monitoring;
        }

        #endregion

        #region Legacy Folder Operations (Fallback)

        private async Task<CommandResult> CreateFolderLegacyAsync(string parentPath, string folderName)
        {
            try
            {
                var fullPath = Path.Combine(parentPath, folderName);
                if (Directory.Exists(fullPath))
                {
                    return CommandResult.CreateFailure("Folder already exists");
                }

                Directory.CreateDirectory(fullPath);
                return CommandResult.CreateSuccess($"Folder '{folderName}' created successfully");
            }
            catch (Exception ex)
            {
                return CommandResult.CreateFailure($"Failed to create folder: {ex.Message}", ex);
            }
        }

        private async Task<CommandResult> DeleteFolderLegacyAsync(string folderPath, bool useRecycleBin)
        {
            try
            {
                if (!Directory.Exists(folderPath))
                {
                    return CommandResult.CreateFailure("Folder does not exist");
                }

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

                return CommandResult.CreateSuccess("Folder deleted successfully");
            }
            catch (Exception ex)
            {
                return CommandResult.CreateFailure($"Failed to delete folder: {ex.Message}", ex);
            }
        }

        private async Task<CommandResult> RenameFolderLegacyAsync(string folderPath, string newName)
        {
            try
            {
                if (!Directory.Exists(folderPath))
                {
                    return CommandResult.CreateFailure("Folder does not exist");
                }

                var parentPath = Path.GetDirectoryName(folderPath);
                var newPath = Path.Combine(parentPath, newName);

                if (Directory.Exists(newPath))
                {
                    return CommandResult.CreateFailure("A folder with that name already exists");
                }

                Directory.Move(folderPath, newPath);
                return CommandResult.CreateSuccess($"Folder renamed to '{newName}' successfully");
            }
            catch (Exception ex)
            {
                return CommandResult.CreateFailure($"Failed to rename folder: {ex.Message}", ex);
            }
        }

        private async Task<CommandResult> MoveFolderLegacyAsync(string sourcePath, string destinationPath)
        {
            try
            {
                if (!Directory.Exists(sourcePath))
                {
                    return CommandResult.CreateFailure("Source folder does not exist");
                }

                if (Directory.Exists(destinationPath))
                {
                    return CommandResult.CreateFailure("Destination folder already exists");
                }

                Directory.Move(sourcePath, destinationPath);
                return CommandResult.CreateSuccess("Folder moved successfully");
            }
            catch (Exception ex)
            {
                return CommandResult.CreateFailure($"Failed to move folder: {ex.Message}", ex);
            }
        }

        private async Task<CommandResult> CopyFolderLegacyAsync(string sourcePath, string destinationPath)
        {
            try
            {
                if (!Directory.Exists(sourcePath))
                {
                    return CommandResult.CreateFailure("Source folder does not exist");
                }

                if (Directory.Exists(destinationPath))
                {
                    return CommandResult.CreateFailure("Destination folder already exists");
                }

                await Task.Run(() => DirectoryCopy(sourcePath, destinationPath, true));
                return CommandResult.CreateSuccess("Folder copied successfully");
            }
            catch (Exception ex)
            {
                return CommandResult.CreateFailure($"Failed to copy folder: {ex.Message}", ex);
            }
        }

        #endregion

        #region Existing Methods (preserved for compatibility)

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
                        images.Add(new ImageInfo
                        {
                            FilePath = file,
                            FileName = Path.GetFileName(file)
                        });
                    }
                }

                _dispatcher.BeginInvoke(() =>
                {
                    folder.Images.Clear();
                    foreach (var image in images)
                    {
                        folder.Images.Add(image);
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading images for {folder.FolderPath}: {ex.Message}");
            }
        }

        #endregion

        #region Helper Methods

        private void SetupFileSystemWatcher()
        {
            if (string.IsNullOrEmpty(_rootDirectory) || !Directory.Exists(_rootDirectory))
                return;

            _rootWatcher = new FileSystemWatcher(_rootDirectory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.CreationTime | NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };

            _rootWatcher.Created += OnFileSystemChanged;
            _rootWatcher.Deleted += OnFileSystemChanged;
            _rootWatcher.Renamed += OnFileSystemRenamed;

            Debug.WriteLine($"File system watcher configured for: {_rootDirectory}");
        }

        private void OnFileSystemChanged(object sender, FileSystemEventArgs e)
        {
            if (_isDisposed || !Directory.Exists(e.FullPath) && e.ChangeType != WatcherChangeTypes.Deleted)
                return;

            var eventData = new FileSystemEventData
            {
                ChangeType = e.ChangeType,
                FullPath = PathService.NormalizePath(e.FullPath)
            };

            _eventQueue.Enqueue(eventData);
        }

        private void OnFileSystemRenamed(object sender, RenamedEventArgs e)
        {
            if (_isDisposed)
                return;

            var eventData = new FileSystemEventData
            {
                ChangeType = WatcherChangeTypes.Renamed,
                FullPath = PathService.NormalizePath(e.FullPath),
                OldPath = PathService.NormalizePath(e.OldFullPath)
            };

            _eventQueue.Enqueue(eventData);
        }

        private void ProcessQueuedEvents(object state)
        {
            if (_isDisposed || _eventQueue.IsEmpty)
                return;

            lock (_processingLock)
            {
                var processedEvents = new List<FileSystemEventData>();

                while (_eventQueue.TryDequeue(out var eventData))
                {
                    var normalizedPath = eventData.FullPath;

                    // Debounce logic
                    if (_lastEventTime.TryGetValue(normalizedPath, out var lastTime))
                    {
                        if (DateTime.Now - lastTime < _debounceInterval)
                            continue;
                    }

                    _lastEventTime[normalizedPath] = DateTime.Now;
                    processedEvents.Add(eventData);
                }

                // Process events on UI thread
                if (processedEvents.Count > 0)
                {
                    _dispatcher.BeginInvoke(() =>
                    {
                        foreach (var eventData in processedEvents)
                        {
                            ProcessFileSystemEvent(eventData);
                        }
                    });
                }
            }
        }

        private void ProcessFileSystemEvent(FileSystemEventData eventData)
        {
            try
            {
                switch (eventData.ChangeType)
                {
                    case WatcherChangeTypes.Created:
                        HandleFolderCreated(eventData.FullPath, eventData);
                        break;
                    case WatcherChangeTypes.Deleted:
                        HandleFolderDeleted(eventData.FullPath, eventData);
                        break;
                    case WatcherChangeTypes.Renamed:
                        HandleFolderRenamed(eventData.OldPath, eventData.FullPath, eventData);
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error processing file system event: {ex.Message}");
            }
        }

        private void HandleFolderCreated(string folderPath, FileSystemEventData eventData)
        {
            if (!Directory.Exists(folderPath))
                return;

            var entry = new FolderIndexEntry(folderPath);
            if (_folderIndex.TryAdd(entry.FullPath, entry))
            {
                Debug.WriteLine($"Folder added to unified index: {folderPath}");

                // Update state machine
                if (_stateMachine != null)
                {
                    Task.Run(async () => await _stateMachine.TransitionStateAsync(folderPath, FolderState.Available));
                }

                FolderCreated?.Invoke(folderPath);
                FireLegacyFileSystemEvent(folderPath, eventData);
            }
        }

        private void HandleFolderDeleted(string folderPath, FileSystemEventData eventData)
        {
            if (_folderIndex.TryRemove(folderPath, out var removedEntry))
            {
                Debug.WriteLine($"Folder removed from unified index: {folderPath}");

                // Update state machine
                if (_stateMachine != null)
                {
                    Task.Run(async () => await _stateMachine.RemoveFolderAsync(folderPath));
                }

                FolderDeleted?.Invoke(folderPath);
                FireLegacyFileSystemEvent(folderPath, eventData);
            }
        }

        private void HandleFolderRenamed(string oldPath, string newPath, FileSystemEventData eventData)
        {
            if (_folderIndex.TryRemove(oldPath, out var oldEntry))
            {
                var newEntry = new FolderIndexEntry(newPath);
                _folderIndex.TryAdd(newEntry.FullPath, newEntry);

                Debug.WriteLine($"Folder renamed in unified index: {oldPath} → {newPath}");

                // Update state machine
                if (_stateMachine != null)
                {
                    Task.Run(async () =>
                    {
                        await _stateMachine.RemoveFolderAsync(oldPath);
                        await _stateMachine.TransitionStateAsync(newPath, FolderState.Available);
                    });
                }

                FolderRenamed?.Invoke(oldPath, newPath);
                FireLegacyFileSystemEvent(newPath, eventData);
            }
        }

        private void FireLegacyFileSystemEvent(string path, FileSystemEventData eventData)
        {
            try
            {
                var folderInfo = CreateFolderInfoWithoutImagesAsync(path).Result;
                var args = new FileSystemEventArgs(eventData.ChangeType, Path.GetDirectoryName(path), Path.GetFileName(path));
                FileSystemEvent?.Invoke(folderInfo, args, eventData.ChangeType);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error firing legacy file system event: {ex.Message}");
            }
        }

        private void ScanDirectoryRecursive(string path, List<string> directories)
        {
            try
            {
                directories.Add(path);

                foreach (var subdirectory in Directory.GetDirectories(path))
                {
                    ScanDirectoryRecursive(subdirectory, directories);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error scanning directory {path}: {ex.Message}");
            }
        }

        private static void DirectoryCopy(string sourceDirName, string destDirName, bool copySubDirs)
        {
            var dir = new DirectoryInfo(sourceDirName);

            if (!dir.Exists)
            {
                throw new DirectoryNotFoundException($"Source directory does not exist or could not be found: {sourceDirName}");
            }

            DirectoryInfo[] dirs = dir.GetDirectories();
            Directory.CreateDirectory(destDirName);

            FileInfo[] files = dir.GetFiles();
            foreach (FileInfo file in files)
            {
                string tempPath = Path.Combine(destDirName, file.Name);
                file.CopyTo(tempPath, false);
            }

            if (copySubDirs)
            {
                foreach (DirectoryInfo subdir in dirs)
                {
                    string tempPath = Path.Combine(destDirName, subdir.Name);
                    DirectoryCopy(subdir.FullName, tempPath, copySubDirs);
                }
            }
        }

        #endregion

        #region Nested Classes (preserved)

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
            public string OldPath { get; set; }
            public DateTime Timestamp { get; set; } = DateTime.Now;
        }

        #endregion

        #region Disposal

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;

            try
            {
                // Stop monitoring
                StopMonitoringAsync().Wait(5000);

                // Dispose timer
                _eventProcessingTimer?.Dispose();

                // Dispose command system
                if (_commandExecutor != null)
                {
                    _commandExecutor.CommandStarted -= OnCommandStarted;
                    _commandExecutor.CommandCompleted -= OnCommandCompleted;
                    _commandExecutor.CommandFailed -= OnCommandFailed;
                }

                if (_stateMachine != null)
                {
                    _stateMachine.StateChanged -= OnFolderStateChanged;
                }

                _commandSystem?.Dispose();

                Debug.WriteLine("UnifiedFolderService disposed");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during UnifiedFolderService disposal: {ex.Message}");
            }
        }

        #endregion
    }
}