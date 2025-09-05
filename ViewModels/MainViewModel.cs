using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using ImageFolderManager.Models;
using ImageFolderManager.Services;
using ImageFolderManager.Views;
using ImageFolderManager.Controls;
using ImageFolderManager.Commands;
using ImageFolderManager.StateMachine;
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;
using Microsoft.WindowsAPICodePack.Dialogs;
using static ImageFolderManager.Controls.ShellTreeView;
using System.Diagnostics;
using System.Threading;

namespace ImageFolderManager.ViewModels
{
    /// <summary>
    /// Enhanced MainViewModel with Command pattern and State machine integration
    /// </summary>
    public class MainViewModel : ViewModelBase
    {
        #region Sub-ViewModels

        public FolderOperationsViewModel FolderOperations { get; }
        public SearchViewModel Search { get; }
        public ImageLoadingViewModel ImageLoading { get; }
        public TagManagementViewModel TagManagement { get; }

        #endregion

        #region Command System Properties

        /// <summary>
        /// Indicates if the command system is available and operational
        /// </summary>
        public bool IsCommandSystemEnabled => _unifiedFolderService?.IsCommandSystemEnabled ?? false;

        /// <summary>
        /// Get the current command executor for advanced operations
        /// </summary>
        public CommandExecutor CommandExecutor => _unifiedFolderService?.CommandExecutor;

        /// <summary>
        /// Get the folder state machine for state monitoring
        /// </summary>
        public FolderStateMachine StateMachine => _unifiedFolderService?.StateMachine;

        /// <summary>
        /// Command system status message
        /// </summary>
        private string _commandSystemStatus;
        public string CommandSystemStatus
        {
            get => _commandSystemStatus;
            private set => SetProperty(ref _commandSystemStatus, value);
        }

        /// <summary>
        /// Indicates if any command operations are currently in progress
        /// </summary>
        public bool IsCommandOperationInProgress => FolderOperations?.IsOperationInProgress ?? false;

        #endregion

        #region Properties

        private System.Threading.Timer _statusMessageTimer;
        private bool _isImportantStatusMessageActive = false;
        private readonly object _statusMessageLock = new object();

        private readonly UnifiedFolderService _unifiedFolderService;
        private readonly FolderTagService _tagService;
        private readonly List<FolderInfo> _allLoadedFolders;

        private ObservableCollection<FolderInfo> _folders = new ObservableCollection<FolderInfo>();
        public ObservableCollection<FolderInfo> Folders
        {
            get => _folders;
            set => SetProperty(ref _folders, value);
        }

        private FolderInfo _selectedFolder;
        public FolderInfo SelectedFolder
        {
            get => _selectedFolder;
            set
            {
                if (SetProperty(ref _selectedFolder, value))
                {
                    OnSelectedFolderChanged();
                }
            }
        }

        private string _statusMessage = "Ready";
        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        private string _tagInputText = string.Empty;
        public string TagInputText
        {
            get => _tagInputText;
            set => SetProperty(ref _tagInputText, value);
        }

        private ObservableCollection<FolderInfo> _treeViewItems = new ObservableCollection<FolderInfo>();
        public ObservableCollection<FolderInfo> TreeViewItems
        {
            get => _treeViewItems;
            set => SetProperty(ref _treeViewItems, value);
        }

        private FolderInfo _selectedTreeViewItem;
        public FolderInfo SelectedTreeViewItem
        {
            get => _selectedTreeViewItem;
            set
            {
                if (SetProperty(ref _selectedTreeViewItem, value))
                {
                    OnTreeViewSelectionChanged();
                }
            }
        }

        // Preview settings
        public int PreviewWidth => AppSettings.Instance.PreviewWidth;
        public int PreviewHeight => AppSettings.Instance.PreviewHeight;

        /// Checks if folder indexing is currently in progress
        public bool IsIndexing => _unifiedFolderService?.IsIndexing == true;

