using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using ImageFolderManager.Models;
using ImageFolderManager.Services;
using Application = System.Windows.Application;

namespace ImageFolderManager.ViewModels
{
    public partial class MainViewModel
    {
        #region Directory Lifecycle

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
                string normalizedTargetPath = PathService.NormalizePath(path);
                bool rootDirectoryChanged = !PathService.PathsEqual(CurrentRootDirectory, normalizedTargetPath);

                StatusMessage = "Initializing directory monitoring...";

                // Step 1: Stop any existing monitoring first
                await StopMonitoringAsync();

                if (rootDirectoryChanged)
                {
                    await PrepareForRootDirectoryChangeAsync(normalizedTargetPath);
                }

                // Step 2: Initialize TreeView completely BEFORE starting monitoring
                if (_shellTreeView != null)
                {
                    StatusMessage = "Initializing TreeView...";
                    await InitializeTreeViewAsync(normalizedTargetPath);
                }

                // Step 3: Only start monitoring after TreeView is ready
                StatusMessage = "Starting real-time monitoring...";
                await StartMonitoringAsync(normalizedTargetPath);

                // Step 4: Final verification
                await VerifyInitializationAsync(normalizedTargetPath);

                // Keep recent root directories in sync with actual successful loads.
                AppSettings.Instance.AddRecentFolder(normalizedTargetPath);

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
                        if (_shellTreeView?.IsPathMapped(normalizedRootPath) == true)
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
                treeViewValid = _shellTreeView?.IsPathMapped(path) == true;
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

        private async Task PrepareForRootDirectoryChangeAsync(string newRootDirectory)
        {
            _categoryService.SetRootDirectoryScope(newRootDirectory);
            _tagService.ClearCache();
            Search.InvalidateSearchIndex();
            SelectedFolder = null;

            var dispatcher = Application.Current?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
            await dispatcher.InvokeAsync(() =>
            {
                Search.SearchResultFolders.Clear();
            });

            lock (_allLoadedFoldersLock)
            {
                _allLoadedFolders.Clear();
            }

            TagManagement.TagCloud.InvalidateCache();
            await TagManagement.UpdateTagCloudAsync(Array.Empty<FolderInfo>());
        }

        public async Task UpdateTagCloudAsync()
        {
            //var freshFolders = await _unifiedFolderService.LoadFoldersRecursivelyAsync(
            //    AppSettings.Instance.DefaultRootDirectory);

            //_allLoadedFolders.Clear();
            //_allLoadedFolders.AddRange(freshFolders);
            List<FolderInfo> folderSnapshot;
            lock (_allLoadedFoldersLock)
            {
                folderSnapshot = _allLoadedFolders.ToList();
            }

            await TagManagement.UpdateTagCloudAsync(folderSnapshot);
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

                var folders = await _unifiedFolderService.LoadFoldersRecursivelyAsync(
                    AppSettings.Instance.DefaultRootDirectory);

                lock (_allLoadedFoldersLock)
                {
                    _allLoadedFolders.Clear();
                    _allLoadedFolders.AddRange(folders);
                }

                await UpdateTagCloudAsync();
                StatusMessage = "Ready";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error refreshing folder data: {ex.Message}";
                _dialogService.Show($"Error refreshing folder data: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public async Task CleanupAsync()
        {
            try
            {
                CancelSelectedFolderMetadataLoad();

                lock (_importCtsLock)
                {
                    _currentImportCts?.Cancel();
                    _currentImportCts?.Dispose();
                    _currentImportCts = null;
                }

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
                CancelSelectedFolderMetadataLoad();

                lock (_importCtsLock)
                {
                    _currentImportCts?.Cancel();
                    _currentImportCts?.Dispose();
                    _currentImportCts = null;
                }

                _ = _unifiedFolderService?.StopMonitoringAsync();
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
    }
}
