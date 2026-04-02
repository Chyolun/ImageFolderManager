using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using ImageFolderManager.ViewModels;

namespace ImageFolderManager.Services
{
    public enum OperationType
    {
        FolderCreate,
        FolderDelete,
        FolderMove,
        FolderRefresh,
        TagUpdate,
        RatingUpdate
    }

    public class OperationResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public OperationType OperationType { get; set; }
        public Exception Exception { get; set; }
        public Dictionary<string, object> Data { get; set; } = new();
    }

    public interface IServiceContext
    {
        UnifiedFolderService FolderService { get; }
        FolderTagService TagService { get; }
        TagCloudViewModel TagCloud { get; }
        HierarchicalNodeManager NodeManager { get; }
    }

    /// <summary>
    /// Coordinates operations across multiple services to ensure consistency
    /// </summary>
    public class FolderOperationCoordinator : IDisposable
    {
        private readonly UnifiedFolderService _folderService;
        private readonly FolderTagService _tagService;
        private readonly TagCloudViewModel _tagCloud;
        private readonly HierarchicalNodeManager _nodeManager;
        private readonly SemaphoreSlim _coordinationLock = new(1, 1);
        private bool _disposed = false;

        // Events for operation lifecycle
        public event EventHandler<OperationResult> OperationStarted;
        public event EventHandler<OperationResult> OperationCompleted;
        public event EventHandler<OperationResult> OperationFailed;

        public FolderOperationCoordinator(
            UnifiedFolderService folderService,
            FolderTagService tagService,
            TagCloudViewModel tagCloud,
            HierarchicalNodeManager nodeManager)
        {
            _folderService = folderService ?? throw new ArgumentNullException(nameof(folderService));
            _tagService = tagService ?? throw new ArgumentNullException(nameof(tagService));
            _tagCloud = tagCloud ?? throw new ArgumentNullException(nameof(tagCloud));
            _nodeManager = nodeManager ?? throw new ArgumentNullException(nameof(nodeManager));
        }

        /// <summary>
        /// Execute coordinated folder move operation
        /// </summary>
        public async Task<OperationResult> ExecuteFolderMoveAsync(string sourcePath, string destinationPath)
        {
            if (_disposed)
                return new OperationResult { Success = false, Message = "Coordinator disposed" };

            await _coordinationLock.WaitAsync();
            try
            {
                var result = new OperationResult
                {
                    OperationType = OperationType.FolderMove,
                    Data = { ["SourcePath"] = sourcePath, ["DestinationPath"] = destinationPath }
                };

                OperationStarted?.Invoke(this, result);

                // Phase 1: Prepare operation
                var normalizedSource = PathNormalizationService.GetCanonicalPath(sourcePath);
                var normalizedDest = PathNormalizationService.GetCanonicalPath(destinationPath);

                // Phase 2: Update folder service
                // Note: Actual move is handled by file system watcher
                // We coordinate the response to ensure all services are updated

                // Phase 3: Refresh affected nodes
                await RefreshAffectedNodesAsync(normalizedSource, normalizedDest);

                // Phase 4: Update tag cloud if tags exist
                await RefreshTagsIfNeededAsync(normalizedDest);

                result.Success = true;
                result.Message = "Folder move coordinated successfully";
                OperationCompleted?.Invoke(this, result);
                return result;
            }
            catch (Exception ex)
            {
                var errorResult = new OperationResult
                {
                    Success = false,
                    OperationType = OperationType.FolderMove,
                    Message = $"Folder move coordination failed: {ex.Message}",
                    Exception = ex
                };
                OperationFailed?.Invoke(this, errorResult);
                return errorResult;
            }
            finally
            {
                _coordinationLock.Release();
            }
        }

        /// <summary>
        /// Execute coordinated folder refresh operation
        /// </summary>
        public async Task<OperationResult> ExecuteFolderRefreshAsync(string folderPath)
        {
            if (_disposed)
                return new OperationResult { Success = false, Message = "Coordinator disposed" };

            await _coordinationLock.WaitAsync();
            try
            {
                var normalizedPath = PathNormalizationService.GetCanonicalPath(folderPath);
                return await ExecuteFolderRefreshCoreAsync(normalizedPath, publishEvents: true);
            }
            catch (Exception ex)
            {
                var errorResult = new OperationResult
                {
                    Success = false,
                    OperationType = OperationType.FolderRefresh,
                    Message = $"Folder refresh failed: {ex.Message}",
                    Exception = ex
                };
                OperationFailed?.Invoke(this, errorResult);
                return errorResult;
            }
            finally
            {
                _coordinationLock.Release();
            }
        }

        /// <summary>
        /// Core refresh logic. Must only be called while coordination lock is already held.
        /// </summary>
        private async Task<OperationResult> ExecuteFolderRefreshCoreAsync(string normalizedPath, bool publishEvents)
        {
            var result = new OperationResult
            {
                OperationType = OperationType.FolderRefresh,
                Data = { ["FolderPath"] = normalizedPath }
            };

            if (publishEvents)
            {
                OperationStarted?.Invoke(this, result);
            }

            // Check if we can transition to refreshing state
            if (await _nodeManager.TryTransitionToRefreshing(normalizedPath))
            {
                try
                {
                    // Refresh folder in service
                    await _folderService.RefreshFolderAsync(normalizedPath);

                    // Mark refresh complete
                    await _nodeManager.CompleteRefresh(normalizedPath, true);

                    result.Success = true;
                    result.Message = "Folder refreshed successfully";
                }
                catch (Exception)
                {
                    await _nodeManager.CompleteRefresh(normalizedPath, false);
                    throw;
                }
            }
            else
            {
                result.Success = false;
                result.Message = "Cannot refresh - folder is busy";
            }

            if (publishEvents)
            {
                if (result.Success)
                    OperationCompleted?.Invoke(this, result);
                else
                    OperationFailed?.Invoke(this, result);
            }

            return result;
        }

        /// <summary>
        /// Execute coordinated tag update operation
        /// </summary>
        public async Task<OperationResult> ExecuteTagUpdateAsync(string folderPath, List<string> tags, int rating)
        {
            if (_disposed)
                return new OperationResult { Success = false, Message = "Coordinator disposed" };

            await _coordinationLock.WaitAsync();
            try
            {
                var normalizedPath = PathNormalizationService.GetCanonicalPath(folderPath);

                var result = new OperationResult
                {
                    OperationType = OperationType.TagUpdate,
                    Data = { ["FolderPath"] = normalizedPath, ["TagCount"] = tags.Count, ["Rating"] = rating }
                };

                OperationStarted?.Invoke(this, result);

                // Update tags and rating atomically
                await _tagService.SetTagsAndRatingForFolderAsync(normalizedPath, tags, rating);

                // Invalidate tag cloud cache
                _tagCloud.InvalidateCache();

                result.Success = true;
                result.Message = "Tags updated successfully";
                OperationCompleted?.Invoke(this, result);
                return result;
            }
            catch (Exception ex)
            {
                var errorResult = new OperationResult
                {
                    Success = false,
                    OperationType = OperationType.TagUpdate,
                    Message = $"Tag update failed: {ex.Message}",
                    Exception = ex
                };
                OperationFailed?.Invoke(this, errorResult);
                return errorResult;
            }
            finally
            {
                _coordinationLock.Release();
            }
        }

        /// <summary>
        /// Refresh affected nodes after move operation
        /// </summary>
        private async Task RefreshAffectedNodesAsync(string sourcePath, string destinationPath)
        {
            // Reset source node state (it's been moved)
            _nodeManager.RemoveNodeState(sourcePath);

            // Get parent paths that need refreshing
            var sourceParent = System.IO.Path.GetDirectoryName(sourcePath);
            var destParent = System.IO.Path.GetDirectoryName(destinationPath);

            // Refresh parent directories to show updated contents
            if (!string.IsNullOrEmpty(sourceParent))
            {
                await RefreshParentNode(sourceParent);
            }

            if (!string.IsNullOrEmpty(destParent) &&
                !PathNormalizationService.ArePathsEqual(sourceParent, destParent))
            {
                await RefreshParentNode(destParent);
            }
        }

        /// <summary>
        /// Refresh a parent node if it's currently loaded
        /// </summary>
        private async Task RefreshParentNode(string parentPath)
        {
            var normalizedParent = PathNormalizationService.GetCanonicalPath(parentPath);
            var currentState = _nodeManager.GetNodeState(normalizedParent);

            if (currentState == NodeLoadingState.Loaded)
            {
                // We are already inside ExecuteFolderMoveAsync while holding the coordination lock.
                // Re-entering ExecuteFolderRefreshAsync would try to take the same lock again.
                await ExecuteFolderRefreshCoreAsync(normalizedParent, publishEvents: false);
            }
        }

        /// <summary>
        /// Refresh tag cloud if folder has tags
        /// </summary>
        private async Task RefreshTagsIfNeededAsync(string folderPath)
        {
            try
            {
                var tags = await _tagService.GetTagsForFolderAsync(folderPath);
                if (tags.Count > 0)
                {
                    // Folder has tags, refresh tag cloud
                    _tagCloud.InvalidateCache();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking tags for refresh: {ex.Message}");
                // Non-critical error, continue
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _coordinationLock?.Dispose();
        }
    }
}
