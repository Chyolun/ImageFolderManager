using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using ImageFolderManager.Models;
using ImageFolderManager.Services;
using ImageFolderManager.Views;
using Microsoft.VisualBasic.FileIO;

namespace ImageFolderManager.ViewModels
{
    /// <summary>
    /// Handles all folder operations including copy, cut, paste, move, delete, rename
    /// </summary>
    public class FolderOperationsViewModel : ViewModelBase
    {
        private readonly UnifiedFolderService _folderService;
        private readonly Stack<FolderMoveOperation> _undoStack = new Stack<FolderMoveOperation>();

        // Clipboard state
        private List<FolderInfo> _clipboardFolders = new List<FolderInfo>();
        private bool _isCutOperation;

        #region Properties

        public bool IsCutOperation => _isCutOperation;
        public bool HasClipboardContent => _clipboardFolders.Count > 0;

        public IReadOnlyList<FolderInfo> ClipboardFolders => _clipboardFolders;

        #endregion

        #region Commands

        public ICommand UndoFolderMovementCommand { get; }
        public IAsyncRelayCommand<FolderInfo> DeleteFolderCommand { get; }

        #endregion

        #region Events

        public event EventHandler<FolderOperationEventArgs> FolderOperationCompleted;
        public event EventHandler<string> StatusMessageChanged;

        #endregion

        public FolderOperationsViewModel(UnifiedFolderService folderService)
        {
            _folderService = folderService ?? throw new ArgumentNullException(nameof(folderService));

            // Initialize commands
            UndoFolderMovementCommand = new AsyncRelayCommand(UndoLastFolderMovementAsync, CanUndoFolderMovement);

        }

        #region Unified Folder Operations

        /// <summary>
        /// Cuts one or more folders to clipboard
        /// </summary>
        /// <param name="folders">The folders to cut</param>
        public void CutFolders(IEnumerable<FolderInfo> folders)
        {
            if (folders == null) return;

            var folderList = folders.Where(f => f != null).ToList();
            if (folderList.Count == 0) return;

            _clipboardFolders = folderList;
            _isCutOperation = true;

            OnPropertyChanged(nameof(HasClipboardContent));
            OnPropertyChanged(nameof(IsCutOperation));
            OnPropertyChanged(nameof(ClipboardFolders));

            string message = folderList.Count == 1
                ? $"Cut folder '{folderList[0].Name}' to clipboard. Select a destination folder and paste."
                : $"Cut {folderList.Count} folders to clipboard. Select a destination folder and paste.";

            StatusMessageChanged?.Invoke(this, message);
        }


        /// <summary>
        /// Copies one or more folders to clipboard
        /// </summary>
        /// <param name="folders">The folders to copy</param>
        public void CopyFolders(IEnumerable<FolderInfo> folders)
        {
            if (folders == null) return;

            var folderList = folders.Where(f => f != null).ToList();
            if (folderList.Count == 0) return;

            _clipboardFolders = folderList;
            _isCutOperation = false;

            OnPropertyChanged(nameof(HasClipboardContent));
            OnPropertyChanged(nameof(IsCutOperation));
            OnPropertyChanged(nameof(ClipboardFolders));

            string message = folderList.Count == 1
                ? $"Copied folder '{folderList[0].Name}' to clipboard. Select a destination folder and paste."
                : $"Copied {folderList.Count} folders to clipboard. Select a destination folder and paste.";

            StatusMessageChanged?.Invoke(this, message);
        }

