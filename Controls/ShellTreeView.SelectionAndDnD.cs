using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ImageFolderManager.Models;
using ImageFolderManager.Services;
using Path = System.IO.Path;

namespace ImageFolderManager.Controls
{
    public partial class ShellTreeView
    {
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
            if (ct.IsCancellationRequested) return;

            string[] children;
            try
            {
                children = Directory.GetDirectories(path);
            }
            catch (UnauthorizedAccessException) { return; }
            catch (DirectoryNotFoundException) { return; }
            catch (PathTooLongException) { return; }
            catch (IOException) { return; }

            // Sort with StrCmpLogicalW �?identical to FolderNode.EnumerateChildren and Tree View
            Array.Sort(children, (a, b) =>
                WindowsNaturalStringComparer.Instance.Compare(
                    Path.GetFileName(a),
                    Path.GetFileName(b)));

            foreach (var child in children)
            {
                if (ct.IsCancellationRequested) return;

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

                // Check match before recursing �?pre-order means parent before children
                if (name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                    results.Add(normalizedChild);

                // Recurse into subtree
                TraverseForFind(normalizedChild, keyword, results, ct);
            }
        }

        public Task<List<string>> FindFoldersByName(string keyword)
        {
            return FindFoldersByNameAsync(keyword, CancellationToken.None);
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
                        await ChangeRootDirectoryAsync(normalizedPath);
                        return true;
                    }

                    return false;
                }

                await ExpandPathToFolderAsync(normalizedPath, cancellationToken);

                for (int attempt = 0; attempt < 40; attempt++)
                {
                    if (cancellationToken.IsCancellationRequested) return false;

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

                    await Task.Delay(50);
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

    }
}
