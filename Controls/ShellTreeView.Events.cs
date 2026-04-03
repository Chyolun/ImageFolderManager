using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ImageFolderManager.Models;
using ImageFolderManager.Services;
using Path = System.IO.Path;

namespace ImageFolderManager.Controls
{
    public partial class ShellTreeView
    {
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

            // Fire-and-forget; exceptions are handled inside ExpandNodeAsync.
            _ = ExpandNodeAsync(item, node);
        }

        // TreeViewItem_Collapsed: wire this to the Collapsed event in XAML.
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

        // Core expansion logic

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
                        ViewModel.NotifyFolderSelected(folderInfo, loadImages: false);
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
                        ViewModel.NotifyFolderSelected(folderInfo, loadImages: false);
                    }
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
                        ViewModel.NotifyMultiSelectionChanged(_selectedItems.Count, lastFolderName);
                    }
                    else
                    {
                        ViewModel.NotifyMultiSelectionChanged(_selectedItems.Count, lastFolderName: null);
                    }
                }
                else
                {
                    // No items selected
                    ViewModel.NotifySelectionCleared();
                }
            }
        }

        private void LoadImagesForFolder(FolderInfo folder)
        {
            if (folder == null) return;

            if (ViewModel != null)
            {
                ViewModel.NotifyFolderSelected(folder, loadImages: true);
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

                // New Sibling Folder, disabled when selected node is the root
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

    }
}
