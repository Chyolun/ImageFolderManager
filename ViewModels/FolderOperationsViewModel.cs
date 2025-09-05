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
using ImageFolderManager.Commands;
using ImageFolderManager.StateMachine;
using Microsoft.VisualBasic.FileIO;

namespace ImageFolderManager.ViewModels
{
    /// <summary>
    /// Enhanced folder operations ViewModel with Command pattern integration
    /// </summary>
    public class FolderOperationsViewModel : ViewModelBase
    {
        private readonly UnifiedFolderService _folderService;
        private readonly Stack<FolderMoveOperation> _undoStack = new Stack<FolderMoveOperation>();

        // Clipboard state
        private List<FolderInfo> _clipboardFolders = new List<FolderInfo>();
        private bool _isCutOperation;

        // Command system integration
        private CancellationTokenSource _currentOperationCts;

        #region Properties

        public bool IsCutOperation => _isCutOperation;
        public bool HasClipboardContent => _clipboardFolders.Count > 0;
        public IReadOnlyList<FolderInfo> ClipboardFolders => _clipboardFolders;

        // Command system status
        public bool IsCommandSystemEnabled => _folderService.IsCommandSystemEnabled;
        public bool CanUndoCommands => IsCommandSystemEnabled && _folderService.CommandExecutor?.HistoryCount > 0;

        // Current operation status
        private bool _isOperationInProgress;
        public bool IsOperationInProgress
        {
            get => _isOperationInProgress;
            private set => SetProperty(ref _isOperationInProgress, value);
        }

        private string _currentOperationStatus;
        public string CurrentOperationStatus
        {
            get => _currentOperationStatus;
            private set => SetProperty(ref _currentOperationStatus, value);
        }

        #endregion

        #region Commands

        public ICommand UndoFolderMovementCommand { get; }
        public IAsyncRelayCommand<FolderInfo> DeleteFolderCommand { get; }
        public IAsyncRelayCommand UndoLastCommandCommand { get; }
        public ICommand CancelCurrentOperationCommand { get; }

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
            DeleteFolderCommand = new AsyncRelayCommand<FolderInfo>(DeleteFolderAsync);
            UndoLastCommandCommand = new AsyncRelayCommand(UndoLastCommandAsync, () => CanUndoCommands);
            CancelCurrentOperationCommand = new RelayCommand(CancelCurrentOperation, () => IsOperationInProgress);

            // Subscribe to command system events if available
            if (_folderService.IsCommandSystemEnabled)
            {
                _folderService.CommandExecuted += OnCommandExecuted;
                _folderService.FolderStateChanged += OnFolderStateChanged;
            }
        }

        #region Command System Event Handlers

