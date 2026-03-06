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
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;
using Microsoft.WindowsAPICodePack.Dialogs;
using static ImageFolderManager.Controls.ShellTreeView;
using System.Diagnostics;
using System.Threading;

namespace ImageFolderManager.ViewModels
{
    /// <summary>
    /// Refactored MainViewModel that coordinates between separate focused ViewModels
    /// </summary>
    public class MainViewModel : ViewModelBase
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
        private readonly FolderTagService _tagService;
        private readonly List<FolderInfo> _allLoadedFolders;

        // Operation synchronization mechanism
        private readonly SemaphoreSlim _folderOperationSemaphore = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _initializationSemaphore = new SemaphoreSlim(1, 1);

        // State tracking
        private volatile bool _isTreeViewInitialized = false;
        private volatile bool _isMonitoringActive = false;

        private ShellTreeView _shellTreeView;

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

        public MainViewModel()
        {
            _instanceId = ++_instanceCounter;
            // Initialize shared category service
            var categoryService = new TagCategoryService();
            // Initialize services
            _nodeManager = new HierarchicalNodeManager();
           
            _tagService = new FolderTagService(categoryService);
            _allLoadedFolders = new List<FolderInfo>();
            _unifiedFolderService = new UnifiedFolderService(_tagService, _nodeManager);
            // Initialize sub-ViewModels with enhanced TagCloudViewModel
            FolderOperations = new FolderOperationsViewModel(_unifiedFolderService);
            Search = new SearchViewModel(_unifiedFolderService, _allLoadedFolders);
            ImageLoading = new ImageLoadingViewModel(_unifiedFolderService);
 
            var tagCloud = new TagCloudViewModel(categoryService);
            _coordinator = new FolderOperationCoordinator(_unifiedFolderService, _tagService, tagCloud, _nodeManager);
            TagManagement = new TagManagementViewModel(_tagService, tagCloud, _coordinator);

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

        public async Task SetDefaultRootDirectoryAsync()
        {
            var dialog = new FolderBrowserDialog();
            if (!string.IsNullOrEmpty(AppSettings.Instance.DefaultRootDirectory))
            {
                dialog.SelectedPath = AppSettings.Instance.DefaultRootDirectory;
            }

            if (dialog.ShowDialog() == DialogResult.OK)
            {
                var path = dialog.SelectedPath;
                AppSettings.Instance.DefaultRootDirectory = path;

                if (PathService.DirectoryExists(path))
                {
                    await LoadDirectoryAsync(path);
                }
            }
        }

        public async Task LoadDirectoryAsync(string path)
        {
            await _initializationSemaphore.WaitAsync();

            try
            {
                StatusMessage = "Initializing directory monitoring...";

                // Step 1: Stop any existing monitoring first
                await StopMonitoringAsync();

                // Step 2: Initialize TreeView completely BEFORE starting monitoring
                if (_shellTreeView != null)
                {
                    StatusMessage = "Initializing TreeView...";
                    await InitializeTreeViewAsync(path);
                }

                // Step 3: Only start monitoring after TreeView is ready
                StatusMessage = "Starting real-time monitoring...";
                await StartMonitoringAsync(path);

                // Step 4: Final verification
                await VerifyInitializationAsync(path);

                StatusMessage = $"Directory loaded successfully. Monitoring {_unifiedFolderService.IndexedFolderCount} folders.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error loading directory: {ex.Message}";
                Debug.WriteLine($"LoadDirectoryAsync error: {ex}");

                // Cleanup on failure
                await CleanupOnFailureAsync();
                throw;
            }
            finally
            {
                _initializationSemaphore.Release();
            }
        }

        /// <summary>
        /// Initialize TreeView and wait for completion
        /// </summary>
        private async Task InitializeTreeViewAsync(string path)
        {
            _isTreeViewInitialized = false;

            try
            {
                if (_shellTreeView != null)
                {
                    // ShellTreeView.SetRootDirectory is thread-safe and awaits full initialization.
                    await _shellTreeView.SetRootDirectory(path);
                }

                // Wait for TreeView initialization to complete with timeout
                await WaitForTreeViewInitializationAsync(path, TimeSpan.FromSeconds(10));

                _isTreeViewInitialized = true;
                Debug.WriteLine("TreeView initialization completed successfully");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"TreeView initialization failed: {ex.Message}");
                throw new InvalidOperationException($"Failed to initialize TreeView: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Wait for TreeView initialization with proper timeout and validation
        /// </summary>
        private async Task WaitForTreeViewInitializationAsync(string rootPath, TimeSpan timeout)
        {
            var cancellationToken = new CancellationTokenSource(timeout).Token;
            var normalizedRootPath = PathService.NormalizePath(rootPath);

            try
            {
                // Poll for initialization completion
                while (!cancellationToken.IsCancellationRequested)
                {
                    bool isInitialized = false;

                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (_shellTreeView?._pathToTreeViewItem != null &&
                            _shellTreeView._pathToTreeViewItem.ContainsKey(normalizedRootPath))
                        {
                            isInitialized = true;
                            Debug.WriteLine($"TreeView initialization verified - root path mapped: {normalizedRootPath}");
                        }
                    });

                    if (isInitialized)
                    {
                        // Additional delay to ensure stability
                        await Task.Delay(200, cancellationToken);
                        return;
                    }

                    // Wait before next check
                    await Task.Delay(100, cancellationToken);
                }

                throw new TimeoutException("TreeView initialization timed out");
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("TreeView initialization timed out");
            }
        }

        /// <summary>
        /// Start monitoring only after TreeView is ready
        /// </summary>
        private async Task StartMonitoringAsync(string path)
        {
            if (!_isTreeViewInitialized)
            {
                throw new InvalidOperationException("Cannot start monitoring: TreeView not initialized");
            }

            try
            {
                // Start monitoring with initial index build
                await _unifiedFolderService.StartMonitoringAsync(path);
                _isMonitoringActive = true;

                Debug.WriteLine("File system monitoring started successfully");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to start monitoring: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Stop existing monitoring safely
        /// </summary>
        private async Task StopMonitoringAsync()
        {
            if (_isMonitoringActive)
            {
                try
                {
                    await _unifiedFolderService.StopMonitoringAsync();
                    _isMonitoringActive = false;
                    Debug.WriteLine("Previous monitoring stopped");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error stopping previous monitoring: {ex.Message}");
                    // Continue with initialization despite stop error
                }
            }
        }

        /// <summary>
        /// Verify that initialization completed successfully
        /// </summary>
        private async Task VerifyInitializationAsync(string path)
        {
            // Verify TreeView state
            bool treeViewValid = false;
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                treeViewValid = _shellTreeView?._pathToTreeViewItem?.ContainsKey(PathService.NormalizePath(path)) == true;
            });

            if (!treeViewValid)
            {
                throw new InvalidOperationException("TreeView initialization verification failed");
            }

            // Verify monitoring state
            if (!_isMonitoringActive || _unifiedFolderService.IndexedFolderCount == 0)
            {
                Debug.WriteLine("Warning: Monitoring verification shows unusual state");
            }

            Debug.WriteLine($"Initialization verification completed - TreeView: {treeViewValid}, Monitoring: {_isMonitoringActive}");
        }

        /// <summary>
        /// Cleanup resources on initialization failure
        /// </summary>
        private async Task CleanupOnFailureAsync()
        {
            try
            {
                _isTreeViewInitialized = false;
                _isMonitoringActive = false;

                await StopMonitoringAsync();

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    _shellTreeView?.ClearTreeView();
                });

                Debug.WriteLine("Cleanup completed after initialization failure");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during cleanup: {ex.Message}");
            }
        }

        public void SetSelectedFolderWithoutLoading(FolderInfo folder)
        {
            SelectedFolder = folder;

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

        /// <summary>
        /// Shows a modern dialog for selecting multiple folders for import using Windows API Code Pack
        /// </summary>
        /// <returns>List of selected folder paths, or null if cancelled</returns>
        private Task<List<string>> ShowMultiFolderSelectionDialogAsync()
        {
            return Task.FromResult(ShowMultiFolderSelectionDialog());
        }

        /// <summary>
        /// Shows a modern dialog for selecting multiple folders for import (synchronous version)
        /// Uses Windows API Code Pack's CommonOpenFileDialog for native multi-folder selection
        /// </summary>
        /// <returns>List of selected folder paths, or null if cancelled</returns>
        private List<string> ShowMultiFolderSelectionDialog()
        {
            try
            {
                // Check if Windows API Code Pack is available (Windows Vista and later)
                if (!CommonOpenFileDialog.IsPlatformSupported)
                {
                    // Fallback to the legacy method if API Code Pack is not supported
                    return ShowLegacyMultiFolderSelectionDialog();
                }

                using (var dialog = new CommonOpenFileDialog())
                {
                    // Configure dialog for folder selection
                    dialog.Title = "Select Folders to Import";
                    dialog.IsFolderPicker = true;
                    dialog.Multiselect = true;
                    dialog.AllowNonFileSystemItems = false;
                    dialog.EnsureFileExists = true;
                    dialog.EnsurePathExists = true;
                    dialog.EnsureReadOnly = false;
                    dialog.EnsureValidNames = true;
                    dialog.ShowPlacesList = true;

                    // Set initial directory if available
                    if (!string.IsNullOrEmpty(AppSettings.Instance.DefaultRootDirectory))
                    {
                        var parentDir = Directory.GetParent(AppSettings.Instance.DefaultRootDirectory)?.FullName;
                        if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
                        {
                            dialog.InitialDirectory = parentDir;
                        }
                    }

                    // Show the dialog
                    CommonFileDialogResult result = dialog.ShowDialog(Application.Current.MainWindow);

                    if (result == CommonFileDialogResult.Ok)
                    {
                        // Get all selected folder paths
                        var selectedFolders = dialog.FileNames.ToList();

                        // Validate selected folders
                        var validFolders = new List<string>();
                        var invalidFolders = new List<string>();

                        foreach (var folderPath in selectedFolders)
                        {
                            if (Directory.Exists(folderPath))
                            {
                                validFolders.Add(folderPath);
                            }
                            else
                            {
                                invalidFolders.Add(folderPath);
                            }
                        }

                        // Show warning for any invalid selections
                        if (invalidFolders.Count > 0)
                        {
                            MessageBox.Show(
                                $"The following selected items are not valid folders and will be ignored:\n\n" +
                                string.Join("\n", invalidFolders.Select(f => $"- {Path.GetFileName(f)}")),
                                "Invalid Selections",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);
                        }

                        if (validFolders.Count > 0)
                        {
                            // Show success message with selection summary
                            var folderNames = validFolders.Select(Path.GetFileName).ToList();
                            var summaryMessage = validFolders.Count == 1
                                ? $"Selected folder: {folderNames[0]}"
                                : $"Selected {validFolders.Count} folders:\n- " + string.Join("\n- ", folderNames.Take(5));

                            if (validFolders.Count > 5)
                            {
                                summaryMessage += $"\n... and {validFolders.Count - 5} more folders";
                            }

                            StatusMessage = $"Selected {validFolders.Count} folder(s) for import.";

                            return validFolders;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Log the error and fallback to legacy dialog
                System.Diagnostics.Debug.WriteLine($"Error using CommonOpenFileDialog: {ex.Message}");
                MessageBox.Show(
                    "Unable to use modern folder selection dialog. Falling back to legacy method.",
                    "Dialog Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                return ShowLegacyMultiFolderSelectionDialog();
            }

            return null; // User cancelled or no valid folders selected
        }

        /// <summary>
        /// Legacy fallback method for multi-folder selection using Windows Forms FolderBrowserDialog
        /// This method is kept as a fallback for older systems or when Windows API Code Pack is not available
        /// </summary>
        /// <returns>List of selected folder paths, or null if cancelled</returns>
        private List<string> ShowLegacyMultiFolderSelectionDialog()
        {
            var selectedFolders = new List<string>();

            // Use Windows Forms FolderBrowserDialog - must run on STA thread (UI thread)
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Select a folder to import (you can run this multiple times for multiple folders)";
                dialog.ShowNewFolderButton = false;

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    selectedFolders.Add(dialog.SelectedPath);

                    // Ask if user wants to select more folders
                    var result = MessageBox.Show(
                        $"Selected: {Path.GetFileName(dialog.SelectedPath)}\n\nDo you want to select additional folders for batch import?",
                        "Select More Folders?",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        // Allow selection of additional folders
                        while (true)
                        {
                            using (var additionalDialog = new System.Windows.Forms.FolderBrowserDialog())
                            {
                                additionalDialog.Description = $"Select additional folder to import ({selectedFolders.Count} already selected)";
                                additionalDialog.ShowNewFolderButton = false;

                                if (additionalDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                                {
                                    if (!selectedFolders.Contains(additionalDialog.SelectedPath, StringComparer.OrdinalIgnoreCase))
                                    {
                                        selectedFolders.Add(additionalDialog.SelectedPath);

                                        var continueResult = MessageBox.Show(
                                            $"Selected {selectedFolders.Count} folders total.\n\nSelect another folder?",
                                            "Select More Folders?",
                                            MessageBoxButton.YesNo,
                                            MessageBoxImage.Question);

                                        if (continueResult == MessageBoxResult.No)
                                            break;
                                    }
                                    else
                                    {
                                        MessageBox.Show("This folder has already been selected.",
                                            "Duplicate Selection", MessageBoxButton.OK, MessageBoxImage.Information);
                                    }
                                }
                                else
                                {
                                    break; // User cancelled additional selection
                                }
                            }
                        }
                    }
                }
            }

            return selectedFolders.Count > 0 ? selectedFolders : null;
        }

        /// <summary>
        /// Handles the import folder operation by showing the import dialog and processing the import
        /// </summary>
        public async Task ImportFolderAsync()
        {
            try
            {
                // Check if root directory is set
                if (string.IsNullOrEmpty(AppSettings.Instance.DefaultRootDirectory) ||
                    !Directory.Exists(AppSettings.Instance.DefaultRootDirectory))
                {
                    StatusMessage = "Please set a valid root directory first.";
                    MessageBox.Show("Please set a valid root directory in Settings before importing folders.",
                        "No Root Directory", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Use a more advanced folder selection dialog that supports multiple selection
                var sourceFolders = await ShowMultiFolderSelectionDialogAsync();

                if (sourceFolders == null || sourceFolders.Count == 0)
                {
                    StatusMessage = "Import cancelled - no folders selected.";
                    return;
                }

                // Validate selected folders and check for duplicates
                var validFolders = new List<string>();
                var skippedDueToLocation = new List<string>();
                var skippedDueToNameDuplication = new Dictionary<string, string>(); // folderName -> existingFullPath

                // Create a lookup dictionary for existing folder names to their full paths
                var existingFolderLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var folder in _allLoadedFolders)
                {
                    var folderName = Path.GetFileName(folder.FolderPath);
                    if (!existingFolderLookup.ContainsKey(folderName))
                    {
                        existingFolderLookup[folderName] = folder.FolderPath;
                    }
                }

                foreach (var folderPath in sourceFolders)
                {
                    if (!Directory.Exists(folderPath))
                        continue;

                    var folderName = Path.GetFileName(folderPath);

                    // Check if folder is not already within the root directory
                    if (PathService.IsPathWithin(AppSettings.Instance.DefaultRootDirectory, folderPath))
                    {
                        skippedDueToLocation.Add(folderName);
                        continue;
                    }

                    // Check for duplicate folder names in the existing collection
                    if (existingFolderLookup.ContainsKey(folderName))
                    {
                        skippedDueToNameDuplication[folderName] = existingFolderLookup[folderName];
                        continue;
                    }

                    validFolders.Add(folderPath);
                }

                // Create comprehensive skip message if any folders were skipped
                var skipMessages = new List<string>();

                if (skippedDueToLocation.Count > 0)
                {
                    skipMessages.Add($"Already in root directory: {string.Join(", ", skippedDueToLocation)}");
                }

                if (skippedDueToNameDuplication.Count > 0)
                {
                    var duplicateNames = string.Join(", ", skippedDueToNameDuplication.Keys);
                    skipMessages.Add($"Duplicate folder names: {duplicateNames}");
                }

                // Check if any valid folders remain
                if (validFolders.Count == 0)
                {
                    var noValidMessage = "No valid folders selected for import.";

                    if (skippedDueToLocation.Count > 0 && skippedDueToNameDuplication.Count > 0)
                    {
                        noValidMessage += " All folders were skipped due to being already in root directory or having duplicate names.";
                    }
                    else if (skippedDueToLocation.Count > 0)
                    {
                        noValidMessage += " All folders were skipped because they are already within the current root directory.";
                    }
                    else if (skippedDueToNameDuplication.Count > 0)
                    {
                        noValidMessage += " All folders were skipped because folders with the same names already exist.";
                    }
                    else
                    {
                        noValidMessage += " Folders must be outside the current root directory and have unique names.";
                    }

                    MessageBox.Show(noValidMessage,
                        "No Valid Folders", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Prepare skip information for the dialog with detailed path information
                var skipInfo = new Dictionary<string, string>();
                foreach (var folder in skippedDueToLocation)
                {
                    skipInfo[folder] = "Already in root directory";
                }
                foreach (var kvp in skippedDueToNameDuplication)
                {
                    skipInfo[kvp.Key] = $"Duplicate name - existing folder: {kvp.Value}";
                }

                // Show import dialog with valid folders only and skip information
                var importDialog = new ImportFolderDialog(
                    validFolders,
                    AppSettings.Instance.DefaultRootDirectory,
                    _allLoadedFolders.ToList(),
                    _tagService,
                    skipInfo.Count > 0 ? skipInfo : null)
                {
                    Owner = Application.Current.MainWindow
                };

                if (importDialog.ShowDialog() == true && importDialog.DialogConfirmed)
                {
                    // Update status to show final import count
                    var totalSkipped = skippedDueToLocation.Count + skippedDueToNameDuplication.Count;
                    if (totalSkipped > 0)
                    {
                        StatusMessage = $"Importing {validFolders.Count} folder(s). {totalSkipped} folder(s) were skipped.";
                    }
                    else
                    {
                        StatusMessage = $"Importing {validFolders.Count} folder(s)...";
                    }

                    await ProcessFolderImportAsync(validFolders, importDialog.DestinationPath);
                }
                else
                {
                    StatusMessage = "Import cancelled by user.";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error during import: {ex.Message}";
                MessageBox.Show($"An error occurred during folder import:\n\n{ex.Message}",
                    "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }



        /// <summary>
        /// Processes the actual folder import operation
        /// </summary>
        /// <param name="sourceFolders">List of source folder paths to import</param>
        /// <param name="destinationPath">Destination path for the import</param>
        private async Task ProcessFolderImportAsync(List<string> sourceFolders, string destinationPath)
        {
            try
            {
                StatusMessage = $"Importing {sourceFolders.Count} folder(s)...";

                // Create progress dialog for the import operation
                var progressDialog = new ProgressDialog(
                    "Importing Folders",
                    $"Importing {sourceFolders.Count} folder(s)...")
                {
                    Owner = Application.Current.MainWindow
                };

                var importResults = new List<FolderImportResult>();

                // Perform the import operation
                var importTask = Task.Run(async () =>
                {
                    int processed = 0;
                    int total = sourceFolders.Count;

                    foreach (var sourceFolderPath in sourceFolders)
                    {
                        var result = new FolderImportResult
                        {
                            SourcePath = sourceFolderPath,
                            FolderName = Path.GetFileName(sourceFolderPath)
                        };

                        try
                        {
                            // Update progress
                            var progressStart = (double)processed / total;
                            progressDialog.UpdateProgress(progressStart, $"Starting import: {result.FolderName}");

                            // Determine final destination path
                            string finalDestinationPath;
                            if (sourceFolders.Count == 1)
                            {
                                // For single folder import, use the exact destination path from dialog
                                finalDestinationPath = destinationPath;
                            }
                            else
                            {
                                // For multiple folder import, create subfolder in destination
                                finalDestinationPath = Path.Combine(destinationPath, result.FolderName);
                            }

                            // Ensure destination doesn't already exist, create unique name if needed
                            if (Directory.Exists(finalDestinationPath))
                            {
                                finalDestinationPath = PathService.GetUniqueDirectoryPath(
                                    Path.GetDirectoryName(finalDestinationPath),
                                    Path.GetFileName(finalDestinationPath));
                            }

                            // Calculate progress parameters for this folder
                            double baseProgress = (double)processed / total;
                            double progressWeight = 1.0 / total;

                            // Perform the copy operation
                            await CopyDirectoryAsync(sourceFolderPath, finalDestinationPath, progressDialog, baseProgress, progressWeight);

                            result.DestinationPath = finalDestinationPath;
                            result.Success = true;

                            // Verify the copy was successful
                            if (Directory.Exists(finalDestinationPath))
                            {
                                result.Message = "Import completed successfully";

                                // Update progress to show completion of this folder
                                var progressEnd = (double)(processed + 1) / total;
                                progressDialog.UpdateProgress(progressEnd, $"Completed: {result.FolderName}");

                                // Delete the source folder after successful copy (move semantics)
                                try
                                {
                                    Directory.Delete(sourceFolderPath, recursive: true);
                                }
                                catch (Exception deleteEx)
                                {
                                    // Non-fatal: log the failure but keep the import result as success
                                    System.Diagnostics.Debug.WriteLine(
                                        $"[Import] Failed to delete source '{sourceFolderPath}': {deleteEx.Message}");
                                    result.Message = "Imported successfully (source folder could not be removed automatically).";
                                }
                            }
                            else
                            {
                                result.Success = false;
                                result.Message = "Import failed - destination folder not created";
                            }
                        }
                        catch (Exception ex)
                        {
                            result.Success = false;
                            result.Message = $"Import failed: {ex.Message}";
                        }

                        importResults.Add(result);
                        processed++;
                    }

                    return importResults;
                });

                // Show progress dialog and wait for completion
                progressDialog.ShowDialog();

                var results = await importTask;

                // Update progress to complete
                progressDialog.UpdateProgress(1.0, "Import operation completed");
                await Task.Delay(500); // Brief pause to show completion

                // Close progress dialog
                progressDialog.Close();

                // Process results and update UI
                await ProcessImportResultsAsync(results);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Import failed: {ex.Message}";
                throw;
            }
        }

        /// <summary>
        /// Copies a directory and all its contents asynchronously
        /// </summary>
        /// <param name="sourcePath">Source directory path</param>
        /// <param name="destinationPath">Destination directory path</param>
        /// <param name="progressDialog">Progress dialog for status updates</param>
        /// <param name="baseProgress">Base progress value for this operation</param>
        /// <param name="progressWeight">Weight of this operation in overall progress</param>
        private async Task CopyDirectoryAsync(string sourcePath, string destinationPath, ProgressDialog progressDialog = null, double baseProgress = 0.0, double progressWeight = 1.0)
        {
            await Task.Run(() =>
            {
                // Create destination directory
                Directory.CreateDirectory(destinationPath);

                try
                {
                    // Count total files for more accurate progress
                    var allFiles = Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories);
                    var totalFiles = allFiles.Length;
                    int processedFiles = 0;

                    // Copy files recursively with progress tracking
                    CopyDirectoryWithProgress(sourcePath, destinationPath, progressDialog, baseProgress, progressWeight, ref processedFiles, totalFiles);
                }
                catch (Exception)
                {
                    // If counting fails, fall back to simple copy
                    progressDialog?.UpdateProgress(baseProgress + (progressWeight * 0.5), $"Copying contents of {Path.GetFileName(sourcePath)}...");
                    CopyDirectorySync(sourcePath, destinationPath);
                    progressDialog?.UpdateProgress(baseProgress + progressWeight, $"Completed copying {Path.GetFileName(sourcePath)}");
                }
            });
        }

        /// <summary>
        /// Helper method to copy directory with progress tracking
        /// </summary>
        private void CopyDirectoryWithProgress(string sourcePath, string destinationPath, ProgressDialog progressDialog, double baseProgress, double progressWeight, ref int processedFiles, int totalFiles)
        {
            // Ensure destination directory exists
            Directory.CreateDirectory(destinationPath);

            // Copy all files in current directory
            var files = Directory.GetFiles(sourcePath);
            foreach (var file in files)
            {
                string fileName = Path.GetFileName(file);
                string destFile = Path.Combine(destinationPath, fileName);
                File.Copy(file, destFile, true);

                processedFiles++;
                if (progressDialog != null && totalFiles > 0)
                {
                    double currentProgress = baseProgress + (progressWeight * processedFiles / totalFiles);
                    progressDialog.UpdateProgress(Math.Min(currentProgress, baseProgress + progressWeight), $"Copying: {fileName}");
                }
            }

            // Recursively copy subdirectories
            var subdirectories = Directory.GetDirectories(sourcePath);
            foreach (var subdirectory in subdirectories)
            {
                string dirName = Path.GetFileName(subdirectory);
                string destDir = Path.Combine(destinationPath, dirName);

                if (progressDialog != null)
                {
                    double currentProgress = baseProgress + (progressWeight * processedFiles / totalFiles);
                    progressDialog.UpdateProgress(Math.Min(currentProgress, baseProgress + progressWeight), $"Copying folder: {dirName}");
                }

                // Recursive call
                CopyDirectoryWithProgress(subdirectory, destDir, progressDialog, baseProgress, progressWeight, ref processedFiles, totalFiles);
            }
        }

        /// <summary>
        /// Synchronous directory copy helper method
        /// </summary>
        /// <param name="sourcePath">Source directory path</param>
        /// <param name="destinationPath">Destination directory path</param>
        private void CopyDirectorySync(string sourcePath, string destinationPath)
        {
            Directory.CreateDirectory(destinationPath);

            // Copy files
            foreach (var file in Directory.GetFiles(sourcePath))
            {
                string fileName = Path.GetFileName(file);
                string destFile = Path.Combine(destinationPath, fileName);
                File.Copy(file, destFile, true);
            }

            // Copy subdirectories
            foreach (var subdirectory in Directory.GetDirectories(sourcePath))
            {
                string dirName = Path.GetFileName(subdirectory);
                string destDir = Path.Combine(destinationPath, dirName);
                CopyDirectorySync(subdirectory, destDir);
            }
        }

        /// <summary>
        /// Processes import results and updates the application state
        /// </summary>
        /// <param name="results">List of import results</param>
        private async Task ProcessImportResultsAsync(List<FolderImportResult> results)
        {
            var successCount = results.Count(r => r.Success);
            var failureCount = results.Count(r => !r.Success);

            // Update status message
            if (failureCount == 0)
            {
                StatusMessage = $"Successfully imported {successCount} folder(s).";
            }
            else
            {
                StatusMessage = $"Import completed: {successCount} successful, {failureCount} failed.";
            }

            // Show detailed results if there were any failures
            if (failureCount > 0)
            {
                var failedFolders = results.Where(r => !r.Success).ToList();
                var errorMessage = "The following folders failed to import:\n\n";
                errorMessage += string.Join("\n", failedFolders.Select(f => $"- {f.FolderName}: {f.Message}"));

                MessageBox.Show(errorMessage, "Import Errors", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            // Refresh the folder tree to show imported folders
            await RefreshAfterImportAsync(results.Where(r => r.Success).Select(r => r.DestinationPath).ToList());
        }

        /// <summary>
        /// Refreshes the application after successful import
        /// </summary>
        /// <param name="importedPaths">List of successfully imported folder paths</param>
        private async Task RefreshAfterImportAsync(List<string> importedPaths)
        {
            try
            {
                // Refresh all folders data to include the newly imported folders
                await RefreshAllFoldersDataAsync();

                // If we have a shell tree view, refresh it
                if (_shellTreeView != null)
                {
                    await _shellTreeView.RefreshTreeFull();
                }

                // Select the first imported folder if available
                if (importedPaths.Count > 0 && _allLoadedFolders.Count > 0)
                {
                    var importedFolder = _allLoadedFolders.FirstOrDefault(f =>
                        importedPaths.Any(path => PathService.PathsEqual(f.FolderPath, path)));

                    if (importedFolder != null)
                    {
                        await SetSelectedFolderAsync(importedFolder);

                        // Select in tree view if available
                        if (_shellTreeView != null)
                        {
                            _shellTreeView.SelectPath(importedFolder.FolderPath);
                        }
                    }
                }

                StatusMessage += " Folder tree refreshed.";
            }
            catch (Exception ex)
            {
                StatusMessage += $" Warning: Failed to refresh folder tree - {ex.Message}";
            }
        }



        public async Task UpdateTagCloudAsync()
        {
            //var freshFolders = await _unifiedFolderService.LoadFoldersRecursivelyAsync(
            //    AppSettings.Instance.DefaultRootDirectory);

            //_allLoadedFolders.Clear();
            //_allLoadedFolders.AddRange(freshFolders);

            await TagManagement.UpdateTagCloudAsync(_allLoadedFolders);
        }

        public async Task RefreshAllFoldersDataAsync()
        {
            if (string.IsNullOrEmpty(AppSettings.Instance.DefaultRootDirectory) ||
                !Directory.Exists(AppSettings.Instance.DefaultRootDirectory))
            {
                return;
            }

            try
            {
                StatusMessage = "Refreshing folder data...";

                _allLoadedFolders.Clear();
                var folders = await _unifiedFolderService.LoadFoldersRecursivelyAsync(
                    AppSettings.Instance.DefaultRootDirectory);
                _allLoadedFolders.AddRange(folders);

                await UpdateTagCloudAsync();
                StatusMessage = "Ready";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error refreshing folder data: {ex.Message}";
                MessageBox.Show($"Error refreshing folder data: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public async Task CleanupAsync()
        {
            try
            {
                if (_unifiedFolderService != null)
                {
                    await _unifiedFolderService.StopMonitoringAsync();
                    _unifiedFolderService.Dispose();
                }

                ImageLoading?.CancelCurrentLoading();
                _nodeManager?.Dispose();
                _coordinator?.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error during cleanup: {ex.Message}");
            }
        }


        public void Cleanup()
        {
            try
            {
                _unifiedFolderService?.StopMonitoringAsync().Wait(2000);
                _unifiedFolderService?.Dispose();
                ImageLoading?.CancelCurrentLoading();
                _nodeManager?.Dispose();
                _coordinator?.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error during cleanup: {ex.Message}");
            }
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
        private bool CanExecuteCutCommand() => _shellTreeView?.HasSelectedItems() ?? false;
        private void ExecuteCutCommand()
        {
            if (_shellTreeView != null)
            {
                _shellTreeView.MultiFolderCut_Click(this, new RoutedEventArgs());
            }
        }

        private bool CanExecuteCopyCommand() => _shellTreeView?.HasSelectedItems() ?? false;
        private void ExecuteCopyCommand()
        {
            if (_shellTreeView != null)
            {
                _shellTreeView.MultiFolderCopy_Click(this, new RoutedEventArgs());
            }
        }

        private bool CanExecutePasteCommand() => HasClipboardContent();
        private void ExecutePasteCommand()
        {
            if(_shellTreeView != null)
            {
                _shellTreeView.Paste_Click(this, new RoutedEventArgs());
            }
        }

        private bool CanExecuteDeleteCommand() => _shellTreeView?.HasSelectedItems() ?? false;
        private void ExecuteDeleteCommand()
        {
            if (_shellTreeView != null)
            {
                _shellTreeView.MultiFolderDelete_Click(this, new RoutedEventArgs());
            }
        }

        // Method to set the ShellTreeView reference
        #endregion

        #region Private Methods

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

        /// <summary>
        /// Gets all currently loaded folders for duplicate search functionality
        /// </summary>
        /// <returns>List of all loaded FolderInfo objects</returns>
        public List<FolderInfo> GetAllLoadedFolders()
        {
            try
            {
                // Return a copy of the list to prevent external modification
                return _allLoadedFolders?.ToList() ?? new List<FolderInfo>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting all loaded folders: {ex.Message}");
                return new List<FolderInfo>();
            }
        }


        /// <summary>
        /// Gets the count of loaded folders
        /// </summary>
        /// <returns>Number of currently loaded folders</returns>
        public int GetLoadedFolderCount()
        {
            return _allLoadedFolders?.Count ?? 0;
        }

        /// <summary>
        /// Finds duplicate folder names within the current root directory with optional filtering
        /// </summary>
        /// <returns>Dictionary where key is folder name and value is list of folders with that name</returns>
        public Dictionary<string, List<FolderInfo>> FindDuplicateFolders()
        {
            try
            {
                var duplicates = new Dictionary<string, List<FolderInfo>>(StringComparer.OrdinalIgnoreCase);
                var allFolders = GetAllLoadedFolders();

                if (!allFolders.Any())
                {
                    return duplicates;
                }

                // Apply filters if enabled
                var filteredFolders = ApplyDuplicateFilters(allFolders);

                // Group folders by their name (case-insensitive)
                var folderGroups = filteredFolders
                    .Where(f => !string.IsNullOrEmpty(f.FolderPath))
                    .GroupBy(f => Path.GetFileName(f.FolderPath), StringComparer.OrdinalIgnoreCase);

                // Only keep groups with more than one folder (duplicates)
                foreach (var group in folderGroups.Where(g => g.Count() > 1))
                {
                    duplicates[group.Key] = group.ToList();
                }

                return duplicates;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error finding duplicate folders: {ex.Message}");
                return new Dictionary<string, List<FolderInfo>>();
            }
        }

        /// <summary>
        /// Applies duplicate detection filters to the folder collection
        /// </summary>
        /// <param name="folders">Collection of folders to filter</param>
        /// <returns>Filtered collection of folders</returns>
        private IEnumerable<FolderInfo> ApplyDuplicateFilters(IEnumerable<FolderInfo> folders)
        {
            if (!AppSettings.Instance.EnableDuplicateFilters)
            {
                return folders;
            }

            var filteredFolders = folders.Where(folder =>
            {
                if (string.IsNullOrEmpty(folder.FolderPath))
                    return false;

                var folderName = Path.GetFileName(folder.FolderPath);

                if (string.IsNullOrEmpty(folderName))
                    return false;

                // Apply minimum length filter
                if (folderName.Length < AppSettings.Instance.MinFolderNameLength)
                {
                    System.Diagnostics.Debug.WriteLine($"Filtered out folder '{folderName}' - below minimum length ({AppSettings.Instance.MinFolderNameLength})");
                    return false;
                }

                // Apply exclusion list filter
                if (AppSettings.Instance.IsFolderNameExcluded(folderName))
                {
                    System.Diagnostics.Debug.WriteLine($"Filtered out folder '{folderName}' - in exclusion list");
                    return false;
                }

                return true;
            });

            return filteredFolders;
        }

        /// <summary>
        /// Gets duplicate folder statistics with filter information
        /// </summary>
        /// <returns>Tuple containing (total folders, filtered folders, duplicate groups count, total duplicate folders)</returns>
        public (int totalFolders, int filteredFolders, int duplicateGroups, int duplicateFolders) GetDuplicateStatsWithFilters()
        {
            try
            {
                var allFolders = GetAllLoadedFolders();
                var filteredFolders = ApplyDuplicateFilters(allFolders).ToList();
                var duplicates = FindDuplicateFolders();

                int totalFolders = allFolders.Count;
                int filteredCount = filteredFolders.Count;
                int duplicateGroups = duplicates.Count;
                int duplicateFolders = duplicates.Values.Sum(list => list.Count);

                return (totalFolders, filteredCount, duplicateGroups, duplicateFolders);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting duplicate stats with filters: {ex.Message}");
                return (0, 0, 0, 0);
            }
        }

        /// <summary>
        /// Gets duplicate folder statistics (legacy method for backward compatibility)
        /// </summary>
        /// <returns>Tuple containing (total folders, duplicate groups count, total duplicate folders)</returns>
        public (int totalFolders, int duplicateGroups, int duplicateFolders) GetDuplicateStats()
        {
            var stats = GetDuplicateStatsWithFilters();
            return (stats.totalFolders, stats.duplicateGroups, stats.duplicateFolders);
        }

       


        private void OnSelectedFolderChanged()
        {
            if (SelectedFolder != null)
            {
                // Don't set the status message here, as it would override operation messages
                // The ShellTreeView already sets a status message when a folder is selected

                Task.Run(async () =>
                {
                    await TagManagement.LoadFolderMetadataAsync(SelectedFolder);
                    // Ensure UI properties are updated on the UI thread
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        OnPropertyChanged(nameof(DisplayTagLine));
                        OnPropertyChanged(nameof(TagDisplayItems));
                    });
                });
            }
            else
            {
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
                   _allLoadedFolders.Select(f => f.FolderPath).ToList();
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
            var folder = _allLoadedFolders.FirstOrDefault(f =>
                PathService.PathsEqual(f.FolderPath, e.Folder?.FolderPath));

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

        #endregion

        #region Service Event HandlersIn ExecuteFolderOperationOnUIThread

        private async void OnIndexedFolderCreated(string folderPath)
        {
            var newFolder = await _unifiedFolderService.CreateFolderInfoWithoutImagesAsync(folderPath);
            if (newFolder != null)
            {
                _allLoadedFolders.Add(newFolder);
                await Search.PerformSilentSearchAsync();
                await UpdateTagCloudAsync();
            }
        }

        private async void OnIndexedFolderDeleted(string folderPath)
        {
            _allLoadedFolders.RemoveAll(f =>
                PathService.PathsEqual(f.FolderPath, folderPath) ||
                PathService.IsPathWithin(folderPath, f.FolderPath));

            await Search.PerformSilentSearchAsync();
            await UpdateTagCloudAsync();
        }

        private async void OnIndexedFolderRenamed(string oldPath, string newPath)
        {
            // Collect items to modify first
            var itemsToUpdate = new List<(FolderInfo folder, string newPath)>();

            for (int i = 0; i < _allLoadedFolders.Count; i++)
            {
                var folder = _allLoadedFolders[i];
                if (folder.FolderPath == oldPath)
                {
                    itemsToUpdate.Add((folder, newPath));
                }
                else if (PathService.IsPathWithin(oldPath, folder.FolderPath))
                {
                    itemsToUpdate.Add((folder, newPath + folder.FolderPath.Substring(oldPath.Length)));
                }
            }

            // Apply updates
            foreach (var (folder, updatedPath) in itemsToUpdate)
            {
                folder.FolderPath = updatedPath;
            }

            await Search.PerformSilentSearchAsync();
            await UpdateTagCloudAsync();
        }

        private async void OnIndexRebuilt(List<string> allFolders)
        {
            StatusMessage = "Rebuilding folder cache from index...";

            _allLoadedFolders.Clear();
            foreach (var folderPath in allFolders)
            {
                try
                {
                    var folder = await _unifiedFolderService.CreateFolderInfoWithoutImagesAsync(folderPath);
                    if (folder != null)
                    {
                        _allLoadedFolders.Add(folder);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error creating FolderInfo for {folderPath}: {ex.Message}");
                }
            }

            await Search.PerformSilentSearchAsync();
            await UpdateTagCloudAsync();

            StatusMessage = $"Index rebuilt. {allFolders.Count} folders loaded.";
        }

        #endregion


        #region ShellTreeView Integration and Refresh Commands

        /// <summary>
        /// Sets the ShellTreeView reference for direct tree operations
        /// </summary>
        /// <param name="shellTreeView">The ShellTreeView control instance</param>
        public void SetShellTreeView(ShellTreeView shellTreeView)
        {
            _shellTreeView = shellTreeView;

            // Subscribe to folder operation events for incremental refresh
            if (FolderOperations != null)
            {
                 // Unsubscribe any existing handlers first
                FolderOperations.FolderOperationCompleted -= OnFolderOperationCompleted;        
                FolderOperations.FolderOperationCompleted += OnFolderOperationCompleted;
             }
            else
            {
                Debug.WriteLine($"Instance #{_instanceId}: ERROR: FolderOperations is NULL - cannot subscribe to events");
            }
        }

        /// <summary>
        /// Manual refresh command - uses full tree rebuild
        /// </summary>
        public ICommand RefreshTreeCommand => _refreshTreeCommand ??= new AsyncRelayCommand(RefreshTreeManualAsync);
        private IAsyncRelayCommand _refreshTreeCommand;

        /// <summary>
        /// Performs a manual full refresh of the tree
        /// </summary>
        private async Task RefreshTreeManualAsync()
        {
            try
            {
                if (_shellTreeView != null)
                {
                    // Use full refresh for manual operations
                    await _shellTreeView.RefreshTreeFull();
                    StatusMessage = "Tree refreshed successfully.";
                }
                else
                {
                    StatusMessage = "Cannot refresh: Tree view not available.";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error refreshing tree: {ex.Message}";
                Debug.WriteLine($"RefreshTreeManualAsync error: {ex}");
            }
        }

        /// <summary>
        /// Handles folder operation completed events and delegates to appropriate refresh method
        /// </summary>
        private async void OnFolderOperationCompleted(object sender, FolderOperationEventArgs e)
        {
            try
            {
                await HandleFolderOperationCompletedAsync(e);
                Debug.WriteLine($"Instance #{_instanceId}: HandleFolderOperationCompletedAsync completed successfully");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Instance #{_instanceId}: ERROR in OnFolderOperationCompleted: {ex.Message}");
                StatusMessage = "An error occurred during the folder operation.";
            }
        }

        //method to get instance info
        public string GetInstanceInfo()
        {
            return $"MainViewModel Instance #{_instanceId}, FolderOps hash: {FolderOperations?.GetHashCode()}";
        }

        /// <summary>
        /// Core processing logic - uses correct async pattern
        /// </summary>
        private async Task HandleFolderOperationCompletedAsync(FolderOperationEventArgs e)
        {
            // Prevent race conditions from concurrent operations
            await _folderOperationSemaphore.WaitAsync();

            try
            {
                // Check if already on UI thread
                if (Application.Current.Dispatcher.CheckAccess())
                {
                    // Already on UI thread, execute directly
                    await ExecuteFolderOperationOnUIThread(e);
                }
                else
                {
                    // Marshal to UI thread without nested async
                    await Application.Current.Dispatcher.InvokeAsync(async () =>
                    {
                        await ExecuteFolderOperationOnUIThread(e);
                    });
                }
           
            }
            catch (Exception)
            {
                throw;
            }
            finally
            {
                _folderOperationSemaphore.Release();
            }
        }

        /// <summary>
        /// Execute folder operation logic on UI thread (must be called from UI thread)
        /// </summary>
        private async Task ExecuteFolderOperationOnUIThread(FolderOperationEventArgs e)
        {

            try
            {
                if (e.Success && _shellTreeView != null)
                {
                    
                    // Validate TreeView state before operation
                    if (!ValidateTreeViewState("folder operation"))
                    {
                        StatusMessage = "TreeView not ready, performing full refresh...";
                        await _shellTreeView.RefreshTreeFull();
                        return;
                    }

                    // Map operation type
                    FolderOperationType operationType = MapToFolderOperationType(e.Operation);
                    if (e.IsUndoOperation &&
                        (e.Operation == FolderOperation.Move || e.Operation == FolderOperation.Refresh))
                    {
                        operationType = FolderOperationType.UndoMove;
                    }

                    if (e.Operation == FolderOperation.Refresh)
                    {
                        await _shellTreeView.RefreshTreeFull();
                        StatusMessage = e.IsUndoOperation
                            ? "Undo completed successfully."
                            : "Refresh completed.";
                        return;
                    }

                    // SPECIAL HANDLING FOR MOVE OPERATIONS
                    if (operationType == FolderOperationType.Move)
                    {
                        // Check if source still exists (it shouldn't after a successful move)
                        bool sourceStillExists = PathService.DirectoryExists(e.SourcePath);

                        if (sourceStillExists)
                        {
                            Debug.WriteLine($"Instance #{_instanceId}: WARNING - Source still exists, this might be a duplicate event");
                        }
                    }

                    string refreshSourcePath = e.SourcePath;
                    string refreshDestPath = e.DestinationPath;

                    if (e.Operation == FolderOperation.Copy && !e.IsUndoOperation)
                    {
                        // For Copy -> Create, the "new path" argument to HandleFolderCreate
                        // must be the destination (the newly created copy).
                        refreshSourcePath = e.DestinationPath;   // pass destPath as the "new" node path
                        refreshDestPath = null;
                        operationType = FolderOperationType.Create;
                    }


                    if (operationType == FolderOperationType.Move
                    && e.IsBatchMove
                    && e.AdditionalDestinationPaths?.Count > 1)
                    {
                        // Batch move - use dedicated method that centers all moved items
                        //var sources = e.AdditionalDestinationPaths
                        //    .Select((dest, i) => i == 0 ? e.SourcePath : dest)   
                        //    .ToList();
                        await _shellTreeView.RefreshTreeIncrementalBatchMove(
                              e.AdditionalSourcePaths,
                              e.AdditionalDestinationPaths);

                        string opName = e.IsUndoOperation ? $"Undo {e.Operation}" : e.Operation.ToString();
                        StatusMessage = $"{opName} completed successfully.";
                        return;
                    }

                    // Execute incremental refresh (guaranteed to be on UI thread)
                    await _shellTreeView.RefreshTreeIncremental(operationType, refreshSourcePath, refreshDestPath);
                    // Update status message
                    // Auto-select the newly created folder in the tree
                    if (operationType == FolderOperationType.Create && !e.IsUndoOperation
                        && !string.IsNullOrEmpty(refreshSourcePath))
                    {
                        // Small delay to ensure the tree node has been fully inserted
                        await Task.Delay(100);
                        await _shellTreeView.NavigateToPathAsync(refreshSourcePath, CancellationToken.None, promptToChangeRoot: false, centerInView: false);
                    }
                    string operationName = e.IsUndoOperation ? $"Undo {e.Operation}" : e.Operation.ToString();
                    StatusMessage = $"{operationName} completed successfully.";
                }
                else if (!e.Success)
                {
                    // Handle operation failure
                    string operationName = e.IsUndoOperation ? $"Undo {e.Operation}" : e.Operation.ToString();
                    StatusMessage = $"{operationName} failed: {e.ErrorMessage}";
           
                }
            }
            catch (Exception)
            {
              
                // Fallback to full refresh on error
                try
                {
                    if (_shellTreeView != null)
                    {
                        await _shellTreeView.RefreshTreeFull();
                    }
                }
                catch (Exception refreshEx)
                {
                    Debug.WriteLine($"Failed to refresh tree after error: {refreshEx.Message}");
                }
                throw;
            }
        }



        /// <summary>
        /// Validates TreeView state before performing operations
        /// </summary>
        private bool ValidateTreeViewState(string operationContext)
        {
            if (_shellTreeView == null)
            {
                Debug.WriteLine($"TreeView is null for {operationContext}");
                return false;
            }

            if (_shellTreeView._pathToTreeViewItem == null || _shellTreeView._pathToTreeViewItem.Count == 0)
            {
                Debug.WriteLine($"TreeView not initialized for {operationContext}");
                return false;
            }

            return true;
        }

      
        /// <summary>
        /// Maps existing FolderOperation enum to new FolderOperationType enum
        /// </summary>
        private FolderOperationType MapToFolderOperationType(FolderOperation operation)
        {
            switch (operation)
            {
                case FolderOperation.Create:
                    return FolderOperationType.Create;
                case FolderOperation.Delete:
                    return FolderOperationType.Delete;
                case FolderOperation.Move:
                    return FolderOperationType.Move;
                case FolderOperation.Copy:
                    return FolderOperationType.Create; // Copy appears as creation in destination
                case FolderOperation.Rename:
                    return FolderOperationType.Rename;
                case FolderOperation.Refresh:
                default:
                    return FolderOperationType.Manual; // Default to manual for unknown operations
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
