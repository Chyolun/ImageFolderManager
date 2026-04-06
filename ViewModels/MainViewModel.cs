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
using Application = System.Windows.Application;
using Microsoft.WindowsAPICodePack.Dialogs;
using static ImageFolderManager.Controls.ShellTreeView;
using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;

namespace ImageFolderManager.ViewModels
{
    /// <summary>
    /// Refactored MainViewModel that coordinates between separate focused ViewModels
    /// </summary>
    public partial class MainViewModel : ViewModelBase, IShellTreeHost
    {
        #region Sub-ViewModels

        public FolderOperationsViewModel FolderOperations { get; }
        public SearchViewModel Search { get; }
        public ImageLoadingViewModel ImageLoading { get; }
        public TagManagementViewModel TagManagement { get; }

        #endregion

        #region Properties

        private static int _instanceCounter = 0;
        private readonly int _instanceId;

        private System.Threading.Timer _statusMessageTimer;
        private bool _isImportantStatusMessageActive = false;
        private readonly object _statusMessageLock = new object();

        private readonly UnifiedFolderService _unifiedFolderService;
        private readonly IFolderOperationOrchestrator _operationOrchestrator;
        private readonly FolderTagService _tagService;
        private readonly List<FolderInfo> _allLoadedFolders;
        private readonly object _allLoadedFoldersLock = new object();
        private readonly IDialogService _dialogService;

        // Operation synchronization mechanism
        private readonly SemaphoreSlim _folderOperationSemaphore = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _initializationSemaphore = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _importOperationSemaphore = new SemaphoreSlim(1, 1);
        private readonly object _importCtsLock = new object();
        private CancellationTokenSource _currentImportCts;
        private readonly object _selectedFolderMetadataLock = new object();
        private CancellationTokenSource _selectedFolderMetadataCts;

        // State tracking
        private volatile bool _isTreeViewInitialized = false;
        private volatile bool _isMonitoringActive = false;

        private IShellTreeViewAdapter _shellTreeView;

        private FolderInfo _selectedFolder;

        private HierarchicalNodeManager _nodeManager;
        private FolderOperationCoordinator _coordinator;
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
            set
            {
                bool changed;
                lock (_statusMessageLock)
                {
                    // If an important message is active, ordinary messages are silently dropped
                    if (_isImportantStatusMessageActive)
                        return;

                    changed = _statusMessage != value;
                    _statusMessage = value;
                }
                // Fire PropertyChanged outside the lock to avoid holding the lock
                // while invoking potentially slow UI-thread callbacks
                if (changed)
                    OnPropertyChanged();
            }
        }

        public ObservableCollection<TagDisplayInfo> TagDisplayItems => TagManagement.TagDisplayItems;
        public ObservableCollection<FolderInfo> RootFolders { get; } = new ObservableCollection<FolderInfo>();

        // Expose properties from sub-ViewModels for backward compatibility
        public ObservableCollection<ImageInfo> Images => ImageLoading.Images;
        public ObservableCollection<string> FolderTags => TagManagement.FolderTags;
        public string DisplayTagLine => TagManagement.DisplayTagLine;
        public int Rating
        {
            get => TagManagement.Rating;
            set => TagManagement.Rating = value;
        }
        public ObservableCollection<StarModel> Stars => TagManagement.Stars;
        public string TagInputText
        {
            get => TagManagement.TagInputText;
            set
            {
                TagManagement.TagInputText = value;
                OnPropertyChanged();
            }
        }
        public string SearchText
        {
            get => Search.SearchText;
            set => Search.SearchText = value;
        }


        public ObservableCollection<FolderInfo> SearchResultFolders => Search.SearchResultFolders;
        public bool IsRealTimeIndexingActive => _unifiedFolderService?.IsIndexing == false &&
                                               !string.IsNullOrEmpty(_unifiedFolderService?.RootDirectory);
        public int IndexedFolderCount => _unifiedFolderService?.IndexedFolderCount ?? 0;

        // save last import folder directory
        private string _lastImportSourceDirectory;

        // Preview settings
        public int PreviewWidth => AppSettings.Instance.PreviewWidth;
        public int PreviewHeight => AppSettings.Instance.PreviewHeight;