        private void OnCommandExecuted(object sender, CommandExecutionEventArgs e)
        {
            // Update UI based on command execution
            OnPropertyChanged(nameof(CanUndoCommands));

            switch (e.Phase)
            {
                case CommandExecutionPhase.Started:
                    CurrentOperationStatus = $"Executing {e.Command.CommandType} operation...";
                    IsOperationInProgress = true;
                    break;

                case CommandExecutionPhase.Completed:
                    CurrentOperationStatus = $"{e.Command.CommandType} completed successfully";
                    IsOperationInProgress = false;

                    // Fire completion event for legacy compatibility
                    FolderOperationCompleted?.Invoke(this, new FolderOperationEventArgs(
                        e.Command.CommandType.ToString(),
                        e.Result?.Success ?? false,
                        e.Result?.Message));
                    break;

                case CommandExecutionPhase.Failed:
                    CurrentOperationStatus = $"{e.Command.CommandType} failed: {e.Result?.Message}";
                    IsOperationInProgress = false;

                    // Show error message
                    Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        MessageBox.Show(
                            $"Operation failed: {e.Result?.Message}",
                            "Folder Operation Error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    });
                    break;
            }

            // Update status message
            StatusMessageChanged?.Invoke(this, CurrentOperationStatus);
        }

        private void OnFolderStateChanged(object sender, FolderStateChangedEventArgs e)
        {
            // Update UI based on folder state changes
            if (e.NewState == FolderState.Processing)
            {
                CurrentOperationStatus = $"Processing folder: {Path.GetFileName(e.Path)}";
            }
            else if (e.NewState == FolderState.Available && e.OldState == FolderState.Processing)
            {
                CurrentOperationStatus = $"Folder operation completed: {Path.GetFileName(e.Path)}";
            }
        }

        #endregion

        #region Enhanced Folder Operations

        /// <summary>
        /// Create a new folder using command pattern or legacy method
        /// </summary>
        public async Task<bool> CreateFolderAsync(string parentPath, string folderName)
        {
            if (string.IsNullOrWhiteSpace(parentPath) || string.IsNullOrWhiteSpace(folderName))
            {
                StatusMessageChanged?.Invoke(this, "Invalid folder name or parent path");
                return false;
            }

            try
            {
                _currentOperationCts = new CancellationTokenSource();

                var result = await _folderService.CreateFolderAsync(parentPath, folderName, _currentOperationCts.Token);

                if (result.Success)
                {
                    var message = $"Created folder '{folderName}' successfully";
                    StatusMessageChanged?.Invoke(this, message);
                    return true;
                }
                else
                {
                    StatusMessageChanged?.Invoke(this, $"Failed to create folder: {result.Message}");
                    return false;
                }
            }
            catch (OperationCanceledException)
            {
                StatusMessageChanged?.Invoke(this, "Folder creation cancelled");
                return false;
            }
            catch (Exception ex)
            {
                StatusMessageChanged?.Invoke(this, $"Error creating folder: {ex.Message}");
                return false;
            }
            finally
            {
                _currentOperationCts?.Dispose();
                _currentOperationCts = null;
            }
        }

        /// <summary>
        /// Delete a folder using command pattern or legacy method
        /// </summary>
        public async Task DeleteFolderAsync(FolderInfo folder)
        {
            if (folder == null || !Directory.Exists(folder.FolderPath))
            {
                StatusMessageChanged?.Invoke(this, "Invalid folder or folder does not exist");
                return;
            }

            // Check if folder can be operated on
            if (IsCommandSystemEnabled && !_folderService.CanOperateOnFolder(folder.FolderPath))
            {
                var state = _folderService.GetFolderState(folder.FolderPath);
                StatusMessageChanged?.Invoke(this, $"Cannot delete folder: currently {state}");
                return;
            }

            var result = MessageBox.Show(
                $"Are you sure you want to delete the folder '{folder.Name}'?\n\nThis will move it to the Recycle Bin.",
                "Confirm Deletion",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            try
            {
                _currentOperationCts = new CancellationTokenSource();

                var deleteResult = await _folderService.DeleteFolderAsync(folder.FolderPath, true, _currentOperationCts.Token);

                if (deleteResult.Success)
                {
                    StatusMessageChanged?.Invoke(this, $"Deleted folder '{folder.Name}' successfully");
                }
                else
                {
                    StatusMessageChanged?.Invoke(this, $"Failed to delete folder: {deleteResult.Message}");
                }
            }
            catch (OperationCanceledException)
            {
                StatusMessageChanged?.Invoke(this, "Folder deletion cancelled");
            }
            catch (Exception ex)
            {
                StatusMessageChanged?.Invoke(this, $"Error deleting folder: {ex.Message}");
            }
            finally
            {
                _currentOperationCts?.Dispose();
                _currentOperationCts = null;
            }
        }

        /// <summary>
        /// Rename a folder using command pattern or legacy method
        /// </summary>
        public async Task<bool> RenameFolderAsync(string folderPath, string newName)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || string.IsNullOrWhiteSpace(newName))
            {
                StatusMessageChanged?.Invoke(this, "Invalid folder path or new name");
                return false;
            }

            // Check if folder can be operated on
            if (IsCommandSystemEnabled && !_folderService.CanOperateOnFolder(folderPath))
            {
                var state = _folderService.GetFolderState(folderPath);
                StatusMessageChanged?.Invoke(this, $"Cannot rename folder: currently {state}");
                return false;
            }

            try
            {
                _currentOperationCts = new CancellationTokenSource();

                var result = await _folderService.RenameFolderAsync(folderPath, newName, _currentOperationCts.Token);

                if (result.Success)
                {
                    StatusMessageChanged?.Invoke(this, $"Renamed folder to '{newName}' successfully");
                    return true;
                }
                else
                {
                    StatusMessageChanged?.Invoke(this, $"Failed to rename folder: {result.Message}");
                    return false;
                }
            }
            catch (OperationCanceledException)
            {
                StatusMessageChanged?.Invoke(this, "Folder rename cancelled");
                return false;
            }
            catch (Exception ex)
            {
                StatusMessageChanged?.Invoke(this, $"Error renaming folder: {ex.Message}");
                return false;
            }
            finally
            {
                _currentOperationCts?.Dispose();
                _currentOperationCts = null;
            }
        }

