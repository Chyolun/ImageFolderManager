using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using ImageFolderManager.Models;
using ImageFolderManager.Services;
using ImageFolderManager.ViewModels;
using Microsoft.WindowsAPICodePack.Shell;
using Path = System.IO.Path;

namespace ImageFolderManager.Controls
{
    public partial class ShellTreeView : UserControl, IShellTreeViewAdapter
    {
        // Event to notify when a folder is selected
        public event Action<FolderInfo> FolderSelected;

        // Reference to the host view model contract
        private IShellTreeHost ViewModel
        {
            get
            {
                var vm = DataContext as IShellTreeHost;
                if (vm == null)
                {
                    // Try to get ViewModel from application level
                    if (Application.Current.MainWindow?.DataContext is IShellTreeHost mainVM)
                    {
                        return mainVM;
                    }
                    Debug.WriteLine("ERROR: ShellTreeView's DataContext does not implement IShellTreeHost");
                }
                return vm;
            }
        }

        // For drag and drop operations
        private Point _startPoint;
        private bool _isDragging;
        private TreeViewItem _draggedItem;
        private FolderNode _draggedFolderNode;

        // Track expanded paths
        private HashSet<string> _expandedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Track selected path
        private string _selectedPath;

        // Dictionary to map ShellObject paths to TreeViewItem
        public Dictionary<string, TreeViewItem> _pathToTreeViewItem =
                new Dictionary<string, TreeViewItem>(StringComparer.OrdinalIgnoreCase);

        public bool HasPathMappings => _pathToTreeViewItem != null && _pathToTreeViewItem.Count > 0;

        // Current root directory
        public string _rootDirectory;

        // Multi-selection support
        private ObservableCollection<TreeViewItem> _selectedItems = new ObservableCollection<TreeViewItem>();
        public ObservableCollection<TreeViewItem> SelectedItems => _selectedItems;

        // Track last selected item for shift selection
        private TreeViewItem _lastSelectedItem;

        // For selection with mouse
        private bool _isMultiSelectActive = false;
        private DateTime _mouseDownTime;
        private const int DRAG_DELAY_MS = 300; // 300ms delay before starting drag
        private const double DRAG_DISTANCE_MULTIPLIER = 1; // Increase drag distance threshold

        // Loading state management
        private bool _isLoading = false;
        private DispatcherTimer _loadingTimer;
        private TreeViewItem _currentDropTarget;
        private Storyboard _dropTargetAnimation;
        private Border _dropTargetOverlay;
        private FolderNode _rootNode;

        // Modern visual effects
        private readonly Duration _animationDuration = new Duration(TimeSpan.FromMilliseconds(200));

        private readonly ReaderWriterLockSlim _pathMappingLock = new ReaderWriterLockSlim();

        private HierarchicalNodeManager _nodeManager;
        private FolderOperationCoordinator _coordinator;

        private readonly Dictionary<TreeViewItem, CancellationTokenSource> _expansionCts
                 = new Dictionary<TreeViewItem, CancellationTokenSource>();
        private const int BATCH_SIZE = 30;
        private const int BATCH_DELAY_MS = 8;

        private bool _isInitializing = false;
        public ShellTreeView()
        {
            InitializeComponent();

            // Initialize loading timer
            _loadingTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _loadingTimer.Tick += LoadingTimer_Tick;
            _selectedItems.CollectionChanged += (_, __) => ViewModel?.RefreshEditCommands();

            // Add DataContext change handler to ensure host contract is always accessible
            this.DataContextChanged += (s, e) => {
                if (e.NewValue is IShellTreeHost host)
                {
                    Debug.WriteLine("ShellTreeView received DataContext implementing IShellTreeHost");
                    host.RefreshEditCommands();

                    // Check if root directory has changed
                    if (!_isInitializing &&
                        PathService.DirectoryExists(AppSettings.Instance.DefaultRootDirectory) &&
                        _rootDirectory != AppSettings.Instance.DefaultRootDirectory)
                    {
                        _ = ChangeRootDirectoryAsync(AppSettings.Instance.DefaultRootDirectory);
                    }
                }
                else
                {
                    // If DataContext does not implement IShellTreeHost, try MainWindow fallback
                    if (Application.Current.MainWindow?.DataContext is IShellTreeHost fallbackHost)
                    {
                        fallbackHost.RefreshEditCommands();
                        Debug.WriteLine("Using MainWindow's DataContext as fallback");
                    }
                }
            };
            //ViewModel.FolderOperations.FolderOperationCompleted += FolderOperations_FolderOperationCompleted;
            // Initialize with default root directory
            _ = LoadDefaultRootDirectoryAsync();

        }

        /// <summary>
        /// Initialize with service dependencies
        /// </summary>
        public void InitializeServices(HierarchicalNodeManager nodeManager, FolderOperationCoordinator coordinator)
        {
            _nodeManager = nodeManager;
            _coordinator = coordinator;
        }

        #region Loading State Management

        private void ShowLoadingIndicator()
        {
            if (LoadingIndicator != null)
            {
                LoadingIndicator.Visibility = Visibility.Visible;
                _isLoading = true;
                _loadingTimer.Start();
            }
        }

        private void HideLoadingIndicator()
        {
            if (LoadingIndicator != null)
            {
                LoadingIndicator.Visibility = Visibility.Collapsed;
                _isLoading = false;
                _loadingTimer.Stop();
            }
        }