        /// Checks if folder indexing is currently in progress
        public bool IsIndexing => _unifiedFolderService?.IsIndexing == true;

        /// Gets the current root directory path
        public string CurrentRootDirectory => _unifiedFolderService?.RootDirectory ?? string.Empty;

        public bool CanUndo => FolderOperations.CanUndo;
        public string UndoDescription => FolderOperations.UndoDescription;
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
        // Edit Commands
        private RelayCommand _cutCommand;
        private RelayCommand _copyCommand;
        private RelayCommand _pasteCommand;
        private RelayCommand _deleteCommand;
        public ICommand CutCommand => _cutCommand;
        public ICommand CopyCommand => _copyCommand;
        public ICommand PasteCommand => _pasteCommand;
        public ICommand DeleteCommand => _deleteCommand;
        public IAsyncRelayCommand UndoCommand => FolderOperations.UndoCommand;
        #endregion

        public MainViewModel(IDialogService dialogService = null)
        {
            _instanceId = ++_instanceCounter;
            _dialogService = dialogService ?? new WpfDialogService();
            AppSettings.DialogService = _dialogService;
            // Initialize shared category service
            var categoryService = new TagCategoryService();
            // Initialize services
            _nodeManager = new HierarchicalNodeManager();

            _tagService = new FolderTagService(categoryService);
            _allLoadedFolders = new List<FolderInfo>();
            _unifiedFolderService = new UnifiedFolderService(
                _tagService,
                _nodeManager,
                enableCommandSystem: true);
            _operationOrchestrator = new FolderOperationOrchestrator(_unifiedFolderService);
            // Initialize sub-ViewModels with enhanced TagCloudViewModel
            FolderOperations = new FolderOperationsViewModel(
                _unifiedFolderService,
                _operationOrchestrator,
                _dialogService);
            Search = new SearchViewModel(
                _unifiedFolderService,
                _allLoadedFolders,
                _allLoadedFoldersLock,
                categoryService);
            ImageLoading = new ImageLoadingViewModel(_unifiedFolderService);

            var tagCloud = new TagCloudViewModel(categoryService);
            _coordinator = new FolderOperationCoordinator(_unifiedFolderService, _tagService, tagCloud, _nodeManager);
            TagManagement = new TagManagementViewModel(_tagService, tagCloud, _coordinator, _dialogService);

            _cutCommand = new RelayCommand(ExecuteCutCommand, CanExecuteCutCommand);
            _copyCommand = new RelayCommand(ExecuteCopyCommand, CanExecuteCopyCommand);
            _pasteCommand = new RelayCommand(ExecutePasteCommand, CanExecutePasteCommand);
            _deleteCommand = new RelayCommand(ExecuteDeleteCommand, CanExecuteDeleteCommand);

            // Initialize commands
            SetRootDirectoryCommand = new AsyncRelayCommand(SetDefaultRootDirectoryAsync);
            CollapseParentDirectoryCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(
                CollapseParentDirectory,
                CanCollapseParentDirectory);

            // Subscribe to events from sub-ViewModels
            SubscribeToSubViewModelEvents();

            // Subscribe to unified service events
            SubscribeToServiceEvents();


            // Initialize coordinator in folder service
            _unifiedFolderService.InitializeCoordinator(_coordinator);
        }

        #region Event Subscriptions