        /// <summary>
        /// Move folders using command pattern or legacy method
        /// </summary>
        public async Task<bool> MoveFoldersAsync(IEnumerable<FolderInfo> folders, string destinationPath)
        {
            if (folders == null || !folders.Any() || string.IsNullOrWhiteSpace(destinationPath))
            {
                StatusMessageChanged?.Invoke(this, "Invalid folders or destination path");
                return false;
            }

            var folderList = folders.ToList();
            var successCount = 0;

            try
            {
                _currentOperationCts = new CancellationTokenSource();

                foreach (var folder in folderList)
                {
                    if (_currentOperationCts.Token.IsCancellationRequested)
                        break;

                    // Check if folder can be operated on
                    if (IsCommandSystemEnabled && !_folderService.CanOperateOnFolder(folder.FolderPath))
                    {
                        var state = _folderService.GetFolderState(folder.FolderPath);
                        StatusMessageChanged?.Invoke(this, $"Skipping {folder.Name}: currently {state}");
                        continue;
                    }

                    var newPath = Path.Combine(destinationPath, folder.Name);
                    var result = await _folderService.MoveFolderAsync(folder.FolderPath, newPath, _currentOperationCts.Token);

                    if (result.Success)
                    {
                        successCount++;
                    }
                    else
                    {
                        StatusMessageChanged?.Invoke(this, $"Failed to move {folder.Name}: {result.Message}");
                    }
                }

                var message = successCount == folderList.Count
                    ? $"Successfully moved {successCount} folder(s)"
                    : $"Moved {successCount} of {folderList.Count} folder(s)";

                StatusMessageChanged?.Invoke(this, message);
                return successCount > 0;
            }
            catch (OperationCanceledException)
            {
                StatusMessageChanged?.Invoke(this, "Move operation cancelled");
                return false;
            }
            catch (Exception ex)
            {
                StatusMessageChanged?.Invoke(this, $"Error moving folders: {ex.Message}");
                return false;
            }
            finally
            {
                _currentOperationCts?.Dispose();
                _currentOperationCts = null;
            }
        }

        /// <summary>
        /// Copy folders using command pattern or legacy method
        /// </summary>
        public async Task<bool> CopyFoldersAsync(IEnumerable<FolderInfo> folders, string destinationPath)
        {
            if (folders == null || !folders.Any() || string.IsNullOrWhiteSpace(destinationPath))
            {
                StatusMessageChanged?.Invoke(this, "Invalid folders or destination path");
                return false;
            }

            var folderList = folders.ToList();
            var successCount = 0;

            try
            {
                _currentOperationCts = new CancellationTokenSource();

                foreach (var folder in folderList)
                {
                    if (_currentOperationCts.Token.IsCancellationRequested)
                        break;

                    var newPath = Path.Combine(destinationPath, folder.Name);
                    var result = await _folderService.CopyFolderAsync(folder.FolderPath, newPath, _currentOperationCts.Token);

                    if (result.Success)
                    {
                        successCount++;
                    }
                    else
                    {
                        StatusMessageChanged?.Invoke(this, $"Failed to copy {folder.Name}: {result.Message}");
                    }
                }

                var message = successCount == folderList.Count
                    ? $"Successfully copied {successCount} folder(s)"
                    : $"Copied {successCount} of {folderList.Count} folder(s)";

                StatusMessageChanged?.Invoke(this, message);
                return successCount > 0;
            }
            catch (OperationCanceledException)
            {
                StatusMessageChanged?.Invoke(this, "Copy operation cancelled");
                return false;
            }
            catch (Exception ex)
            {
                StatusMessageChanged?.Invoke(this, $"Error copying folders: {ex.Message}");
                return false;
            }
            finally
            {
                _currentOperationCts?.Dispose();
                _currentOperationCts = null;
            }
        }