        private void LoadingTimer_Tick(object sender, EventArgs e)
        {
            // Auto-hide loading indicator after 5 seconds if still showing
            if (_isLoading)
            {
                var elapsed = DateTime.Now.Subtract(_loadingStartTime);
                if (elapsed.TotalSeconds > 5)
                {
                    HideLoadingIndicator();
                }
            }
        }

        private DateTime _loadingStartTime;

        #endregion

        #region Initialization and Root Directory Management

        /// <summary>
        /// Clears the TreeView completely - removes all items and resets state
        /// This method is safe to call from any thread
        /// </summary>
        public void ClearTreeView()
        {
            try
            {
                if (Application.Current.Dispatcher.CheckAccess())
                {
                    // Already on UI thread, execute directly
                    ClearTreeViewCore();
                }
                else
                {
                    // Marshal to UI thread
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        ClearTreeViewCore();
                    });
                }

                Debug.WriteLine("TreeView cleared successfully");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error clearing TreeView: {ex.Message}");
                HandleException("Error clearing TreeView", ex, false);
            }
        }

        /// <summary>
        /// Core TreeView clearing logic - must be called from UI thread
        /// </summary>
        private void ClearTreeViewCore()
        {
            // Ensure we're on UI thread
            Debug.Assert(Application.Current.Dispatcher.CheckAccess(),
                "ClearTreeViewCore must be called from UI thread");

            try
            {
                // Clear multi-selection state first
                ClearSelectedItems();

                // Clear the visual tree
                ShellTreeViewControl.Items.Clear();

                // Clear internal mappings
                _pathToTreeViewItem.Clear();

                // Clear expanded paths tracking
                _expandedPaths.Clear();

                // Reset root directory
                _rootDirectory = null;

                // Reset any drag/drop state
                _draggedItem = null;
                _draggedFolderNode = null;
                _lastSelectedItem = null;

                Debug.WriteLine("TreeView core state cleared");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in ClearTreeViewCore: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Sets the root directory and initializes the TreeView
        /// This method is safe to call from any thread and handles proper initialization sequencing
        /// </summary>
        /// <param name="rootPath">The root directory path to set</param>
        /// <param name="showLoadingIndicator">Whether to show loading indicator during initialization</param>
        /// <returns>Task that completes when initialization is finished</returns>
        public async Task SetRootDirectory(string rootPath, bool showLoadingIndicator = true)
        {
            try
            {
                Debug.WriteLine($"SetRootDirectory called with path: {rootPath ?? "null"}");

                // Validate input
                if (!string.IsNullOrEmpty(rootPath) && !PathService.DirectoryExists(rootPath))
                {
                    throw new DirectoryNotFoundException($"Directory does not exist: {rootPath}");
                }

                // Ensure we're on UI thread for the initialization
                if (Application.Current.Dispatcher.CheckAccess())
                {
                    await SetRootDirectoryCore(rootPath, showLoadingIndicator);
                }
                else
                {
                    await Application.Current.Dispatcher.InvokeAsync(async () =>
                    {
                        await SetRootDirectoryCore(rootPath, showLoadingIndicator);
                    });
                }

                Debug.WriteLine($"SetRootDirectory completed successfully for: {rootPath ?? "This PC"}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in SetRootDirectory: {ex.Message}");
                HandleException("Error setting root directory", ex);
                throw;
            }
        }

        /// <summary>
        /// Core root directory setting logic - must be called from UI thread
        /// </summary>
        private async Task SetRootDirectoryCore(string rootPath, bool showLoadingIndicator)
        {
            // Ensure we're on UI thread
            Debug.Assert(Application.Current.Dispatcher.CheckAccess(),
                "SetRootDirectoryCore must be called from UI thread");

            try
            {
                // Show loading indicator if requested
                if (showLoadingIndicator)
                {
                    ShowLoadingIndicator();
                    _loadingStartTime = DateTime.Now;
                }

                // Clear multi-selection state to prevent interference
                ClearSelectedItems();

                // Update root directory
                _rootDirectory = rootPath;

                // Initialize the tree with new root (this calls InitializeShellTreeAsync which clears everything)
                await InitializeShellTreeAsync();

                // Hide loading indicator
                if (showLoadingIndicator)
                {
                    HideLoadingIndicator();
                }

                Debug.WriteLine($"Root directory set successfully: {rootPath ?? "This PC"}");
            }
            catch (Exception ex)
            {
                if (showLoadingIndicator)
                {
                    HideLoadingIndicator();
                }

                Debug.WriteLine($"Error in SetRootDirectoryCore: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Synchronous version of SetRootDirectory for scenarios where async is not suitable
        /// This method schedules the operation on the UI thread and does not wait for completion
        /// </summary>
        /// <param name="rootPath">The root directory path to set</param>
        public void SetRootDirectorySync(string rootPath)
        {
            try
            {
                Debug.WriteLine($"SetRootDirectorySync called with path: {rootPath ?? "null"}");

                if (Application.Current.Dispatcher.CheckAccess())
                {
                    // Already on UI thread, schedule async operation
                    _ = Task.Run(async () =>
                    {
                        await Application.Current.Dispatcher.InvokeAsync(async () =>
                        {
                            await SetRootDirectoryCore(rootPath, true);
                        });
                    });
                }
                else
                {
                    // Marshal to UI thread and schedule
                    Application.Current.Dispatcher.BeginInvoke(new Action(async () =>
                    {
                        await SetRootDirectoryCore(rootPath, true);
                    }));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in SetRootDirectorySync: {ex.Message}");
                HandleException("Error setting root directory (sync)", ex, false);
            }
        }

        /// <summary>
        /// Gets the current root directory
        /// </summary>
        /// <returns>Current root directory path, or null if not set</returns>
        public string GetRootDirectory()
        {
            return _rootDirectory;
        }

        /// <summary>
        /// Checks if the TreeView is properly initialized and ready for operations
        /// </summary>
        /// <returns>True if TreeView is initialized, false otherwise</returns>
        public bool IsTreeViewInitialized()
        {
            try
            {
                if (Application.Current.Dispatcher.CheckAccess())
                {
                    return IsTreeViewInitializedCore();
                }
                else
                {
                    return Application.Current.Dispatcher.Invoke(() => IsTreeViewInitializedCore());
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking TreeView initialization: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Core initialization check - must be called from UI thread
        /// </summary>
        private bool IsTreeViewInitializedCore()
        {
            // Ensure we're on UI thread
            Debug.Assert(Application.Current.Dispatcher.CheckAccess(),
                "IsTreeViewInitializedCore must be called from UI thread");

            // Check if we have items in the tree
            if (ShellTreeViewControl.Items.Count == 0)
                return false;

            // Check if we have path mappings
            if (_pathToTreeViewItem.Count == 0)
                return false;

            // If we have a root directory, verify it's mapped
            if (!string.IsNullOrEmpty(_rootDirectory))
            {
                string normalizedRoot = PathService.NormalizePath(_rootDirectory);
                return _pathToTreeViewItem.ContainsKey(normalizedRoot);
            }

            // If no specific root, we should have at least one item mapped
            return true;
        }

        /// <summary>
        /// Resets the TreeView to default state (This PC view)
        /// </summary>
        public async Task ResetToDefaultState()
        {
            try
            {
                Debug.WriteLine("Resetting TreeView to default state");
                await SetRootDirectory(null, true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error resetting TreeView to default state: {ex.Message}");
                HandleException("Error resetting TreeView", ex);
            }
        }

        /// <summary>
        /// Validates TreeView state and provides detailed diagnostics
        /// This method is useful for debugging initialization issues
        /// </summary>
        /// <param name="context">Context information for logging</param>
        /// <returns>Validation result with details</returns>
        public TreeViewValidationResult ValidateTreeViewState(string context = "Unknown")
        {
            try
            {
                var result = new TreeViewValidationResult
                {
                    Context = context,
                    Timestamp = DateTime.Now
                };

                if (Application.Current.Dispatcher.CheckAccess())
                {
                    PopulateValidationResultCore(result);
                }
                else
                {
                    Application.Current.Dispatcher.Invoke(() => PopulateValidationResultCore(result));
                }

                return result;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error validating TreeView state: {ex.Message}");
                return new TreeViewValidationResult
                {
                    Context = context,
                    Timestamp = DateTime.Now,
                    IsValid = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        /// <summary>
        /// Core validation logic - must be called from UI thread
        /// </summary>
        private void PopulateValidationResultCore(TreeViewValidationResult result)
        {
            // Ensure we're on UI thread
            Debug.Assert(Application.Current.Dispatcher.CheckAccess(),
                "PopulateValidationResultCore must be called from UI thread");

            try
            {
                result.ItemCount = ShellTreeViewControl.Items.Count;
                result.PathMappingCount = _pathToTreeViewItem.Count;
                result.ExpandedPathsCount = _expandedPaths.Count;
                result.RootDirectory = _rootDirectory;
                result.HasSelectedItems = _selectedItems?.Count > 0;

                // Check basic validity
                result.IsValid = result.ItemCount > 0 && result.PathMappingCount > 0;

                // Additional checks
                if (!string.IsNullOrEmpty(_rootDirectory))
                {
                    string normalizedRoot = PathService.NormalizePath(_rootDirectory);
                    result.IsRootMapped = _pathToTreeViewItem.ContainsKey(normalizedRoot);
                    result.IsValid &= result.IsRootMapped;
                }
                else
                {
                    result.IsRootMapped = true; // No specific root required
                }

                result.ErrorMessage = result.IsValid ? null : "TreeView state validation failed";

                Debug.WriteLine($"TreeView validation for '{result.Context}': Valid={result.IsValid}, Items={result.ItemCount}, Mappings={result.PathMappingCount}");
            }
            catch (Exception ex)
            {
                result.IsValid = false;
                result.ErrorMessage = $"Validation error: {ex.Message}";
            }
        }

        private async Task LoadDefaultRootDirectoryAsync()
        {
            _isInitializing = true;
            try
            {
                if (!string.IsNullOrEmpty(AppSettings.Instance.DefaultRootDirectory))
                {
                    ShowLoadingIndicator();
                    _loadingStartTime = DateTime.Now;

                    await Task.Delay(100); // Brief delay to ensure component is fully loaded

                    // Set the root directory from AppSettings
                    _rootDirectory = AppSettings.Instance.DefaultRootDirectory;

                    // Initialize the shell tree - this will clear existing items first
                    await InitializeShellTreeAsync();

                    // Don't call SelectPath here to avoid duplication issues
                    // SelectPath(AppSettings.Instance.DefaultRootDirectory);

                    HideLoadingIndicator();
                }
            }
            catch (Exception ex)
            {
                HideLoadingIndicator();
                HandleException("Error loading default root directory", ex);
            }
            finally
            {
                // Clear the flag so subsequent explicit root-directory changes
                // (triggered by the user) are processed normally.
                _isInitializing = false;
                // Keep initialization guard reset in finally.
            }
        }

        private async Task InitializeShellTreeAsync()
        {
            // Clear all existing state
            ShellTreeViewControl.Items.Clear();
            _pathToTreeViewItem.Clear();
            _expansionCts.Clear();

            if (!Directory.Exists(_rootDirectory))
            {
                Debug.WriteLine($"InitializeShellTreeAsync: path does not exist: {_rootDirectory}");
                return;
            }

            // Build the root node (pure filesystem object - no COM)
            _rootNode = new FolderNode(_rootDirectory);

            // Create the root TreeViewItem on the UI thread
            var rootItem = FolderTreeItemFactory.CreateItem(_rootNode);
            ShellTreeViewControl.Items.Add(rootItem);
            _pathToTreeViewItem[_rootDirectory] = rootItem;

            // Expand the root immediately so the first level is visible.
            // ExpandAsync inserts children in background-priority batches,
            // so the window stays responsive even with hundreds of sub-folders.
            rootItem.IsExpanded = true;
            await ExpandNodeAsync(rootItem, _rootNode);

            if (AppSettings.Instance.AutoExpandFolders)
            {
                // Optional convenience mode: expand first-level folders automatically.
                // Limit the count to keep startup responsive on very large roots.
                var firstLevelItems = rootItem.Items
                    .OfType<TreeViewItem>()
                    .Take(40)
                    .ToList();

                foreach (var childItem in firstLevelItems)
                {
                    if (childItem.Tag is FolderNode childNode && !childItem.IsExpanded)
                    {
                        childItem.IsExpanded = true;
                        await ExpandNodeAsync(childItem, childNode);
                    }
                }
            }

            Debug.WriteLine($"InitializeShellTreeAsync complete: {_rootDirectory}");
        }

        public async Task ChangeRootDirectoryAsync(string newRootDirectory)
        {
            try
            {
                ShowLoadingIndicator();
                _loadingStartTime = DateTime.Now;

                // Clear multi-selection state to prevent interference with root directory change
                ClearSelectedItems();

                // If null or empty, show This PC
                if (string.IsNullOrEmpty(newRootDirectory))
                {
                    _rootDirectory = null;
                    await InitializeShellTreeAsync();
                    HideLoadingIndicator();
                    return;
                }

                // Verify directory exists
                if (!PathService.DirectoryExists(newRootDirectory))
                {
                    HideLoadingIndicator();
                    MessageBox.Show("Cannot set root directory: Directory does not exist.",
                        "Invalid Directory", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Debug.WriteLine($"Changing root directory to: {newRootDirectory}");

                // Store new root
                _rootDirectory = newRootDirectory;

                // Reinitialize tree with new root (this will clear existing items)
                await InitializeShellTreeAsync();

                HideLoadingIndicator();
            }
            catch (Exception ex)
            {
                HideLoadingIndicator();
                HandleException("Error changing root directory", ex);
            }
        }

        public void ChangeRootDirectory(string newRootDirectory)
        {
            _ = ChangeRootDirectoryAsync(newRootDirectory);
        }

        #endregion

        #region Modern Animation Support

        private async Task ExpandItemWithAnimationAsync(TreeViewItem item)
        {
            if (item == null) return;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                item.IsExpanded = true;

                // Add expand animation
                var storyboard = new Storyboard();
                var opacityAnimation = new DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    Duration = _animationDuration,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };

                Storyboard.SetTarget(opacityAnimation, item);
                Storyboard.SetTargetProperty(opacityAnimation, new PropertyPath("Opacity"));
                storyboard.Children.Add(opacityAnimation);

                storyboard.Begin();
            });
        }

        private void AnimateSelection(TreeViewItem item, bool isSelected)
        {
            if (item == null) return;

            // Find the ContentBorder within the item by name
            var border = FindVisualChildByName<Border>(item, "ContentBorder");
            if (border != null)
            {
                if (isSelected)
                {
                    // Use bright background highlight for selection
                    var highlightBrush = new LinearGradientBrush();
                    highlightBrush.StartPoint = new Point(0, 0);
                    highlightBrush.EndPoint = new Point(1, 0);
                    highlightBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0, 120, 212), 0));
                    highlightBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0, 100, 190), 1));

                    border.Background = highlightBrush;
                    border.BorderBrush = new SolidColorBrush(Color.FromRgb(0, 255, 255));
                    border.BorderThickness = new Thickness(1);

                    // Make text more visible on blue background
                    var textBlock = FindVisualChild<TextBlock>(border);
                    if (textBlock != null)
                    {
                        textBlock.Foreground = Brushes.White;
                        textBlock.FontWeight = FontWeights.SemiBold;
                    }
                }
                else
                {
                    // Remove selection highlight
                    border.Background = Brushes.Transparent;
                    border.BorderBrush = null;
                    border.BorderThickness = new Thickness(0);

                    // Reset text appearance
                    var textBlock = FindVisualChild<TextBlock>(border);
                    if (textBlock != null)
                    {
                        textBlock.Foreground = Brushes.White;
                        textBlock.FontWeight = FontWeights.Normal;
                    }
                }
            }
        }


        #endregion

        #region Enhanced Drag and Drop with Modern Visual Feedback
        private T FindVisualChildByName<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);

                if (child is T element && element.Name == name)
                    return element;

                T result = FindVisualChildByName<T>(child, name);
                if (result != null)
                    return result;
            }
            return null;
        }

        // Enhanced drop target highlighting with multiple visual cues
        private void HighlightDropTarget(TreeViewItem item)
        {
            // Clear previous highlights
            ClearDropTargetHighlight();

            if (item != null)
            {
                _currentDropTarget = item;

                // Find the ContentBorder specifically (the main content area)
                var border = FindVisualChildByName<Border>(item, "ContentBorder");
                if (border != null)
                {
                    // Create enhanced visual feedback for entire folder area
                    CreateEnhancedDropHighlight(border, item);
                }
            }
        }

        private void CreateEnhancedDropHighlight(Border border, TreeViewItem item)
        {

            // 1. Create full-width drop indicator overlay
            CreateFullWidthDropIndicator(item);

            // 2. Enhanced pulsing animation for entire content area
            CreateEnhancedDropAnimation(border);
        }

        private void CreateFullWidthDropIndicator(TreeViewItem item)
        {
            // Create a full-width overlay that covers the entire folder content
            var overlayBorder = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false,
                BorderThickness = new Thickness(2),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0, 255, 200)),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.FromArgb(60, 0, 255, 200))
            };

            // Create animated content inside the overlay
            var contentPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };

            // Drop target icon
            var dropIcon = new TextBlock
            {
                Text = "\uD83D\uDCE5",
                FontSize = 16,
                Foreground = new SolidColorBrush(Color.FromRgb(255, 255, 255)),
                Opacity = 0.9
            };

            contentPanel.Children.Add(dropIcon);
            overlayBorder.Child = contentPanel;

            // Animated pulsing for the overlay
            var pulseAnimation = new DoubleAnimation
            {
                From = 0.3,
                To = 0.8,
                Duration = TimeSpan.FromMilliseconds(800),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };

            overlayBorder.BeginAnimation(UIElement.OpacityProperty, pulseAnimation);

            // Find the main content grid to add overlay
            var contentBorder = FindVisualChildByName<Border>(item, "ContentBorder");
            if (contentBorder != null)
            {
                // We need to add to the parent grid that contains the ContentBorder
                var parentGrid = VisualTreeHelper.GetParent(contentBorder) as Grid;
                if (parentGrid != null)
                {
                    // Set to span across both columns (expander + content)
                    Grid.SetColumn(overlayBorder, 1);
                    Grid.SetColumnSpan(overlayBorder, 1);
                    parentGrid.Children.Add(overlayBorder);
                    _dropTargetOverlay = overlayBorder;
                }
            }
        }

        private void CreateEnhancedDropAnimation(Border border)
        {
            // Create complex storyboard with multiple animations
            _dropTargetAnimation = new Storyboard();

            // Opacity pulsing
            var opacityAnimation = new DoubleAnimation
            {
                From = 0.7,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(600),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };

            Storyboard.SetTarget(opacityAnimation, border);
            Storyboard.SetTargetProperty(opacityAnimation, new PropertyPath("Opacity"));

            // Border thickness animation for "breathing" effect
            var thicknessAnimation = new ThicknessAnimation
            {
                From = new Thickness(2),
                To = new Thickness(4),
                Duration = TimeSpan.FromMilliseconds(800),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };

            Storyboard.SetTarget(thicknessAnimation, border);
            Storyboard.SetTargetProperty(thicknessAnimation, new PropertyPath("BorderThickness"));

            _dropTargetAnimation.Children.Add(opacityAnimation);
            _dropTargetAnimation.Children.Add(thicknessAnimation);
            _dropTargetAnimation.Begin();
        }

        private void ClearDropTargetHighlight()
        {
            if (_currentDropTarget != null)
            {
                var border = FindVisualChild<Border>(_currentDropTarget);
                if (border != null)
                {
                    // Stop all animations
                    if (_dropTargetAnimation != null)
                    {
                        _dropTargetAnimation.Stop();
                        _dropTargetAnimation = null;
                    }

                    // Clear all animation properties
                    border.BeginAnimation(UIElement.OpacityProperty, null);
                    border.BeginAnimation(Border.BorderThicknessProperty, null);

                    // Clear transform animations
                    if (border.RenderTransform is ScaleTransform scaleTransform)
                    {
                        scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                        scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    }

                    // Reset visual properties
                    border.Background = null;
                    border.BorderBrush = null;
                    border.BorderThickness = new Thickness(0);
                    border.Effect = null;
                    border.RenderTransform = null;
                    border.Opacity = 1.0;

                    // Remove overlay
                    RemoveDropIndicatorOverlay();
                }

                _currentDropTarget = null;
            }

            // Clear highlights from any other items that might have them
            var allItems = FindVisualChildren<TreeViewItem>(ShellTreeViewControl);
            foreach (var item in allItems)
            {
                if (item != _currentDropTarget && !_selectedItems.Contains(item))
                {
                    var border = FindVisualChild<Border>(item);
                    if (border != null)
                    {
                        border.BeginAnimation(UIElement.OpacityProperty, null);
                        border.Background = null;
                        border.BorderBrush = null;
                        border.BorderThickness = new Thickness(0);
                        border.Effect = null;
                    }
                }
            }
        }

        private void RemoveDropIndicatorOverlay()
        {
            if (_dropTargetOverlay != null && _currentDropTarget != null)
            {
                // Find the parent grid that contains our overlay
                var parentGrid = VisualTreeHelper.GetParent(_dropTargetOverlay) as Grid;
                if (parentGrid != null && parentGrid.Children.Contains(_dropTargetOverlay))
                {
                    parentGrid.Children.Remove(_dropTargetOverlay);
                }
                _dropTargetOverlay = null;
            }
        }

        // Handle drag leave to clear highlights immediately
        private void TreeView_DragLeave(object sender, DragEventArgs e)
        {
            ClearDropTargetHighlight();
        }

        // Enhanced drag over with better feedback
        private void TreeView_DragOver(object sender, DragEventArgs e)
        {
            // Check if the data format is supported
            if (!e.Data.GetDataPresent("FileDrop"))
            {
                e.Effects = DragDropEffects.None;
                ClearDropTargetHighlight();
                e.Handled = true;
                return;
            }

            // Get the item under the cursor
            var targetItem = GetTreeViewItemUnderMouse(e.GetPosition(ShellTreeViewControl));

            if (targetItem == null)
            {
                e.Effects = DragDropEffects.None;
                ClearDropTargetHighlight();
                e.Handled = true;
                return;
            }

            var targetNode = targetItem.Tag as FolderNode;
            if (targetNode == null)
            {
                e.Effects = DragDropEffects.None;
                ClearDropTargetHighlight();
                e.Handled = true;
                return;
            }

            string targetPath = targetNode.FullPath;
            if (!PathService.DirectoryExists(targetPath))
            {
                e.Effects = DragDropEffects.None;
                ClearDropTargetHighlight();
                e.Handled = true;
                return;
            }

            // Get the source paths
            var filePaths = e.Data.GetData("FileDrop") as string[];
            if (filePaths == null || filePaths.Length == 0)
            {
                e.Effects = DragDropEffects.None;
                ClearDropTargetHighlight();
                e.Handled = true;
                return;
            }

            // Validate drop target
            bool validTarget = true;
            foreach (string sourcePath in filePaths)
            {
                if (PathService.PathsEqual(sourcePath, targetPath) ||
                    PathService.IsPathWithin(sourcePath, targetPath))
                {
                    validTarget = false;
                    break;
                }
            }

            if (!validTarget)
            {
                e.Effects = DragDropEffects.None;
                ClearDropTargetHighlight();

                // Show visual feedback for invalid target
                ShowInvalidDropTarget(targetItem);
                e.Handled = true;
                return;
            }

            // Valid target - show enhanced highlight
            HighlightDropTarget(targetItem);

            // Determine operation type
            if ((e.KeyStates & DragDropKeyStates.ControlKey) == DragDropKeyStates.ControlKey)
            {
                e.Effects = DragDropEffects.Copy;
                ShowCopyIndicator(targetItem);
            }
            else
            {
                e.Effects = DragDropEffects.Move;
                ShowMoveIndicator(targetItem);
            }

            e.Handled = true;
        }

        private void ShowInvalidDropTarget(TreeViewItem item)
        {
            if (item == null) return;

            var border = FindVisualChild<Border>(item);
            if (border != null)
            {
                // Red highlight for invalid targets
                border.Background = new SolidColorBrush(Color.FromArgb(100, 255, 0, 0));
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 0, 0));
                border.BorderThickness = new Thickness(2);

                // Shake animation to indicate invalid drop
                var shakeTransform = new TranslateTransform();
                border.RenderTransform = shakeTransform;

                var shakeAnimation = new DoubleAnimation
                {
                    From = 0,
                    To = 5,
                    Duration = TimeSpan.FromMilliseconds(50),
                    AutoReverse = true,
                    RepeatBehavior = new RepeatBehavior(4)
                };

                shakeTransform.BeginAnimation(TranslateTransform.XProperty, shakeAnimation);

                // Auto-clear after animation
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(400)
                };
                timer.Tick += (s, e) =>
                {
                    border.Background = null;
                    border.BorderBrush = null;
                    border.BorderThickness = new Thickness(0);
                    border.RenderTransform = null;
                    timer.Stop();
                };
                timer.Start();
            }
        }

        private void ShowCopyIndicator(TreeViewItem item)
        {
            // Add a "+" icon to indicate copy operation
            var grid = FindVisualChild<Grid>(item);
            if (grid != null)
            {
                var copyIcon = new TextBlock
                {
                    Text = "\uD83D\uDCCB",
                    FontSize = 16,
                    Foreground = new SolidColorBrush(Color.FromRgb(0, 255, 0)),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 0, 25, 0),
                    IsHitTestVisible = false
                };

                // Add to grid temporarily
                grid.Children.Add(copyIcon);

                // Auto-remove when highlight clears
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(100)
                };
                timer.Tick += (s, e) =>
                {
                    if (_currentDropTarget != item)
                    {
                        grid.Children.Remove(copyIcon);
                        timer.Stop();
                    }
                };
                timer.Start();
            }
        }

        private void ShowMoveIndicator(TreeViewItem item)
        {
            // Add an arrow icon to indicate move operation
            var grid = FindVisualChild<Grid>(item);
            if (grid != null)
            {
                var moveIcon = new TextBlock
                {
                    Text = "\u27A4",
                    FontSize = 16,
                    Foreground = new SolidColorBrush(Color.FromRgb(255, 215, 0)),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 0, 25, 0),
                    IsHitTestVisible = false
                };

                grid.Children.Add(moveIcon);

                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(100)
                };
                timer.Tick += (s, e) =>
                {
                    if (_currentDropTarget != item)
                    {
                        grid.Children.Remove(moveIcon);
                        timer.Stop();
                    }
                };
                timer.Start();
            }
        }


        private void TreeView_Drop(object sender, DragEventArgs e)
        {
            // Store the target item for completion animation
            var targetItem = _currentDropTarget;

            // Clear highlight first
            ClearDropTargetHighlight();

            if (!e.Data.GetDataPresent("FileDrop"))
                return;

            // Get the drop target
            if (targetItem == null) return;

            var targetNode = targetItem.Tag as FolderNode;
            if (targetNode == null) return;

            string targetPath = targetNode.FullPath;
            if (!PathService.DirectoryExists(targetPath))
                return;

            // Get the source paths
            var filePaths = e.Data.GetData("FileDrop") as string[];
            if (filePaths == null || filePaths.Length == 0) return;

            // Create target folder FolderInfo
            var targetFolder = new FolderInfo(targetPath);

            // Determine if this is a copy or move operation
            bool isCopy = (e.KeyStates & DragDropKeyStates.ControlKey) == DragDropKeyStates.ControlKey;

            if (ViewModel != null)
            {
                Debug.WriteLine($"Performing drag & drop operation: {(isCopy ? "Copy" : "Move")} to {targetPath}");

                // Save source parent paths for later refresh
                var sourceParentPaths = new HashSet<string>();
                foreach (string sourcePath in filePaths)
                {
                    if (PathService.DirectoryExists(sourcePath))
                    {
                        // Get source folder's parent directory
                        string parentPath = Path.GetDirectoryName(sourcePath);
                        if (!string.IsNullOrEmpty(parentPath))
                        {
                            sourceParentPaths.Add(parentPath);
                        }

                        // Invalidate path cache for source folder and its contents
                        PathService.InvalidatePathCache(sourcePath, true);
                    }
                }

                // Invalidate path cache for target folder
                PathService.InvalidatePathCache(targetPath, false);

                try
                {
                    // Multi-folder operation
                    var sourceFolders = new List<FolderInfo>();
                    foreach (string path in filePaths)
                    {
                        if (PathService.DirectoryExists(path))
                        {
                            sourceFolders.Add(new FolderInfo(path));
                        }
                    }

                    _ = ExecuteDropOperationAsync(
                        sourceFolders,
                        targetFolder,
                        targetPath,
                        sourceParentPaths,
                        isCopy);


                }
                catch (Exception ex)
                {
                    HandleException("Error during drag and drop operation", ex);
                }
            }
            else
            {
                MessageBox.Show("Could not complete drag and drop operation: ViewModel is not available.",
                    "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            // Show completion animation
            if (targetItem != null)
            {
                ShowDropCompletionAnimation(targetItem);
            }
        }

        private async Task ExecuteDropOperationAsync(
            List<FolderInfo> sourceFolders,
            FolderInfo targetFolder,
            string targetPath,
            HashSet<string> sourceParentPaths,
            bool isCopy)
        {
            try
            {
                if (sourceFolders == null || sourceFolders.Count == 0 || ViewModel == null)
                {
                    return;
                }

                if (isCopy)
                {
                    ViewModel.CopyFolders(sourceFolders);
                    await ViewModel.PasteFolders(targetFolder);
                }
                else
                {
                    await ViewModel.MoveFolders(sourceFolders, targetFolder);
                }

                // After the operation completes, invalidate path cache again for the target folder
                PathService.InvalidatePathCache(targetPath, true);

                // Also invalidate path cache for all source parent paths
                foreach (var parentPath in sourceParentPaths)
                {
                    PathService.InvalidatePathCache(parentPath, true);
                }
            }
            catch (Exception ex)
            {
                HandleException("Error during drag and drop operation", ex);
            }
        }

        private void ShowDropCompletionAnimation(TreeViewItem item)
        {
            var border = FindVisualChild<Border>(item);
            if (border != null)
            {
                // Success flash animation
                var successBrush = new SolidColorBrush(Color.FromArgb(150, 0, 255, 0));
                border.Background = successBrush;

                var fadeOutAnimation = new DoubleAnimation
                {
                    From = 1.0,
                    To = 0.0,
                    Duration = TimeSpan.FromMilliseconds(500),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };

                fadeOutAnimation.Completed += (s, e) =>
                {
                    border.Background = null;
                };

                successBrush.BeginAnimation(SolidColorBrush.OpacityProperty, fadeOutAnimation);
            }
        }


        #endregion
        #region Helper Methods for Visual Tree Operations

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child != null && child is T)
                    return (T)child;

                T childOfChild = FindVisualChild<T>(child);
                if (childOfChild != null)
                    return childOfChild;
            }
            return null;
        }

        public static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) yield break;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);

                if (child != null && child is T)
                    yield return (T)child;

                foreach (T descendant in FindVisualChildren<T>(child))
                    yield return descendant;
            }
        }

        #endregion



        public void UpdatePathMapping(string oldPath, string newPath)
        {
            if (_pathToTreeViewItem.TryGetValue(oldPath, out var treeViewItem))
            {
                _pathToTreeViewItem.Remove(oldPath);
                _pathToTreeViewItem[newPath] = treeViewItem;
            }
        }

        public void SelectPath(string path)
        {
            _ = NavigateToPathAsync(path, CancellationToken.None, promptToChangeRoot: true);
        }

        public bool IsPathMapped(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || _pathToTreeViewItem == null)
            {
                return false;
            }

            return _pathToTreeViewItem.ContainsKey(PathService.NormalizePath(path));
        }
        private bool IsPathWithinTreeScope(string path)
        {
            if (string.IsNullOrEmpty(_rootDirectory))
                return true; // No root restriction, anything is in scope

            // Use PathService for efficient comparison
            return PathService.IsPathWithin(_rootDirectory, path);
        }

        private async Task ExpandPathToFolderAsync(string path, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(path)) return;

            // Build list of parent directories that need to be expanded
            var directoriesToExpand = new List<string>();
            var currentDir = new DirectoryInfo(path).Parent;

            while (currentDir != null)
            {
                if (!string.IsNullOrEmpty(_rootDirectory) &&
                    PathService.PathsEqual(currentDir.FullName, _rootDirectory))
                    break;

                directoriesToExpand.Insert(0, PathService.NormalizePath(currentDir.FullName));
                currentDir = currentDir.Parent;
            }

            if (ShellTreeViewControl.Items.Count == 0) return;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (ShellTreeViewControl.Items[0] is TreeViewItem rootItem)
                {
                    rootItem.IsExpanded = true;
                }
            });

            foreach (var dir in directoriesToExpand)
            {
                cancellationToken.ThrowIfCancellationRequested();

                for (int attempt = 0; attempt < 40; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    TreeViewItem parentItem = null;
                    if (_pathToTreeViewItem.TryGetValue(dir, out var foundParent))
                    {
                        parentItem = foundParent;
                    }
                    else
                    {
                        var parentPath = Path.GetDirectoryName(dir);
                        if (!string.IsNullOrEmpty(parentPath) && _pathToTreeViewItem.TryGetValue(PathService.NormalizePath(parentPath), out var parentCandidate))
                        {
                            parentItem = parentCandidate;
                        }
                    }

                    if (parentItem != null)
                    {
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            parentItem.IsExpanded = true;
                        });

                        if (_pathToTreeViewItem.ContainsKey(dir))
                            break;
                    }

                    await Task.Delay(50, cancellationToken);
                }
            }
        }
        public List<FolderInfo> GetSelectedFolderInfos()
        {
            var selectedFolders = new List<FolderInfo>();

            foreach (var item in _selectedItems)
            {
                var folderNode = item.Tag as FolderNode;
                if (folderNode != null)
                {
                    string path = folderNode.FullPath;
                    if (PathService.DirectoryExists(path))
                    {
                        selectedFolders.Add(new FolderInfo(path));
                    }
                }
            }
            return selectedFolders;
        }

        private TreeViewItem GetSelectedTreeViewItem()
        {
            // Return the first selected item or the selected item from the TreeView
            if (_selectedItems.Count > 0)
            {
                return _selectedItems[0];
            }

            return ShellTreeViewControl.SelectedItem as TreeViewItem;
        }



        /// <summary>
        /// Enum for folder operation types to determine refresh strategy
        /// </summary>
        public enum FolderOperationType
        {
            Create,
            Delete,
            Rename,
            Move,
            UndoMove,
            Manual
        }

        // method for test Tree View Refreshing
        private void LogTreeViewState(string context)
        {
            Debug.WriteLine($"=== Tree View State Debug - {context} ===");
            Debug.WriteLine($"Dictionary entries: {_pathToTreeViewItem.Count}");
            Debug.WriteLine($"Tree items count: {ShellTreeViewControl.Items.Count}");
            Debug.WriteLine($"Root directory: {_rootDirectory ?? "null"}");

            if (_pathToTreeViewItem.Count < 10) // Only log details for small trees
            {
                foreach (var kvp in _pathToTreeViewItem)
                {
                    Debug.WriteLine($"  Mapped: {kvp.Key}");
                }
            }
            Debug.WriteLine("=== End Tree View State Debug ===");
        }



    }
    /// <summary>
    /// Result of TreeView state validation
    /// </summary>
    public class TreeViewValidationResult
    {
        public string Context { get; set; }
        public DateTime Timestamp { get; set; }
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; }
        public int ItemCount { get; set; }
        public int PathMappingCount { get; set; }
        public int ExpandedPathsCount { get; set; }
        public string RootDirectory { get; set; }
        public bool IsRootMapped { get; set; }
        public bool HasSelectedItems { get; set; }

        public override string ToString()
        {
            return $"TreeView Validation [{Context}] at {Timestamp:HH:mm:ss} - " +
                   $"Valid: {IsValid}, Items: {ItemCount}, Mappings: {PathMappingCount}, " +
                   $"Root: {RootDirectory ?? "null"}, RootMapped: {IsRootMapped}";
        }


    }

    /// <summary>
    /// Attached behavior that enables animating ScrollViewer.VerticalOffset.
    /// </summary>
    public static class ScrollViewerBehavior
    {
        public static readonly DependencyProperty VerticalOffsetProperty =
            DependencyProperty.RegisterAttached(
                "VerticalOffset",
                typeof(double),
                typeof(ScrollViewerBehavior),
                new PropertyMetadata(0.0, OnVerticalOffsetChanged));

        public static void SetVerticalOffset(DependencyObject target, double value)
            => target.SetValue(VerticalOffsetProperty, value);

        public static double GetVerticalOffset(DependencyObject target)
            => (double)target.GetValue(VerticalOffsetProperty);

        private static void OnVerticalOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ScrollViewer sv)
                sv.ScrollToVerticalOffset((double)e.NewValue);
        }
    }
}

