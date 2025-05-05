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
using Microsoft.VisualBasic.FileIO;
using Microsoft.WindowsAPICodePack.Shell;
using Microsoft.WindowsAPICodePack.Shell.PropertySystem;

namespace ImageFolderManager.Views
{
    public partial class ShellTreeView : UserControl
    {
        // Event to notify when a folder is selected
        public event Action<FolderInfo> FolderSelected;

        // Reference to the main view model - add debug logging
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
                    if (DataContext != null)
                        Debug.WriteLine($"DataContext is of type: {DataContext.GetType().FullName}");
                    else
                        Debug.WriteLine("DataContext is null");
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

        public ShellTreeView()
        {
            InitializeComponent();

            // Add DataContext change handler to ensure MainViewModel is always accessible
            this.DataContextChanged += (s, e) => {
                if (e.NewValue is MainViewModel)
                {
                    Debug.WriteLine("ShellTreeView received correct DataContext (MainViewModel)");
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

            // Manually bind context menu event in case XAML binding doesn't work
            ShellTreeViewControl.ContextMenuOpening += ShellTreeViewControl_ContextMenuOpening;

            // Other initialization code
            LoadDefaultRootDirectoryAsync();
        }

        private async void LoadDefaultRootDirectoryAsync()
        {
            if (!string.IsNullOrEmpty(AppSettings.Instance.DefaultRootDirectory))
            {
                try
                {
                    await Task.Delay(100); // Brief delay to ensure component is fully loaded

                    // Set the root directory from AppSettings
                    _rootDirectory = AppSettings.Instance.DefaultRootDirectory;

                    // Initialize the shell tree
                    InitializeShellTree();

                    // Select the path in the shell tree view
                    SelectPath(AppSettings.Instance.DefaultRootDirectory);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error loading default root directory: {ex.Message}");
                }
            }
        }

        private void ShellTreeViewControl_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            Debug.WriteLine("ShellTreeView DataContext changed");
            Debug.WriteLine($"Old value: {(e.OldValue != null ? e.OldValue.GetType().FullName : "null")}");
            Debug.WriteLine($"New value: {(e.NewValue != null ? e.NewValue.GetType().FullName : "null")}");

            if (e.NewValue is MainViewModel)
            {
                var vm = e.NewValue as MainViewModel;
                Debug.WriteLine("DataContext is MainViewModel");

                if (!string.IsNullOrEmpty(AppSettings.Instance.DefaultRootDirectory) &&
                    Directory.Exists(AppSettings.Instance.DefaultRootDirectory) &&
                    _rootDirectory != AppSettings.Instance.DefaultRootDirectory)
                {
                    // Root directory has changed in settings
                    ChangeRootDirectory(AppSettings.Instance.DefaultRootDirectory);
                }
            }
        }

        private void InitializeShellTree()
        {
            try
            {
                ShellTreeViewControl.Items.Clear();
                _pathToTreeViewItem.Clear();

                if (!string.IsNullOrEmpty(_rootDirectory) && Directory.Exists(_rootDirectory))
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
                Debug.WriteLine($"Error initializing shell tree: {ex.Message}");
                MessageBox.Show($"Error initializing shell tree: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
                if (!Directory.Exists(newRootDirectory))
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

                // Update view
                RefreshTree();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error changing root directory: {ex.Message}");
                MessageBox.Show($"Error changing root directory: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

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
                if (shellObject is ShellFolder shellFolder)
                {
                    string path = GetPathFromShellObject(shellObject);

                    // Only check for subfolders if we have a valid file system path
                    if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                    {
                        // Check if this folder actually has any subfolders before adding the dummy node
                        bool hasSubfolders = DirectoryHasSubdirectories(path);

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
                string path = GetPathFromShellObject(shellObject);
                if (!string.IsNullOrEmpty(path))
                {
                    _pathToTreeViewItem[path] = item;
                }
            }

            return item;
        }

        // Helper method to safely check if a directory has subdirectories
        // Compatible with .NET Framework 4.8
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
                // This gives a visual cue that the folder might contain something even if we can't access it
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking for subdirectories in {path}: {ex.Message}");
                return false;
            }
        }

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
                        string path = GetPathFromShellObject(shellObject);
                        if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                        {
                            var folder = new FolderInfo(path);
                            if (ViewModel != null)
                            {
                                ViewModel._fileSystemWatcher.WatchFolder(folder);

                                // Watch all immediate child folders
                                foreach (var childItem in treeViewItem.Items)
                                {
                                    var childTreeItem = childItem as TreeViewItem;
                                    if (childTreeItem != null)
                                    {
                                        var childShellObject = childTreeItem.Tag as ShellObject;
                                        if (childShellObject != null)
                                        {
                                            string childPath = GetPathFromShellObject(childShellObject);
                                            if (!string.IsNullOrEmpty(childPath) && Directory.Exists(childPath))
                                            {
                                                var childFolder = new FolderInfo(childPath);
                                                ViewModel._fileSystemWatcher.WatchFolder(childFolder);
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

        private string GetPathFromShellObject(ShellObject shellObject)
        {
            try
            {
                if (shellObject.IsFileSystemObject)
                {
                    return shellObject.ParsingName;
                }
            }
            catch { }
            return null;
        }

        public void SelectPath(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                return;

            try
            {
                // If the path isn't within our tree (might be outside root directory),
                // consider changing the root directory
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
                    else
                    {
                        // User doesn't want to change root, we'll attempt to expand to this path anyway
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
                    treeViewItem.BringIntoView();

                    // Notify about the selection
                    NotifyFolderSelection(treeViewItem);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error selecting path: {ex.Message}");
            }
        }

        private bool IsPathWithinTreeScope(string path)
        {
            if (string.IsNullOrEmpty(_rootDirectory))
                return true; // No root restriction, anything is in scope

            // Check if path is within the root directory
            return path.StartsWith(_rootDirectory, StringComparison.OrdinalIgnoreCase) ||
                   path.Equals(_rootDirectory, StringComparison.OrdinalIgnoreCase);
        }

        private void ExpandPathToShellObject(ShellObject shellObject)
        {
            if (shellObject == null) return;

            try
            {
                // Get the full path
                string path = GetPathFromShellObject(shellObject);
                if (string.IsNullOrEmpty(path)) return;

                // Build a list of parent directories that need to be expanded
                var directoriesToExpand = new List<string>();
                var currentDir = new DirectoryInfo(path).Parent;

                while (currentDir != null)
                {
                    // Stop when we reach the root directory level
                    if (!string.IsNullOrEmpty(_rootDirectory) &&
                        currentDir.FullName.Equals(_rootDirectory, StringComparison.OrdinalIgnoreCase))
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
                                var dirShellObject = ShellObject.FromParsingName(dir);
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

        // Multi-select support - Custom mouse event handlers

        private void TreeView_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
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

                                // Notify about the selection
                                NotifyFolderSelection(treeViewItem);
                            }

                            // Don't mark as handled to allow drag operations
                        }

                        _isMultiSelectActive = false;
                    }
                }
            }
        }

        // Helper methods for multi-selection

        private bool IsItemSelected(TreeViewItem item)
        {
            return _selectedItems.Contains(item);
        }

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

        private void ClearSelectedItems()
        {
            foreach (var item in _selectedItems.ToList())
            {
                item.Background = null;
            }

            _selectedItems.Clear();
        }

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

        private List<TreeViewItem> GetAllVisibleTreeViewItems()
        {
            var items = new List<TreeViewItem>();
            CollectVisibleTreeViewItems(ShellTreeViewControl, items);
            return items;
        }

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

        private void NotifyFolderSelection(TreeViewItem treeViewItem)
        {
            var shellObject = treeViewItem.Tag as ShellObject;
            if (shellObject == null) return;

            string path = GetPathFromShellObject(shellObject);
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

            _selectedPath = path;

            Debug.WriteLine($"Selected folder: {path}");

            // Create a FolderInfo for the selected path
            var folderInfo = new FolderInfo(path);

            // Notify listeners
            FolderSelected?.Invoke(folderInfo);
        }

        private List<FolderInfo> GetSelectedFolderInfos()
        {
            var selectedFolders = new List<FolderInfo>();

            foreach (var item in _selectedItems)
            {
                var shellObject = item.Tag as ShellObject;
                if (shellObject != null)
                {
                    string path = GetPathFromShellObject(shellObject);
                    if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                    {
                        selectedFolders.Add(new FolderInfo(path));
                    }
                }
            }

            return selectedFolders;
        }

        private T FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null && !(current is T))
            {
                current = VisualTreeHelper.GetParent(current);
            }
            return current as T;
        }

        // Context menu handlers with multi-selection support
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

            // Add menu items for both single and multi-selection
            if (selectedFolders.Count == 1)
            {
                // Single selection menu
                var newFolderItem = new MenuItem { Header = "New Folder" };
                newFolderItem.Click += (s, args) =>
                {
                    Debug.WriteLine("New Folder clicked");
                    NewFolder_Click(s, args);
                };
                contextMenu.Items.Add(newFolderItem);
            }

            // Common operations for both single and multi-selections
            var cutItem = new MenuItem { Header = "Cut" };
            cutItem.Click += (s, args) =>
            {
                Debug.WriteLine("Cut clicked");
                MultiFolderCut_Click(s, args);
            };
            contextMenu.Items.Add(cutItem);

            var copyItem = new MenuItem { Header = "Copy" };
            copyItem.Click += (s, args) =>
            {
                Debug.WriteLine("Copy clicked");
                MultiFolderCopy_Click(s, args);
            };
            contextMenu.Items.Add(copyItem);

            var pasteItem = new MenuItem { Header = "Paste" };
            pasteItem.Click += (s, args) =>
            {
                Debug.WriteLine("Paste clicked");
                Paste_Click(s, args);
            };
            pasteItem.IsEnabled = ViewModel != null && ViewModel.HasClipboardContent();
            contextMenu.Items.Add(pasteItem);

            contextMenu.Items.Add(new Separator());

            if (selectedFolders.Count == 1)
            {
                // Single selection specific actions
                var renameItem = new MenuItem { Header = "Rename" };
                renameItem.Click += (s, args) =>
                {
                    Debug.WriteLine("Rename clicked");
                    Rename_Click(s, args);
                };
                contextMenu.Items.Add(renameItem);
            }

            var deleteItemText = selectedFolders.Count > 1 ? $"Delete ({selectedFolders.Count} items)" : "Delete";
            var deleteItem = new MenuItem { Header = deleteItemText };
            deleteItem.Click += (s, args) =>
            {
                Debug.WriteLine("Delete clicked");
                MultiFolderDelete_Click(s, args);
            };
            contextMenu.Items.Add(deleteItem);

            contextMenu.Items.Add(new Separator());

            if (selectedFolders.Count == 1)
            {
                // Show in Explorer only for single selection
                var showItem = new MenuItem { Header = "Show in Explorer" };
                showItem.Click += (s, args) =>
                {
                    Debug.WriteLine("Show in Explorer clicked");
                    ShowInExplorer_Click(s, args);
                };
                contextMenu.Items.Add(showItem);
            }

            // Set the context menu
            ShellTreeViewControl.ContextMenu = contextMenu;
        }

        // Multi-selection operations
        private void MultiFolderCut_Click(object sender, RoutedEventArgs e)
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

        private void MultiFolderCopy_Click(object sender, RoutedEventArgs e)
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

        private void MultiFolderDelete_Click(object sender, RoutedEventArgs e)
        {
            var selectedFolders = GetSelectedFolderInfos();
            if (selectedFolders.Count == 0) return;

            if (ViewModel != null)
            {
                // ViewModel.DeleteMultipleFolders 已经包含确认对话框和所有业务逻辑
                ViewModel.DeleteMultipleFolders(selectedFolders);

                // 清空选择并刷新树
                ClearSelectedItems();
                RefreshTree();
            }
            else
            {
                MessageBox.Show("Could not delete folders: ViewModel is not available.",
                    "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void NewFolder_Click(object sender, RoutedEventArgs e)
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

            string path = GetPathFromShellObject(shellObject);
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                Debug.WriteLine($"Invalid path: {path}");
                return;
            }

            // 创建 FolderInfo 并调用 ViewModel
            var folderInfo = new FolderInfo(path);

            if (ViewModel != null)
            {
                Debug.WriteLine($"Calling ViewModel.CreateNewFolder for {path}");
                ViewModel.CreateNewFolder(folderInfo);

                // CreateNewFolder 方法会自动处理树形结构的更新
                // 这里我们就不需要手动刷新树了
            }
            else
            {
                Debug.WriteLine("ViewModel is null");
                MessageBox.Show("Could not create folder: ViewModel is not available.",
                    "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Cut_Click(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("Cut_Click handler called");

            var treeViewItem = GetSelectedTreeViewItem();
            if (treeViewItem == null) return;

            var shellObject = treeViewItem.Tag as ShellObject;
            if (shellObject == null) return;

            string path = GetPathFromShellObject(shellObject);
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

            var folderInfo = new FolderInfo(path);

            if (ViewModel != null)
            {
                Debug.WriteLine($"Calling ViewModel.CutFolder for {path}");
                ViewModel.CutFolder(folderInfo);
            }
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("Copy_Click handler called");

            var treeViewItem = GetSelectedTreeViewItem();
            if (treeViewItem == null) return;

            var shellObject = treeViewItem.Tag as ShellObject;
            if (shellObject == null) return;

            string path = GetPathFromShellObject(shellObject);
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

            var folderInfo = new FolderInfo(path);

            if (ViewModel != null)
            {
                Debug.WriteLine($"Calling ViewModel.CopyFolder for {path}");
                ViewModel.CopyFolder(folderInfo);
       
            }
            else
            {
                Debug.WriteLine("ViewModel is null");
                MessageBox.Show("Could not copy folder: ViewModel is not available.",
                    "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // 
        private void Paste_Click(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("Paste_Click handler called");

            var treeViewItem = GetSelectedTreeViewItem();
            if (treeViewItem == null) return;

            var shellObject = treeViewItem.Tag as ShellObject;
            if (shellObject == null) return;

            string path = GetPathFromShellObject(shellObject);
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

            // 存储展开的状态，以在粘贴操作前保存
            var expandedItems = new HashSet<string>();
            foreach (var item in FindVisualChildren<TreeViewItem>(ShellTreeViewControl))
            {
                if (item.IsExpanded && item.Tag is ShellObject so)
                {
                    string expandedPath = GetPathFromShellObject(so);
                    if (!string.IsNullOrEmpty(expandedPath))
                    {
                        expandedItems.Add(expandedPath);
                    }
                }
            }

            // 创建目标文件夹的 FolderInfo
            var folderInfo = new FolderInfo(path);

            if (ViewModel != null)
            {
                Debug.WriteLine($"Calling ViewModel.PasteFolder for {path}");

                if (ViewModel.HasClipboardContent())
                {
                    // 保存剪切操作的源路径，用于后续刷新
                    string sourceParentPath = null;
                    FolderInfo sourceFolder = null;

                    if (ViewModel.ClipboardFolder != null)
                    {
                        sourceFolder = ViewModel.ClipboardFolder;
                        if (sourceFolder.Parent != null)
                        {
                            sourceParentPath = sourceFolder.Parent.FolderPath;
                        }
                        else
                        {
                            sourceParentPath = Path.GetDirectoryName(sourceFolder.FolderPath);
                        }
                    }

                    ViewModel.PasteFolder(folderInfo);

                    // 延迟刷新树形视图，让用户看到粘贴操作的状态信息
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    timer.Tick += (s, args) =>
                    {
                        timer.Stop();

                        // 如果是剪切操作，刷新源父目录
                        if (treeViewItem.IsExpanded)
                        {
                            treeViewItem.Items.Clear();
                            treeViewItem.Items.Add(new TreeViewItem { Header = "Loading..." });
                            treeViewItem.IsExpanded = false;
                            treeViewItem.IsExpanded = true;
                        }

                        if (ViewModel.IsCutOperation && !string.IsNullOrEmpty(sourceParentPath) && Directory.Exists(sourceParentPath))
                        {
                            Debug.WriteLine($"Refreshing source parent folder: {sourceParentPath}");

                            if (_pathToTreeViewItem.TryGetValue(sourceParentPath, out var sourceParentItem) && sourceParentItem != null)
                            {
                                if (sourceParentItem.IsExpanded)
                                {
                                    sourceParentItem.Items.Clear();
                                    sourceParentItem.Items.Add(new TreeViewItem { Header = "Loading..." });
                                    sourceParentItem.IsExpanded = false;
                                    sourceParentItem.IsExpanded = true;
                                }
                            }
                        }

                        RefreshTree();

                        // 恢复展开的状态
                        foreach (var expandedPath in expandedItems)
                        {
                            if (_pathToTreeViewItem.TryGetValue(expandedPath, out var item))
                            {
                                item.IsExpanded = true;
                            }
                        }
                    };
                    timer.Start();
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

        private void Rename_Click(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("Rename_Click handler called");

            var treeViewItem = GetSelectedTreeViewItem();
            if (treeViewItem == null) return;

            var shellObject = treeViewItem.Tag as ShellObject;
            if (shellObject == null) return;

            string path = GetPathFromShellObject(shellObject);
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

            // 不允许重命名根目录
            if (!string.IsNullOrEmpty(_rootDirectory) &&
                path.Equals(_rootDirectory, StringComparison.OrdinalIgnoreCase))
            {
                Debug.WriteLine("Cannot rename root directory");
                MessageBox.Show("Cannot rename the root directory.",
                    "Rename Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 保存旧路径和树形视图项
            string oldPath = path;
            var oldItem = treeViewItem;
            bool wasExpanded = oldItem.IsExpanded;
            var parentItem = FindParentTreeViewItem(oldItem);

            // 创建 FolderInfo 并调用 ViewModel
            var folderInfo = new FolderInfo(path);

            if (ViewModel != null)
            {
                Debug.WriteLine($"Calling ViewModel.RenameFolder for {path}");

                // 执行重命名操作
                ViewModel.RenameFolder(folderInfo);

                // 重命名完成后更新树形视图
                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        // 获取新路径
                        string newPath = folderInfo.FolderPath;

                        if (!string.IsNullOrEmpty(newPath) && Directory.Exists(newPath))
                        {
                            // 更新路径映射
                            if (_pathToTreeViewItem.ContainsKey(oldPath))
                            {
                                _pathToTreeViewItem.Remove(oldPath);
                                _pathToTreeViewItem[newPath] = oldItem;
                            }

                            // 创建新的 ShellObject 以防止旧路径引用问题
                            var newShellObject = ShellObject.FromParsingName(newPath);

                            // 更新 TreeViewItem 的 Tag 和 Header
                            oldItem.Tag = newShellObject;
                            oldItem.Header = CreateShellObjectHeader(newShellObject);

                            // 如果父项存在，刷新父项的子节点排序
                            if (parentItem != null)
                            {
                                // 重新对子节点进行排序
                                var children = parentItem.Items.Cast<TreeViewItem>().ToList();
                                parentItem.Items.Clear();

                                // 按名称排序
                                foreach (var child in children.OrderBy(x =>
                                    (x.Tag as ShellObject)?.Name ?? ""))
                                {
                                    parentItem.Items.Add(child);
                                }
                            }

                            // 恢复展开状态
                            oldItem.IsExpanded = wasExpanded;

                            // 确保选中状态
                            oldItem.IsSelected = true;

                            Debug.WriteLine($"Successfully updated TreeViewItem from {oldPath} to {newPath}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error updating TreeViewItem after rename: {ex.Message}");
                        // 如果出错，则退回到刷新整个树的方案
                        RefreshTree();
                        SelectPath(folderInfo.FolderPath);
                    }
                }, DispatcherPriority.Normal);
            }
            else
            {
                Debug.WriteLine("ViewModel is null");
                MessageBox.Show("Could not rename folder: ViewModel is not available.",
                    "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void UpdatePathMapping(string oldPath, string newPath)
        {
            if (_pathToTreeViewItem.TryGetValue(oldPath, out var treeViewItem))
            {
                _pathToTreeViewItem.Remove(oldPath);
                _pathToTreeViewItem[newPath] = treeViewItem;
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("Delete_Click handler called");

            var treeViewItem = GetSelectedTreeViewItem();
            if (treeViewItem == null) return;

            var shellObject = treeViewItem.Tag as ShellObject;
            if (shellObject == null) return;

            string path = GetPathFromShellObject(shellObject);
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

            if (!string.IsNullOrEmpty(_rootDirectory) &&
                path.Equals(_rootDirectory, StringComparison.OrdinalIgnoreCase))
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
              
                ViewModel.DeleteFolderCommand.Execute(folderInfo);

                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                timer.Tick += (s, args) =>
                {
                    timer.Stop();

                    if (!string.IsNullOrEmpty(parentPath) && Directory.Exists(parentPath))
                    {
                        SelectPath(parentPath);
                    }

                    RefreshTree();
                };
                timer.Start();
            }
            else
            {
                Debug.WriteLine("ViewModel is null");
                MessageBox.Show("Could not delete folder: ViewModel is not available.",
                    "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShowInExplorer_Click(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("ShowInExplorer_Click handler called");

            var treeViewItem = GetSelectedTreeViewItem();
            if (treeViewItem == null) return;

            var shellObject = treeViewItem.Tag as ShellObject;
            if (shellObject == null) return;

            string path = GetPathFromShellObject(shellObject);
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

            // 创建 FolderInfo 并调用 ViewModel
            var folderInfo = new FolderInfo(path);

            if (ViewModel != null)
            {
                Debug.WriteLine($"Calling ViewModel.ShowInExplorer for {path}");
                ViewModel.ShowInExplorer(folderInfo);
            }
            else
            {
                Debug.WriteLine("ViewModel is null, using direct Process.Start instead");
                // 后备方案，如果 ViewModel 不可用直接打开资源管理器
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

        private TreeViewItem GetSelectedTreeViewItem()
        {
            // Return the first selected item or the selected item from the TreeView
            if (_selectedItems.Count > 0)
            {
                return _selectedItems[0];
            }

            return ShellTreeViewControl.SelectedItem as TreeViewItem;
        }

        // Method to refresh the tree after operations that modify the file system
        public void RefreshTree()
        {
            try
            {
                var treeViewItem = GetSelectedTreeViewItem();
                if (treeViewItem == null) return;

                var shellObject = treeViewItem.Tag as ShellObject;
                if (shellObject == null) return;

                // Remember current selection path and all expanded paths
                string currentPath = GetPathFromShellObject(shellObject);
                var expandedItems = new HashSet<string>();

                // Store all currently expanded items
                foreach (var item in FindVisualChildren<TreeViewItem>(ShellTreeViewControl))
                {
                    if (item.IsExpanded && item.Tag is ShellObject so)
                    {
                        string path = GetPathFromShellObject(so);
                        if (!string.IsNullOrEmpty(path))
                        {
                            expandedItems.Add(path);
                        }
                    }
                }

                // Find the parent path to refresh
                string parentPath = Path.GetDirectoryName(currentPath);

                // Try to find the parent item
                TreeViewItem parentItem = null;
                if (!string.IsNullOrEmpty(parentPath) && Directory.Exists(parentPath))
                {
                    if (_pathToTreeViewItem.TryGetValue(parentPath, out parentItem))
                    {
                        // Refresh the parent (will rebuild all children)
                        if (parentItem.IsExpanded)
                        {
                            parentItem.Items.Clear();
                            parentItem.Items.Add(new TreeViewItem { Header = "Loading..." });
                            parentItem.IsExpanded = false;
                            parentItem.IsExpanded = true; // This will trigger TreeViewItem_Expanded
                        }
                    }
                }

                // If we couldn't refresh the parent, try refreshing the current item
                if (parentItem == null && treeViewItem.IsExpanded)
                {
                    treeViewItem.Items.Clear();
                    treeViewItem.Items.Add(new TreeViewItem { Header = "Loading..." });
                    treeViewItem.IsExpanded = false;
                    treeViewItem.IsExpanded = true;
                }

                // Re-expand all previously expanded items that still exist
                foreach (var path in expandedItems)
                {
                    if (Directory.Exists(path) && _pathToTreeViewItem.TryGetValue(path, out var item))
                    {
                        item.IsExpanded = true;
                    }
                }

                // Try to restore selection to the current path if it still exists
                // or to the parent path if current path was deleted
                if (Directory.Exists(currentPath))
                {
                    SelectPath(currentPath);
                }
                else if (!string.IsNullOrEmpty(parentPath) && Directory.Exists(parentPath))
                {
                    SelectPath(parentPath);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error refreshing tree: {ex.Message}");
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
        // Drag & Drop implementation 
        private void TreeView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Store the mouse position for potential drag operation
            _startPoint = e.GetPosition(null);

            // Handle multi-selection
            TreeView_PreviewMouseDown(sender, e);
        }

        private void TreeView_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && !_isDragging)
            {
                Point position = e.GetPosition(null);

                // Check if the mouse has moved far enough to initiate drag
                if (Math.Abs(position.X - _startPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(position.Y - _startPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    StartDrag(e);
                }
            }
        }

        private void TreeView_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isDragging = false;
        }

        private void StartDrag(MouseEventArgs e)
        {
            // For multi-selection, we'll need to handle dragging multiple items
            if (_selectedItems.Count <= 0) return;

            _isDragging = true;

            // If we're dragging multiple items, we'll need to pass all paths
            if (_selectedItems.Count == 1)
            {
                // Single item drag (existing behavior)
                var draggedItem = _selectedItems[0];
                _draggedItem = draggedItem;
                _draggedShellObject = draggedItem.Tag as ShellObject;

                if (_draggedShellObject == null) return;

                string path = GetPathFromShellObject(_draggedShellObject);
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

                // Don't allow dragging the root directory
                if (!string.IsNullOrEmpty(_rootDirectory) &&
                    path.Equals(_rootDirectory, StringComparison.OrdinalIgnoreCase))
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
                        string path = GetPathFromShellObject(shellObject);
                        if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                        {
                            // Don't allow dragging the root directory
                            if (!string.IsNullOrEmpty(_rootDirectory) &&
                                path.Equals(_rootDirectory, StringComparison.OrdinalIgnoreCase))
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

            string targetPath = GetPathFromShellObject(targetShellObject);
            if (string.IsNullOrEmpty(targetPath) || !Directory.Exists(targetPath))
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
                // Check if we're trying to drop into itself or its child
                if (sourcePath == targetPath ||
                    targetPath.StartsWith(sourcePath + Path.DirectorySeparatorChar))
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

        // 修改 TreeView_Drop 方法以使用 ViewModel 的多选操作
        private void TreeView_Drop(object sender, DragEventArgs e)
        {
            // 清除高亮
            ClearDropTargetHighlight();

            if (!e.Data.GetDataPresent("FileDrop"))
                return;

            // 获取拖放目标
            var targetItem = GetTreeViewItemUnderMouse(e.GetPosition(ShellTreeViewControl));
            if (targetItem == null) return;

            var targetShellObject = targetItem.Tag as ShellObject;
            if (targetShellObject == null) return;

            string targetPath = GetPathFromShellObject(targetShellObject);
            if (string.IsNullOrEmpty(targetPath) || !Directory.Exists(targetPath))
                return;

            // 获取源路径
            var filePaths = e.Data.GetData("FileDrop") as string[];
            if (filePaths == null || filePaths.Length == 0) return;

            // 创建目标文件夹的 FolderInfo
            var targetFolder = new FolderInfo(targetPath);

            // 确定是复制还是移动操作
            bool isCopy = (e.KeyStates & DragDropKeyStates.ControlKey) == DragDropKeyStates.ControlKey;

            if (ViewModel != null)
            {
                Debug.WriteLine($"Performing drag & drop operation: {(isCopy ? "Copy" : "Move")} to {targetPath}");

                if (filePaths.Length == 1)
                {
                    // 单个文件夹操作 - 使用现有的单选方法
                    string sourcePath = filePaths[0];
                    if (!Directory.Exists(sourcePath)) return;

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
                    // 多文件夹操作 - 使用 ViewModel 的多选方法
                    var sourceFolders = new List<FolderInfo>();
                    foreach (string path in filePaths)
                    {
                        if (Directory.Exists(path))
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

                // 刷新树
                RefreshTree();
            }
            else
            {
                MessageBox.Show("Could not complete drag and drop operation: ViewModel is not available.",
                    "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Add helper method to refresh a specific TreeViewItem
        private void RefreshTreeItem(TreeViewItem item)
        {
            if (item == null) return;

            bool wasExpanded = item.IsExpanded;

            // Clear and add dummy item to force reload
            item.Items.Clear();
            item.Items.Add(new TreeViewItem { Header = "Loading..." });

            // Force a refresh by toggling IsExpanded if it was already expanded
            if (wasExpanded)
            {
                item.IsExpanded = false;
                item.IsExpanded = true; // This will trigger TreeViewItem_Expanded event
            }

            // Ensure it's expanded after the refresh if it was expanded before
            item.IsExpanded = wasExpanded;
        }

        // Add helper method to refresh root items
        private void RefreshRootItems()
        {
            // Store expanded state to restore later
            var expandedPaths = new HashSet<string>();
            foreach (var item in FindVisualChildren<TreeViewItem>(ShellTreeViewControl))
            {
                if (item.IsExpanded && item.Tag is ShellObject so)
                {
                    string path = GetPathFromShellObject(so);
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
                if (Directory.Exists(path) && _pathToTreeViewItem.TryGetValue(path, out var item))
                {
                    item.IsExpanded = true;
                }
            }

            // Restore selection
            if (!string.IsNullOrEmpty(selectedPath) && Directory.Exists(selectedPath))
            {
                SelectPath(selectedPath);
            }
        }

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

        // Helper method to find all visual children
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
    }
}