        /// <summary>
        /// Undo the last command-based operation
        /// </summary>
        public async Task UndoLastCommandAsync()
        {
            if (!IsCommandSystemEnabled)
            {
                StatusMessageChanged?.Invoke(this, "Undo not available: Command system not enabled");
                return;
            }

            try
            {
                _currentOperationCts = new CancellationTokenSource();

                var result = await _folderService.UndoLastOperationAsync(_currentOperationCts.Token);

                if (result.Success)
                {
                    StatusMessageChanged?.Invoke(this, "Last operation undone successfully");
                }
                else
                {
                    StatusMessageChanged?.Invoke(this, $"Failed to undo: {result.Message}");
                }
            }
            catch (OperationCanceledException)
            {
                StatusMessageChanged?.Invoke(this, "Undo operation cancelled");
            }
            catch (Exception ex)
            {
                StatusMessageChanged?.Invoke(this, $"Error undoing operation: {ex.Message}");
            }
            finally
            {
                _currentOperationCts?.Dispose();
                _currentOperationCts = null;
                OnPropertyChanged(nameof(CanUndoCommands));
            }
        }

        /// <summary>
        /// Cancel the current operation if one is in progress
        /// </summary>
        public void CancelCurrentOperation()
        {
            _currentOperationCts?.Cancel();
            StatusMessageChanged?.Invoke(this, "Operation cancelled by user");
        }

        #endregion

        #region Clipboard Operations (Enhanced)

        /// <summary>
        /// Cut folders to clipboard with state validation
        /// </summary>
        public void CutFolders(IEnumerable<FolderInfo> folders)
        {
            if (folders == null) return;

            var folderList = folders.Where(f => f != null).ToList();
            if (folderList.Count == 0) return;

            // Validate folder states if command system is enabled
            if (IsCommandSystemEnabled)
            {
                var invalidFolders = folderList.Where(f => !_folderService.CanOperateOnFolder(f.FolderPath)).ToList();
                if (invalidFolders.Any())
                {
                    var invalidNames = string.Join(", ", invalidFolders.Select(f => f.Name));
                    StatusMessageChanged?.Invoke(this, $"Cannot cut folders currently being processed: {invalidNames}");

                    // Remove invalid folders from selection
                    folderList = folderList.Except(invalidFolders).ToList();
                    if (folderList.Count == 0) return;
                }
            }

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
        /// Copy folders to clipboard with state validation
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
                ? $"Copied folder '{folderList[0].Name}' to clipboard. Select a destination folder and paste."
                : $"Copied {folderList.Count} folders to clipboard. Select a destination folder and paste.";

            StatusMessageChanged?.Invoke(this, message);
        }

        /// <summary>
        /// Paste folders from clipboard using enhanced operations
        /// </summary>
        public async Task<bool> PasteFoldersAsync(string destinationPath)
        {
            if (!HasClipboardContent || string.IsNullOrWhiteSpace(destinationPath))
            {
                StatusMessageChanged?.Invoke(this, "No folders in clipboard or invalid destination");
                return false;
            }

            if (!Directory.Exists(destinationPath))
            {
                StatusMessageChanged?.Invoke(this, "Destination folder does not exist");
                return false;
            }

            bool success;

            if (_isCutOperation)
            {
                success = await MoveFoldersAsync(_clipboardFolders, destinationPath);
            }
            else
            {
                success = await CopyFoldersAsync(_clipboardFolders, destinationPath);
            }

            if (success)
            {
                // Clear clipboard after successful paste
                ClearClipboard();
            }

            return success;
        }