        /// Gets the current root directory path
        public string CurrentRootDirectory => _unifiedFolderService?.RootDirectory ?? string.Empty;

        #endregion

        #region Commands

        // Delegate to sub-ViewModels
        public IAsyncRelayCommand SaveTagsCommand => TagManagement.SaveTagsCommand;
        public IAsyncRelayCommand SearchCommand => Search.SearchCommand;
        public ICommand SetRatingCommand => TagManagement.SetRatingCommand;
        public ICommand EditTagsCommand => TagManagement.EditTagsCommand;

        public IAsyncRelayCommand<FolderInfo> DeleteFolderCommand => FolderOperations.DeleteFolderCommand;
        public IAsyncRelayCommand TagsCloudCommand => TagManagement.TagsCloudCommand;

        // Main commands
        public IAsyncRelayCommand SetRootDirectoryCommand { get; }
        public ICommand CollapseParentDirectoryCommand { get; }

        // Edit Commands (Enhanced with command system awareness)
        private RelayCommand _cutCommand;
        private RelayCommand _copyCommand;
        private RelayCommand _pasteCommand;
        private RelayCommand _deleteCommand;
        public ICommand CutCommand => _cutCommand;
        public ICommand CopyCommand => _copyCommand;
        public ICommand PasteCommand => _pasteCommand;
        public ICommand DeleteCommand => _deleteCommand;

        // Enhanced undo with command system integration
        public ICommand UndoFolderMovementCommand => FolderOperations.UndoFolderMovementCommand;
        public IAsyncRelayCommand UndoLastCommandCommand => FolderOperations.UndoLastCommandCommand;

        // Command system specific commands
        public ICommand CancelCurrentOperationCommand => FolderOperations.CancelCurrentOperationCommand;
        public ICommand RefreshCommandSystemStatusCommand { get; }

        #endregion

        public MainViewModel()
        {
            // Initialize shared category service
            var categoryService = new TagCategoryService();

            // Initialize services
            _unifiedFolderService = new UnifiedFolderService();
            _tagService = new FolderTagService(categoryService);
            _allLoadedFolders = new List<FolderInfo>();

            // Initialize sub-ViewModels with enhanced capabilities
            FolderOperations = new FolderOperationsViewModel(_unifiedFolderService);
            Search = new SearchViewModel(_unifiedFolderService, _allLoadedFolders);
            ImageLoading = new ImageLoadingViewModel(_unifiedFolderService);
            TagManagement = new TagManagementViewModel(_tagService, new TagCloudViewModel(categoryService));

            // Initialize commands
            _cutCommand = new RelayCommand(ExecuteCutCommand, CanExecuteCutCommand);
            _copyCommand = new RelayCommand(ExecuteCopyCommand, CanExecuteCopyCommand);
            _pasteCommand = new RelayCommand(ExecutePasteCommand, CanExecutePasteCommand);
            _deleteCommand = new RelayCommand(ExecuteDeleteCommand, CanExecuteDeleteCommand);

            SetRootDirectoryCommand = new AsyncRelayCommand(SetDefaultRootDirectoryAsync);
            CollapseParentDirectoryCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(
                CollapseParentDirectory,
                CanCollapseParentDirectory);
            RefreshCommandSystemStatusCommand = new RelayCommand(RefreshCommandSystemStatus);

            // Subscribe to events from sub-ViewModels
            SubscribeToSubViewModelEvents();

            // Subscribe to unified service events
            SubscribeToServiceEvents();

            // Subscribe to command system events
            SubscribeToCommandSystemEvents();

            // Initialize command system status
            UpdateCommandSystemStatus();
        }

        #region Command System Integration