        private void SubscribeToSubViewModelEvents()
        {
            // Forward status messages
            FolderOperations.StatusMessageChanged += (s, message) => SetImportantStatusMessage(message);
            FolderOperations.UndoManager.StateChanged += (_, __) =>
            {
                OnPropertyChanged(nameof(CanUndo));
                OnPropertyChanged(nameof(UndoDescription));
            };
            Search.StatusMessageChanged += (s, message) => StatusMessage = message;
            ImageLoading.StatusMessageChanged += (s, message) => StatusMessage = message;
            TagManagement.StatusMessageChanged += (s, message) => StatusMessage = message;

            FolderOperations.PropertyChanged += OnFolderOperationsPropertyChanged;

            // Handle search result selection
            Search.SearchResultSelected += (s, folder) =>
            {
                SelectedFolder = folder;
            };

            // Handle tag updates
            TagManagement.TagsUpdated += async (s, e) =>
            {
                await HandleTagsUpdated(e);
                // Notify that DisplayTagLine has changed
            };

            // Handle tag cloud request
            TagManagement.TagCloudRequested += (s, e) =>
            {
                ShowTagCloud();
            };

            // Enhanced property forwarding from TagManagement
            TagManagement.PropertyChanged += (s, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(TagManagement.DisplayTagLine):
                        OnPropertyChanged(nameof(DisplayTagLine));
                        break;
                    case nameof(TagManagement.FolderTags):
                        OnPropertyChanged(nameof(FolderTags));
                        OnPropertyChanged(nameof(DisplayTagLine)); // DisplayTagLine depends on FolderTags
                        break;
                    case nameof(TagManagement.TagInputText):
                        OnPropertyChanged(nameof(TagInputText));
                        break;
                    case nameof(TagManagement.Stars):
                        OnPropertyChanged(nameof(Stars));
                        break;
                    case nameof(TagManagement.Rating):
                        OnPropertyChanged(nameof(Rating));
                        break;
                    case nameof(TagManagement.TagDisplayItems):
                        OnPropertyChanged(nameof(TagDisplayItems));
                        break;
                }
            };

