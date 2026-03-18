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
    public partial class ShellTreeView : UserControl
    {
        // Event to notify when a folder is selected
        public event Action<FolderInfo> FolderSelected;

        // Reference to the main view model
        private MainViewModel ViewModel
        {
            get
            {
                var vm = DataContext as MainViewModel;
                if (vm == null)
                {
                    // Try to get ViewModel from application level
                    if (Application.Current.MainWindow?.DataContext is MainViewModel mainVM)
                    {
                        return mainVM;
                    }
                    Debug.WriteLine("ERROR: ShellTreeView's DataContext is not MainViewModel");
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

            // Add DataContext change handler to ensure MainViewModel is always accessible
            this.DataContextChanged += (s, e) => {
                if (e.NewValue is MainViewModel)
                {
                    Debug.WriteLine("ShellTreeView received correct DataContext (MainViewModel)");

                    // Check if root directory has changed
                    if (!_isInitializing &&
                        PathService.DirectoryExists(AppSettings.Instance.DefaultRootDirectory) &&
                        _rootDirectory != AppSettings.Instance.DefaultRootDirectory)
                    {
                        ChangeRootDirectory(AppSettings.Instance.DefaultRootDirectory);
                    }
                }
                else
                {
                    // If DataContext is not MainViewModel, try to get from MainWindow
                    if (Application.Current.MainWindow?.DataContext is MainViewModel)
                    {
                        Debug.WriteLine("Using MainWindow's DataContext as fallback");
                    }
                }
            };
            //ViewModel.FolderOperations.FolderOperationCompleted += FolderOperations_FolderOperationCompleted;
            // Initialize with default root directory
            LoadDefaultRootDirectoryAsync();

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

        private async void LoadDefaultRootDirectoryAsync()
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
                // ──────────────────────────────────────────────────────────────
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

            // Build the root node (pure filesystem object — no COM)
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

            Debug.WriteLine($"InitializeShellTreeAsync complete: {_rootDirectory}");
        }

        public async void ChangeRootDirectory(string newRootDirectory)
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
                Text = "📁⬇",
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
                    Text = "📋",
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
                    Text = "➤",
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

                    Task.Run(async () =>
                    {
                        await Application.Current.Dispatcher.InvokeAsync(async () =>
                        {
                            if (isCopy)
                            {
                                ViewModel.CopyFolders(sourceFolders);
                                await ViewModel.PasteFolders(targetFolder);
                            }
                            else
                            {
                                await ViewModel.MoveFolders(sourceFolders, targetFolder);
                            }
                        });


                    });


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

        #region Selection Management with Modern Visual Feedback

        private void SelectItem(TreeViewItem item)
        {
            if (item == null) return;

            // Apply modern visual selection with animation
            AnimateSelection(item, true);

            if (!_selectedItems.Contains(item))
            {
                _selectedItems.Add(item);
            }

            _lastSelectedItem = item;
        }

        private bool IsItemSelected(TreeViewItem item)
        {
            return _selectedItems.Contains(item);
        }

        private void UnselectItem(TreeViewItem item)
        {
            if (item == null) return;

            // Remove visual selection with animation
            AnimateSelection(item, false);

            if (_selectedItems.Contains(item))
            {
                _selectedItems.Remove(item);
            }
        }

        private void ClearSelectedItems()
        {
            foreach (var item in _selectedItems.ToList())
            {
                AnimateSelection(item, false);
            }

            _selectedItems.Clear();
        }

        #endregion 

        #region Selection Management

        private void SelectItemRange(TreeViewItem start, TreeViewItem end)
        {
            // Get all visible tree view items in display order
            var allItems = GetAllVisibleTreeViewItems();

            // Find the indices of start and end items
            int startIndex = allItems.IndexOf(start);
            int endIndex = allItems.IndexOf(end);

            if (startIndex == -1 || endIndex == -1) return;

            // Ensure startIndex <= endIndex for proper range selection
            if (startIndex > endIndex)
            {
                int temp = startIndex;
                startIndex = endIndex;
                endIndex = temp;
            }

            // Clear previous selection
            ClearSelectedItems();

            // Select all items in the range (inclusive)
            for (int i = startIndex; i <= endIndex; i++)
            {
                SelectItem(allItems[i]);
            }

            // Update last selected item to the end of range
            _lastSelectedItem = end;
        }

        private List<TreeViewItem> GetAllVisibleTreeViewItems()
        {
            var result = new List<TreeViewItem>(256);
            CollectExpanded(ShellTreeViewControl.Items, result);
            return result;
        }

        private static void CollectExpanded(ItemCollection items, List<TreeViewItem> result)
        {
            foreach (var obj in items)
            {
                if (!(obj is TreeViewItem item)) continue;
                if (item.Tag as string == "__PLACEHOLDER__") continue;
                result.Add(item);
                if (item.IsExpanded && item.Items.Count > 0)
                    CollectExpanded(item.Items, result);
            }
        }

        private void CollectVisibleItems(ItemCollection items, List<TreeViewItem> result)
        {
            foreach (var item in items)
            {
                if (item is TreeViewItem tvi)
                {
                    result.Add(tvi);
                    if (tvi.IsExpanded && tvi.Items.Count > 0)
                        CollectVisibleItems(tvi.Items, result);
                }
            }
        }

        public bool CollapseDirectory(string directoryPath)
        {
            try
            {
                // Normalize the path
                directoryPath = PathService.NormalizePath(directoryPath);

                // Check if the path exists
                if (!PathService.DirectoryExists(directoryPath))
                {
                    Debug.WriteLine($"Cannot collapse directory - path does not exist: {directoryPath}");
                    return false;
                }

                // Try to find the TreeViewItem corresponding to this directory
                if (_pathToTreeViewItem.TryGetValue(directoryPath, out var treeViewItem))
                {
                    // If found, collapse it
                    treeViewItem.IsExpanded = false;

                    // Bring the collapsed item into view
                    treeViewItem.BringIntoView();

                    Debug.WriteLine($"Successfully collapsed directory: {directoryPath}");
                    return true;
                }
                else
                {
                    // If not found in the dictionary, try to search for it
                    Debug.WriteLine($"Directory not found in path mapping, attempting to search: {directoryPath}");

                    // Search for the item in the tree view
                    TreeViewItem foundItem = FindTreeViewItemByPath(directoryPath);

                    if (foundItem != null)
                    {
                        // If found, collapse it
                        foundItem.IsExpanded = false;

                        // Bring the collapsed item into view
                        foundItem.BringIntoView();

                        Debug.WriteLine($"Successfully found and collapsed directory: {directoryPath}");
                        return true;
                    }

                    Debug.WriteLine($"Failed to find directory in tree view: {directoryPath}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error collapsing directory: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Finds all folders whose name contains <paramref name="keyword"/> (case-insensitive).
        /// Traversal order follows the tree's natural top-to-bottom order (pre-order DFS).
        /// </summary>
        public async Task<List<string>> FindFoldersByNameAsync(string keyword, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(keyword))
                return new List<string>();

            string root = _rootDirectory;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                return new List<string>();

            string normalizedKeyword = keyword.Trim();

            // Collect results on background thread using filesystem DFS,
            // but sorted strictly with StrCmpLogicalW to match Tree View order.
            return await Task.Run(() =>
            {
                var results = new List<string>();
                TraverseForFind(root, normalizedKeyword, results, cancellationToken);
                return results;
            }, cancellationToken);
        }

        /// <summary>
        /// Recursive pre-order DFS that sorts children with StrCmpLogicalW,
        /// exactly matching the Tree View's display order.
        /// </summary>
        private void TraverseForFind(string path, string keyword, List<string> results, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            string[] children;
            try
            {
                children = Directory.GetDirectories(path);
            }
            catch (UnauthorizedAccessException) { return; }
            catch (DirectoryNotFoundException) { return; }
            catch (PathTooLongException) { return; }
            catch (IOException) { return; }

            // Sort with StrCmpLogicalW — identical to FolderNode.EnumerateChildren and Tree View
            Array.Sort(children, (a, b) =>
                WindowsNaturalStringComparer.Instance.Compare(
                    Path.GetFileName(a),
                    Path.GetFileName(b)));

            foreach (var child in children)
            {
                ct.ThrowIfCancellationRequested();

                // Skip hidden / system folders (same rule as FolderNode.EnumerateChildren)
                try
                {
                    var attrs = File.GetAttributes(child);
                    if ((attrs & FileAttributes.Hidden) != 0 ||
                        (attrs & FileAttributes.System) != 0)
                        continue;
                }
                catch { continue; }

                string normalizedChild = PathService.NormalizePath(child);
                string name = Path.GetFileName(normalizedChild);

                // Check match before recursing — pre-order means parent before children
                if (name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                    results.Add(normalizedChild);

                // Recurse into subtree
                TraverseForFind(normalizedChild, keyword, results, ct);
            }
        }

        public List<string> FindFoldersByName(string keyword)
        {
            try
            {
                return FindFoldersByNameAsync(keyword, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch
            {
                return new List<string>();
            }
        }

        /// <summary>
        /// Navigates to the given path: expands parents, selects and scrolls the item into view.
        /// </summary>
        public async Task<bool> NavigateToPathAsync(string path, CancellationToken cancellationToken = default, bool promptToChangeRoot = false, bool centerInView = false)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            string normalizedPath = PathService.NormalizePath(path);
            if (!PathService.DirectoryExists(normalizedPath))
                return false;

            try
            {
                bool isWithinTree = IsPathWithinTreeScope(normalizedPath);
                if (!isWithinTree)
                {
                    if (!promptToChangeRoot)
                        return false;

                    var result = MessageBox.Show(
                        $"The selected path '{normalizedPath}' is not within the current tree view. Do you want to change the root directory to this path?",
                        "Change Root Directory",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        ChangeRootDirectory(normalizedPath);
                        return true;
                    }

                    return false;
                }

                await ExpandPathToFolderAsync(normalizedPath, cancellationToken);

                for (int attempt = 0; attempt < 40; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (_pathToTreeViewItem.TryGetValue(normalizedPath, out var treeViewItem))
                    {
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            ClearSelectedItems();
                            SelectItem(treeViewItem);
                            NotifyFolderSelectionWithoutLoading(treeViewItem);
                            if (centerInView)
                                treeViewItem.BringIntoView();
                        }, DispatcherPriority.Normal);

                        if (centerInView)
                        {
                            await Application.Current.Dispatcher.InvokeAsync(() =>
                                ScrollToCenter(treeViewItem),
                                DispatcherPriority.Background);
                        }

                        return true;
                    }

                    var parentPath = Path.GetDirectoryName(normalizedPath);
                    if (!string.IsNullOrEmpty(parentPath) && _pathToTreeViewItem.TryGetValue(PathService.NormalizePath(parentPath), out var parentItem))
                    {
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            parentItem.IsExpanded = true;
                        });
                    }

                    await Task.Delay(50, cancellationToken);
                }

                return false;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                HandleException("Error navigating to path", ex, false);
                return false;
            }
        }

        public void NavigateToPath(string path)
        {
            _ = NavigateToPathAsync(path);
        }
        private TreeViewItem FindTreeViewItemByPath(string path)
        {
            // Normalize the path
            path = PathService.NormalizePath(path);

            // First check in our dictionary
            if (_pathToTreeViewItem.TryGetValue(path, out var item))
            {
                return item;
            }

            // If not found in dictionary, search recursively through the tree view
            foreach (var rootItem in ShellTreeViewControl.Items)
            {
                var treeViewItem = rootItem as TreeViewItem;
                if (treeViewItem != null)
                {
                    var result = FindTreeViewItemByPathRecursive(treeViewItem, path);
                    if (result != null)
                    {
                        return result;
                    }
                }
            }

            return null;
        }

        private TreeViewItem FindTreeViewItemByPathRecursive(TreeViewItem parentItem, string path)
        {
            // Check if this is the item we're looking for
            if (parentItem.Tag is FolderNode folderNode)
            {
                string itemPath = folderNode.FullPath;
                if (PathService.PathsEqual(itemPath, path))
                {
                    return parentItem;
                }
            }

            // If this item is not expanded, we can't search its children
            if (!parentItem.IsExpanded)
            {
                return null;
            }

            // Search through all children
            foreach (var childObj in parentItem.Items)
            {
                var childItem = parentItem.ItemContainerGenerator.ContainerFromItem(childObj) as TreeViewItem;
                if (childItem != null)
                {
                    var result = FindTreeViewItemByPathRecursive(childItem, path);
                    if (result != null)
                    {
                        return result;
                    }
                }
            }

            return null;
        }

        private void SelectAllVisibleItems()
        {
            const int MAX_SELECT = 200;
            var allVisible = GetAllVisibleTreeViewItems();
            ClearSelectedItems();

            int count = 0;
            foreach (var item in allVisible)
            {
                if (count++ >= MAX_SELECT) break;
                SelectItem(item);
            }

            if (allVisible.Count > MAX_SELECT)
                Debug.WriteLine($"SelectAll limited to {MAX_SELECT} of {allVisible.Count} items");

        }

        #endregion

        #region Drag & Drop Support
        private TreeViewItem GetTreeViewItemUnderMouse(Point mousePosition)
        {
            HitTestResult result = VisualTreeHelper.HitTest(ShellTreeViewControl, mousePosition);

            if (result != null)
            {
                DependencyObject obj = result.VisualHit;

                while (obj != null && !(obj is TreeViewItem))
                {
                    obj = VisualTreeHelper.GetParent(obj);
                }

                return obj as TreeViewItem;
            }

            return null;
        }

        private T FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null && !(current is T))
            {
                current = VisualTreeHelper.GetParent(current);
            }
            return current as T;
        }

        private TreeViewItem FindParentTreeViewItem(TreeViewItem item)
        {
            DependencyObject parent = VisualTreeHelper.GetParent(item);
            while (parent != null && !(parent is TreeViewItem))
            {
                parent = VisualTreeHelper.GetParent(parent);
            }
            return parent as TreeViewItem;
        }

        private void HandleException(string operation, Exception ex, bool showMessageBox = true)
        {
            Debug.WriteLine($"{operation}: {ex.Message}");

            if (showMessageBox)
            {
                MessageBox.Show($"{operation}: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Event Handlers

        private void TreeViewItem_Expanded(object sender, RoutedEventArgs e)
        {
            if (!(sender is TreeViewItem item)) return;
            if (!(item.Tag is FolderNode node)) return;

            // Record for state restoration
            _expandedPaths.Add(node.FullPath);

            // Only trigger a load if the item still contains just the placeholder
            if (!FolderTreeItemFactory.HasOnlyPlaceholder(item)) return;

            e.Handled = true; // Don't bubble to parent items

            // Fire and forget — any exceptions are caught inside ExpandNodeAsync
            _ = ExpandNodeAsync(item, node);
        }

        // ── TreeViewItem_Collapsed (new — wire to Collapsed event in XAML) ────
        // Cancels any in-flight load when the user collapses a node quickly.

        private void TreeViewItem_Collapsed(object sender, RoutedEventArgs e)
        {
            if (!(sender is TreeViewItem item)) return;

            if (_expansionCts.TryGetValue(item, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
                _expansionCts.Remove(item);
            }
        }

        // ── Core expansion logic ──────────────────────────────────────────────

        private async Task ExpandNodeAsync(TreeViewItem parentItem, FolderNode parentNode)
        {
            // Cancel any previous in-flight expansion of this exact item
            if (_expansionCts.TryGetValue(parentItem, out var old))
            {
                old.Cancel();
                old.Dispose();
            }
            var cts = new CancellationTokenSource();
            _expansionCts[parentItem] = cts;

            ShowLoadingIndicator();
            try
            {
                await FolderTreeItemFactory.ExpandAsync(
                    parentItem, parentNode, _pathToTreeViewItem, cts.Token);
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine($"Expansion cancelled: {parentNode.FullPath}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ExpandNodeAsync error: {ex.Message}");
            }
            finally
            {
                HideLoadingIndicator();
                if (_expansionCts.ContainsKey(parentItem))
                {
                    _expansionCts.Remove(parentItem);
                    cts.Dispose();
                }
            }
        }

        private void ShellTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            // This event is still useful for keyboard navigation
            var treeViewItem = e.NewValue as TreeViewItem;
            if (treeViewItem == null) return;

            // Only process if no MultiSelect active (e.g., keyboard navigation)
            if (!_isMultiSelectActive && Keyboard.Modifiers == ModifierKeys.None)
            {
                ClearSelectedItems();
                SelectItem(treeViewItem);

                NotifyFolderSelection(treeViewItem);
            }
        }

        private void TreeView_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // Handle double-click to load images
            if (e.ClickCount == 2 && e.ChangedButton == MouseButton.Left)
            {
                var hitTestResult = VisualTreeHelper.HitTest(ShellTreeViewControl, e.GetPosition(ShellTreeViewControl));
                if (hitTestResult != null)
                {
                    var treeViewItem = FindAncestor<TreeViewItem>(hitTestResult.VisualHit);
                    if (treeViewItem != null)
                    {
                        HandleFolderDoubleClick(treeViewItem);
                        e.Handled = true;
                        return;
                    }
                }
            }

            // Regular single-click handling
            if (e.ChangedButton == MouseButton.Left)
            {
                // Get the TreeViewItem under the mouse
                var hitTestResult = VisualTreeHelper.HitTest(ShellTreeViewControl, e.GetPosition(ShellTreeViewControl));
                if (hitTestResult != null)
                {
                    var treeViewItem = FindAncestor<TreeViewItem>(hitTestResult.VisualHit);
                    if (treeViewItem != null)
                    {
                        // Handle selection based on modifier keys
                        ModifierKeys modifiers = Keyboard.Modifiers;
                        _isMultiSelectActive = modifiers != ModifierKeys.None;

                        if (modifiers == ModifierKeys.Control)
                        {
                            // CTRL+Click: Toggle selection of the clicked item
                            if (IsItemSelected(treeViewItem))
                            {
                                UnselectItem(treeViewItem);
                                // If we unselected the last selected item, find a new one
                                if (_lastSelectedItem == treeViewItem)
                                {
                                    _lastSelectedItem = _selectedItems.Count > 0 ?
                                        _selectedItems.Last() : null;
                                }
                            }
                            else
                            {
                                SelectItem(treeViewItem);
                                _lastSelectedItem = treeViewItem;
                            }

                            // Notify about multi-selection change
                            NotifyMultiSelectionChanged();
                            e.Handled = true;
                        }
                        else if (modifiers == ModifierKeys.Shift && _lastSelectedItem != null)
                        {
                            // SHIFT+Click: Select range between last selected item and current item
                            SelectItemRange(_lastSelectedItem, treeViewItem);
                            NotifyMultiSelectionChanged();
                            e.Handled = true;
                        }
                        else if (modifiers == ModifierKeys.None)
                        {
                            // Single selection behavior
                            bool wasAlreadySelected = IsItemSelected(treeViewItem);

                            // Key fix: If the item was already selected and is part of a multi-selection,
                            // keep all selections intact. Only clear selection if clicking on a non-selected item.
                            if (!wasAlreadySelected)
                            {
                                ClearSelectedItems();
                                SelectItem(treeViewItem);
                                _lastSelectedItem = treeViewItem;

                                // Notify about the selection change
                                NotifyFolderSelectionWithoutLoading(treeViewItem);
                            }
                            else if (_selectedItems.Count == 1)
                            {
                                // If only one item is selected, it's a simple selection
                                // Ensure the item is selected (redundant but for clarity)
                                SelectItem(treeViewItem);
                                _lastSelectedItem = treeViewItem;

                                // Notify about the selection change
                                NotifyFolderSelectionWithoutLoading(treeViewItem);
                            }
                            // If it's already part of a multi-selection, do nothing to preserve the selection

                            // Don't mark as handled to allow drag operations
                        }

                        _isMultiSelectActive = false;
                    }
                    else
                    {
                        // Clicked on empty space - clear selection
                        if (Keyboard.Modifiers == ModifierKeys.None)
                        {
                            ClearSelectedItems();
                            _lastSelectedItem = null;
                            NotifyMultiSelectionChanged();
                        }
                    }
                }
            }
        }

        private void HandleFolderDoubleClick(TreeViewItem treeViewItem)
        {
            try
            {
                var folderNode = treeViewItem.Tag as FolderNode;
                if (folderNode == null) return;

                string path = folderNode.FullPath;
                if (string.IsNullOrEmpty(path)) return;

                // Make sure the item is selected
                if (!IsItemSelected(treeViewItem))
                {
                    ClearSelectedItems();
                    SelectItem(treeViewItem);
                    _lastSelectedItem = treeViewItem;
                }

                // Create FolderInfo and load images
                var folderInfo = new FolderInfo(path);
                LoadImagesForFolder(folderInfo);
            }
            catch (Exception ex)
            {
                HandleException("Error handling folder double-click", ex);
            }
        }

        private void NotifyFolderSelection(TreeViewItem treeViewItem)
        {
            try
            {
                var folderNode = treeViewItem.Tag as FolderNode;
                if (folderNode == null) return;

                string path = folderNode.FullPath;
                if (!PathService.DirectoryExists(path)) return;

                _selectedPath = path;

                Debug.WriteLine($"Selected folder: {path}");

                // Create a FolderInfo for the selected path
                var folderInfo = new FolderInfo(path);

                // Only update status message if we're not in a multi-selection state
                // This prevents overriding the multi-selection status message
                if (ViewModel != null)
                {
                    // Check if we're in a multi-selection state
                    if (_selectedItems.Count <= 1)
                    {
                        ViewModel.StatusMessage = $"Selected: {folderInfo.Name} ({path})";
                    }
                }

                // Notify listeners
                FolderSelected?.Invoke(folderInfo);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in folder selection notification: {ex.Message}");
            }
        }

        private void NotifyFolderSelectionWithoutLoading(TreeViewItem treeViewItem)
        {
            try
            {
                var folderNode = treeViewItem.Tag as FolderNode;
                if (folderNode == null) return;

                string path = folderNode.FullPath;
                if (string.IsNullOrEmpty(path)) return;

                _selectedPath = path;

                Debug.WriteLine($"Selected folder (without loading images): {path}");

                // Create a FolderInfo for the selected path
                var folderInfo = new FolderInfo(path);

                // Only update status message if we're not in a multi-selection state
                if (ViewModel != null)
                {
                    // Check if we're in a multi-selection state before updating the status message
                    if (_selectedItems.Count <= 1)
                    {
                        ViewModel.StatusMessage = $"Selected: {folderInfo.Name} ({path})";
                    }

                    ViewModel.SetSelectedFolderWithoutLoading(folderInfo);
                }
                else
                {
                    // Fallback to regular notification
                    FolderSelected?.Invoke(folderInfo);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in folder selection notification: {ex.Message}");
            }
        }

        private void NotifyMultiSelectionChanged()
        {
            if (ViewModel != null)
            {
                if (_selectedItems.Count == 1)
                {
                    // Single item selected - use regular notification
                    NotifyFolderSelectionWithoutLoading(_selectedItems[0]);
                }
                else if (_selectedItems.Count > 1)
                {
                    // Multiple items selected - update status message with the new format
                    var lastSelectedItem = _selectedItems.Last();
                    var folderNode = lastSelectedItem.Tag as FolderNode;

                    if (folderNode != null)
                    {
                        string path = folderNode.FullPath;
                        string lastFolderName = Path.GetFileName(path);

                        // First clear the selected folder in ViewModel (this will trigger image clearing)
                        ViewModel.SetSelectedFolderWithoutLoading(null);

                        // Then update status message with our custom format - this will override "Images cleared"
                        ViewModel.StatusMessage = $"A total of {_selectedItems.Count} folders, including {lastFolderName}, are selected.";
                    }
                    else
                    {
                        // Fallback to original message if we can't get the last folder name
                        ViewModel.SetSelectedFolderWithoutLoading(null);
                        ViewModel.StatusMessage = $"Selected {_selectedItems.Count} folders";
                    }
                }
                else
                {
                    // No items selected
                    ViewModel.StatusMessage = "No folders selected";
                    ViewModel.SetSelectedFolderWithoutLoading(null);
                }
            }
        }

        private void LoadImagesForFolder(FolderInfo folder)
        {
            if (folder == null) return;

            if (ViewModel != null)
            {
                // Call the ViewModel method to load images
                _ = ViewModel.LoadImagesForSelectedFolderAsync();
            }
            else
            {
                // Fallback to just selecting folder if ViewModel is not available
                FolderSelected?.Invoke(folder);
            }
        }

        private void ShellTreeViewControl_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            Debug.WriteLine("Context menu opening");

            // Get the current tree view item under cursor
            Point position = Mouse.GetPosition(ShellTreeViewControl);
            var item = GetTreeViewItemUnderMouse(position);

            // Get the selected folders
            var selectedFolders = GetSelectedFolderInfos();

            // Keyboard-triggered context menu or stale selection fallback
            if (selectedFolders.Count == 0 && item?.Tag is FolderNode node && PathService.DirectoryExists(node.FullPath))
            {
                selectedFolders.Add(new FolderInfo(node.FullPath));
            }

            if (selectedFolders.Count == 0)
            {
                e.Handled = true;
                return;
            }
            // Create context menu
            var contextMenu = new ContextMenu();

            // Add "Load Images" option for single selection
            if (selectedFolders.Count == 1)
            {
                var loadImagesItem = new MenuItem { Header = "Load Images", InputGestureText = "Double-click" };
                loadImagesItem.Click += (s, args) => {
                    Debug.WriteLine("Load Images clicked");
                    LoadImagesForFolder(selectedFolders[0]);
                };
                contextMenu.Items.Add(loadImagesItem);
                contextMenu.Items.Add(new Separator());
            }

            // Add menu items for both single and multi-selection
            if (selectedFolders.Count == 1)
            {
                // Single selection menu
                var newFolderItem = new MenuItem { Header = "New Folder", InputGestureText = "Ctrl+N" };
                newFolderItem.Click += (s, args) => {
                    Debug.WriteLine("New Folder clicked");
                    NewFolder_Click(s, args);
                };
                contextMenu.Items.Add(newFolderItem);

                // New Sibling Folder — disabled when selected node IS the root
                var newSiblingFolderItem = new MenuItem { Header = "New Sibling Folder" };
                newSiblingFolderItem.IsEnabled =
                    !string.IsNullOrEmpty(_rootDirectory) &&
                    !PathService.PathsEqual(selectedFolders[0].FolderPath, _rootDirectory);
                newSiblingFolderItem.Click += (s, args) => {
                    Debug.WriteLine("New Sibling Folder clicked");
                    NewSiblingFolder_Click(s, args);
                };
                contextMenu.Items.Add(newSiblingFolderItem);
            }

            if (selectedFolders.Count > 1)
            {
                // Add separator before batch operations
                contextMenu.Items.Add(new Separator());

                // Add "Batch Tags" option
                var batchTagsItem = new MenuItem { Header = "Batch Tags..." };
                batchTagsItem.Click += (s, args) => {
                    Debug.WriteLine("Batch Tags clicked");
                    BatchTags_Click(s, args);
                };
                contextMenu.Items.Add(batchTagsItem);
            }

            // Common operations for both single and multi-selections
            var cutItem = new MenuItem { Header = "Cut", InputGestureText = "Ctrl+X" };
            cutItem.Click += (s, args) => {
                Debug.WriteLine("Cut clicked");
                MultiFolderCut_Click(s, args);
            };
            contextMenu.Items.Add(cutItem);

            var copyItem = new MenuItem { Header = "Copy", InputGestureText = "Ctrl+C" };
            copyItem.Click += (s, args) => {
                Debug.WriteLine("Copy clicked");
                MultiFolderCopy_Click(s, args);
            };
            contextMenu.Items.Add(copyItem);

            var pasteItem = new MenuItem { Header = "Paste", InputGestureText = "Ctrl+V" };
            pasteItem.Click += (s, args) => {
                Debug.WriteLine("Paste clicked");
                Paste_Click(s, args);
            };
            pasteItem.IsEnabled = ViewModel != null && ViewModel.HasClipboardContent();
            contextMenu.Items.Add(pasteItem);

            contextMenu.Items.Add(new Separator());


            if (selectedFolders.Count == 1)
            {
                // Show in Explorer only for single selection
                var showItem = new MenuItem { Header = "Show in Explorer" };
                showItem.Click += (s, args) => {
                    Debug.WriteLine("Show in Explorer clicked");
                    ShowInExplorer_Click(s, args);
                };
                contextMenu.Items.Add(showItem);
            }

            var deleteItemText = selectedFolders.Count > 1 ? $"Delete ({selectedFolders.Count} items)" : "Delete";
            var deleteItem = new MenuItem { Header = deleteItemText, InputGestureText = "Delete" };
            deleteItem.Click += (s, args) => {
                Debug.WriteLine("Delete clicked");
                MultiFolderDelete_Click(s, args);
            };
            contextMenu.Items.Add(deleteItem);

            contextMenu.Items.Add(new Separator());

            if (selectedFolders.Count == 1)
            {
                // Single selection specific actions
                var renameItem = new MenuItem { Header = "Rename", InputGestureText = "F2" };
                renameItem.Click += (s, args) => {
                    Debug.WriteLine("Rename clicked");
                    Rename_Click(s, args);
                };
                contextMenu.Items.Add(renameItem);
            }

            // Set the context menu
            ShellTreeViewControl.ContextMenu = contextMenu;
        }

        private void StartDrag(MouseEventArgs e)
        {
            // For multi-selection, we'll need to handle dragging multiple items
            if (_selectedItems.Count <= 0) return;

            // Add an additional check to prevent accidental drags
            Point currentPosition = e.GetPosition(null);
            double distance = Math.Sqrt(
                Math.Pow(currentPosition.X - _startPoint.X, 2) +
                Math.Pow(currentPosition.Y - _startPoint.Y, 2));

            // Only proceed if distance is significant
            if (distance < SystemParameters.MinimumHorizontalDragDistance * 2)
            {
                return;
            }

            _isDragging = true;

            // Collect paths from all selected items
            var paths = new List<string>();

            foreach (var item in _selectedItems)
            {
                var folderNode = item.Tag as FolderNode;
                if (folderNode != null)
                {
                    string path = folderNode.FullPath;
                    if (PathService.DirectoryExists(path))
                    {
                        // Don't allow dragging the root directory
                        if (!string.IsNullOrEmpty(_rootDirectory) &&
                            PathService.PathsEqual(path, _rootDirectory))
                        {
                            continue;
                        }

                        paths.Add(path);
                    }
                }
            }

            if (paths.Count > 0)
            {
                // Set reference to the first item for visual dragging effect
                if (_selectedItems.Count > 0)
                {
                    _draggedItem = _selectedItems[0];
                    _draggedFolderNode = _draggedItem.Tag as FolderNode;
                }

                // Create drag data with all selected paths
                DataObject dragData = new DataObject("FileDrop", paths.ToArray());
                DragDrop.DoDragDrop(ShellTreeViewControl, dragData, DragDropEffects.Move | DragDropEffects.Copy);
            }
        }


        private void ShellTreeView_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete)
            {

                MultiFolderDelete_Click(sender, new RoutedEventArgs());

                e.Handled = true;
            }
            else if (e.Key == Key.F2)
            {
                if (_selectedItems.Count == 1)
                {
                    Rename_Click(sender, new RoutedEventArgs());
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.X && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (_selectedItems.Count > 0)
                {
                    MultiFolderCut_Click(sender, new RoutedEventArgs());
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (_selectedItems.Count > 0)
                {
                    MultiFolderCopy_Click(sender, new RoutedEventArgs());
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                Paste_Click(sender, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                // CTRL+A: Select all visible items
                SelectAllVisibleItems();
                NotifyMultiSelectionChanged();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                // ESC: Clear selection
                ClearSelectedItems();
                _lastSelectedItem = null;
                NotifyMultiSelectionChanged();
                e.Handled = true;
            }
        }

        private void TreeView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Store the mouse position for potential drag operation
            _startPoint = e.GetPosition(null);
            _mouseDownTime = DateTime.Now; // Record when mouse was pressed

            // Handle multi-selection
            TreeView_PreviewMouseDown(sender, e);
        }

        private void TreeView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var hitTestResult = VisualTreeHelper.HitTest(ShellTreeViewControl, e.GetPosition(ShellTreeViewControl));
            if (hitTestResult == null) return;

            var treeViewItem = FindAncestor<TreeViewItem>(hitTestResult.VisualHit);
            if (treeViewItem == null) return;

            // Preserve existing multi-selection when right-clicking outside it.
            if (_selectedItems.Count > 1 && !IsItemSelected(treeViewItem))
                return;

            if (!IsItemSelected(treeViewItem))
            {
                ClearSelectedItems();
                SelectItem(treeViewItem);
                _lastSelectedItem = treeViewItem;
                NotifyFolderSelectionWithoutLoading(treeViewItem);
            }
        }

        private void TreeView_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && !_isDragging)
            {
                // Calculate time since mouse button was pressed
                TimeSpan timeSinceMouseDown = DateTime.Now - _mouseDownTime;

                // Only start drag if mouse has been pressed for at least DRAG_DELAY_MS milliseconds
                if (timeSinceMouseDown.TotalMilliseconds >= DRAG_DELAY_MS)
                {
                    Point position = e.GetPosition(null);

                    // Increase drag distance threshold by multiplying system parameters
                    double horizontalThreshold = SystemParameters.MinimumHorizontalDragDistance * DRAG_DISTANCE_MULTIPLIER;
                    double verticalThreshold = SystemParameters.MinimumVerticalDragDistance * DRAG_DISTANCE_MULTIPLIER;

                    // Check if the mouse has moved far enough to initiate drag
                    if (Math.Abs(position.X - _startPoint.X) > horizontalThreshold ||
                        Math.Abs(position.Y - _startPoint.Y) > verticalThreshold)
                    {
                        // Make sure we're actually over a draggable item
                        var item = GetTreeViewItemUnderMouse(position);
                        if (item != null && item.Tag is FolderNode)
                        {
                            StartDrag(e);
                        }
                    }
                }
            }
        }

        private void TreeView_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // Release any drag operation
            if (_isDragging)
            {
                _isDragging = false;
                e.Handled = true;
            }
            else if (e.ChangedButton == MouseButton.Left)
            {
                // If not dragging, this might be a regular click on a selected item
                // Let's check if we clicked on an already selected item
                var hitTestResult = VisualTreeHelper.HitTest(ShellTreeViewControl, e.GetPosition(ShellTreeViewControl));
                if (hitTestResult != null)
                {
                    var treeViewItem = FindAncestor<TreeViewItem>(hitTestResult.VisualHit);
                    if (treeViewItem != null && IsItemSelected(treeViewItem) && _selectedItems.Count > 1)
                    {
                        // This is a click on an already selected item in a multi-selection
                        // We need to trigger the notification since we didn't clear other selections
                        NotifyFolderSelectionWithoutLoading(treeViewItem);
                    }
                }
            }
        }


        #endregion

        #region Context Menu Action Handlers

        /// <summary>
        /// Checks if there are any selected items in the tree view
        /// </summary>
        /// <returns>True if there are selected items</returns>
        public bool HasSelectedItems()
        {
            return _selectedItems != null && _selectedItems.Count > 0;
        }

        private void NewFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Debug.WriteLine("NewFolder_Click handler called");

                var treeViewItem = GetSelectedTreeViewItem();
                if (treeViewItem == null)
                {
                    Debug.WriteLine("No TreeViewItem selected");
                    return;
                }

                var folderNode = treeViewItem.Tag as FolderNode;
                if (folderNode == null)
                {
                    Debug.WriteLine("Selected item has no FolderNode");
                    return;
                }

                string path = folderNode.FullPath;
                if (!PathService.DirectoryExists(path))
                {
                    Debug.WriteLine($"Invalid path: {path}");
                    return;
                }
                //string parentPath = Path.GetDirectoryName(path);
                // Create FolderInfo and call ViewModel
                var folderInfo = new FolderInfo(path);

                if (ViewModel != null)
                {
                    _ = ViewModel.CreateNewFolder(folderInfo);

                }
                else
                {
                    Debug.WriteLine("ViewModel is null");
                    MessageBox.Show("Could not create folder: ViewModel is not available.",
                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }


            }
            catch (Exception ex)
            {
                HandleException("Error creating new folder", ex);
            }
        }

        private void NewSiblingFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Debug.WriteLine("NewSiblingFolder_Click handler called");

                var treeViewItem = GetSelectedTreeViewItem();
                if (treeViewItem == null)
                {
                    Debug.WriteLine("No TreeViewItem selected");
                    return;
                }

                var folderNode = treeViewItem.Tag as FolderNode;
                if (folderNode == null)
                {
                    Debug.WriteLine("Selected item has no FolderNode");
                    return;
                }

                string currentPath = folderNode.FullPath;
                if (!PathService.DirectoryExists(currentPath))
                {
                    Debug.WriteLine($"Invalid path: {currentPath}");
                    return;
                }

                // Cannot create sibling of root
                if (!string.IsNullOrEmpty(_rootDirectory) &&
                    PathService.PathsEqual(currentPath, _rootDirectory))
                {
                    MessageBox.Show("Cannot create a sibling folder for the root directory.",
                        "Operation Not Allowed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string parentPath = Path.GetDirectoryName(currentPath);
                if (string.IsNullOrEmpty(parentPath) || !PathService.DirectoryExists(parentPath))
                {
                    Debug.WriteLine($"Cannot resolve parent path for: {currentPath}");
                    return;
                }

                // Reuse existing CreateNewFolder logic with the parent as the target
                var parentFolderInfo = new FolderInfo(parentPath);

                if (ViewModel != null)
                {
                    _ = ViewModel.CreateNewFolder(parentFolderInfo);
                }
                else
                {
                    Debug.WriteLine("ViewModel is null");
                    MessageBox.Show("Could not create folder: ViewModel is not available.",
                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                HandleException("Error creating sibling folder", ex);
            }
        }

        private void BatchTags_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Debug.WriteLine("BatchTags_Click handler called");

                var selectedFolders = GetSelectedFolderInfos();
                if (selectedFolders.Count <= 1) return;

                if (ViewModel != null)
                {
                    Debug.WriteLine($"Calling ViewModel.BatchUpdateTags for {selectedFolders.Count} folders");
                    _ = ViewModel.BatchUpdateTags(selectedFolders);
                }
                else
                {
                    Debug.WriteLine("ViewModel is null");
                    MessageBox.Show("Could not perform batch tag operation: ViewModel is not available.",
                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                HandleException("Error performing batch tag operation", ex);
            }
        }

        public void MultiFolderCut_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selectedFolders = GetSelectedFolderInfos();
                if (selectedFolders.Count == 0) return;

                if (ViewModel != null)
                {
                    ViewModel.CutFolders(selectedFolders);
                }
                else
                {
                    MessageBox.Show("Could not cut folders: ViewModel is not available.",
                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                HandleException("Error cutting folders", ex);
            }
        }

        public void MultiFolderCopy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selectedFolders = GetSelectedFolderInfos();
                if (selectedFolders.Count == 0) return;

                if (ViewModel != null)
                {
                    ViewModel.CopyFolders(selectedFolders);
                }
                else
                {
                    MessageBox.Show("Could not copy folders: ViewModel is not available.",
                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                HandleException("Error copying folders", ex);
            }
        }

        public void Paste_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Debug.WriteLine("Paste_Click handler called");

                var treeViewItem = GetSelectedTreeViewItem();
                if (treeViewItem == null) return;

                var folderNode = treeViewItem.Tag as FolderNode;
                if (folderNode == null) return;

                string path = folderNode.FullPath;
                if (!PathService.DirectoryExists(path)) return;

                // Store expanded state
                var expandedItems = new HashSet<string>();
                foreach (var item in FindVisualChildren<TreeViewItem>(ShellTreeViewControl))
                {
                    if (item.IsExpanded && item.Tag is FolderNode so)
                    {
                        string expandedPath = so.FullPath;
                        if (!string.IsNullOrEmpty(expandedPath))
                        {
                            expandedItems.Add(expandedPath);
                        }
                    }
                }

                // Create target folder FolderInfo
                var folderInfo = new FolderInfo(path);

                if (ViewModel != null)
                {
                    Debug.WriteLine($"Calling ViewModel.PasteFolder for {path}");

                    if (ViewModel.HasClipboardContent())
                    {
                        // Get the source directory before the paste operation
                        string sourceDir = ViewModel.GetClipboardSourceDirectory();

                        // Execute paste operation
                        _ = ViewModel.PasteFolders(folderInfo).ContinueWith(t =>
                        {
                            if (t.Exception != null) HandleException("Paste failed", t.Exception);
                        });


                    }
                    else
                    {
                        Debug.WriteLine("No clipboard content available");
                        MessageBox.Show("No folder is currently in clipboard. Please copy or cut a folder first.",
                            "Paste Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                else
                {
                    Debug.WriteLine("ViewModel is null");
                    MessageBox.Show("Could not paste folder: ViewModel is not available.",
                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                HandleException("Error pasting folder", ex);
            }
        }

        private void Rename_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Debug.WriteLine("Rename_Click handler called");

                var treeViewItem = GetSelectedTreeViewItem();
                if (treeViewItem == null) return;

                var folderNode = treeViewItem.Tag as FolderNode;
                if (folderNode == null) return;

                string path = folderNode.FullPath;
                if (!PathService.DirectoryExists(path)) return;

                // Don't allow renaming root directory
                if (!string.IsNullOrEmpty(_rootDirectory) &&
                    PathService.PathsEqual(path, _rootDirectory))
                {
                    Debug.WriteLine("Cannot rename root directory");
                    MessageBox.Show("Cannot rename the root directory.",
                        "Rename Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Save old path and tree view item
                string oldPath = path;
                var oldItem = treeViewItem;
                bool wasExpanded = oldItem.IsExpanded;
                var parentItem = FindParentTreeViewItem(oldItem);

                // Create FolderInfo and call ViewModel
                var folderInfo = new FolderInfo(path);

                if (ViewModel != null)
                {
                    Debug.WriteLine($"Calling ViewModel.RenameFolder for {path}");

                    // Execute rename operation through ViewModel
                    _ = ViewModel.RenameFolder(folderInfo);
                }
                else
                {
                    Debug.WriteLine("ViewModel is null");
                    MessageBox.Show("Could not rename folder: ViewModel is not available.",
                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                HandleException("Error renaming folder", ex);
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Debug.WriteLine("Delete_Click handler called");

                var treeViewItem = GetSelectedTreeViewItem();
                if (treeViewItem == null) return;

                var folderNode = treeViewItem.Tag as FolderNode;
                if (folderNode == null) return;

                string path = folderNode.FullPath;
                if (!PathService.DirectoryExists(path)) return;

                if (!string.IsNullOrEmpty(_rootDirectory) &&
                    PathService.PathsEqual(path, _rootDirectory))
                {
                    Debug.WriteLine("Cannot delete root directory");
                    MessageBox.Show("Cannot delete the root directory.",
                        "Delete Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string parentPath = Path.GetDirectoryName(path);
                var folderInfo = new FolderInfo(path);

                if (ViewModel != null)
                {
                    // Execute delete command through ViewModel
                    // ViewModel.DeleteFolderCommand.Execute(folderInfo);
                    _ = Task.Run(async () => await ViewModel.DeleteFolders(new[] { folderInfo }));
                }
                else
                {
                    Debug.WriteLine("ViewModel is null");
                    MessageBox.Show("Could not delete folder: ViewModel is not available.",
                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                HandleException("Error deleting folder", ex);
            }
        }

        /// <summary>
        /// Handles the "Delete" context menu item click for multiple folders
        /// </summary>
        public void MultiFolderDelete_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selectedFolders = GetSelectedFolderInfos();
                if (selectedFolders.Count == 0) return;

                if (ViewModel != null)
                {
                    // Execute delete operation through ViewModel
                    _ = ViewModel.DeleteFolders(selectedFolders);

                    // Clear selection and refresh tree
                    ClearSelectedItems();
                }

                else
                {
                    MessageBox.Show("Could not delete folders: ViewModel is not available.",
                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                HandleException("Error deleting folders", ex);
            }
        }

        /// <summary>
        /// Handles the "Show in Explorer" context menu item click
        /// </summary>
        private void ShowInExplorer_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Debug.WriteLine("ShowInExplorer_Click handler called");

                var treeViewItem = GetSelectedTreeViewItem();
                if (treeViewItem == null) return;

                var folderNode = treeViewItem.Tag as FolderNode;
                if (folderNode == null) return;

                string path = folderNode.FullPath;
                if (!PathService.DirectoryExists(path)) return;

                // Create FolderInfo and call ViewModel
                var folderInfo = new FolderInfo(path);

                if (ViewModel != null)
                {
                    Debug.WriteLine($"Calling ViewModel.ShowInExplorer for {path}");
                    ViewModel.ShowInExplorer(folderInfo);
                }
                else
                {
                    Debug.WriteLine("ViewModel is null, using direct Process.Start instead");
                    // Fallback if ViewModel is unavailable
                    try
                    {
                        System.Diagnostics.Process.Start("explorer.exe", path);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error opening explorer: {ex.Message}");
                        MessageBox.Show($"Error opening folder in Explorer: {ex.Message}",
                            "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                HandleException("Error showing folder in Explorer", ex);
            }
        }

        #endregion

        #region Enhanced Refresh Mechanism

        /// <summary>
        /// Performs a full tree rebuild - used for manual refresh operations
        /// </summary>
        public async Task RefreshTreeFull(string pathToSelect = null, bool preserveExpanded = true)
        {
            try
            {
                ShowLoadingIndicator();
                LogTreeViewState("Before Full Refresh");
                _loadingStartTime = DateTime.Now;

                // Store the current selection if not provided
                if (string.IsNullOrEmpty(pathToSelect))
                {
                    var treeViewItem = GetSelectedTreeViewItem();
                    if (treeViewItem != null && treeViewItem.Tag is FolderNode folderNode)
                    {
                        pathToSelect = folderNode.FullPath;
                    }
                }

                // Get all expanded paths to restore later if requested
                var expandedPaths = new HashSet<string>();
                if (preserveExpanded)
                {
                    foreach (var item in FindVisualChildren<TreeViewItem>(ShellTreeViewControl))
                    {
                        if (item.IsExpanded && item.Tag is FolderNode so)
                        {
                            string path = so.FullPath;
                            if (!string.IsNullOrEmpty(path))
                            {
                                expandedPaths.Add(path);
                            }
                        }
                    }
                }

                // Complete tree rebuild
                await InitializeShellTreeAsync();

                // Restore expanded state with animations
                await RestoreExpandedStateAsync(expandedPaths);

                // Restore selection
                if (PathService.DirectoryExists(pathToSelect))
                {
                    SelectPath(pathToSelect);
                }
                LogTreeViewState("After Full Refresh");
                HideLoadingIndicator();
            }
            catch (Exception ex)
            {
                HideLoadingIndicator();
                HandleException("Error performing full tree refresh", ex);
            }
        }

        private readonly Dictionary<string, DateTime> _recentOperations = new Dictionary<string, DateTime>();
        private readonly TimeSpan _operationCooldown = TimeSpan.FromMilliseconds(1000); // 1 second cooldown

        /// <summary>
        /// Check if an operation was recently performed to prevent duplicates
        /// </summary>
        private bool IsRecentOperation(FolderOperationType operationType, string sourcePath, string destinationPath = null)
        {
            string operationKey = $"{operationType}:{sourcePath}:{destinationPath}";

            if (_recentOperations.TryGetValue(operationKey, out DateTime lastTime))
            {
                if (DateTime.Now - lastTime < _operationCooldown)
                {
                    Debug.WriteLine($"DUPLICATE OPERATION DETECTED: {operationKey} (last performed {DateTime.Now - lastTime} ago)");
                    return true;
                }
            }

            _recentOperations[operationKey] = DateTime.Now;

            // Clean up old entries
            var oldEntries = _recentOperations.Where(kvp => DateTime.Now - kvp.Value > _operationCooldown).ToList();
            foreach (var entry in oldEntries)
            {
                _recentOperations.Remove(entry.Key);
            }

            return false;
        }

        /// <summary>
        /// Performs incremental updates for specific folder operations
        /// </summary>
        public async Task RefreshTreeIncremental(
             FolderOperationType operationType,
             string sourcePath,
             string destinationPath = null)
        {
            if (IsRecentOperation(operationType, sourcePath, destinationPath))
                return;

            switch (operationType)
            {
                case FolderOperationType.Create:
                    await HandleFolderCreate(sourcePath);
                    break;

                case FolderOperationType.Delete:
                    await HandleFolderDelete(sourcePath);
                    break;

                case FolderOperationType.Rename:
                    await HandleFolderRename(sourcePath, destinationPath);
                    break;

                case FolderOperationType.Move:
                    if (!_pathToTreeViewItem.ContainsKey(
                            PathService.NormalizePath(destinationPath ?? "")))
                        await HandleFolderMove(sourcePath, destinationPath);
                    break;
            }
        }

        /// <summary>
        /// Overload for batch move operations — processes all pairs then
        /// scrolls the viewport to center all moved items collectively.
        /// </summary>
        public async Task RefreshTreeIncrementalBatchMove(
            List<string> sourcePaths,
            List<string> destinationPaths)
        {
            if (sourcePaths == null || destinationPaths == null) return;
            if (sourcePaths.Count != destinationPaths.Count) return;

            // Process each move individually (existing logic handles tree node updates)
            for (int i = 0; i < sourcePaths.Count; i++)
            {
                string normalizedDest = PathService.NormalizePath(destinationPaths[i]);
                if (_pathToTreeViewItem.ContainsKey(normalizedDest)) continue;

                await HandleFolderMove(sourcePaths[i], destinationPaths[i]);
                EmergencyRemoveDuplicates();
            }

            // After all moves, select every moved item and scroll to their collective center
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                ClearSelectedItems();

                var movedItems = new List<TreeViewItem>();
                foreach (var dest in destinationPaths)
                {
                    string normalized = PathService.NormalizePath(dest);
                    if (_pathToTreeViewItem.TryGetValue(normalized, out var tvi))
                    {
                        SelectItem(tvi);
                        movedItems.Add(tvi);
                    }
                }

                if (movedItems.Count > 0)
                {
                    // BringIntoView the first item so the tree renders item positions,
                    // then animate to the group center
                    movedItems[0].BringIntoView();
                    ScrollToCenterMultiple(movedItems);
                }
            }, DispatcherPriority.Loaded);
        }

        /// <summary>
        /// Emergency method to remove all duplicate children from TreeView
        /// </summary>
        public void EmergencyRemoveDuplicates()
        {

            try
            {
                var duplicatesRemoved = 0;

                foreach (var kvp in _pathToTreeViewItem.ToList())
                {
                    var parentItem = kvp.Value.Parent as TreeViewItem;
                    if (parentItem == null) continue;

                    // Check for duplicates in this parent
                    var childNames = new Dictionary<string, List<TreeViewItem>>();

                    foreach (TreeViewItem child in parentItem.Items)
                    {
                        string childName = "";
                        if (child.Header is StackPanel panel)
                        {
                            foreach (var element in panel.Children)
                            {
                                if (element is TextBlock textBlock)
                                {
                                    childName = textBlock.Text;
                                    break;
                                }
                            }
                        }
                        else
                        {
                            childName = child.Header?.ToString() ?? "";
                        }

                        if (!childNames.ContainsKey(childName))
                        {
                            childNames[childName] = new List<TreeViewItem>();
                        }
                        childNames[childName].Add(child);
                    }

                    // Remove duplicates (keep only the first one)
                    foreach (var nameGroup in childNames)
                    {
                        if (nameGroup.Value.Count > 1)
                        {

                            // Keep the first, remove the rest
                            for (int i = 1; i < nameGroup.Value.Count; i++)
                            {
                                parentItem.Items.Remove(nameGroup.Value[i]);
                                duplicatesRemoved++;
                                Debug.WriteLine($"Removed duplicate: {nameGroup.Key}");
                            }
                        }
                    }
                }

                if (duplicatesRemoved > 0)
                {
                    ShellTreeViewControl.UpdateLayout();
                }
                else
                {
                    Debug.WriteLine("No duplicates found");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in emergency cleanup: {ex.Message}");
            }
        }



        /// <summary>
        /// Handles folder creation by adding new tree item
        /// </summary>
        private async Task HandleFolderCreate(string newFolderPath)
        {
            if (string.IsNullOrEmpty(newFolderPath)) return;

            string parentPath = Path.GetDirectoryName(newFolderPath);
            if (parentPath == null) return;

            string normalizedParent = PathService.NormalizePath(parentPath);
            string normalizedNew = PathService.NormalizePath(newFolderPath);

            if (_pathToTreeViewItem.ContainsKey(normalizedNew)) return;

            if (!_pathToTreeViewItem.TryGetValue(normalizedParent, out var parentItem))
                return;

            // If the parent has only a placeholder (not yet expanded), just ensure
            // the placeholder exists so the arrow stays visible.
            if (!parentItem.IsExpanded)
            {
                if (!HasExpansionIndicator(parentItem))
                    AddDummyNode(parentItem);
                return;
            }

            // Parent is expanded — insert the new node in sorted position
            var newNode = new FolderNode(newFolderPath);
            var newItem = FolderTreeItemFactory.CreateItem(newNode);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                // Probe sub-dirs before touching parent (small I/O, stays off UI pump)
                // Already done inside CreateItem → HasSubDirectories

                // Find sorted insertion index
                int insertAt = 0;
                for (int i = 0; i < parentItem.Items.Count; i++)
                {
                    if (!(parentItem.Items[i] is TreeViewItem sibling)) continue;
                    if (!(sibling.Tag is FolderNode sibNode)) continue;
                    if (WindowsNaturalStringComparer.Instance.Compare(sibNode.Name, newNode.Name) > 0)
                        break;
                    insertAt = i + 1;
                }
                parentItem.Items.Insert(insertAt, newItem);
                _pathToTreeViewItem[normalizedNew] = newItem;

                // Select the new folder without forcing viewport jump
                ClearSelectedItems();
                SelectItem(newItem);
                _lastSelectedItem = newItem;
                NotifyFolderSelectionWithoutLoading(newItem);
                // Invalidate parent's cached children so the next expansion is fresh
                if (parentItem.Tag is FolderNode parentNode)
                    parentNode.InvalidateChildren();
            }, DispatcherPriority.Normal);
        }


        /// <summary>
        /// Handles folder deletion - removes item from tree
        /// </summary>
        private async Task HandleFolderDelete(string deletedPath)
        {
            if (string.IsNullOrEmpty(deletedPath)) return;
            string normalized = PathService.NormalizePath(deletedPath);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (!_pathToTreeViewItem.TryGetValue(normalized, out var item)) return;

                var parentTvi = FindParentTreeViewItem(item);
                if (parentTvi != null)
                {
                    parentTvi.Items.Remove(item);
                    if (parentTvi.Tag is FolderNode pn) pn.InvalidateChildren();
                    // Hide expand arrow if no children remain
                    if (parentTvi.Items.Count == 0)
                    {
                        // No placeholder needed — folder is now empty
                    }
                }
                else
                {
                    ShellTreeViewControl.Items.Remove(item);
                }

                // Remove this path and all descendants from the map
                var toRemove = _pathToTreeViewItem.Keys
                    .Where(k => k.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                foreach (var k in toRemove)
                    _pathToTreeViewItem.Remove(k);

                _nodeManager?.RemoveNodeState(normalized);
            }, DispatcherPriority.Normal);
        }

        /// <summary>
        /// Handles folder rename - updates existing item
        /// </summary>
        private async Task HandleFolderRename(string oldPath, string newPath)
        {

            if (string.IsNullOrEmpty(oldPath) || string.IsNullOrEmpty(newPath))
                return;

            if (_pathToTreeViewItem.TryGetValue(oldPath, out var renamedItem))
            {
                bool wasSelected = IsItemSelected(renamedItem);

                // Update the TreeViewItem's tag and header
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (renamedItem.Tag is FolderNode oldFolderNode)
                    {
                        try
                        {
                            // Create new folderNode for the renamed folder
                            var newFolderNode = new FolderNode(newPath);
                            renamedItem.Tag = newFolderNode;
                            renamedItem.Header = FolderTreeItemFactory.CreateHeader(newFolderNode.Name);
                            // Update path mapping
                            _pathToTreeViewItem.Remove(oldPath);
                            _pathToTreeViewItem[newPath] = renamedItem;

                            // Update any child path mappings
                            UpdateChildPathMappings(oldPath, newPath);

                            // Invalidate cache
                            string parentPath = Path.GetDirectoryName(newPath);
                            if (!string.IsNullOrEmpty(parentPath))
                            {
                                PathService.InvalidatePathCache(parentPath, false);
                            }

                            // Keep selection state without forcing navigation/scroll
                            if (wasSelected)
                            {
                                _lastSelectedItem = renamedItem;
                                NotifyFolderSelectionWithoutLoading(renamedItem);
                            }
                        }
                        catch (Exception ex)
                        {
                            HandleException("Error updating renamed folder", ex);
                            // Fallback to refreshing parent directory
                            string parentPath = Path.GetDirectoryName(newPath);
                            if (!string.IsNullOrEmpty(parentPath))
                            {
                                _ = RefreshParentDirectory(parentPath);
                            }
                        }
                    }
                });
            }
            else
            {
                // Item not found, refresh parent directory
                string parentPath = Path.GetDirectoryName(newPath);
                if (!string.IsNullOrEmpty(parentPath))
                {
                    await RefreshParentDirectory(parentPath);
                }
            }
        }

        private void ScrollToCenter(TreeViewItem item)
        {
            var scrollViewer = FindVisualChild<ScrollViewer>(ShellTreeViewControl);
            if (scrollViewer == null) return;

            var transform = item.TransformToAncestor(scrollViewer);
            var itemPosition = transform.Transform(new Point(0, 0));

            double itemTop = itemPosition.Y + scrollViewer.VerticalOffset;
            double itemHeight = item.ActualHeight;
            double viewportHeight = scrollViewer.ViewportHeight;

            double targetOffset = itemTop - (viewportHeight / 2) + (itemHeight / 2);
            targetOffset = Math.Max(0, Math.Min(targetOffset, scrollViewer.ScrollableHeight));

            // Animate scroll position
            var animation = new DoubleAnimation
            {
                From = scrollViewer.VerticalOffset,
                To = targetOffset,
                Duration = TimeSpan.FromMilliseconds(300),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            scrollViewer.BeginAnimation(ScrollViewerBehavior.VerticalOffsetProperty, animation);
        }

        /// <summary>
        /// Scrolls the TreeView so that the visual midpoint of all specified items
        /// is centered in the viewport. Used after batch move operations.
        /// </summary>
        private void ScrollToCenterMultiple(IEnumerable<TreeViewItem> items)
        {
            var scrollViewer = FindVisualChild<ScrollViewer>(ShellTreeViewControl);
            if (scrollViewer == null) return;

            var visibleItems = items
                .Where(item => item != null && item.IsVisible)
                .ToList();

            if (visibleItems.Count == 0) return;
            if (visibleItems.Count == 1) { ScrollToCenter(visibleItems[0]); return; }

            // Collect absolute Y positions of all items
            var yPositions = new List<double>();
            foreach (var item in visibleItems)
            {
                try
                {
                    var transform = item.TransformToAncestor(scrollViewer);
                    var pos = transform.Transform(new Point(0, 0));
                    double absTop = pos.Y + scrollViewer.VerticalOffset;
                    yPositions.Add(absTop);
                    yPositions.Add(absTop + item.ActualHeight);
                }
                catch (InvalidOperationException)
                {
                    // Item may not be in the visual tree yet — skip
                }
            }

            if (yPositions.Count == 0) return;

            double groupTop = yPositions.Min();
            double groupBottom = yPositions.Max();
            double groupCenter = (groupTop + groupBottom) / 2.0;

            double targetOffset = groupCenter - scrollViewer.ViewportHeight / 2.0;
            targetOffset = Math.Max(0, Math.Min(targetOffset, scrollViewer.ScrollableHeight));

            var animation = new DoubleAnimation
            {
                From = scrollViewer.VerticalOffset,
                To = targetOffset,
                Duration = TimeSpan.FromMilliseconds(350),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            scrollViewer.BeginAnimation(ScrollViewerBehavior.VerticalOffsetProperty, animation);
        }

        /// <summary>
        /// Atomically update path mapping to prevent corruption
        /// </summary>
        private bool TryUpdatePathMapping(string oldPath, string newPath, TreeViewItem item)
        {
            var normalizedOldPath = PathNormalizationService.GetCanonicalPath(oldPath);
            var normalizedNewPath = PathNormalizationService.GetCanonicalPath(newPath);

            _pathMappingLock.EnterWriteLock();
            try
            {
                // Remove old mapping and add new one atomically
                if (_pathToTreeViewItem.ContainsKey(normalizedOldPath))
                {
                    _pathToTreeViewItem.Remove(normalizedOldPath);
                    _pathToTreeViewItem[normalizedNewPath] = item;
                    return true;
                }
                return false;
            }
            finally
            {
                _pathMappingLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Safely add path mapping with conflict detection
        /// </summary>
        private bool TrySafeAddPathMapping(string path, TreeViewItem item)
        {
            var normalizedPath = PathNormalizationService.GetCanonicalPath(path);

            _pathMappingLock.EnterWriteLock();
            try
            {
                // Check for existing mapping before adding
                if (_pathToTreeViewItem.ContainsKey(normalizedPath))
                {
                    Debug.WriteLine($"Warning: Duplicate path mapping attempted for {normalizedPath}");
                    return false;
                }

                _pathToTreeViewItem[normalizedPath] = item;
                return true;
            }
            finally
            {
                _pathMappingLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Safely remove path mapping
        /// </summary>
        private bool TrySafeRemovePathMapping(string path)
        {
            var normalizedPath = PathNormalizationService.GetCanonicalPath(path);

            _pathMappingLock.EnterWriteLock();
            try
            {
                return _pathToTreeViewItem.Remove(normalizedPath);
            }
            finally
            {
                _pathMappingLock.ExitWriteLock();
            }
        }



        /// <summary>
        /// Enhanced folder move handling with complete cleanup and loading state management
        /// </summary>
        private async Task HandleFolderMove(string sourcePath, string destinationPath)
        {
            string moveId = Guid.NewGuid().ToString("N").Substring(0, 8);
            bool destParentWasNotLoaded = false; // Variable to track destination parent loading state
            bool wasSourceSelected = false;

            try
            {
                if (string.IsNullOrEmpty(sourcePath) || string.IsNullOrEmpty(destinationPath))
                {
                    return;
                }

                string normalizedSourcePath = PathService.NormalizePath(sourcePath);
                string normalizedDestPath = PathService.NormalizePath(destinationPath);


                // ===== STEP 1: PREVENT DUPLICATES =====
                if (_pathToTreeViewItem.ContainsKey(normalizedDestPath))
                {
                    return;
                }

                // ===== STEP 2: FIND SOURCE AND DESTINATION ITEMS =====
                TreeViewItem sourceItem;
                if (!_pathToTreeViewItem.TryGetValue(normalizedSourcePath, out sourceItem))
                {
                    await HandleFolderCreate(normalizedDestPath);
                    return;
                }
                wasSourceSelected = IsItemSelected(sourceItem);

                TreeViewItem sourceParent = sourceItem.Parent as TreeViewItem;
                if (sourceParent == null)
                {
                    return;
                }

                string destParentPath = Path.GetDirectoryName(normalizedDestPath);
                string normalizedDestParentPath = PathService.NormalizePath(destParentPath);

                TreeViewItem destParentItem;
                if (!_pathToTreeViewItem.TryGetValue(normalizedDestParentPath, out destParentItem))
                {
                    await HandleFolderCreate(normalizedDestPath);
                    await HandleFolderDelete(normalizedSourcePath);
                    return;
                }

                // ===== STEP 3: CHECK DESTINATION PARENT LOADING STATE =====
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    // Check if destination parent is in "not yet loaded" state
                    destParentWasNotLoaded = FolderTreeItemFactory.HasOnlyPlaceholder(destParentItem);
                });

                // ===== STEP 4: REMOVE SOURCE ITEM =====            
                var sourceItemsToRemove = new List<TreeViewItem>();
                foreach (TreeViewItem child in sourceParent.Items)
                {
                    if (child == sourceItem)
                    {
                        sourceItemsToRemove.Add(child);
                        break;
                    }
                }

                foreach (var item in sourceItemsToRemove)
                {
                    sourceParent.Items.Remove(item);
                }

                // Remove from path mapping
                TrySafeRemovePathMapping(normalizedSourcePath);

                // ===== STEP 5: UPDATE FOLDERNODE AND ADD TO DESTINATION =====
                if (destParentWasNotLoaded)
                {
                    // ── BUG 2 FIX ─────────────────────────────────────────────────
                    // Target node was unexpanded and held only a placeholder.
                    // Instead of trying to manually populate siblings (the old complex
                    // background-loading block that was also broken), we simply trigger
                    // the node's normal lazy-expansion.  ExpandNodeAsync will scan the
                    // filesystem – which now contains the moved folder – and build every
                    // child TreeViewItem correctly, registering all paths in the mapping.
                    var destNode = destParentItem.Tag as FolderNode;
                    if (destNode != null)
                    {
                        // Call ExpandNodeAsync directly so we can await its completion
                        // before trying to select the moved item below.
                        await ExpandNodeAsync(destParentItem, destNode);
                    }

                    // Make the node visually expanded.  At this point HasOnlyPlaceholder
                    // is false (real items were loaded), so TreeViewItem_Expanded will
                    // NOT re-trigger ExpandNodeAsync – no double expansion.
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        destParentItem.IsExpanded = true;
                    });
                    // ──────────────────────────────────────────────────────────────
                }
                else
                {
                    // Normal case: parent already had its children loaded.
                    // Re-use the existing sourceItem (updated to the new path) and
                    // insert it directly.
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        var newFolderNode = new FolderNode(normalizedDestPath);
                        sourceItem.Tag = newFolderNode;
                        sourceItem.Header = FolderTreeItemFactory.CreateHeader(newFolderNode.Name);

                        destParentItem.Items.Add(sourceItem);

                        if (!TryUpdatePathMapping(normalizedSourcePath, normalizedDestPath, sourceItem))
                            TrySafeAddPathMapping(normalizedDestPath, sourceItem);
                    });

                    await EnsureNaturalSorting(destParentItem);
                }

                // ===== STEP 6: SORT AND UI UPDATE (only if parent was already loaded) =====

                if (!destParentWasNotLoaded)
                {
                    // Only sort if parent was already loaded to avoid interfering with lazy loading
                    await EnsureNaturalSorting(destParentItem);
                }
                else
                {
                    Debug.WriteLine($"[{moveId}] Skipped sorting for previously unloaded parent");
                }

                // ===== STEP 6.5: SCROLL TO MOVED ITEM =====
                if (destParentWasNotLoaded)
                {
                    // Background loading is still in progress — wait briefly for it to register
                    await Task.Delay(300);
                }

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (wasSourceSelected && _pathToTreeViewItem.TryGetValue(normalizedDestPath, out var movedItem))
                    {
                        ClearSelectedItems();
                        SelectItem(movedItem);
                        _lastSelectedItem = movedItem;
                        NotifyFolderSelectionWithoutLoading(movedItem);
                    }
                }, DispatcherPriority.Loaded);
                // ===== STEP 7: FINAL VERIFICATION =====
                // Verify the moved item's FolderNode points to correct path
                if (_pathToTreeViewItem.TryGetValue(normalizedDestPath, out var verifyItem))
                {
                    if (verifyItem.Tag is FolderNode folderNode)
                    {
                        string itemPath = folderNode.FullPath;
                    }
                }

                if (_coordinator != null)
                {
                    _ = Task.Run(async () =>
                    {
                        await _coordinator.ExecuteFolderMoveAsync(normalizedSourcePath, normalizedDestPath);
                    });
                }
            }
            catch (Exception)
            {
                throw; // Re-throw to trigger any higher-level error handling
            }
        }


        /// <summary>
        /// Finds the correct natural insertion index for a new item using Windows file system ordering
        /// </summary>
        private int FindNaturalInsertionIndex(TreeViewItem parentItem, TreeViewItem newItem)
        {
            if (!(newItem.Tag is FolderNode newFolderNode))
                return GetRealChildrenCount(parentItem);

            string newName = newFolderNode.Name;
            int insertIndex = 0;

            // Iterate through all children to find the correct natural position
            for (int i = 0; i < parentItem.Items.Count; i++)
            {
                if (parentItem.Items[i] is TreeViewItem existingItem)
                {
                    // Skip dummy nodes (loading indicators) - they don't have proper folderNode tags
                    if (existingItem.Tag is FolderNode existingFolderNode)
                    {
                        // Compare names using Windows natural comparison (handles numeric sequences properly)
                        if (WindowsNaturalStringComparer.Instance.Compare(newName, existingFolderNode.Name) < 0)
                        {
                            return insertIndex;
                        }
                        insertIndex = i + 1;
                    }
                    // For dummy nodes, we continue without incrementing insertIndex
                }
            }

            return insertIndex;
        }


        /// <summary>
        /// Gets the count of real folder children (excluding dummy nodes)
        /// </summary>
        private int GetRealChildrenCount(TreeViewItem parentItem)
        {
            int count = 0;
            foreach (var item in parentItem.Items)
            {
                if (item is TreeViewItem treeItem && treeItem.Tag is FolderNode)
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Ensures that all folders in a parent container are properly sorted using Windows natural ordering.
        /// This method maintains consistency with Windows Explorer file system ordering.
        /// </summary>
        private async Task EnsureNaturalSorting(TreeViewItem parentItem)
        {
            if (parentItem == null) return;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    // Extract all real folder items (excluding dummy nodes)
                    var folderItems = new List<(TreeViewItem item, string name)>();
                    var dummyNodes = new List<TreeViewItem>();

                    foreach (TreeViewItem child in parentItem.Items.OfType<TreeViewItem>())
                    {
                        if (child.Tag is FolderNode folderNode)
                        {
                            folderItems.Add((child, folderNode.Name));
                        }
                        else
                        {
                            // This is likely a dummy node (expansion indicator)
                            dummyNodes.Add(child);
                        }
                    }

                    // Sort folder items using Windows natural ordering (same as Windows Explorer)
                    folderItems.Sort((a, b) => WindowsNaturalStringComparer.Instance.Compare(a.name, b.name));

                    // Clear and re-add items in correct natural order
                    parentItem.Items.Clear();

                    // Add sorted folder items first
                    foreach (var (item, _) in folderItems)
                    {
                        parentItem.Items.Add(item);
                    }

                    // Add dummy nodes at the end (they will be removed when parent expands anyway)
                    foreach (var dummyNode in dummyNodes)
                    {
                        parentItem.Items.Add(dummyNode);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"🔧 [EnsureNaturalSorting] Error: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Ensures parent has expansion indicator if it contains subfolders
        /// </summary>
        private async Task EnsureParentHasExpansionIndicator(TreeViewItem parentItem)
        {
            if (parentItem.Tag is FolderNode folderNode)
            {
                string parentPath = folderNode.FullPath;
                if (!string.IsNullOrEmpty(parentPath))
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        // Check if parent should have expansion indicator
                        if (ShouldHaveExpansionIndicator(parentPath) && !HasExpansionIndicator(parentItem))
                        {
                            AddDummyNode(parentItem);
                        }
                    });
                }
            }
        }

        /// <summary>
        /// Updates parent expansion indicator based on remaining children
        /// </summary>
        private void UpdateParentExpansionIndicator(TreeViewItem parentItem)
        {
            if (parentItem.Tag is FolderNode folderNode)
            {
                string parentPath = folderNode.FullPath;
                if (!string.IsNullOrEmpty(parentPath))
                {
                    bool shouldHaveIndicator = ShouldHaveExpansionIndicator(parentPath);
                    bool currentlyHasIndicator = HasExpansionIndicator(parentItem);
                    bool isExpanded = parentItem.IsExpanded;

                    if (isExpanded)
                    {
                        if (currentlyHasIndicator)
                        {

                            RemoveDummyNode(parentItem);
                        }
                    }
                    else
                    {
                        if (shouldHaveIndicator && !currentlyHasIndicator)
                        {
                            AddDummyNode(parentItem);
                        }
                        else if (!shouldHaveIndicator && currentlyHasIndicator)
                        {
                            RemoveDummyNode(parentItem);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Checks if a directory should have expansion indicator
        /// </summary>
        private bool ShouldHaveExpansionIndicator(string directoryPath)
        {
            if (!Directory.Exists(directoryPath)) return false;
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(
                    directoryPath, "*", SearchOption.TopDirectoryOnly))
                {
                    var attrs = File.GetAttributes(dir);
                    if ((attrs & FileAttributes.Hidden) != 0 ||
                        (attrs & FileAttributes.System) != 0)
                        continue;
                    return true; // Found one — stop immediately
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Checks if TreeViewItem currently has expansion indicator (dummy node)
        /// </summary>
        private bool HasExpansionIndicator(TreeViewItem item) =>
            FolderTreeItemFactory.HasOnlyPlaceholder(item);

        /// <summary>
        /// Adds dummy node for expansion indicator
        /// </summary>
        private void AddDummyNode(TreeViewItem item)
        {
            if (!HasExpansionIndicator(item))
                item.Items.Add(FolderTreeItemFactory.MakePlaceholder());
        }


        /// <summary>
        /// Helper method to identify loading headers
        /// </summary>
        private bool IsLoadingHeader(object header)
        {
            if (header is StackPanel panel && panel.Children.Count == 2)
            {
                return panel.Children[1] is TextBlock textBlock &&
                       textBlock.Text == "Loading...";
            }
            return false;
        }

        /// <summary>
        /// Removes dummy node - updated to handle TreeViewItem objects
        /// </summary>
        private void RemoveDummyNode(TreeViewItem item)
        {
            var itemsToRemove = new List<object>();

            foreach (var child in item.Items)
            {
                if (child is TreeViewItem treeItem)
                {
                    // Remove dummy nodes identified by tag or loading header
                    if (treeItem.Tag as string == "DUMMY_NODE" ||
                        (!treeItem.IsEnabled && IsLoadingHeader(treeItem.Header)))
                    {
                        itemsToRemove.Add(child);
                    }
                }
                // Legacy support: remove old string-based loading indicators
                else if (child is string str && str == "Loading...")
                {
                    itemsToRemove.Add(child);
                }
            }

            foreach (var itemToRemove in itemsToRemove)
            {
                item.Items.Remove(itemToRemove);
            }
        }


        /// <summary>
        /// Updates child path mappings after parent path change
        /// </summary>
        private void UpdateChildPathMappings(string oldParentPath, string newParentPath)
        {
            var allAffectedPaths = new List<string>();

            // Collect all paths that need updating (including deeply nested)
            foreach (var path in _pathToTreeViewItem.Keys.ToList())
            {
                if (path.StartsWith(oldParentPath + Path.DirectorySeparatorChar) ||
                    path.Equals(oldParentPath, StringComparison.OrdinalIgnoreCase))
                {
                    allAffectedPaths.Add(path);
                }
            }

            // Update all paths atomically to prevent partial state
            var tempMappings = new Dictionary<string, TreeViewItem>();
            foreach (var oldPath in allAffectedPaths)
            {
                string newPath = oldPath.Replace(oldParentPath, newParentPath);
                var item = _pathToTreeViewItem[oldPath];
                tempMappings[newPath] = item;

                // Update the TreeViewItem's folderNode as well
                if (item.Tag is FolderNode folderNode)
                {
                    try
                    {
                        var newFolderNode = new FolderNode(newPath);
                        item.Tag = newFolderNode;
                        item.Header = FolderTreeItemFactory.CreateItem(newFolderNode);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Failed to update FolderNode for {newPath}: {ex.Message}");
                    }
                }
            }

            // Remove old mappings and add new ones
            foreach (var oldPath in allAffectedPaths)
            {
                _pathToTreeViewItem.Remove(oldPath);
            }

            foreach (var kvp in tempMappings)
            {
                _pathToTreeViewItem[kvp.Key] = kvp.Value;
            }
        }

        /// <summary>
        /// Refreshes a specific parent directory
        /// </summary>
        private async Task RefreshParentDirectory(string parentPath)
        {
            if (_pathToTreeViewItem.TryGetValue(parentPath, out var parentItem))
            {
                // Invalidate cache
                PathService.InvalidatePathCache(parentPath, false);

                // If parent is expanded, refresh its children
                if (parentItem.IsExpanded)
                {
                    await RefreshTreeViewItemChildren(parentItem);
                }
            }
        }

        /// <summary>
        /// Refreshes children of a specific TreeViewItem
        /// </summary>
        private async Task RefreshTreeViewItemChildren(TreeViewItem parentItem)
        {
            if (!(parentItem.Tag is FolderNode folderNode))
                return;

            string parentPath = folderNode.FullPath;
            if (string.IsNullOrEmpty(parentPath) || !Directory.Exists(parentPath))
                return;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                // Store current children for comparison
                var currentChildren = new Dictionary<string, TreeViewItem>();
                var itemsToRemove = new List<TreeViewItem>();

                foreach (TreeViewItem child in parentItem.Items.OfType<TreeViewItem>())
                {
                    if (child.Tag is FolderNode childFolderNode)
                    {
                        string childPath = childFolderNode.FullPath;
                        if (!string.IsNullOrEmpty(childPath))
                        {
                            currentChildren[childPath] = child;
                        }
                    }
                }

                // Get actual subdirectories
                var actualSubdirs = Directory.GetDirectories(parentPath, "*", SearchOption.TopDirectoryOnly)
                    .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // Remove items that no longer exist
                foreach (var kvp in currentChildren)
                {
                    if (!actualSubdirs.Contains(kvp.Key))
                    {
                        itemsToRemove.Add(kvp.Value);
                        _pathToTreeViewItem.Remove(kvp.Key);
                    }
                }

                foreach (var item in itemsToRemove)
                {
                    parentItem.Items.Remove(item);
                }

                // Add new items that don't exist in tree
                foreach (var subdirPath in actualSubdirs)
                {
                    if (!currentChildren.ContainsKey(subdirPath))
                    {
                        try
                        {
                            var newFolderNode = new FolderNode(subdirPath);
                            var newTreeItem = FolderTreeItemFactory.CreateItem(newFolderNode);

                            int insertIndex = FindNaturalInsertionIndex(parentItem, newTreeItem);
                            parentItem.Items.Insert(insertIndex, newTreeItem);

                            _pathToTreeViewItem[subdirPath] = newTreeItem;

                            // Add entrance animation
                            AnimateItemEntrance(newTreeItem);
                        }
                        catch (Exception ex)
                        {
                            HandleException($"Error adding subdirectory {subdirPath}", ex);
                        }
                    }
                }
            });
        }

        /// <summary>
        /// Restores expanded state for specified paths
        /// </summary>
        private async Task RestoreExpandedStateAsync(HashSet<string> expandedPaths)
        {
            await Task.Run(async () =>
            {
                foreach (var path in expandedPaths)
                {
                    if (PathService.DirectoryExists(path) && _pathToTreeViewItem.TryGetValue(path, out var item))
                    {
                        await Application.Current.Dispatcher.InvokeAsync(async () =>
                        {
                            await ExpandItemWithAnimationAsync(item);
                        });
                        await Task.Delay(50); // Small delay between expansions for smooth animation
                    }
                }
            });
        }

        /// <summary>
        /// Adds entrance animation to newly created items
        /// </summary>
        private void AnimateItemEntrance(TreeViewItem item)
        {
            // Simple fade-in animation
            item.Opacity = 0;
            var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300));
            item.BeginAnimation(TreeViewItem.OpacityProperty, animation);
        }

        /// <summary>
        /// Legacy method for backward compatibility - now routes to appropriate refresh type
        /// </summary>
        [Obsolete("Use RefreshTreeFull() for manual refresh or RefreshTreeIncremental() for operation-based refresh")]
        public Task RefreshTree(string pathToSelect = null, bool preserveExpanded = true)
        {
            // Default to full refresh for backward compatibility
            return RefreshTreeFull(pathToSelect, preserveExpanded);
        }

        #endregion

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