        private void SubscribeToCommandSystemEvents()
        {
            if (_unifiedFolderService?.IsCommandSystemEnabled == true)
            {
                _unifiedFolderService.CommandExecuted += OnCommandExecuted;
                _unifiedFolderService.FolderStateChanged += OnFolderStateChanged;

                CommandSystemStatus = "Command system initialized and ready";
                Debug.WriteLine("Command system events subscribed successfully");
            }
            else
            {
                CommandSystemStatus = "Command system not available - using legacy operations";
                Debug.WriteLine("Command system not available, using legacy mode");
            }
        }

        private void OnCommandExecuted(object sender, CommandExecutionEventArgs e)
        {
            // Update properties that depend on command system state
            OnPropertyChanged(nameof(IsCommandOperationInProgress));

            // Update command system status based on execution phase
            switch (e.Phase)
            {
                case CommandExecutionPhase.Started:
                    CommandSystemStatus = $"Executing {e.Command.CommandType}: {e.Command.CommandId}";
                    break;

                case CommandExecutionPhase.Completed:
                    CommandSystemStatus = $"Completed {e.Command.CommandType} successfully";

                    // Refresh UI if needed based on command type
                    if (ShouldRefreshUIAfterCommand(e.Command))
                    {
                        _ = Task.Run(async () => await RefreshFolderStructureAsync());
                    }
                    break;

                case CommandExecutionPhase.Failed:
                    CommandSystemStatus = $"Failed {e.Command.CommandType}: {e.Result?.Message}";
                    break;
            }
        }