            // Property forwarding from Search
            Search.PropertyChanged += (s, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(Search.SearchText):
                        OnPropertyChanged(nameof(SearchText));
                        break;
                    case nameof(Search.SearchResultFolders):
                        OnPropertyChanged(nameof(SearchResultFolders));
                        break;
                }
            };

            // Property forwarding from ImageLoading
            ImageLoading.PropertyChanged += (s, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(ImageLoading.Images):
                        OnPropertyChanged(nameof(Images));
                        break;
                    case nameof(ImageLoading.IsLoadingImages):
                        OnPropertyChanged(nameof(ImageLoading.IsLoadingImages));
                        break;
                }
            };
        }

        private void SubscribeToServiceEvents()
        {
            _unifiedFolderService.FolderCreated += OnIndexedFolderCreated;
            _unifiedFolderService.FolderDeleted += OnIndexedFolderDeleted;
            _unifiedFolderService.FolderRenamed += OnIndexedFolderRenamed;
            _unifiedFolderService.IndexRebuilt += OnIndexRebuilt;
        }

        private void OnFolderOperationsPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(FolderOperationsViewModel.HasClipboardContent))
            {
                RefreshEditCommands();
            }
        }

        public void RefreshEditCommands()
        {
            _cutCommand?.NotifyCanExecuteChanged();
            _copyCommand?.NotifyCanExecuteChanged();
            _pasteCommand?.NotifyCanExecuteChanged();
            _deleteCommand?.NotifyCanExecuteChanged();
        }


        #endregion

        #region Public Methods

        public void SetSelectedFolderWithoutLoading(FolderInfo folder)
        {
            SelectedFolder = folder;

        }

        public void NotifyFolderSelected(FolderInfo folder, bool loadImages)
        {
            if (folder == null)
            {
                NotifySelectionCleared();
                return;
            }

            SetSelectedFolderWithoutLoading(folder);
            StatusMessage = $"Selected: {folder.Name} ({folder.FolderPath})";

            if (loadImages)
            {
                _ = LoadImagesForSelectedFolderAsync();
            }
        }

        public void NotifyMultiSelectionChanged(int selectedCount, string lastFolderName)
        {
            if (selectedCount <= 0)
            {
                NotifySelectionCleared();
                return;
            }

            SetSelectedFolderWithoutLoading(null);
            StatusMessage = string.IsNullOrWhiteSpace(lastFolderName)
                ? $"Selected {selectedCount} folders"
                : $"A total of {selectedCount} folders, including {lastFolderName}, are selected.";
        }

        public void NotifySelectionCleared()
        {
            SetSelectedFolderWithoutLoading(null);
            StatusMessage = "No folders selected";
        }

        public async Task SetSelectedFolderAsync(FolderInfo folder)
        {
            SetSelectedFolderWithoutLoading(folder);

            if (folder != null)
            {
                StatusMessage = $"Loading images from '{folder.Name}'...";
                await LoadImagesForSelectedFolderAsync();
            }
        }

        public async Task LoadImagesForSelectedFolderAsync()
        {
            if (SelectedFolder == null) return;
            await ImageLoading.LoadImagesAsync(SelectedFolder);
        }

        public void FolderExpanded(FolderInfo folder)
        {
            folder.LoadChildren();
            folder.IsExpanded = true;
        }

        public async Task SetPreviewSize(int width, int height, int maxCacheSize, int threadCount)
        {
            bool sizeChanged = PreviewWidth != width || PreviewHeight != height;

            AppSettings.Instance.PreviewWidth = width;
            AppSettings.Instance.PreviewHeight = height;
            AppSettings.Instance.MaxCacheSize = maxCacheSize;
            AppSettings.Instance.ParallelThreadCount = threadCount;

            if (AppSettings.Instance.ParallelThreadCount != threadCount)
            {
                ImageCache.UpdateParallelThreadCount(threadCount);
            }

            if (sizeChanged)
            {
                ImageCache.ClearCache();
            }

            if (SelectedFolder != null)
            {
                await LoadImagesForSelectedFolderAsync();
            }

            OnPropertyChanged(nameof(PreviewWidth));
            OnPropertyChanged(nameof(PreviewHeight));
        }

        #endregion


        #region Delegation Methods for Backward Compatibility

        public bool HasClipboardContent() => FolderOperations.HasClipboardContent;

        public void ShowInExplorer(FolderInfo folder) => System.Diagnostics.Process.Start("explorer.exe", folder.FolderPath);

        public async Task CreateNewFolder(FolderInfo parentFolder) => await FolderOperations.CreateNewFolderAsync(parentFolder);

        public async Task DeleteFolder(FolderInfo folder) => await FolderOperations.DeleteFolderAsync(folder);

        public async Task<bool> DeleteFolders(IEnumerable<FolderInfo> folders) => await FolderOperations.DeleteFoldersAsync(folders);

        public async Task<bool> MoveFolders(IEnumerable<FolderInfo> sources, FolderInfo target) => await FolderOperations.MoveFoldersAsync(sources, target);

        public async Task RenameFolder(FolderInfo folder) => await FolderOperations.RenameFolderAsync(folder);

        // New unified methods
        public void CutFolders(IEnumerable<FolderInfo> folders) => FolderOperations.CutFolders(folders);

        public void CopyFolders(IEnumerable<FolderInfo> folders) => FolderOperations.CopyFolders(folders);

        public async Task<bool> PasteFolders(FolderInfo targetFolder) => await FolderOperations.PasteFoldersAsync(targetFolder);

        // Get clipboard source directory
        public string GetClipboardSourceDirectory()
        {
            var clipboardFolders = FolderOperations.ClipboardFolders;
            if (clipboardFolders.Count == 0) return null;

            // For single folder, return the folder path
            if (clipboardFolders.Count == 1)
                return clipboardFolders[0].FolderPath;

            // For multiple folders, return their common parent directory if they have one
            var parentPaths = clipboardFolders
                .Select(f => Path.GetDirectoryName(f.FolderPath))
                .Distinct()
                .ToList();

            return parentPaths.Count == 1 ? parentPaths[0] : null;
        }

        public async Task BatchUpdateTags(List<FolderInfo> folders) => await TagManagement.BatchUpdateTagsAsync(folders);

        public async Task RenameTag(string oldTag, string newTag, List<string> folderPaths = null)
        {
            // If no folder paths provided, get all of them
            if (folderPaths == null || folderPaths.Count == 0)
            {
                folderPaths = GetAllIndexedFolderPaths();
            }

            await TagManagement.RenameTagAsync(oldTag, newTag);

            // Refresh tag cloud after renaming
            await UpdateTagCloudAsync();
        }



        #endregion




        #region  Edit Command implementation methods
        private bool CanExecuteCutCommand() => _shellTreeView?.HasSelectedItems() == true;
        private void ExecuteCutCommand()
        {
            var selectedFolders = GetSelectedFoldersFromTree();
            if (selectedFolders.Count > 0)
            {
                FolderOperations.CutFolders(selectedFolders);
                RefreshEditCommands();
            }
        }

        private bool CanExecuteCopyCommand() => _shellTreeView?.HasSelectedItems() == true;
        private void ExecuteCopyCommand()
        {
            var selectedFolders = GetSelectedFoldersFromTree();
            if (selectedFolders.Count > 0)
            {
                FolderOperations.CopyFolders(selectedFolders);
                RefreshEditCommands();
            }
        }

        private bool CanExecutePasteCommand() =>
            HasClipboardContent() && GetPrimarySelectedFolderFromTree() != null;
        private void ExecutePasteCommand()
        {
            var targetFolder = GetPrimarySelectedFolderFromTree();
            if (targetFolder != null)
            {
                _ = FolderOperations.PasteFoldersAsync(targetFolder);
            }
        }

        private bool CanExecuteDeleteCommand() => _shellTreeView?.HasSelectedItems() == true;
        private void ExecuteDeleteCommand()
        {
            var selectedFolders = GetSelectedFoldersFromTree();
            if (selectedFolders.Count > 0)
            {
                _ = FolderOperations.DeleteFoldersAsync(selectedFolders);
            }
        }

        // Method to set the ShellTreeView reference
        #endregion

        #region Private Methods

        private List<FolderInfo> GetSelectedFoldersFromTree()
        {
            if (_shellTreeView == null)
            {
                return new List<FolderInfo>();
            }

            return _shellTreeView.GetSelectedFolderInfos()
                .Where(f => f != null && Directory.Exists(f.FolderPath))
                .ToList();
        }

        private FolderInfo GetPrimarySelectedFolderFromTree()
        {
            return GetSelectedFoldersFromTree().FirstOrDefault();
        }

        /// <summary>
        /// Sets an important status message that will be displayed for at least the specified duration
        /// before allowing it to be overridden by less important messages.
        /// </summary>
        /// <param name="message">The message to display</param>
        /// <param name="durationMs">Duration in milliseconds to protect the message from being overridden</param>
        public void SetImportantStatusMessage(string message, int durationMs = 1000)
        {
            bool changed;
            lock (_statusMessageLock)
            {
                // 1) Set the flag FIRST, so any concurrent ordinary writes are blocked immediately
                _isImportantStatusMessageActive = true;

                // 2) Write the backing field directly - bypasses the setter to avoid nested lock acquisition
                changed = _statusMessage != message;
                _statusMessage = message;

                // 3) Reset the expiry timer
                _statusMessageTimer?.Dispose();
                _statusMessageTimer = new System.Threading.Timer(_ =>
                {
                    lock (_statusMessageLock)
                    {
                        _isImportantStatusMessageActive = false;
                    }
                }, null, durationMs, System.Threading.Timeout.Infinite);
            }
            // 4) Fire PropertyChanged outside the lock, consistent with the setter
            if (changed)
                OnPropertyChanged(nameof(StatusMessage));
        }

        private void OnSelectedFolderChanged()
        {
            if (SelectedFolder != null)
            {
                // Don't set the status message here, as it would override operation messages
                // The ShellTreeView already sets a status message when a folder is selected
                var selectedFolderSnapshot = SelectedFolder;
                var cancellationToken = ResetSelectedFolderMetadataToken();
                _ = LoadSelectedFolderMetadataAsync(selectedFolderSnapshot, cancellationToken);
            }
            else
            {
                CancelSelectedFolderMetadataLoad();
                ImageLoading.ClearImages();

                // Ensure we're on the UI thread for collection modifications
                Application.Current.Dispatcher.Invoke(() =>
                {
                    TagManagement.FolderTags.Clear();
                    TagManagement.TagDisplayItems.Clear(); // Also clear tag display items
                    OnPropertyChanged(nameof(DisplayTagLine));
                    OnPropertyChanged(nameof(TagDisplayItems));
                });
            }
        }

        private CancellationToken ResetSelectedFolderMetadataToken()
        {
            lock (_selectedFolderMetadataLock)
            {
                _selectedFolderMetadataCts?.Cancel();
                _selectedFolderMetadataCts?.Dispose();
                _selectedFolderMetadataCts = new CancellationTokenSource();
                return _selectedFolderMetadataCts.Token;
            }
        }

        private void CancelSelectedFolderMetadataLoad()
        {
            lock (_selectedFolderMetadataLock)
            {
                _selectedFolderMetadataCts?.Cancel();
                _selectedFolderMetadataCts?.Dispose();
                _selectedFolderMetadataCts = null;
            }
        }

        private async Task LoadSelectedFolderMetadataAsync(FolderInfo selectedFolder, CancellationToken cancellationToken)
        {
            try
            {
                await TagManagement.LoadFolderMetadataAsync(selectedFolder, cancellationToken);

                if (cancellationToken.IsCancellationRequested ||
                    SelectedFolder == null ||
                    !PathService.PathsEqual(SelectedFolder.FolderPath, selectedFolder.FolderPath))
                {
                    return;
                }

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    OnPropertyChanged(nameof(DisplayTagLine));
                    OnPropertyChanged(nameof(TagDisplayItems));
                }, DispatcherPriority.Background, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine($"Folder metadata load canceled for: {selectedFolder?.FolderPath}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load selected folder metadata: {ex.Message}");
            }
        }

        private bool CanCollapseParentDirectory()
        {
            return SelectedFolder != null &&
                   !string.IsNullOrEmpty(SelectedFolder.FolderPath) &&
                   !string.IsNullOrEmpty(Path.GetDirectoryName(SelectedFolder.FolderPath));
        }

        public List<string> GetAllIndexedFolderPaths()
        {
            // Return all indexed folder paths from the unified folder service
            return _unifiedFolderService?.IndexedFolders?.ToList() ??
                   GetAllLoadedFoldersSnapshot().Select(f => f.FolderPath).ToList();
        }

        private void CollapseParentDirectory()
        {
            if (SelectedFolder == null || string.IsNullOrEmpty(SelectedFolder.FolderPath))
            {
                StatusMessage = "No folder selected.";
                return;
            }

            string parentPath = Path.GetDirectoryName(SelectedFolder.FolderPath);
            if (string.IsNullOrEmpty(parentPath))
            {
                StatusMessage = "Selected folder has no parent directory.";
                return;
            }

            StatusMessage = $"Collapsing parent directory: {Path.GetFileName(parentPath)}";
        }

        private void ShowTagCloud()
        {
            // This would be handled by the View
            // The View would check for existing TagCloudWindow and show it
            StatusMessage = "Opening tag cloud...";
        }

        private async Task HandleTagsUpdated(TagsUpdatedEventArgs e)
        {
            // Update folder metadata in all loaded folders
            FolderInfo folder;
            lock (_allLoadedFoldersLock)
            {
                folder = _allLoadedFolders.FirstOrDefault(f =>
                    PathService.PathsEqual(f.FolderPath, e.Folder?.FolderPath));
            }

            if (folder != null)
            {
                // No need for null check since Tags is now guaranteed to be non-null
                folder.Tags = new ObservableCollection<string>(e.Tags);
                folder.Rating = e.Rating;
            }

            // After a tag update, refresh the tag display items
            if (e.Folder != null)
            {
                await TagManagement.LoadFolderMetadataAsync(e.Folder);
            }
            Search.InvalidateSearchIndex();
            await UpdateTagCloudAsync();
        }

        public async Task DeleteTagFromAllFoldersAsync(string tagToDelete, List<string> folderPaths)
        {
            if (string.IsNullOrEmpty(tagToDelete) || folderPaths == null || folderPaths.Count == 0)
                return;

            // Call the service to delete the tag from all folders
            await TagManagement.DeleteTagFromAllFoldersAsync(tagToDelete, folderPaths);

            // Refresh tag cloud
            await UpdateTagCloudAsync();

            // Refresh current folder tags if needed
            if (SelectedFolder != null)
            {
                await TagManagement.LoadFolderMetadataAsync(SelectedFolder);
            }
        }

        internal List<FolderInfo> GetAllLoadedFoldersSnapshot()
        {
            lock (_allLoadedFoldersLock)
            {
                return _allLoadedFolders.ToList();
            }
        }

        #endregion

        public void DebugCheckFolderOperationsState()
        {

            if (FolderOperations != null)
            {
                // Check if we can access the event
                try
                {
                    // This will test if the event subscription works
                    var eventInfo = FolderOperations.GetType().GetEvent("FolderOperationCompleted");
                }
                catch (Exception)
                {

                }
            }
        }
    }
}
