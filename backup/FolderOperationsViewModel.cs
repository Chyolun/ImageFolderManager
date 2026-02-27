using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// Handles all folder operations with backward compatibility and optional command system support
    /// </summary>
    public class FolderOperationsViewModel : ViewModelBase
    {
        private readonly UnifiedFolderService _folderService;

        // Legacy clipboard state for backward compatibility
        private List<FolderInfo> _clipboardFolders = new List<FolderInfo>();
        private bool _isCutOperation;
        private static int _instanceCounter = 0;
        private readonly int _instanceId;

        // Legacy undo support
        private readonly Stack<FolderMoveOperation> _undoStack = new Stack<FolderMoveOperation>();

        #region Properties

        public bool IsCutOperation => _isCutOperation;
        public bool HasClipboardContent => _clipboardFolders.Count > 0;
        public IReadOnlyList<FolderInfo> ClipboardFolders => _clipboardFolders;

        #endregion

        #region Commands

        public ICommand UndoFolderMovementCommand { get; }
        public IAsyncRelayCommand<FolderInfo> DeleteFolderCommand { get; }
        public IAsyncRelayCommand<FolderInfo> CreateNewFolderCommand { get; }

        #endregion

        #region Events

        public event EventHandler<FolderOperationEventArgs> FolderOperationCompleted;
        public event EventHandler<string> StatusMessageChanged;

        #endregion

        public FolderOperationsViewModel(UnifiedFolderService folderService)
        {
            _instanceId = ++_instanceCounter;
            Debug.WriteLine($"=== FolderOperationsViewModel Constructor (Instance #{_instanceId}) ===");

            _folderService = folderService ?? throw new ArgumentNullException(nameof(folderService));


            // Initialize commands
            UndoFolderMovementCommand = new AsyncRelayCommand(UndoLastFolderMovementAsync, CanUndoFolderMovement);
            DeleteFolderCommand = new AsyncRelayCommand<FolderInfo>(DeleteFolderAsync, CanDeleteFolder);
            CreateNewFolderCommand = new AsyncRelayCommand<FolderInfo>(CreateNewFolderAsync, CanCreateNewFolder);

            Debug.WriteLine($"=== FolderOperationsViewModel Constructor Completed (Instance #{_instanceId}) ===");
        }

        #region Legacy Folder Operations (Backward Compatibility)

        /// <summary>
        /// Creates a new folder with user input
        /// </summary>
        public async Task CreateNewFolderAsync(FolderInfo parentFolder)
        {
            if (parentFolder == null || !Directory.Exists(parentFolder.FolderPath))
                return;

            try
            {
                // Simple input dialog (you may want to replace with a proper dialog)
                string folderName = Microsoft.VisualBasic.Interaction.InputBox(
                    "Enter folder name:", "Create New Folder", "New Folder");

                if (string.IsNullOrWhiteSpace(folderName))
                    return;

                bool success = await _folderService.CreateFolderAsync(parentFolder.FolderPath, folderName);

                if (success)
                {
                    UpdateStatus($"Created folder '{folderName}' successfully.");
                    OnFolderOperationCompleted(FolderOperationEventArgs.CreateSuccess(
                        FolderOperation.Create,
                        Path.Combine(parentFolder.FolderPath, folderName)));
                }
                else
                {
                    MessageBox.Show($"Failed to create folder '{folderName}'",
                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error creating folder: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Deletes a single folder
        /// </summary>
        public async Task<bool> DeleteFolderAsync(FolderInfo folder)
        {
            if (folder == null || !Directory.Exists(folder.FolderPath))
                return false;

            try
            {
                var result = MessageBox.Show(
                    $"Are you sure you want to delete the folder:\n\n{folder.FolderPath}?\n\nThis will move it to the Recycle Bin.",
                    "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                    return false;

                bool success = await _folderService.DeleteFolderAsync(folder.FolderPath, true);

                if (success)
                {
                    UpdateStatus($"Deleted folder '{folder.Name}' successfully.");
                    OnFolderOperationCompleted(FolderOperationEventArgs.CreateSuccess(
                        FolderOperation.Delete, folder.FolderPath));
                    return true;
                }
                else
                {
                    MessageBox.Show($"Failed to delete folder '{folder.Name}'",
                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }

            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error deleting folder: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        /// <summary>
        /// Deletes multiple folders
        /// </summary>
        public async Task<bool> DeleteFoldersAsync(IEnumerable<FolderInfo> folders)
        {
            if (folders == null) return false;

            var folderList = folders.Where(f => f != null && Directory.Exists(f.FolderPath)).ToList();
            if (folderList.Count == 0) return false;

            // Single folder delete
            if (folderList.Count == 1)
            {
                return await DeleteFolderAsync(folderList[0]);
            }

            // Multiple folder delete with confirmation
            var result = MessageBox.Show(
                $"Are you sure you want to delete {folderList.Count} folders?\n\nThey will be moved to the Recycle Bin.",
                "Confirm Delete Multiple", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return false;

            var progressDialog = new ProgressDialog(
                "Deleting Folders",
                $"Deleting {folderList.Count} folders...");

            progressDialog.Owner = Application.Current.MainWindow;

            bool overallSuccess = true;
            int processed = 0;

            var deleteTask = Task.Run(async () =>
            {
                foreach (var folder in folderList)
                {
                    try
                    {
                        double progress = (double)processed / folderList.Count;
                        progressDialog.UpdateProgress(progress, $"Deleting: {folder.Name}");

                        bool success = await _folderService.DeleteFolderAsync(folder.FolderPath, true);
                        if (!success)
                        {
                            overallSuccess = false;
                        }
                        OnFolderOperationCompleted(FolderOperationEventArgs.CreateSuccess(
                           FolderOperation.Delete,
                           folder.FolderPath,
                           null,
                           false));

                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error deleting {folder.FolderPath}: {ex.Message}");
                        overallSuccess = false;
                    }

                    processed++;
                }

                progressDialog.UpdateProgress(1.0, "Delete operation completed");
            });

            progressDialog.ShowDialog();
            await deleteTask;

            UpdateStatus(overallSuccess
                ? $"Successfully deleted {folderList.Count} folders."
                : $"Deleted {processed} folders with some errors.");

            return overallSuccess;
        }

        /// <summary>
        /// Renames a folder with user input
        /// </summary>
        public async Task<bool> RenameFolderAsync(FolderInfo folder)
        {
            if (folder == null || !Directory.Exists(folder.FolderPath))
                return false;

            try
            {
                string newName = Microsoft.VisualBasic.Interaction.InputBox(
                    "Enter new folder name:", "Rename Folder", folder.Name);

                if (string.IsNullOrWhiteSpace(newName) || newName == folder.Name)
                    return false;

                bool success = await _folderService.RenameFolderAsync(folder.FolderPath, newName);

                if (success)
                {
                    var newPath = Path.Combine(Path.GetDirectoryName(folder.FolderPath), newName);
                    UpdateStatus($"Renamed folder from '{folder.Name}' to '{newName}'.");

                    OnFolderOperationCompleted(FolderOperationEventArgs.CreateSuccess(
                        FolderOperation.Rename,
                        folder.FolderPath,
                        newPath));

                    return true;
                }
                else
                {
                    MessageBox.Show($"Failed to rename folder to '{newName}'",
                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error renaming folder: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        /// <summary>
        /// Cuts folders to clipboard
        /// </summary>
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
                ? $"Cut folder '{folderList[0].Name}' to clipboard."
                : $"Cut {folderList.Count} folders to clipboard.";

            UpdateStatus(message);
        }

        /// <summary>
        /// Copies folders to clipboard
        /// </summary>
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
                ? $"Copied folder '{folderList[0].Name}' to clipboard."
                : $"Copied {folderList.Count} folders to clipboard.";

            UpdateStatus(message);
        }

        /// <summary>
        /// Pastes clipboard content to target folder
        /// </summary>
        public async Task<bool> PasteFoldersAsync(FolderInfo targetFolder)
        {
            if (targetFolder == null || !HasClipboardContent) return false;

            bool success;

            if (_isCutOperation)
            {
                success = await MoveFoldersAsync(_clipboardFolders, targetFolder);
            }
            else
            {
                success = await CopyFoldersAsync(_clipboardFolders, targetFolder);
            }

            if (success && _isCutOperation)
            {
                ClearClipboard();
            }

            CommandManager.InvalidateRequerySuggested();
            return success;
        }

        /// <summary>
        /// Moves folders to target folder
        /// </summary>
        public async Task<bool> MoveFoldersAsync(IEnumerable<FolderInfo> sourceFolders, FolderInfo targetFolder)
        {
            if (sourceFolders == null || targetFolder == null) return false;

            var folderList = sourceFolders.Where(f => f != null && Directory.Exists(f.FolderPath)).ToList();
            if (folderList.Count == 0) return false;


            // Single folder move
            if (folderList.Count == 1)
            {
                return await MoveSingleFolderAsync(folderList[0], targetFolder);
            }

            // Multiple folder move with progress
            return await MoveMultipleFoldersAsync(folderList, targetFolder);
        }

        /// <summary>
        /// Copies folders to target folder
        /// </summary>
        public async Task<bool> CopyFoldersAsync(IEnumerable<FolderInfo> sourceFolders, FolderInfo targetFolder)
        {
            if (sourceFolders == null || targetFolder == null) return false;

            var folderList = sourceFolders.Where(f => f != null && Directory.Exists(f.FolderPath)).ToList();
            if (folderList.Count == 0) return false;

            var progressDialog = new ProgressDialog(
                "Copying Folders",
                $"Copying {folderList.Count} folders...");

            progressDialog.Owner = Application.Current.MainWindow;

            bool overallSuccess = true;
            int processed = 0;

            var copyTask = Task.Run(async () =>
            {
                foreach (var sourceFolder in folderList)
                {
                    try
                    {
                        double progress = (double)processed / folderList.Count;
                        progressDialog.UpdateProgress(progress, $"Copying: {sourceFolder.Name}");

                        var destinationPath = Path.Combine(targetFolder.FolderPath, sourceFolder.Name);
                        await CopyDirectoryAsync(sourceFolder.FolderPath, destinationPath);
                        OnFolderOperationCompleted(FolderOperationEventArgs.CreateSuccess(
                            FolderOperation.Copy,
                            sourceFolder.FolderPath,
                            targetFolder.FolderPath));

                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error copying {sourceFolder.FolderPath}: {ex.Message}");
                        overallSuccess = false;
                    }

                    processed++;
                }

                progressDialog.UpdateProgress(1.0, "Copy operation completed");
            });

            progressDialog.ShowDialog();
            await copyTask;

            UpdateStatus(overallSuccess
                ? $"Successfully copied {folderList.Count} folders."
                : $"Copied {processed} folders with some errors.");

            return overallSuccess;
        }

        /// <summary>
        /// Clears the clipboard content
        /// </summary>
        public void ClearClipboard()
        {
            _clipboardFolders.Clear();
            _isCutOperation = false;

            OnPropertyChanged(nameof(HasClipboardContent));
            OnPropertyChanged(nameof(IsCutOperation));
            OnPropertyChanged(nameof(ClipboardFolders));

            UpdateStatus("Clipboard cleared.");
        }

        #endregion

        #region Undo Operations

        /// <summary>
        /// Undo the last folder movement
        /// </summary>
        public async Task UndoLastFolderMovementAsync()
        {
            if (_undoStack.Count == 0) return;

            var operation = _undoStack.Pop();

            try
            {
                if (operation.IsMultipleMove)
                {
                    // Undo multiple folder move
                    for (int i = 0; i < operation.SourcePaths.Count; i++)
                    {
                        var currentPath = Path.Combine(operation.DestinationPath, Path.GetFileName(operation.SourcePaths[i]));
                        if (Directory.Exists(currentPath))
                        {
                            await _folderService.MoveFolderAsync(currentPath, operation.SourcePaths[i]);
                        }
                    }
                }
                else
                {
                    // Undo single folder move
                    var currentPath = Path.Combine(operation.DestinationPath, Path.GetFileName(operation.SourcePaths[0]));
                    if (Directory.Exists(currentPath))
                    {
                        await _folderService.MoveFolderAsync(currentPath, operation.SourcePaths[0]);
                    }
                }

                UpdateStatus("Undo operation completed successfully.");

                OnFolderOperationCompleted(FolderOperationEventArgs.CreateSuccess(
                    FolderOperation.Move,
                    operation.DestinationPath,
                    operation.SourcePaths.FirstOrDefault(),
                    true)); // isUndoOperation = true

                CommandManager.InvalidateRequerySuggested();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during undo operation: {ex.Message}",
                    "Undo Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Command Validation

        private bool CanDeleteFolder(FolderInfo folder)
        {
            return folder != null && Directory.Exists(folder.FolderPath);
        }

        private bool CanCreateNewFolder(FolderInfo parentFolder)
        {
            return parentFolder != null && Directory.Exists(parentFolder.FolderPath);
        }

        private bool CanUndoFolderMovement()
        {
            return _undoStack.Count > 0;
        }

        #endregion

        #region Helper Methods

        private async Task<bool> MoveSingleFolderAsync(FolderInfo sourceFolder, FolderInfo targetFolder)
        {
            try
            {
                var destinationPath = Path.Combine(targetFolder.FolderPath, sourceFolder.Name);

                // Check if destination already exists
                if (Directory.Exists(destinationPath))
                {
                    MessageBox.Show($"A folder named '{sourceFolder.Name}' already exists in the destination.",
                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                bool success = await _folderService.MoveFolderAsync(sourceFolder.FolderPath, destinationPath);

                if (success)
                {
                    // Add to undo stack
                    var operation = new FolderMoveOperation
                    {
                        SourcePaths = new List<string> { sourceFolder.FolderPath },
                        DestinationPath = targetFolder.FolderPath,
                        IsMultipleMove = false,
                        Timestamp = DateTime.Now
                    };
                    _undoStack.Push(operation);

                    UpdateStatus($"Moved folder '{sourceFolder.Name}' to '{targetFolder.Name}'.");

                    OnFolderOperationCompleted(FolderOperationEventArgs.CreateSuccess(
                       FolderOperation.Move,
                        sourceFolder.FolderPath,
                       destinationPath));

                    CommandManager.InvalidateRequerySuggested();
                    return true;
                }
                else
                {
                    MessageBox.Show($"Failed to move folder '{sourceFolder.Name}'",
                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error moving folder: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private async Task<bool> MoveMultipleFoldersAsync(List<FolderInfo> folderList, FolderInfo targetFolder)
        {
            var progressDialog = new ProgressDialog(
                "Moving Folders",
                $"Moving {folderList.Count} folders...");

            progressDialog.Owner = Application.Current.MainWindow;

            var operation = new FolderMoveOperation
            {
                SourcePaths = folderList.Select(f => f.FolderPath).ToList(),
                DestinationPath = targetFolder.FolderPath,
                IsMultipleMove = true,
                Timestamp = DateTime.Now
            };

            bool overallSuccess = true;
            int processed = 0;

            var moveTask = Task.Run(async () =>
            {
                foreach (var sourceFolder in folderList)
                {
                    try
                    {
                        double progress = (double)processed / folderList.Count;
                        progressDialog.UpdateProgress(progress, $"Moving: {sourceFolder.Name}");

                        var destinationPath = Path.Combine(targetFolder.FolderPath, sourceFolder.Name);
                        bool success = await _folderService.MoveFolderAsync(sourceFolder.FolderPath, destinationPath);

                        if (!success)
                        {
                            overallSuccess = false;
                        }

                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error moving {sourceFolder.FolderPath}: {ex.Message}");
                        overallSuccess = false;
                    }

                    processed++;
                }

                progressDialog.UpdateProgress(1.0, "Move operation completed");
            });

            progressDialog.ShowDialog();
            await moveTask;

            if (overallSuccess)
            {
                _undoStack.Push(operation);
            }

            UpdateStatus(overallSuccess
                ? $"Successfully moved {folderList.Count} folders."
                : $"Moved {processed} folders with some errors.");

            foreach (var folder in folderList)
            {
                string newPath = Path.Combine(targetFolder.FolderPath, Path.GetFileName(folder.FolderPath));

                // Fire individual move events for proper TreeView refresh
                OnFolderOperationCompleted(FolderOperationEventArgs.CreateSuccess(
                    FolderOperation.Move,
                    folder.FolderPath,  // source path
                    newPath));          // destination path
            }

            CommandManager.InvalidateRequerySuggested();
            return overallSuccess;
        }

        private Task CopyDirectoryAsync(string sourceDir, string destinationDir)
        {
            return Task.Run(() => CopyDirectoryInternal(sourceDir, destinationDir));
        }

        private void CopyDirectoryInternal(string sourceDir, string destinationDir)
        {
            var dir = new DirectoryInfo(sourceDir);
            if (!dir.Exists)
                throw new DirectoryNotFoundException($"Source directory not found: {dir.FullName}");
            Directory.CreateDirectory(destinationDir);
            // Copy files
            foreach (FileInfo file in dir.GetFiles())
            {
                string targetFilePath = Path.Combine(destinationDir, file.Name);
                file.CopyTo(targetFilePath);
            }

            // Copy subdirectories
            foreach (DirectoryInfo subDir in dir.GetDirectories())
            {
                string newDestinationDir = Path.Combine(destinationDir, subDir.Name);
                CopyDirectoryInternal(subDir.FullName, newDestinationDir);
            }
        }

        private void UpdateStatus(string message)
        {
            StatusMessageChanged?.Invoke(this, message);
        }

        private void OnFolderOperationCompleted(FolderOperationEventArgs e)
        {

            FolderOperationCompleted?.Invoke(this, e);
        }

        #endregion


    }
}
