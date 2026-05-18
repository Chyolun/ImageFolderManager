using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using ImageFolderManager.Controls;
using ImageFolderManager.Models;
using ImageFolderManager.Services;
using static ImageFolderManager.Controls.ShellTreeView;
using Application = System.Windows.Application;

namespace ImageFolderManager.ViewModels
{
    public partial class MainViewModel
    {
        #region Service Event HandlersIn ExecuteFolderOperationOnUIThread

        private void OnIndexedFolderCreated(string folderPath)
        {
            _ = OnIndexedFolderCreatedAsync(folderPath);
        }

        private async Task OnIndexedFolderCreatedAsync(string folderPath)
        {
            try
            {
                var newFolder = await _unifiedFolderService.CreateFolderInfoWithoutImagesAsync(folderPath);
                if (newFolder != null)
                {
                    lock (_allLoadedFoldersLock)
                    {
                        _allLoadedFolders.Add(newFolder);
                    }
                    Search.InvalidateSearchIndex();
                    await Search.PerformSilentSearchAsync();
                    await TagManagement.TagCloud.ApplyFolderUpdateAsync(newFolder);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"OnIndexedFolderCreatedAsync failed: {ex.Message}");
            }
        }

        private void OnIndexedFolderDeleted(string folderPath)
        {
            _ = OnIndexedFolderDeletedAsync(folderPath);
        }

        private async Task OnIndexedFolderDeletedAsync(string folderPath)
        {
            try
            {
                List<string> removedFolderPaths;
                lock (_allLoadedFoldersLock)
                {
                    removedFolderPaths = _allLoadedFolders
                        .Where(f =>
                            PathService.PathsEqual(f.FolderPath, folderPath) ||
                            PathService.IsPathWithin(folderPath, f.FolderPath))
                        .Select(f => f.FolderPath)
                        .ToList();

                    _allLoadedFolders.RemoveAll(f =>
                        PathService.PathsEqual(f.FolderPath, folderPath) ||
                        PathService.IsPathWithin(folderPath, f.FolderPath));
                }
                Search.InvalidateSearchIndex();
                await Search.PerformSilentSearchAsync();
                await TagManagement.TagCloud.RemoveFoldersAsync(removedFolderPaths);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"OnIndexedFolderDeletedAsync failed: {ex.Message}");
            }
        }

        private void OnIndexedFolderRenamed(string oldPath, string newPath)
        {
            _ = OnIndexedFolderRenamedAsync(oldPath, newPath);
        }

        private async Task OnIndexedFolderRenamedAsync(string oldPath, string newPath)
        {
            try
            {
                // Collect items to modify first
                var itemsToUpdate = new List<(FolderInfo folder, string newPath)>();
                var renames = new List<(string oldPath, string newPath)>();

                lock (_allLoadedFoldersLock)
                {
                    for (int i = 0; i < _allLoadedFolders.Count; i++)
                    {
                        var folder = _allLoadedFolders[i];
                        if (folder.FolderPath == oldPath)
                        {
                            itemsToUpdate.Add((folder, newPath));
                            renames.Add((folder.FolderPath, newPath));
                        }
                        else if (PathService.IsPathWithin(oldPath, folder.FolderPath))
                        {
                            string updatedPath = newPath + folder.FolderPath.Substring(oldPath.Length);
                            itemsToUpdate.Add((folder, updatedPath));
                            renames.Add((folder.FolderPath, updatedPath));
                        }
                    }
                }

                // Apply updates
                foreach (var (folder, updatedPath) in itemsToUpdate)
                {
                    folder.FolderPath = updatedPath;
                }

                Search.InvalidateSearchIndex();
                await Search.PerformSilentSearchAsync();
                await TagManagement.TagCloud.RenameFolderPathsAsync(renames);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"OnIndexedFolderRenamedAsync failed: {ex.Message}");
            }
        }

        private void OnIndexRebuilt(List<string> allFolders)
        {
            _ = OnIndexRebuiltAsync(allFolders);
        }

        private async Task OnIndexRebuiltAsync(List<string> allFolders)
        {
            try
            {
                StatusMessage = "Rebuilding folder cache from index...";

                var rebuiltFolders = await _unifiedFolderService.CreateFolderInfosWithoutImagesAsync(allFolders);

                lock (_allLoadedFoldersLock)
                {
                    _allLoadedFolders.Clear();
                    _allLoadedFolders.AddRange(rebuiltFolders);
                }

                Search.InvalidateSearchIndex();
                await Search.PerformSilentSearchAsync();
                await UpdateTagCloudAsync();

                StatusMessage = $"Index rebuilt. {allFolders.Count} folders loaded.";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"OnIndexRebuiltAsync failed: {ex.Message}");
                StatusMessage = "Index rebuild failed.";
            }
        }

        #endregion


        #region ShellTreeView Integration and Refresh Commands

        /// <summary>
        /// Sets the ShellTreeView reference for direct tree operations
        /// </summary>
        /// <param name="shellTreeView">The ShellTreeView control instance</param>
        public void SetShellTreeView(IShellTreeViewAdapter shellTreeView)
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
        private void OnFolderOperationCompleted(object sender, FolderOperationEventArgs e)
        {
            _ = OnFolderOperationCompletedAsync(e);
        }

        private async Task OnFolderOperationCompletedAsync(FolderOperationEventArgs e)
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

            if (!_shellTreeView.HasPathMappings)
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

    }
}