        /// <summary>
        /// Copies one or more folders to a target destination
        /// </summary>
        /// <param name="sourceFolders">The folders to copy</param>
        /// <param name="targetFolder">The destination folder</param>
        /// <returns>True if operation was successful</returns>
        /// <summary>
        /// Copies one or more folders to a target destination with incremental refresh support
        /// </summary>
        /// <param name="sourceFolders">The folders to copy</param>
        /// <param name="targetFolder">The destination folder</param>
        /// <returns>True if all folders were copied successfully</returns>
        public async Task<bool> CopyFoldersAsync(IEnumerable<FolderInfo> sourceFolders, FolderInfo targetFolder)
        {
            if (sourceFolders == null || targetFolder == null) return false;

            var folderList = sourceFolders.Where(f => f != null).ToList();
            if (folderList.Count == 0) return false;

            // Single folder copy
            if (folderList.Count == 1)
            {
                var sourceFolder = folderList[0];

                if (sourceFolder == targetFolder ||
                    PathService.IsPathWithin(targetFolder.FolderPath, sourceFolder.FolderPath))
                {
                    MessageBox.Show("Cannot copy a folder into itself or its subfolder.",
                        "Invalid Operation", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                string folderName = Path.GetFileName(sourceFolder.FolderPath);
                string destinationPath = PathService.GetUniqueDirectoryPath(targetFolder.FolderPath, folderName);

                try
                {
                    // Copy directory and all contents
                    await Task.Run(() => CopyDirectory(sourceFolder.FolderPath, destinationPath));

                    // Trigger incremental refresh event for copy operation (treated as create)
                    OnFolderOperationCompleted(FolderOperationEventArgs.CreateSuccess(
                        FolderOperation.Create, // Copy appears as creation in destination
                        targetFolder.FolderPath, // Parent directory
                        destinationPath));       // New copied folder

                    UpdateStatus($"Copied folder '{sourceFolder.Name}' to '{targetFolder.Name}'.");
                    return true;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error copying folder: {ex.Message}",
                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);

                    // Trigger failure event
                    OnFolderOperationCompleted(FolderOperationEventArgs.CreateFailure(
                        FolderOperation.Copy,
                        sourceFolder.FolderPath,
                        ex.Message));

                    return false;
                }
            }

            // Multiple folders copy with progress dialog
            var progressDialog = new ProgressDialog(
                "Copying Folders",
                $"Copying {folderList.Count} folders...");

            progressDialog.Owner = Application.Current.MainWindow;

            bool success = await ProcessMultipleFolderOperation(
                folderList,
                progressDialog,
                async (folder, progress) =>
                {
                    try
                    {
                        string folderName = Path.GetFileName(folder.FolderPath);
                        string destinationPath = PathService.GetUniqueDirectoryPath(targetFolder.FolderPath, folderName);

                        await Task.Run(() => CopyDirectory(folder.FolderPath, destinationPath));

                        // Trigger incremental refresh for each successful copy
                        OnFolderOperationCompleted(FolderOperationEventArgs.CreateSuccess(
                            FolderOperation.Create, // Copy appears as creation
                            targetFolder.FolderPath,
                            destinationPath));

                        return true;
                    }
                    catch (Exception ex)
                    {
                        // Trigger failure event for each failed copy
                        OnFolderOperationCompleted(FolderOperationEventArgs.CreateFailure(
                            FolderOperation.Copy,
                            folder.FolderPath,
                            ex.Message));

                        return false;
                    }
                },
                "Copying");

            return success;
        }


        public void ClearClipboard()
        {
            if (_clipboardFolders.Count > 0)
            {
                _clipboardFolders.Clear();
                _isCutOperation = false;

                OnPropertyChanged(nameof(HasClipboardContent));
                OnPropertyChanged(nameof(IsCutOperation));
                OnPropertyChanged(nameof(ClipboardFolders));
                CommandManager.InvalidateRequerySuggested();

                StatusMessageChanged?.Invoke(this, "Clipboard cleared.");
            }
        }


        /// <summary>
        /// Pastes clipboard content to target folder
        /// </summary>
        /// <param name="targetFolder">The destination folder</param>
        /// <returns>True if operation was successful</returns>
        public async Task<bool> PasteFoldersAsync(FolderInfo targetFolder)
        {
            if (targetFolder == null || !HasClipboardContent) return false;

            bool success;
            bool wasCutOperation = _isCutOperation;

            if (_isCutOperation)
            {
                success = await MoveFoldersAsync(_clipboardFolders, targetFolder);
            }
            else
            {
                 success = await CopyFoldersAsync(_clipboardFolders, targetFolder);
    
            }
            if (success && wasCutOperation)
                {
                    ClearClipboard();
                }
            CommandManager.InvalidateRequerySuggested();
            return success;
        }

        /// <summary>
        /// Deletes one or more folders with incremental refresh support
        /// </summary>
        /// <param name="folders">The folders to delete</param>
        /// <returns>True if all folders were deleted successfully</returns>
        public async Task<bool> DeleteFoldersAsync(IEnumerable<FolderInfo> folders)
        {
            if (folders == null) return false;

            var folderList = folders.Where(f => f != null).ToList();
            if (folderList.Count == 0) return false;

            // Single folder delete
            if (folderList.Count == 1)
            {
                var folder = folderList[0];
                var result = MessageBox.Show(
                    $"Are you sure you want to delete the folder:\n\n{folder.FolderPath}?",
                    "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes) return false;

                try
                {
                    FileSystem.DeleteDirectory(
                        folder.FolderPath,
                        UIOption.OnlyErrorDialogs,
                        RecycleOption.SendToRecycleBin);

                    // Trigger incremental refresh event for single delete
                    OnFolderOperationCompleted(FolderOperationEventArgs.CreateSuccess(
                        FolderOperation.Delete,
                        folder.FolderPath)); // Source path only for delete operations

                    UpdateStatus($"Deleted folder '{folder.Name}'.");
                    return true;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to delete folder: {ex.Message}",
                        "Error", MessageBoxButton.OK, MessageBoxImage.Error);

                    // Trigger failure event
                    OnFolderOperationCompleted(FolderOperationEventArgs.CreateFailure(
                        FolderOperation.Delete,
                        folder.FolderPath,
                        ex.Message));

                    return false;
                }
            }

            // Multiple folders delete with progress dialog
            var batchResult = MessageBox.Show(
                $"Are you sure you want to delete {folderList.Count} folders?",
                "Confirm Deletion", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (batchResult != MessageBoxResult.Yes) return false;

            var progressDialog = new ProgressDialog(
                "Deleting Folders",
                $"Deleting {folderList.Count} folders...");

            progressDialog.Owner = Application.Current.MainWindow;

            // Create undo operation record for batch delete
            var operation = new FolderMoveOperation
            {
                SourcePaths = folderList.Select(f => f.FolderPath).ToList(),
                DestinationPath = "RecycleBin",
                IsMultipleMove = true,
                Timestamp = DateTime.Now,
                SourceParentPaths = folderList
                    .Select(f => Path.GetDirectoryName(f.FolderPath))
                    .Distinct()
                    .ToList()
            };

            bool success = await ProcessMultipleFolderOperation(
                folderList,
                progressDialog,
                async (folder, progress) =>
                {
                    try
                    {
                        FileSystem.DeleteDirectory(
                            folder.FolderPath,
                            UIOption.OnlyErrorDialogs,
                            RecycleOption.SendToRecycleBin);

                        // Trigger incremental refresh for each successful delete
                        OnFolderOperationCompleted(FolderOperationEventArgs.CreateSuccess(
                            FolderOperation.Delete,
                            folder.FolderPath));

                        return true;
                    }
                    catch (Exception ex)
                    {
                        // Trigger failure event for each failed delete
                        OnFolderOperationCompleted(FolderOperationEventArgs.CreateFailure(
                            FolderOperation.Delete,
                            folder.FolderPath,
                            ex.Message));

                        return false;
                    }
                },
                "Deleting");

            if (success)
            {
                _undoStack.Push(operation);
                CommandManager.InvalidateRequerySuggested();
            }

            return success;
        }

        /// <summary>
        /// Moves one or more folders to a target destination with incremental refresh support
        /// </summary>
        /// <param name="sourceFolders">The folders to move</param>
        /// <param name="targetFolder">The destination folder</param>
        /// <returns>True if all folders were moved successfully</returns>
        public async Task<bool> MoveFoldersAsync(IEnumerable<FolderInfo> sourceFolders, FolderInfo targetFolder)
        {
            if (sourceFolders == null || targetFolder == null) return false;

            var folderList = sourceFolders.Where(f => f != null).ToList();
            if (folderList.Count == 0) return false;

            // Single folder move
            if (folderList.Count == 1)
            {
                var sourceFolder = folderList[0];

                if (sourceFolder == targetFolder ||
                    PathService.IsPathWithin(sourceFolder.FolderPath, targetFolder.FolderPath))
                {
                    MessageBox.Show("Cannot move a folder into itself or its subfolder.",
                        "Invalid Operation", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                string folderName = Path.GetFileName(sourceFolder.FolderPath);
                string destinationPath = PathService.GetUniqueDirectoryPath(targetFolder.FolderPath, folderName);

                var operation = new FolderMoveOperation
                {
                    SourcePaths = new List<string> { sourceFolder.FolderPath },
                    DestinationPath = targetFolder.FolderPath,
                    IsMultipleMove = false,
                    Timestamp = DateTime.Now,
                    SourceParentPaths = new List<string> { Path.GetDirectoryName(sourceFolder.FolderPath) }
                };

                try
                {
                    Directory.Move(sourceFolder.FolderPath, destinationPath);

                    _undoStack.Push(operation);
                    CommandManager.InvalidateRequerySuggested();

                    // Trigger incremental refresh event for move operation
                    OnFolderOperationCompleted(FolderOperationEventArgs.CreateSuccess(
                        FolderOperation.Move,
                        sourceFolder.FolderPath, // Source path
                        destinationPath));       // Destination path

                    UpdateStatus($"Moved folder '{sourceFolder.Name}' to '{targetFolder.Name}'.");

                    // Clear clipboard if this was from a cut operation
                    if (_isCutOperation && _clipboardFolders.Contains(sourceFolder))
                    {
                        _clipboardFolders.Clear();
                        OnPropertyChanged(nameof(HasClipboardContent));
                        OnPropertyChanged(nameof(IsCutOperation));
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error moving folder: {ex.Message}",
                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);

                    // Trigger failure event
                    OnFolderOperationCompleted(FolderOperationEventArgs.CreateFailure(
                        FolderOperation.Move,
                        sourceFolder.FolderPath,
                        ex.Message));

                    return false;
                }
            }

            // Multiple folders move with progress dialog
            var progressDialog = new ProgressDialog(
                "Moving Folders",
                $"Moving {folderList.Count} folders...");

            progressDialog.Owner = Application.Current.MainWindow;

            var batchOperation = new FolderMoveOperation
            {
                SourcePaths = folderList.Select(f => f.FolderPath).ToList(),
                DestinationPath = targetFolder.FolderPath,
                IsMultipleMove = true,
                Timestamp = DateTime.Now,
                SourceParentPaths = folderList
                    .Select(f => Path.GetDirectoryName(f.FolderPath))
                    .Distinct()
                    .ToList()
            };

            bool success = await ProcessMultipleFolderOperation(
                folderList,
                progressDialog,
                async (folder, progress) =>
                {
                    try
                    {
                        string folderName = Path.GetFileName(folder.FolderPath);
                        string destinationPath = PathService.GetUniqueDirectoryPath(targetFolder.FolderPath, folderName);

                        Directory.Move(folder.FolderPath, destinationPath);

                        // Trigger incremental refresh for each successful move
                        OnFolderOperationCompleted(FolderOperationEventArgs.CreateSuccess(
                            FolderOperation.Move,
                            folder.FolderPath,
                            destinationPath));

                        return true;
                    }
                    catch (Exception ex)
                    {
                        // Trigger failure event for each failed move
                        OnFolderOperationCompleted(FolderOperationEventArgs.CreateFailure(
                            FolderOperation.Move,
                            folder.FolderPath,
                            ex.Message));

                        return false;
                    }
                },
                "Moving");

            if (success)
            {
                _undoStack.Push(batchOperation);
                CommandManager.InvalidateRequerySuggested();

                // Clear clipboard if this was from a cut operation
                if (_isCutOperation)
                {
                    _clipboardFolders.Clear();
                    OnPropertyChanged(nameof(HasClipboardContent));
                    OnPropertyChanged(nameof(IsCutOperation));
                }
            }

            return success;
        }




        #endregion

        #region Helper Methods

        private async Task<bool> CopyFolderInternalAsync(FolderInfo sourceFolder, FolderInfo targetFolder)
        {
            if (sourceFolder == null || targetFolder == null) return false;

            string folderName = Path.GetFileName(sourceFolder.FolderPath);
            string destinationPath = PathService.GetUniqueDirectoryPath(targetFolder.FolderPath, folderName);

            try
            {
                await Task.Run(() => CopyDirectory(sourceFolder.FolderPath, destinationPath));

                OnFolderOperationCompleted(new FolderOperationEventArgs
                {
                    Operation = FolderOperation.Copy,
                    SourcePath = sourceFolder.FolderPath,
                    DestinationPath = destinationPath,
                    Success = true
                });

                UpdateStatus($"Copied folder '{sourceFolder.Name}' to '{targetFolder.Name}'.");
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error copying folder: {ex.Message}",
                    "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        /// <summary>
        /// Helper method to copy a directory and all its contents recursively
        /// </summary>
        /// <param name="sourcePath">Source directory path</param>
        /// <param name="destinationPath">Destination directory path</param>
        private void CopyDirectory(string sourcePath, string destinationPath)
        {
            // Create destination directory
            Directory.CreateDirectory(destinationPath);

            // Copy all files
            foreach (string file in Directory.GetFiles(sourcePath))
            {
                string fileName = Path.GetFileName(file);
                string destFile = Path.Combine(destinationPath, fileName);
                File.Copy(file, destFile, true);
            }

            // Copy all subdirectories recursively
            foreach (string directory in Directory.GetDirectories(sourcePath))
            {
                string dirName = Path.GetFileName(directory);
                string destDir = Path.Combine(destinationPath, dirName);
                CopyDirectory(directory, destDir);
            }
        }

        private async Task<bool> ProcessMultipleFolderOperation(
            List<FolderInfo> folders,
            ProgressDialog progressDialog,
            Func<FolderInfo, double, Task<bool>> operation,
            string operationName)
        {
            using (var cts = new System.Threading.CancellationTokenSource())
            {
                progressDialog.CancelRequested += (s, e) => cts.Cancel();

                var task = Task.Run(async () =>
                {
                    int total = folders.Count;
                    int processed = 0;
                    int successful = 0;

                    foreach (var folder in folders)
                    {
                        if (cts.Token.IsCancellationRequested)
                            break;

                        double progress = (double)processed / total;
                        progressDialog.UpdateProgress(progress, $"{operationName} {processed + 1} of {total} folders: {folder.Name}");

                        bool result = await operation(folder, progress);
                        if (result) successful++;

                        processed++;
                    }

                    progressDialog.UpdateProgress(1.0, $"{operationName} completed. {successful} of {total} folders processed successfully.");

                    return successful > 0;
                }, cts.Token);

                progressDialog.ShowDialog();

                if (progressDialog.IsCancelled && !cts.IsCancellationRequested)
                {
                    cts.Cancel();
                }

                try
                {
                    return await task;
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Creates a new folder in the specified parent directory with incremental refresh support
        /// </summary>
        /// <param name="parentFolder">The parent folder where the new folder will be created</param>
        /// <returns>True if the folder was created successfully</returns>
        public async Task<bool> CreateNewFolderAsync(FolderInfo parentFolder)
        {
            if (parentFolder == null) return false;

            string folderName = Microsoft.VisualBasic.Interaction.InputBox(
                "Enter folder name:", "Create New Folder", "New Folder");

            if (string.IsNullOrWhiteSpace(folderName))
                return false;

            if (folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                MessageBox.Show("The folder name contains invalid characters.",
                    "Invalid Name", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            string newPath = Path.Combine(parentFolder.FolderPath, folderName);

            if (Directory.Exists(newPath))
            {
                MessageBox.Show($"A folder named '{folderName}' already exists in this location.",
                    "Cannot Create Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            try
            {
                Directory.CreateDirectory(newPath);

                // Trigger incremental refresh event with proper source/destination paths
                OnFolderOperationCompleted(FolderOperationEventArgs.CreateSuccess(
                    FolderOperation.Create,
                    parentFolder.FolderPath, // Source is parent directory
                    newPath));               // Destination is new folder path

                UpdateStatus($"Created folder '{folderName}'.");
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error creating folder: {ex.Message}",
                    "Create Folder Failed", MessageBoxButton.OK, MessageBoxImage.Error);

                // Trigger failure event for proper error handling
                OnFolderOperationCompleted(FolderOperationEventArgs.CreateFailure(
                    FolderOperation.Create,
                    parentFolder.FolderPath,
                    ex.Message));

                return false;
            }
        }

        /// <summary>
        /// Renames a folder with incremental refresh support
        /// </summary>
        /// <param name="folder">The folder to rename</param>
        /// <returns>True if the folder was renamed successfully</returns>
        public async Task<bool> RenameFolderAsync(FolderInfo folder)
        {
            if (folder == null) return false;

            string oldName = Path.GetFileName(folder.FolderPath);
            string newName = Microsoft.VisualBasic.Interaction.InputBox(
                "Enter new folder name:", "Rename Folder", oldName);

            if (string.IsNullOrWhiteSpace(newName) || newName == oldName)
                return false;

            if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                MessageBox.Show("The folder name contains invalid characters.",
                    "Invalid Name", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            string parentPath = Path.GetDirectoryName(folder.FolderPath);
            string newPath = Path.Combine(parentPath, newName);

            if (Directory.Exists(newPath))
            {
                MessageBox.Show($"A folder named '{newName}' already exists in this location.",
                    "Cannot Rename Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            try
            {
                Directory.Move(folder.FolderPath, newPath);

                // Trigger incremental refresh event for rename operation
                OnFolderOperationCompleted(FolderOperationEventArgs.CreateSuccess(
                    FolderOperation.Rename,
                    folder.FolderPath, // Old path
                    newPath));         // New path

                UpdateStatus($"Renamed folder from '{oldName}' to '{newName}'.");
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error renaming folder: {ex.Message}",
                    "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);

                // Trigger failure event for proper error handling
                OnFolderOperationCompleted(FolderOperationEventArgs.CreateFailure(
                    FolderOperation.Rename,
                    folder.FolderPath,
                    ex.Message));

                return false;
            }
        }

        /// <summary>
        /// Updates the status message and notifies listeners
        /// </summary>
        /// <param name="message">The status message to display</param>
        private void UpdateStatus(string message)
        {
            try
            {
                if (Application.Current.Dispatcher.CheckAccess())
                {
                    // Already on UI thread
                    StatusMessageChanged?.Invoke(this, message);
                }
                else
                {
                    // Marshal to UI thread
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        StatusMessageChanged?.Invoke(this, message);
                    }));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating status: {ex.Message}");
            }
        }


        /// <summary>
        /// Triggers the FolderOperationCompleted event with proper error handling
        /// </summary>
        /// <param name="e">Event arguments containing operation details</param>
        protected virtual void OnFolderOperationCompleted(FolderOperationEventArgs e)
        {
            try
            {
                // Always ensure events are fired on the UI thread
                if (Application.Current.Dispatcher.CheckAccess())
                {
                    // Already on UI thread, fire directly
                    FolderOperationCompleted?.Invoke(this, e);
                }
                else
                {
                    // Marshal to UI thread
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            FolderOperationCompleted?.Invoke(this, e);
                        }
                        catch (Exception ex)
                        {
                            // Log error but don't throw to avoid breaking the operation flow
                            System.Diagnostics.Debug.WriteLine($"Error in FolderOperationCompleted event (dispatched): {ex.Message}");
                            UpdateStatus($"Warning: Refresh may not have updated properly");
                        }
                    }));
                }
            }
            catch (Exception ex)
            {
                // Log error but don't throw to avoid breaking the operation flow
                System.Diagnostics.Debug.WriteLine($"Error in FolderOperationCompleted event: {ex.Message}");
                UpdateStatus($"Warning: Refresh may not have updated properly");
            }
        }

        #endregion

        #region Undo Operations

        private bool CanUndoFolderMovement()
        {
            return _undoStack.Count > 0;
        }

        /// <summary>
        /// Undoes the last folder movement operation with incremental refresh support
        /// </summary>
        /// <returns>Task representing the async operation</returns>
        public async Task UndoLastFolderMovementAsync()
        {
            if (_undoStack.Count == 0)
            {
                UpdateStatus("Nothing to undo.");
                return;
            }

            var lastOperation = _undoStack.Pop();
            CommandManager.InvalidateRequerySuggested();

            // Handle delete operations (cannot be undone)
            if (lastOperation.DestinationPath == "RecycleBin")
            {
                UpdateStatus("Cannot undo delete operation - folders were moved to recycle bin.");
                return;
            }

            try
            {
                if (lastOperation.IsMultipleMove)
                {
                    await UndoMultipleFolderMove(lastOperation);
                }
                else
                {
                    await UndoSingleFolderMove(lastOperation);
                }

                UpdateStatus("Undo operation completed successfully.");
            }
            catch (Exception ex)
            {
                UpdateStatus($"Undo operation failed: {ex.Message}");

                // Trigger failure event for undo operation
                OnFolderOperationCompleted(FolderOperationEventArgs.CreateFailure(
                    FolderOperation.Move,
                    lastOperation.DestinationPath,
                    ex.Message,
                    isUndoOperation: true));
            }
        }

        /// <summary>
        /// Undoes a single folder move operation
        /// </summary>
        /// <param name="operation">The operation to undo</param>
        /// <returns>Task representing the async operation</returns>
        private async Task UndoSingleFolderMove(FolderMoveOperation operation)
        {
            string sourcePath = operation.SourcePaths.FirstOrDefault();
            if (string.IsNullOrEmpty(sourcePath))
                throw new InvalidOperationException("Invalid undo operation - missing source path");

            string folderName = Path.GetFileName(sourcePath);
            string currentPath = Path.Combine(operation.DestinationPath, folderName);

            if (!Directory.Exists(currentPath))
                throw new DirectoryNotFoundException($"Cannot undo - folder not found at: {currentPath}");

            if (Directory.Exists(sourcePath))
                throw new InvalidOperationException($"Cannot undo - original location is occupied: {sourcePath}");

            // Perform the undo move
            Directory.Move(currentPath, sourcePath);

            // Trigger incremental refresh event for undo operation
            OnFolderOperationCompleted(FolderOperationEventArgs.CreateSuccess(
                FolderOperation.Move,
                currentPath, // Current location (source for undo)
                sourcePath,  // Original location (destination for undo)
                isUndoOperation: true));
        }

        /// <summary>
        /// Undoes multiple folder move operations
        /// </summary>
        /// <param name="operation">The batch operation to undo</param>
        /// <returns>Task representing the async operation</returns>
        private async Task UndoMultipleFolderMove(FolderMoveOperation operation)
        {
            var progressDialog = new ProgressDialog(
                "Undoing Move Operation",
                $"Restoring {operation.SourcePaths.Count} folders...");

            progressDialog.Owner = Application.Current.MainWindow;

            using (var cts = new CancellationTokenSource())
            {
                progressDialog.CancelRequested += (s, e) => cts.Cancel();

                var task = Task.Run(async () =>
                {
                    int total = operation.SourcePaths.Count;
                    int processed = 0;
                    int successful = 0;

                    // Collect all results instead of individual UI updates
                    var results = new List<FolderOperationEventArgs>();

                    foreach (string sourcePath in operation.SourcePaths)
                    {
                        if (cts.Token.IsCancellationRequested)
                            break;

                        try
                        {
                            string folderName = Path.GetFileName(sourcePath);
                            string currentPath = Path.Combine(operation.DestinationPath, folderName);

                            if (Directory.Exists(currentPath) && !Directory.Exists(sourcePath))
                            {
                                Directory.Move(currentPath, sourcePath);

                                // Collect success result
                                results.Add(FolderOperationEventArgs.CreateSuccess(
                                    FolderOperation.Move,
                                    currentPath,
                                    sourcePath,
                                    isUndoOperation: true));

                                successful++;
                            }
                        }
                        catch (Exception ex)
                        {
                            // Collect failure result
                            results.Add(FolderOperationEventArgs.CreateFailure(
                                FolderOperation.Move,
                                sourcePath,
                                ex.Message,
                                isUndoOperation: true));
                        }

                        processed++;
                        double percentage = (double)processed / total * 100;

                        // Update progress only
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            progressDialog.UpdateProgress(percentage,
                                $"Restored {successful} of {processed} folders...");
                        });
                    }

                    // Single batch UI update for all operations
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        foreach (var result in results)
                        {
                            OnFolderOperationCompleted(result);
                        }

                        progressDialog.UpdateProgress(100,
                            $"Undo completed: {successful} of {total} folders restored successfully.");
                    });

                    return successful > 0;
                }, cts.Token);

                progressDialog.ShowDialog();

                if (progressDialog.IsCancelled && !cts.IsCancellationRequested)
                {
                    cts.Cancel();
                }

                try
                {
                    await task;
                }
                catch (OperationCanceledException)
                {
                    UpdateStatus("Undo operation was cancelled.");
                }
            }
        }


        #endregion
    }
}