        private void OnFolderStateChanged(object sender, FolderStateChangedEventArgs e)
        {
            // Update UI elements based on folder state changes
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                // Update any folder-specific UI indicators
                var affectedFolder = FindFolderByPath(e.Path);
                if (affectedFolder != null)
                {
                    // Trigger property change to update UI bindings
                    OnPropertyChanged(nameof(SelectedFolder));
                }
            });
        }

        private bool ShouldRefreshUIAfterCommand(IFolderCommand command)
        {
            // Refresh UI for operations that change folder structure
            return command.CommandType == FolderCommandType.Create ||
                   command.CommandType == FolderCommandType.Delete ||
                   command.CommandType == FolderCommandType.Move ||
                   command.CommandType == FolderCommandType.Rename;
        }

        private void UpdateCommandSystemStatus()
        {
            if (IsCommandSystemEnabled)
            {
                var historyCount = CommandExecutor?.HistoryCount ?? 0;
                CommandSystemStatus = $"Command system active - {historyCount} operations in history";
            }
            else
            {
                CommandSystemStatus = "Using legacy folder operations";
            }
        }

        private void RefreshCommandSystemStatus()
        {
            UpdateCommandSystemStatus();
            OnPropertyChanged(nameof(IsCommandSystemEnabled));
            OnPropertyChanged(nameof(IsCommandOperationInProgress));
        }

        /// <summary>
        /// Get detailed folder state information for UI display
        /// </summary>
        public string GetFolderStateInfo(string folderPath)
        {
            if (!IsCommandSystemEnabled || string.IsNullOrEmpty(folderPath))
                return "Available";

            return FolderOperations.GetFolderStateDisplay(folderPath);
        }

        /// <summary>
        /// Check if operations can be performed on the currently selected folder
        /// </summary>
        public bool CanOperateOnSelectedFolder()
        {
            if (SelectedFolder == null)
                return false;

            return FolderOperations.CanOperateOnFolder(SelectedFolder.FolderPath);
        }

        #endregion

        #region Event Subscriptions

        private void SubscribeToSubViewModelEvents()
        {
            // Forward status messages with command system awareness
            FolderOperations.StatusMessageChanged += (s, message) => SetImportantStatusMessage($"[Folder Ops] {message}");
            Search.StatusMessageChanged += (s, message) => StatusMessage = $"[Search] {message}";
            ImageLoading.StatusMessageChanged += (s, message) => StatusMessage = $"[Images] {message}";
            TagManagement.StatusMessageChanged += (s, message) => StatusMessage = $"[Tags] {message}";

            FolderOperations.PropertyChanged += OnFolderOperationsPropertyChanged;

            // Handle search result selection
            Search.SearchResultSelected += OnSearchResultSelected;

            // Handle folder operations completion with command system integration
            FolderOperations.FolderOperationCompleted += OnFolderOperationCompleted;
        }

        private void OnFolderOperationCompleted(object sender, FolderOperationEventArgs e)
        {
            // Refresh the UI after successful operations
            if (e.Success)
            {
                _ = Task.Run(async () => await RefreshCurrentView());
            }

            // Update command system status after operations
            if (IsCommandSystemEnabled)
            {
                RefreshCommandSystemStatus();
            }
        }

        private void SubscribeToServiceEvents()
        {
            if (_unifiedFolderService != null)
            {
                _unifiedFolderService.FolderCreated += OnFolderCreated;
                _unifiedFolderService.FolderDeleted += OnFolderDeleted;
                _unifiedFolderService.FolderRenamed += OnFolderRenamed;
                _unifiedFolderService.IndexRebuilt += OnIndexRebuilt;
            }
        }

        private void OnFolderOperationsPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Update main viewmodel properties based on folder operations changes
            if (e.PropertyName == nameof(FolderOperations.IsOperationInProgress))
            {
                OnPropertyChanged(nameof(IsCommandOperationInProgress));
            }
        }

        #endregion

        #region Enhanced Folder Operations

        /// <summary>
        /// Enhanced cut command with command system awareness
        /// </summary>
        private void ExecuteCutCommand()
        {
            if (SelectedFolder != null)
            {
                // Check if folder can be operated on before cutting
                if (IsCommandSystemEnabled && !FolderOperations.CanOperateOnFolder(SelectedFolder.FolderPath))
                {
                    var state = FolderOperations.GetFolderStateDisplay(SelectedFolder.FolderPath);
                    SetImportantStatusMessage($"Cannot cut folder: currently {state}");
                    return;
                }

                FolderOperations.CutFolders(new[] { SelectedFolder });
            }
        }

        private bool CanExecuteCutCommand()
        {
            return SelectedFolder != null &&
                   (!IsCommandSystemEnabled || FolderOperations.CanOperateOnFolder(SelectedFolder.FolderPath));
        }

        /// <summary>
        /// Enhanced copy command with command system awareness
        /// </summary>
        private void ExecuteCopyCommand()
        {
            if (SelectedFolder != null)
            {
                FolderOperations.CopyFolders(new[] { SelectedFolder });
            }
        }

        private bool CanExecuteCopyCommand()
        {
            return SelectedFolder != null;
        }

        /// <summary>
        /// Enhanced paste command with command system integration
        /// </summary>
        private async void ExecutePasteCommand()
        {
            if (SelectedFolder != null)
            {
                // Check destination folder state if command system is enabled
                if (IsCommandSystemEnabled && !FolderOperations.CanOperateOnFolder(SelectedFolder.FolderPath))
                {
                    var state = FolderOperations.GetFolderStateDisplay(SelectedFolder.FolderPath);
                    SetImportantStatusMessage($"Cannot paste to folder: currently {state}");
                    return;
                }

                await FolderOperations.PasteFoldersAsync(SelectedFolder.FolderPath);
            }
        }

        private bool CanExecutePasteCommand()
        {
            return SelectedFolder != null &&
                   FolderOperations.HasClipboardContent &&
                   (!IsCommandSystemEnabled || FolderOperations.CanOperateOnFolder(SelectedFolder.FolderPath));
        }

        /// <summary>
        /// Enhanced delete command with command system integration
        /// </summary>
        private async void ExecuteDeleteCommand()
        {
            if (SelectedFolder != null)
            {
                await FolderOperations.DeleteFolderAsync(SelectedFolder);
            }
        }

        private bool CanExecuteDeleteCommand()
        {
            return SelectedFolder != null &&
                   (!IsCommandSystemEnabled || FolderOperations.CanOperateOnFolder(SelectedFolder.FolderPath));
        }

        #endregion

        #region Directory Management (Enhanced)

        /// <summary>
        /// Set root directory with command system initialization
        /// </summary>
        public async Task SetDefaultRootDirectoryAsync()
        {
            try
            {
                var dialog = new CommonOpenFileDialog
                {
                    IsFolderPicker = true,
                    Title = "Select Root Directory for Image Folder Management"
                };

                if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    string selectedPath = dialog.FileName;
                    StatusMessage = $"Initializing folder monitoring for: {selectedPath}";

                    // Stop any existing monitoring
                    await _unifiedFolderService.StopMonitoringAsync();

                    // Start monitoring with command system integration
                    await _unifiedFolderService.StartMonitoringAsync(selectedPath);

                    // Load folder structure
                    await LoadFolderStructureAsync(selectedPath);

                    // Update command system status
                    if (IsCommandSystemEnabled)
                    {
                        RefreshCommandSystemStatus();
                        SetImportantStatusMessage($"Command system active for: {selectedPath}");
                    }
                    else
                    {
                        SetImportantStatusMessage($"Folder monitoring started (legacy mode): {selectedPath}");
                    }
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error setting root directory: {ex.Message}";
                MessageBox.Show($"Failed to set root directory: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task LoadFolderStructureAsync(string rootPath)
        {
            try
            {
                StatusMessage = "Loading folder structure...";

                await Task.Run(async () =>
                {
                    var rootFolder = await _unifiedFolderService.CreateFolderInfoWithoutImagesAsync(rootPath);
                    await LoadChildFolders(rootFolder);

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        TreeViewItems.Clear();
                        TreeViewItems.Add(rootFolder);

                        Folders.Clear();
                        if (rootFolder.Children.Any())
                        {
                            foreach (var child in rootFolder.Children)
                            {
                                Folders.Add(child);
                            }
                        }

                        _allLoadedFolders.Clear();
                        CollectAllFolders(rootFolder, _allLoadedFolders);
                    });
                });

                StatusMessage = $"Loaded {_allLoadedFolders.Count} folders";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error loading folder structure: {ex.Message}";
                Debug.WriteLine($"Error in LoadFolderStructureAsync: {ex}");
            }
        }

        #endregion

        #region Helper Methods

        private FolderInfo FindFolderByPath(string path)
        {
            return _allLoadedFolders.FirstOrDefault(f =>
                string.Equals(f.FolderPath, path, StringComparison.OrdinalIgnoreCase));
        }

        private async Task RefreshFolderStructureAsync()
        {
            if (!string.IsNullOrEmpty(CurrentRootDirectory))
            {
                await LoadFolderStructureAsync(CurrentRootDirectory);
            }
        }

        private async Task RefreshCurrentView()
        {
            Application.Current?.Dispatcher.BeginInvoke(async () =>
            {
                // Refresh the current folder's children if needed
                if (SelectedTreeViewItem != null)
                {
                    await LoadChildFolders(SelectedTreeViewItem);

                    Folders.Clear();
                    foreach (var child in SelectedTreeViewItem.Children)
                    {
                        Folders.Add(child);
                    }
                }
            });
        }

        #endregion

        #region Existing Methods (preserved for compatibility)

        private async void OnSelectedFolderChanged()
        {
            if (SelectedFolder != null)
            {
                await TagManagement.LoadFolderMetadataAsync(SelectedFolder);
                await ImageLoading.LoadImagesForFolderAsync(SelectedFolder);

                // Update command availability based on folder state
                if (IsCommandSystemEnabled)
                {
                    OnPropertyChanged(nameof(CanOperateOnSelectedFolder));
                }
            }
        }

        private async void OnTreeViewSelectionChanged()
        {
            if (SelectedTreeViewItem != null)
            {
                await LoadChildFolders(SelectedTreeViewItem);

                Folders.Clear();
                foreach (var child in SelectedTreeViewItem.Children)
                {
                    Folders.Add(child);
                }

                StatusMessage = $"Loaded {SelectedTreeViewItem.Children.Count} subfolders";
            }
        }

        private async Task LoadChildFolders(FolderInfo parentFolder)
        {
            if (parentFolder == null || !Directory.Exists(parentFolder.FolderPath))
                return;

            try
            {
                parentFolder.Children.Clear();

                var subdirectories = Directory.GetDirectories(parentFolder.FolderPath);

                foreach (var subdirectory in subdirectories)
                {
                    var childFolder = await _unifiedFolderService.CreateFolderInfoWithoutImagesAsync(subdirectory);
                    parentFolder.Children.Add(childFolder);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading child folders for {parentFolder.FolderPath}: {ex.Message}");
            }
        }

        private void CollectAllFolders(FolderInfo folder, List<FolderInfo> allFolders)
        {
            allFolders.Add(folder);
            foreach (var child in folder.Children)
            {
                CollectAllFolders(child, allFolders);
            }
        }

        private void OnSearchResultSelected(object sender, FolderInfo folder)
        {
            SelectedFolder = folder;
            StatusMessage = $"Selected folder from search: {folder.Name}";
        }

        // File system event handlers
        private void OnFolderCreated(string folderPath)
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                StatusMessage = $"Folder created: {Path.GetFileName(folderPath)}";
            });
        }

        private void OnFolderDeleted(string folderPath)
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                StatusMessage = $"Folder deleted: {Path.GetFileName(folderPath)}";
            });
        }

        private void OnFolderRenamed(string oldPath, string newPath)
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                StatusMessage = $"Folder renamed: {Path.GetFileName(oldPath)} → {Path.GetFileName(newPath)}";
            });
        }

        private void OnIndexRebuilt(List<string> indexedPaths)
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                StatusMessage = $"Index rebuilt: {indexedPaths.Count} folders indexed";
            });
        }

        private void CollapseParentDirectory()
        {
            // Implementation for collapsing parent directory
            StatusMessage = "Collapsed parent directory";
        }

        private bool CanCollapseParentDirectory()
        {
            return SelectedTreeViewItem != null;
        }

        private void SetImportantStatusMessage(string message)
        {
            lock (_statusMessageLock)
            {
                StatusMessage = message;
                _isImportantStatusMessageActive = true;

                _statusMessageTimer?.Dispose();
                _statusMessageTimer = new System.Threading.Timer(
                    _ =>
                    {
                        lock (_statusMessageLock)
                        {
                            if (_isImportantStatusMessageActive)
                            {
                                _isImportantStatusMessageActive = false;
                                Application.Current?.Dispatcher.BeginInvoke(() =>
                                {
                                    if (IsCommandSystemEnabled)
                                    {
                                        StatusMessage = CommandSystemStatus;
                                    }
                                    else
                                    {
                                        StatusMessage = "Ready";
                                    }
                                });
                            }
                        }
                    },
                    null,
                    TimeSpan.FromSeconds(3),
                    TimeSpan.FromMilliseconds(-1)
                );
            }
        }

        #endregion

        #region Disposal

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Unsubscribe from command system events
                if (_unifiedFolderService?.IsCommandSystemEnabled == true)
                {
                    _unifiedFolderService.CommandExecuted -= OnCommandExecuted;
                    _unifiedFolderService.FolderStateChanged -= OnFolderStateChanged;
                }

                // Dispose services
                _unifiedFolderService?.Dispose();
                _statusMessageTimer?.Dispose();

                // Dispose sub-ViewModels
                FolderOperations?.Dispose();
                Search?.Dispose();
                ImageLoading?.Dispose();
                TagManagement?.Dispose();
            }

            base.Dispose(disposing);
        }

        #endregion
    }
}