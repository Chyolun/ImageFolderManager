using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ImageFolderManager.Models;
using ImageFolderManager.Services;
using ImageFolderManager.ViewModels;
using Microsoft.WindowsAPICodePack.Shell;

namespace ImageFolderManager.Views
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
        private ShellObject _draggedShellObject;

        // Track expanded paths
        private HashSet<string> _expandedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Track selected path
        private string _selectedPath;

        // Dictionary to map ShellObject paths to TreeViewItem
        private Dictionary<string, TreeViewItem> _pathToTreeViewItem =
                new Dictionary<string, TreeViewItem>(StringComparer.OrdinalIgnoreCase);

        // Current root directory
        private string _rootDirectory;

        // Multi-selection support
        private ObservableCollection<TreeViewItem> _selectedItems = new ObservableCollection<TreeViewItem>();
        public ObservableCollection<TreeViewItem> SelectedItems => _selectedItems;

        // Track last selected item for shift selection
        private TreeViewItem _lastSelectedItem;

        // For selection with mouse
        private bool _isMultiSelectActive = false;
        private DateTime _mouseDownTime;
        private const int DRAG_DELAY_MS = 300; // 300ms delay before starting drag
        private const double DRAG_DISTANCE_MULTIPLIER = 1.5; // Increase drag distance threshold


        public ShellTreeView()
        {
            InitializeComponent();

            // Add DataContext change handler to ensure MainViewModel is always accessible
            this.DataContextChanged += (s, e) => {
                if (e.NewValue is MainViewModel)
                {
                    Debug.WriteLine("ShellTreeView received correct DataContext (MainViewModel)");

                    // Check if root directory has changed
                    if (PathService.DirectoryExists(AppSettings.Instance.DefaultRootDirectory) &&
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

            // Manually bind context menu event
            ShellTreeViewControl.ContextMenuOpening += ShellTreeViewControl_ContextMenuOpening;

            // Initialize with default root directory
            LoadDefaultRootDirectoryAsync();
        }

        private async void LoadDefaultRootDirectoryAsync()
        {
            try
            {
                if (!string.IsNullOrEmpty(AppSettings.Instance.DefaultRootDirectory))
                {
                    await Task.Delay(100); // Brief delay to ensure component is fully loaded

                    // Set the root directory from AppSettings
                    _rootDirectory = AppSettings.Instance.DefaultRootDirectory;

                    // Initialize the shell tree
                    InitializeShellTree();

                    // Select the path in the shell tree view
                    SelectPath(AppSettings.Instance.DefaultRootDirectory);
                }
            }
            catch (Exception ex)
            {
                HandleException("Error loading default root directory", ex);
            }
        }

        private void InitializeShellTree()
        {
            try
            {
                ShellTreeViewControl.Items.Clear();
                _pathToTreeViewItem.Clear();

                if (PathService.DirectoryExists(_rootDirectory))
                {
                    // Use the specified root directory
                    var rootShellObject = ShellObject.FromParsingName(_rootDirectory);
                    var rootItem = CreateShellTreeViewItem(rootShellObject);
                    ShellTreeViewControl.Items.Add(rootItem);

                    // Auto expand the root
                    rootItem.IsExpanded = true;
                    _expandedPaths.Add(rootShellObject.ParsingName);
                }
                else
                {
                    // Fall back to "This PC" if no root directory is specified
                    var desktop = ShellObject.FromParsingName("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}");
                    var rootItem = CreateShellTreeViewItem(desktop);
                    ShellTreeViewControl.Items.Add(rootItem);

                    // Auto expand "This PC"
                    rootItem.IsExpanded = true;
                    _expandedPaths.Add(desktop.ParsingName);
                }
            }
            catch (Exception ex)
            {
                HandleException("Error initializing shell tree", ex);
            }
        }

        /// <summary>
        /// Changes the root directory of the ShellTreeView
        /// </summary>
        public void ChangeRootDirectory(string newRootDirectory)
        {
            try
            {
                // Clear multi-selection state to prevent interference with root directory change
                ClearSelectedItems();

                // If null or empty, show This PC
                if (string.IsNullOrEmpty(newRootDirectory))
                {
                    _rootDirectory = null;
                    InitializeShellTree();
                    return;
                }

                // Verify directory exists
                if (!PathService.DirectoryExists(newRootDirectory))
                {
                    MessageBox.Show("Cannot set root directory: Directory does not exist.",
                        "Invalid Directory", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Debug.WriteLine($"Changing root directory to: {newRootDirectory}");

                // Store new root
                _rootDirectory = newRootDirectory;

                // Reinitialize tree with new root
                InitializeShellTree();

                // Select this path
                SelectPath(newRootDirectory);
            }
            catch (Exception ex)
            {
                HandleException("Error changing root directory", ex);
            }
        }

        /// <summary>
        /// Updates the path mapping when a path is renamed
        /// </summary>
        public void UpdatePathMapping(string oldPath, string newPath)
        {
            if (_pathToTreeViewItem.TryGetValue(oldPath, out var treeViewItem))
            {
                _pathToTreeViewItem.Remove(oldPath);
                _pathToTreeViewItem[newPath] = treeViewItem;
            }
        }

        /// <summary>
        /// Completely rebuilds a TreeViewItem with correct subdirectory status
        /// </summary>
        private void RebuildTreeItem(TreeViewItem item)
        {
            if (item == null) return;

            bool wasExpanded = item.IsExpanded;
            var shellObject = item.Tag as ShellObject;
            if (shellObject == null) return;

            // Clear all child items
            item.Items.Clear();

            // Get the actual path
            string path = PathService.GetPathFromShellObject(shellObject);
            if (string.IsNullOrEmpty(path)) return;

            // Only check for subfolders if this is a file system folder
            if (PathService.DirectoryExists(path))
            {
                // Check if this directory actually has any subdirectories
                bool hasSubdirectories = false;
                try
                {
                    // Always check directly with the filesystem for the most up-to-date information
                    // This is crucial for newly created or moved folders
                    string[] subdirectories = Directory.GetDirectories(path, "*", SearchOption.TopDirectoryOnly);
                    hasSubdirectories = subdirectories.Length > 0;
                }
                catch (UnauthorizedAccessException)
                {
                    // For unauthorized directories, assume they might have subdirectories
                    hasSubdirectories = true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error checking for subdirectories: {ex.Message}");
                    // Assume no subdirectories on error
                    hasSubdirectories = false;
                }

                // Only add dummy item if it has subdirectories
                if (hasSubdirectories)
                {
                    item.Items.Add(new TreeViewItem { Header = "Loading..." });
                }
            }
            else if (shellObject is ShellFolder)
            {
                // For special shell folders, add the dummy item
                item.Items.Add(new TreeViewItem { Header = "Loading..." });
            }

            // If it was expanded before, expand it again to reload children
            if (wasExpanded)
            {
                item.IsExpanded = false;
                item.IsExpanded = true;
            }
        }


        /// <summary>
        /// Comprehensive tree refresh method that handles various refresh scenarios
        /// </summary>
        public void RefreshTree(string pathToSelect = null, bool preserveExpanded = true)
        {
            try
            {
                // Store the current selection if not provided
                if (string.IsNullOrEmpty(pathToSelect))
                {
                    var treeViewItem = GetSelectedTreeViewItem();
                    if (treeViewItem != null && treeViewItem.Tag is ShellObject shellObject)
                    {
                        pathToSelect = PathService.GetPathFromShellObject(shellObject);
                    }
                }

                // Get all expanded paths to restore later if requested
                var expandedPaths = new HashSet<string>();
                if (preserveExpanded)
                {
                    foreach (var item in FindVisualChildren<TreeViewItem>(ShellTreeViewControl))
                    {
                        if (item.IsExpanded && item.Tag is ShellObject so)
                        {
                            string path = PathService.GetPathFromShellObject(so);
                            if (!string.IsNullOrEmpty(path))
                            {
                                expandedPaths.Add(path);
                            }
                        }
                    }
                }

                // Find the item to refresh
                TreeViewItem itemToRefresh = null;

                // If we have a selected path, we'll try to find its parent for refreshing
                if (!string.IsNullOrEmpty(pathToSelect))
                {
                    string parentPath = Path.GetDirectoryName(pathToSelect);

                    // Try to refresh the parent of the selected path
                    if (PathService.DirectoryExists(parentPath))
                    {
                        if (_pathToTreeViewItem.TryGetValue(parentPath, out itemToRefresh))
                        {
                            RefreshTreeItem(itemToRefresh);
                        }
                    }

                    // If we couldn't refresh the parent, try refreshing the current path
                    if (itemToRefresh == null && PathService.DirectoryExists(pathToSelect))
                    {
                        if (_pathToTreeViewItem.TryGetValue(pathToSelect, out itemToRefresh))
                        {
                            RefreshTreeItem(itemToRefresh);
                        }
                    }
                }

                // If we couldn't find an item to refresh, refresh root items
                if (itemToRefresh == null)
                {
                    RefreshRootItems();
                }

                // Important: Validate and fix all tree items to ensure expander triangles are correct
                foreach (var item in FindVisualChildren<TreeViewItem>(ShellTreeViewControl))
                {
                    if (item.Tag is ShellObject so)
                    {
                        string path = PathService.GetPathFromShellObject(so);
                        if (!string.IsNullOrEmpty(path) && PathService.DirectoryExists(path))
                        {
                            // If this item is in the expanded paths list, we'll expand it again
                            bool shouldBeExpanded = expandedPaths.Contains(path);

                            // But first ensure it has the correct children
                            RebuildTreeItem(item);

                            // Then restore expanded state if needed
                            if (shouldBeExpanded && preserveExpanded)
                            {
                                item.IsExpanded = true;
                            }
                        }
                    }
                }

                // Restore selection
                if (PathService.DirectoryExists(pathToSelect))
                {
                    SelectPath(pathToSelect);
                }
                else if (!string.IsNullOrEmpty(pathToSelect))
                {
                    // If the selected path doesn't exist anymore, try to select its parent
                    string parentPath = Path.GetDirectoryName(pathToSelect);
                    if (PathService.DirectoryExists(parentPath))
                    {
                        SelectPath(parentPath);
                    }
                }
            }
            catch (Exception ex)
            {
                HandleException("Error refreshing tree", ex);
            }
        }

        /// <summary>
        /// Helper method to refresh a specific TreeViewItem
        /// </summary>
        private void RefreshTreeItem(TreeViewItem item)
        {
            if (item == null) return;
            RebuildTreeItem(item);

            //bool wasExpanded = item.IsExpanded;

            //// Clear and add dummy item to force reload
            //item.Items.Clear();
            //item.Items.Add(new TreeViewItem { Header = "Loading..." });


            //// Force a refresh by toggling IsExpanded if it was already expanded
            //if (wasExpanded)
            //{
            //    item.IsExpanded = false;
            //    item.IsExpanded = true; // This will trigger TreeViewItem_Expanded event
            //}
        }

        /// <summary>
        /// Helper method to refresh root items
        /// </summary>
        private void RefreshRootItems()
        {
            // Store expanded state to restore later
            var expandedPaths = new HashSet<string>();
            foreach (var item in FindVisualChildren<TreeViewItem>(ShellTreeViewControl))
            {
                if (item.IsExpanded && item.Tag is ShellObject so)
                {
                    string path = PathService.GetPathFromShellObject(so);
                    if (!string.IsNullOrEmpty(path))
                    {
                        expandedPaths.Add(path);
                    }
                }
            }

            // Remember current selection
            string selectedPath = _selectedPath;

            // Re-initialize the tree with the same root
            InitializeShellTree();

            // Expand previously expanded paths
            foreach (var path in expandedPaths)
            {
                if (PathService.DirectoryExists(path) && _pathToTreeViewItem.TryGetValue(path, out var item))
                {
                    item.IsExpanded = true;
                }
            }

            // Restore selection
            if (PathService.DirectoryExists(selectedPath))
            {
                SelectPath(selectedPath);
            }
        }

        /// <summary>
        /// Selects a path in the tree view, expanding all necessary nodes
        /// </summary>
        public void SelectPath(string path)
        {
            if (!PathService.DirectoryExists(path))
                return;

            try
            {
                // If the path isn't within our tree, consider changing the root directory
                bool isWithinTree = IsPathWithinTreeScope(path);
                if (!isWithinTree)
                {
                    // Ask user if they want to change the root
                    var result = MessageBox.Show(
                        $"The selected path '{path}' is not within the current tree view. Do you want to change the root directory to this path?",
                        "Change Root Directory",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        // Change root to this path
                        ChangeRootDirectory(path);
                        return;
                    }
                }

                // Create a shell object from the path
                var shellObject = ShellObject.FromParsingName(path);

                // Expand all parent folders
                ExpandPathToShellObject(shellObject);

                // If we already have a TreeViewItem for this path, select it
                if (_pathToTreeViewItem.TryGetValue(path, out var treeViewItem))
                {
                    // Clear previous selections
                    ClearSelectedItems();

                    // Select the item
                    SelectItem(treeViewItem);
                    //treeViewItem.BringIntoView();

                    // Notify about the selection
                    NotifyFolderSelection(treeViewItem);
                }
            }
            catch (Exception ex)
            {
                HandleException("Error selecting path", ex, false);
            }
        }

        /// <summary>
        /// Creates a TreeViewItem for a ShellObject
        /// </summary>
        private TreeViewItem CreateShellTreeViewItem(ShellObject shellObject)
{
    // Create the tree item
    var item = new TreeViewItem
    {
        Tag = shellObject,
        IsExpanded = false
    };

    // Set header with icon and text
    item.Header = CreateShellObjectHeader(shellObject);

    try
    {
        // Only add a dummy node if this is a folder AND it contains subfolders
        if (shellObject is ShellFolder)
        {
            string path = PathService.GetPathFromShellObject(shellObject);

            // Only check for subfolders if we have a valid file system path
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                // Check directly with the file system - don't use cached information
                bool hasSubfolders = false;
                try
                {
                    string[] subdirectories = Directory.GetDirectories(path, "*", SearchOption.TopDirectoryOnly);
                    hasSubfolders = subdirectories.Length > 0;
                }
                catch (UnauthorizedAccessException)
                {
                    // For unauthorized directories, assume they might have subdirectories
                    hasSubfolders = true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error checking for subdirectories: {ex.Message}");
                    hasSubfolders = false;
                }

                if (hasSubfolders)
                {
                    // Only add the dummy node if there are actually subdirectories
                    item.Items.Add(new TreeViewItem { Header = "Loading..." });
                }
            }
            else
            {
                // For special shell folders (like This PC, etc.), add dummy node as before
                item.Items.Add(new TreeViewItem { Header = "Loading..." });
            }
        }
    }
    catch (Exception ex)
    {
        Debug.WriteLine($"Error checking for subfolders: {ex.Message}");
        // If there's an error, don't add dummy node to be safe
    }

    // Store in dictionary for quick lookups
    if (!string.IsNullOrEmpty(shellObject.ParsingName))
    {
        string path = PathService.GetPathFromShellObject(shellObject);
        if (!string.IsNullOrEmpty(path))
        {
            _pathToTreeViewItem[path] = item;
        }
    }

    return item;
}

        /// <summary>
        /// Creates a header for a ShellObject with an icon and text
        /// </summary>
        private StackPanel CreateShellObjectHeader(ShellObject shellObject)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };

            try
            {
                // Add the icon
                var smallIcon = shellObject.Thumbnail.SmallBitmapSource;
                if (smallIcon != null)
                {
                    var img = new Image
                    {
                        Source = smallIcon,
                        Width = 16,
                        Height = 16,
                        Margin = new Thickness(0, 0, 4, 0)
                    };
                    panel.Children.Add(img);
                }

                // Add the text (display name)
                var textBlock = new TextBlock
                {
                    Text = shellObject.Name,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brushes.White
                };
                panel.Children.Add(textBlock);
            }
            catch
            {
                // Fallback if thumbnail fails
                var textBlock = new TextBlock
                {
                    Text = shellObject.Name,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brushes.White
                };
                panel.Children.Add(textBlock);
            }

            return panel;
        }

        /// <summary>
        /// Helper method to safely check if a directory has subdirectories
        /// </summary>
        private bool DirectoryHasSubdirectories(string path)
        {
            try
            {
                // Use GetDirectories with explicit System.IO.SearchOption.TopDirectoryOnly
                // We only need to know if there's at least one subdirectory
                var dirs = Directory.GetDirectories(path, "*", System.IO.SearchOption.TopDirectoryOnly);
                return dirs.Length > 0;
            }
            catch (UnauthorizedAccessException)
            {
                // For unauthorized directories, assume they might have subdirectories
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking for subdirectories in {path}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Checks if a path is within the current tree scope
        /// </summary>
        private bool IsPathWithinTreeScope(string path)
        {
            if (string.IsNullOrEmpty(_rootDirectory))
                return true; // No root restriction, anything is in scope

            // Use PathService for efficient comparison
            return PathService.IsPathWithin(_rootDirectory, path);
        }

        /// <summary>
        /// Expands all parent folders to a given ShellObject
        /// </summary>
        private void ExpandPathToShellObject(ShellObject shellObject)
        {
            if (shellObject == null) return;

            try
            {
                // Get the full path using PathService
                string path = PathService.GetPathFromShellObject(shellObject);
                if (string.IsNullOrEmpty(path)) return;

                // Build a list of parent directories that need to be expanded
                var directoriesToExpand = new List<string>();
                var currentDir = new DirectoryInfo(path).Parent;

                while (currentDir != null)
                {
                    // Stop when we reach the root directory level
                    if (!string.IsNullOrEmpty(_rootDirectory) &&
                        PathService.PathsEqual(currentDir.FullName, _rootDirectory))
                        break;

                    directoriesToExpand.Insert(0, currentDir.FullName);
                    currentDir = currentDir.Parent;
                }

                // Expand the root item first
                if (ShellTreeViewControl.Items.Count > 0)
                {
                    var rootItem = ShellTreeViewControl.Items[0] as TreeViewItem;
                    if (rootItem != null)
                    {
                        rootItem.IsExpanded = true;

                        // Now find and expand each parent directory
                        foreach (var dir in directoriesToExpand)
                        {
                            // Try to find the folder in the tree
                            try
                            {
                                if (_pathToTreeViewItem.TryGetValue(dir, out var treeViewItem))
                                {
                                    treeViewItem.IsExpanded = true;
                                }
                            }
                            catch
                            {
                                // Skip if we can't find this folder
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error expanding path: {ex.Message}");
            }
        }

        /// <summary>
        /// Notifies listeners about a folder selection
        /// </summary>
        private void NotifyFolderSelection(TreeViewItem treeViewItem)
        {
            try
            {
                var shellObject = treeViewItem.Tag as ShellObject;
                if (shellObject == null) return;

                string path = PathService.GetPathFromShellObject(shellObject);
                if (!PathService.DirectoryExists(path)) return;

                _selectedPath = path;

                Debug.WriteLine($"Selected folder: {path}");

                // Create a FolderInfo for the selected path
                var folderInfo = new FolderInfo(path);

                // Notify listeners
                FolderSelected?.Invoke(folderInfo);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in folder selection notification: {ex.Message}");
            }
        }

        /// <summary>
        /// Public method to get selected folder infos for integration with menu commands
        /// </summary>
        public List<FolderInfo> GetSelectedFolderInfos()
        {
            var selectedFolders = new List<FolderInfo>();

            foreach (var item in _selectedItems)
            {
                var shellObject = item.Tag as ShellObject;
                if (shellObject != null)
                {
                    string path = PathService.GetPathFromShellObject(shellObject);
                    if (PathService.DirectoryExists(path))
                    {
                        selectedFolders.Add(new FolderInfo(path));
                    }
                }
            }
            return selectedFolders;
        }

        /// <summary>
        /// Gets the currently selected TreeViewItem
        /// </summary>
        private TreeViewItem GetSelectedTreeViewItem()
        {
            // Return the first selected item or the selected item from the TreeView
            if (_selectedItems.Count > 0)
            {
                return _selectedItems[0];
            }

            return ShellTreeViewControl.SelectedItem as TreeViewItem;
        }

        #region Selection Management

        /// <summary>
        /// Selects a TreeViewItem
        /// </summary>
        private void SelectItem(TreeViewItem item)
        {
            if (item == null) return;

            // Apply visual selection style
            item.Background = new SolidColorBrush(Color.FromArgb(120, 0, 120, 215));

            if (!_selectedItems.Contains(item))
            {
                _selectedItems.Add(item);
            }

            _lastSelectedItem = item;
        }

        /// <summary>
        /// Checks if a TreeViewItem is selected
        /// </summary>
        private bool IsItemSelected(TreeViewItem item)
        {
            return _selectedItems.Contains(item);
        }

        /// <summary>
        /// Unselects a TreeViewItem
        /// </summary>
        private void UnselectItem(TreeViewItem item)
        {
            if (item == null) return;

            // Remove visual selection style
            item.Background = null;

            if (_selectedItems.Contains(item))
            {
                _selectedItems.Remove(item);
            }
        }

        /// <summary>
        /// Clears all selected items
        /// </summary>
        private void ClearSelectedItems()
        {
            foreach (var item in _selectedItems.ToList())
            {
                item.Background = null;
            }

            _selectedItems.Clear();
        }

        /// <summary>
        /// Selects a range of TreeViewItems
        /// </summary>
        private void SelectItemRange(TreeViewItem start, TreeViewItem end)
        {
            // First, get all tree view items in the visible tree
            var allItems = GetAllVisibleTreeViewItems();

            // Find the indices of start and end items
            int startIndex = allItems.IndexOf(start);
            int endIndex = allItems.IndexOf(end);

            if (startIndex == -1 || endIndex == -1) return;

            // Ensure startIndex <= endIndex
            if (startIndex > endIndex)
            {
                int temp = startIndex;
                startIndex = endIndex;
                endIndex = temp;
            }

            // Clear previous selection
            ClearSelectedItems();

            // Select all items in the range
            for (int i = startIndex; i <= endIndex; i++)
            {
                SelectItem(allItems[i]);
            }
        }

        /// <summary>
        /// Gets all visible TreeViewItems
        /// </summary>
        private List<TreeViewItem> GetAllVisibleTreeViewItems()
        {
            var items = new List<TreeViewItem>();
            CollectVisibleTreeViewItems(ShellTreeViewControl, items);
            return items;
        }

        /// <summary>
        /// Collects all visible TreeViewItems recursively
        /// </summary>
        private void CollectVisibleTreeViewItems(ItemsControl container, List<TreeViewItem> items)
        {
            foreach (var item in container.Items)
            {
                var treeViewItem = container.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
                if (treeViewItem != null)
                {
                    items.Add(treeViewItem);

                    if (treeViewItem.IsExpanded)
                    {
                        CollectVisibleTreeViewItems(treeViewItem, items);
                    }
                }
            }
        }

        /// <summary>
        /// Collapses a directory in the tree view
        /// </summary>
        /// <param name="directoryPath">The path of the directory to collapse</param>
        /// <returns>True if the directory was successfully collapsed, false otherwise</returns>
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
        /// Finds a TreeViewItem by its path
        /// </summary>
        /// <param name="path">The path to find</param>
        /// <returns>The found TreeViewItem or null if not found</returns>
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

        /// <summary>
        /// Recursively searches for a TreeViewItem by its path
        /// </summary>
        /// <param name="parentItem">The parent item to search within</param>
        /// <param name="path">The path to find</param>
        /// <returns>The found TreeViewItem or null if not found</returns>
        private TreeViewItem FindTreeViewItemByPathRecursive(TreeViewItem parentItem, string path)
        {
            // Check if this is the item we're looking for
            if (parentItem.Tag is ShellObject shellObject)
            {
                string itemPath = PathService.GetPathFromShellObject(shellObject);
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
            // Clear current selection
            ClearSelectedItems();

            // Get all visible items and select them
            var allVisibleItems = GetAllVisibleTreeViewItems();
            foreach (var item in allVisibleItems)
            {
                SelectItem(item);
            }

            // Set the last selected item
            if (allVisibleItems.Count > 0)
            {
                _lastSelectedItem = allVisibleItems.Last();
            }
        }

        #endregion

        #region Drag & Drop Support

        /// <summary>
        /// Gets the TreeViewItem under the mouse
        /// </summary>
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

        /// <summary>
        /// Highlights a TreeViewItem as a drop target
        /// </summary>
        private void HighlightDropTarget(TreeViewItem item)
        {
            // Clear previous highlights
            ClearDropTargetHighlight();

            if (item != null)
            {
                // Add drop target highlight style
                item.Background = new SolidColorBrush(Color.FromArgb(80, 0, 120, 215));
            }
        }

        /// <summary>
        /// Clears all drop target highlights
        /// </summary>
        private void ClearDropTargetHighlight()
        {
            // Find all TreeViewItems and clear their background
            var allItems = FindVisualChildren<TreeViewItem>(ShellTreeViewControl);
            foreach (var item in allItems)
            {
                // Skip items that are in the selection
                if (!_selectedItems.Contains(item))
                {
                    item.Background = null;
                }
            }
        }

        /// <summary>
        /// Finds a parent of type T in the visual tree
        /// </summary>
        private T FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null && !(current is T))
            {
                current = VisualTreeHelper.GetParent(current);
            }
            return current as T;
        }

        /// <summary>
        /// Finds all visual children of type T in the visual tree
        /// </summary>
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

        private TreeViewItem FindParentTreeViewItem(TreeViewItem item)
        {
            DependencyObject parent = VisualTreeHelper.GetParent(item);
            while (parent != null && !(parent is TreeViewItem))
            {
                parent = VisualTreeHelper.GetParent(parent);
            }
            return parent as TreeViewItem;
        }

        /// <summary>
        /// Standardized exception handling
        /// </summary>
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

        /// <summary>
        /// Handles the TreeViewItem.Expanded event
        /// </summary>
        private void TreeViewItem_Expanded(object sender, RoutedEventArgs e)
        {
            var treeViewItem = sender as TreeViewItem;
            if (treeViewItem == null) return;

            var shellObject = treeViewItem.Tag as ShellObject;
            if (shellObject == null) return;

            // Add the path to expanded paths set
            if (!string.IsNullOrEmpty(shellObject.ParsingName))
            {
                _expandedPaths.Add(shellObject.ParsingName);
            }

            // Check if this is the first expansion (contains dummy node)
            if (treeViewItem.Items.Count == 1 &&
                (treeViewItem.Items[0] as TreeViewItem)?.Header as string == "Loading...")
            {
                // Clear the dummy node
                treeViewItem.Items.Clear();

                // Get the shell folder
                var shellFolder = shellObject as ShellFolder;
                if (shellFolder != null)
                {
                    try
                    {
                        // Add child folders
                        foreach (var childObject in shellFolder.Where(child => child is ShellFolder)
                                                             .OrderBy(child => child.Name))
                        {
                            try
                            {
                                var childItem = CreateShellTreeViewItem(childObject);
                                treeViewItem.Items.Add(childItem);
                            }
                            catch
                            {
                                // Skip items that cause errors
                            }
                        }

                        // Watch this folder for changes
                        string path = PathService.GetPathFromShellObject(shellObject);
                        if (PathService.DirectoryExists(path))
                        {
                            var folder = new FolderInfo(path);
                            if (ViewModel != null)
                            {
                                // Tell ViewModel that folder was expanded
                                ViewModel.FolderExpanded(folder);

                                // Watch all immediate child folders
                                foreach (var childItem in treeViewItem.Items)
                                {
                                    var childTreeItem = childItem as TreeViewItem;
                                    if (childTreeItem != null)
                                    {
                                        var childShellObject = childTreeItem.Tag as ShellObject;
                                        if (childShellObject != null)
                                        {
                                            string childPath = PathService.GetPathFromShellObject(childShellObject);
                                            if (PathService.DirectoryExists(childPath))
                                            {
                                                var childFolder = new FolderInfo(childPath);
                                                ViewModel.WatchFolder(childFolder);

                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error expanding tree: {ex.Message}");
                    }
                }
            }

            e.Handled = true; // Prevent event bubbling
        }

        /// <summary>
        /// Handles the TreeView.SelectedItemChanged event
        /// </summary>
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

        /// <summary>
        /// Modified mouse handling with double-click support
        /// </summary>
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
                        var modifiers = Keyboard.Modifiers;
                        _isMultiSelectActive = true;

                        if (modifiers == ModifierKeys.Control)
                        {
                            // Toggle selection of the clicked item
                            if (IsItemSelected(treeViewItem))
                            {
                                UnselectItem(treeViewItem);
                            }
                            else
                            {
                                SelectItem(treeViewItem);
                                _lastSelectedItem = treeViewItem;
                            }
                            e.Handled = true;
                        }
                        else if (modifiers == ModifierKeys.Shift && _lastSelectedItem != null)
                        {
                            // Select range between last selected item and current item
                            SelectItemRange(_lastSelectedItem, treeViewItem);
                            e.Handled = true;
                        }
                        else if (modifiers == ModifierKeys.None)
                        {
                            // Single selection (clear others)
                            if (!IsItemSelected(treeViewItem))
                            {
                                ClearSelectedItems();
                                SelectItem(treeViewItem);
                                _lastSelectedItem = treeViewItem;

                                // Notify about selection (but don't load images)
                                NotifyFolderSelectionWithoutLoading(treeViewItem);
                            }

                            // Don't mark as handled to allow drag operations
                        }

                        _isMultiSelectActive = false;
                    }
                }
            }
        }

        /// <summary>
        /// Handles double-click on a folder
        /// </summary>
        private void HandleFolderDoubleClick(TreeViewItem treeViewItem)
        {
            try
            {
                var shellObject = treeViewItem.Tag as ShellObject;
                if (shellObject == null) return;

                string path = PathService.GetPathFromShellObject(shellObject);
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

        /// <summary>
        /// Notifies about folder selection without loading images
        /// </summary>
        private void NotifyFolderSelectionWithoutLoading(TreeViewItem treeViewItem)
        {
            try
            {
                var shellObject = treeViewItem.Tag as ShellObject;
                if (shellObject == null) return;

                string path = PathService.GetPathFromShellObject(shellObject);
                if (string.IsNullOrEmpty(path)) return;

                _selectedPath = path;

                Debug.WriteLine($"Selected folder (without loading images): {path}");

                // Create a FolderInfo for the selected path
                var folderInfo = new FolderInfo(path);

                // Notify listeners to update without loading images
                if (ViewModel != null)
                {
                    // Update UI without loading images
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

        /// <summary>
        /// Loads images for the specified folder
        /// </summary>
        private void LoadImagesForFolder(FolderInfo folder)
        {
            if (folder == null) return;

            if (ViewModel != null)
            {
                // Call the ViewModel method to load images
                ViewModel.LoadImagesForSelectedFolderAsync();
            }
            else
            {
                // Fallback to just selecting folder if ViewModel is not available
                FolderSelected?.Invoke(folder);
            }
        }

        /// <summary>
        /// Modified context menu creation to include "Load Images" option
        /// </summary>
        private void ShellTreeViewControl_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            Debug.WriteLine("Context menu opening");

            // Get the current tree view item under cursor
            Point position = Mouse.GetPosition(ShellTreeViewControl);
            var item = GetTreeViewItemUnderMouse(position);

            if (item == null)
            {
                e.Handled = true;
                return;
            }

            // If the item under cursor is not already selected, select it only
            if (!IsItemSelected(item))
            {
                ClearSelectedItems();
                SelectItem(item);

                // Update selected folder but don't load images
                NotifyFolderSelectionWithoutLoading(item);
            }

            // Get the selected folders
            var selectedFolders = GetSelectedFolderInfos();
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
            var deleteItem = new MenuItem { Header = deleteItemText , InputGestureText = "Delete" };
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

        /// <summary>
        /// Handles the TreeView.KeyDown event for keyboard shortcuts
        /// </summary>
        private void ShellTreeView_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete)
            {
                if (_selectedItems.Count > 1)
                {
                    MultiFolderDelete_Click(sender, new RoutedEventArgs());
                }
                else
                {
                    Delete_Click(sender, new RoutedEventArgs());
                }
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
                MultiFolderCut_Click(sender, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                MultiFolderCopy_Click(sender, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                Paste_Click(sender, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                // Select all visible items
                SelectAllVisibleItems();
                e.Handled = true;
            }
        }

        /// <summary>
        /// Handles the TreeView.PreviewMouseLeftButtonDown event for drag & drop
        /// </summary>
        private void TreeView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Store the mouse position for potential drag operation
            _startPoint = e.GetPosition(null);
            _mouseDownTime = DateTime.Now; // Record when mouse was pressed

            // Handle multi-selection
            TreeView_PreviewMouseDown(sender, e);
        }

        /// <summary>
        /// Handles the TreeView.PreviewMouseMove event for drag & drop
        /// </summary>
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
                        if (item != null && item.Tag is ShellObject)
                        {
                            StartDrag(e);
                        }
                    }
                }
            }
        }


        /// <summary>
        /// Handles the TreeView.PreviewMouseLeftButtonUp event for drag & drop
        /// </summary>
        private void TreeView_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isDragging = false;
        }

        /// <summary>
        /// Starts a drag operation
        /// </summary>
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

            // If we're dragging multiple items, we'll need to pass all paths
            if (_selectedItems.Count == 1)
            {
                // Single item drag (existing behavior)
                var draggedItem = _selectedItems[0];
                _draggedItem = draggedItem;
                _draggedShellObject = draggedItem.Tag as ShellObject;

                if (_draggedShellObject == null) return;

                string path = PathService.GetPathFromShellObject(_draggedShellObject);
                if (!PathService.DirectoryExists(path)) return;

                // Don't allow dragging the root directory
                if (!string.IsNullOrEmpty(_rootDirectory) &&
                   PathService.PathsEqual(path, _rootDirectory))
                {
                    return;
                }

                // Create drag data with one path
                DataObject dragData = new DataObject("FileDrop", new string[] { path });
                DragDrop.DoDragDrop(ShellTreeViewControl, dragData, DragDropEffects.Move | DragDropEffects.Copy);
            }
            else
            {
                // Multi-item drag
                var paths = new List<string>();

                foreach (var item in _selectedItems)
                {
                    var shellObject = item.Tag as ShellObject;
                    if (shellObject != null)
                    {
                        string path = PathService.GetPathFromShellObject(shellObject);
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
                    // Create drag data with multiple paths
                    DataObject dragData = new DataObject("FileDrop", paths.ToArray());
                    DragDrop.DoDragDrop(ShellTreeViewControl, dragData, DragDropEffects.Move | DragDropEffects.Copy);
                }
            }
        }

        /// <summary>
        /// Handles the TreeView.DragOver event
        /// </summary>
        private void TreeView_DragOver(object sender, DragEventArgs e)
        {
            // Check if the data format is supported
            if (!e.Data.GetDataPresent("FileDrop"))
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            // Get the item under the cursor
            var targetItem = GetTreeViewItemUnderMouse(e.GetPosition(ShellTreeViewControl));
            if (targetItem == null)
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            var targetShellObject = targetItem.Tag as ShellObject;
            if (targetShellObject == null)
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            string targetPath = PathService.GetPathFromShellObject(targetShellObject);
            if (!PathService.DirectoryExists(targetPath))
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            // Get the source paths
            var filePaths = e.Data.GetData("FileDrop") as string[];
            if (filePaths == null || filePaths.Length == 0)
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            // Check each source path against the target
            foreach (string sourcePath in filePaths)
            {
                // Check if we're trying to drop into itself or its child using PathService
                if (PathService.PathsEqual(sourcePath, targetPath) ||
                    PathService.IsPathWithin(sourcePath, targetPath))
                {
                    e.Effects = DragDropEffects.None;
                    e.Handled = true;
                    return;
                }
            }

            // Determine if this is a copy or move operation - default to MOVE
            // unless Ctrl key is pressed for COPY
            if ((e.KeyStates & DragDropKeyStates.ControlKey) == DragDropKeyStates.ControlKey)
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.Move; // Default to MOVE
            }

            e.Handled = true;

            // Highlight the drop target
            HighlightDropTarget(targetItem);
        }

        /// <summary>
        /// Handles the TreeView.Drop event
        /// </summary>
        private void TreeView_Drop(object sender, DragEventArgs e)
        {
            // Clear highlight
            ClearDropTargetHighlight();

            if (!e.Data.GetDataPresent("FileDrop"))
                return;

            // Get the drop target
            var targetItem = GetTreeViewItemUnderMouse(e.GetPosition(ShellTreeViewControl));
            if (targetItem == null) return;

            var targetShellObject = targetItem.Tag as ShellObject;
            if (targetShellObject == null) return;

            string targetPath = PathService.GetPathFromShellObject(targetShellObject);
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
                    // Perform operation based on number of items
                    if (filePaths.Length == 1)
                    {
                        // Single folder operation
                        string sourcePath = filePaths[0];
                        if (!PathService.DirectoryExists(sourcePath)) return;

                        var sourceFolder = new FolderInfo(sourcePath);

                        if (isCopy)
                        {
                            ViewModel.CopyFolder(sourceFolder);
                            ViewModel.PasteFolder(targetFolder);
                        }
                        else
                        {
                            ViewModel.MoveFolder(sourceFolder, targetFolder);
                        }
                    }
                    else
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

                        if (sourceFolders.Count > 0)
                        {
                            if (isCopy)
                            {
                                ViewModel.CopyMultipleFolders(sourceFolders);
                                ViewModel.PasteFolder(targetFolder);
                            }
                            else
                            {
                                ViewModel.MoveMultipleFolders(sourceFolders, targetFolder);
                            }
                        }
                    }

                    // After the operation completes, invalidate path cache again for the target folder
                    PathService.InvalidatePathCache(targetPath, true);

                    // Also invalidate path cache for all source parent paths
                    foreach (var parentPath in sourceParentPaths)
                    {
                        PathService.InvalidatePathCache(parentPath, true);
                    }

                    // Refresh the tree with proper cache state
                    RefreshTree(targetPath, true);

                    // Also refresh all source parent paths
                    foreach (var parentPath in sourceParentPaths)
                    {
                        if (PathService.DirectoryExists(parentPath))
                        {
                            RefreshTree(parentPath, true);
                        }
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
        }


        #endregion

        #region Context Menu Action Handlers

        /// <summary>
        /// Handles the "New Folder" context menu item click
        /// </summary>
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

                var shellObject = treeViewItem.Tag as ShellObject;
                if (shellObject == null)
                {
                    Debug.WriteLine("Selected item has no ShellObject");
                    return;
                }

                string path = PathService.GetPathFromShellObject(shellObject);
                if (!PathService.DirectoryExists(path))
                {
                    Debug.WriteLine($"Invalid path: {path}");
                    return;
                }

                // Create FolderInfo and call ViewModel
                var folderInfo = new FolderInfo(path);

                if (ViewModel != null)
                {
                    Debug.WriteLine($"Calling ViewModel.CreateNewFolder for {path}");
                    ViewModel.CreateNewFolder(folderInfo);
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

        /// <summary>
        /// Handles the "Batch Tags" context menu item click
        /// </summary>
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
                    ViewModel.BatchUpdateTags(selectedFolders);
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

        /// <summary>
        /// Handles the "Cut" context menu item click for multiple folders
        /// </summary>
        private void MultiFolderCut_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selectedFolders = GetSelectedFolderInfos();
                if (selectedFolders.Count == 0) return;

                if (ViewModel != null)
                {
                    ViewModel.CutMultipleFolders(selectedFolders);
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

        /// <summary>
        /// Handles the "Copy" context menu item click for multiple folders
        /// </summary>
        private void MultiFolderCopy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selectedFolders = GetSelectedFolderInfos();
                if (selectedFolders.Count == 0) return;

                if (ViewModel != null)
                {
                    ViewModel.CopyMultipleFolders(selectedFolders);
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

        /// <summary>
        /// Handles the "Paste" context menu item click
        /// </summary>
        private void Paste_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Debug.WriteLine("Paste_Click handler called");

                var treeViewItem = GetSelectedTreeViewItem();
                if (treeViewItem == null) return;

                var shellObject = treeViewItem.Tag as ShellObject;
                if (shellObject == null) return;

                string path = PathService.GetPathFromShellObject(shellObject);
                if (!PathService.DirectoryExists(path)) return;

                // Store expanded state
                var expandedItems = new HashSet<string>();
                foreach (var item in FindVisualChildren<TreeViewItem>(ShellTreeViewControl))
                {
                    if (item.IsExpanded && item.Tag is ShellObject so)
                    {
                        string expandedPath = PathService.GetPathFromShellObject(so);
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
                        ViewModel.PasteFolder(folderInfo);

                        // First refresh the source directory (if different from destination)
                        if (!string.IsNullOrEmpty(sourceDir) &&
                            !path.Equals(sourceDir, StringComparison.OrdinalIgnoreCase) &&
                            PathService.DirectoryExists(sourceDir))
                        {
                            RefreshTree(sourceDir, true);
                        }

                        RefreshTree(path, true);
                           
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

        /// <summary>
        /// Handles the "Rename" context menu item click
        /// </summary>
        private void Rename_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Debug.WriteLine("Rename_Click handler called");

                var treeViewItem = GetSelectedTreeViewItem();
                if (treeViewItem == null) return;

                var shellObject = treeViewItem.Tag as ShellObject;
                if (shellObject == null) return;

                string path = PathService.GetPathFromShellObject(shellObject);
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
                    ViewModel.RenameFolder(folderInfo);

                    // Update tree after rename
                    Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        RefreshTree(folderInfo.FolderPath, true);
                    }, DispatcherPriority.Normal);
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

        /// <summary>
        /// Handles the "Delete" context menu item click
        /// </summary>
        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Debug.WriteLine("Delete_Click handler called");

                var treeViewItem = GetSelectedTreeViewItem();
                if (treeViewItem == null) return;

                var shellObject = treeViewItem.Tag as ShellObject;
                if (shellObject == null) return;

                string path = PathService.GetPathFromShellObject(shellObject);
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
                    ViewModel.DeleteFolderCommand.Execute(folderInfo);                 
                    RefreshTree(parentPath, true);

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
        private void MultiFolderDelete_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selectedFolders = GetSelectedFolderInfos();
                if (selectedFolders.Count == 0) return;

                if (ViewModel != null)
                {
                    // Execute delete operation through ViewModel
                    ViewModel.DeleteMultipleFolders(selectedFolders);

                    // Clear selection and refresh tree
                    ClearSelectedItems();
                    RefreshTree();
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

                var shellObject = treeViewItem.Tag as ShellObject;
                if (shellObject == null) return;

                string path = PathService.GetPathFromShellObject(shellObject);
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
    }
}