        /// <summary>
        /// Clear the clipboard
        /// </summary>
        public void ClearClipboard()
        {
            _clipboardFolders.Clear();
            _isCutOperation = false;

            OnPropertyChanged(nameof(HasClipboardContent));
            OnPropertyChanged(nameof(IsCutOperation));
            OnPropertyChanged(nameof(ClipboardFolders));

            StatusMessageChanged?.Invoke(this, "Clipboard cleared");
        }

        #endregion

        #region Legacy Support (preserved for backward compatibility)

        /// <summary>
        /// Legacy undo folder movement (for compatibility)
        /// </summary>
        private async Task UndoLastFolderMovementAsync()
        {
            if (_undoStack.Count == 0)
            {
                // Try command system undo if available
                if (IsCommandSystemEnabled)
                {
                    await UndoLastCommandAsync();
                }
                else
                {
                    StatusMessageChanged?.Invoke(this, "No folder movements to undo");
                }
                return;
            }

            var operation = _undoStack.Pop();

            try
            {
                if (Directory.Exists(operation.NewPath) && !Directory.Exists(operation.OriginalPath))
                {
                    Directory.Move(operation.NewPath, operation.OriginalPath);
                    StatusMessageChanged?.Invoke(this, $"Undid movement of '{operation.FolderName}'");
                }
                else
                {
                    StatusMessageChanged?.Invoke(this, "Cannot undo: folder state has changed");
                }
            }
            catch (Exception ex)
            {
                StatusMessageChanged?.Invoke(this, $"Failed to undo movement: {ex.Message}");
                _undoStack.Push(operation); // Put it back if failed
            }

            OnPropertyChanged(nameof(CanUndoCommands));
        }

        private bool CanUndoFolderMovement()
        {
            return _undoStack.Count > 0 || CanUndoCommands;
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Get folder state display string
        /// </summary>
        public string GetFolderStateDisplay(string folderPath)
        {
            if (!IsCommandSystemEnabled || string.IsNullOrEmpty(folderPath))
                return "Available";

            var state = _folderService.GetFolderState(folderPath);
            return state.ToString();
        }

        /// <summary>
        /// Check if a specific folder can be operated on
        /// </summary>
        public bool CanOperateOnFolder(string folderPath)
        {
            if (!IsCommandSystemEnabled)
                return true;

            return _folderService.CanOperateOnFolder(folderPath);
        }

        #endregion

        #region Disposal

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Unsubscribe from events
                if (_folderService.IsCommandSystemEnabled)
                {
                    _folderService.CommandExecuted -= OnCommandExecuted;
                    _folderService.FolderStateChanged -= OnFolderStateChanged;
                }

                // Cancel any ongoing operations
                _currentOperationCts?.Cancel();
                _currentOperationCts?.Dispose();
                _currentOperationCts = null;
            }

            base.Dispose(disposing);
        }

        #endregion
    }

    #region Event Args and Supporting Classes

    /// <summary>
    /// Event arguments for folder operations
    /// </summary>
    public class FolderOperationEventArgs : EventArgs
    {
        public string Operation { get; }
        public bool Success { get; }
        public string Message { get; }

        public FolderOperationEventArgs(string operation, bool success, string message = null)
        {
            Operation = operation;
            Success = success;
            Message = message;
        }
    }

    /// <summary>
    /// Legacy folder move operation for undo support
    /// </summary>
    public class FolderMoveOperation
    {
        public string FolderName { get; set; }
        public string OriginalPath { get; set; }
        public string NewPath { get; set; }
        public DateTime MovedAt { get; set; }
    }

    #endregion
}