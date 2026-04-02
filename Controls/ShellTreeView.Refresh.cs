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
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ImageFolderManager.Models;
using ImageFolderManager.Services;
using Path = System.IO.Path;

namespace ImageFolderManager.Controls
{
    public partial class ShellTreeView
    {
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
                    await HandleFolderMove(sourcePath, destinationPath);
                    break;

                case FolderOperationType.UndoMove:
                    // Undo move uses the same tree delta application as move.
                    await HandleFolderMove(sourcePath, destinationPath);
                    break;
            }
        }

        /// <summary>
        /// Overload for batch move operations ¡ª processes all pairs then
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

            // Parent is expanded ¡ª insert the new node in sorted position
            var newNode = new FolderNode(newFolderPath);
            var newItem = FolderTreeItemFactory.CreateItem(newNode);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                // Probe sub-dirs before touching parent (small I/O, stays off UI pump)
                // Already done inside CreateItem ¡ú HasSubDirectories

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
                        // No placeholder needed ¡ª folder is now empty
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
                    // Item may not be in the visual tree yet ¡ª skip
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
                    // Destination already exists in the tree mapping.
                    // This can happen in batch moves when the first handled move expands
                    // an unexpanded destination parent and loads all moved children at once.
                    // We still need to remove any stale source node from the old parent.
                    if (!PathService.PathsEqual(normalizedSourcePath, normalizedDestPath) &&
                        _pathToTreeViewItem.ContainsKey(normalizedSourcePath))
                    {
                        await HandleFolderDelete(normalizedSourcePath);
                    }
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
                    // ©¤©¤ BUG 2 FIX ©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤
                    // Target node was unexpanded and held only a placeholder.
                    // Instead of trying to manually populate siblings (the old complex
                    // background-loading block that was also broken), we simply trigger
                    // the node's normal lazy-expansion.  ExpandNodeAsync will scan the
                    // filesystem ¨C which now contains the moved folder ¨C and build every
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
                    // NOT re-trigger ExpandNodeAsync ¨C no double expansion.
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        destParentItem.IsExpanded = true;
                    });
                    // ©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤
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
                    // Background loading is still in progress ¡ª wait briefly for it to register
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
                    System.Diagnostics.Debug.WriteLine($"?? [EnsureNaturalSorting] Error: {ex.Message}");
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
                    return true; // Found one ¡ª stop immediately
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
    }
}
