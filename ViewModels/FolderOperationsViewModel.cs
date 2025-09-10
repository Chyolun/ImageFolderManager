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
using System.Windows.Controls;

namespace ImageFolderManager.ViewModels
{
    /// <summary>
    /// Handles all folder operations using the command pattern with state management
    /// </summary>
    public class FolderOperationsViewModel : ViewModelBase
    {
        private readonly UnifiedFolderService _folderService;
        private readonly CommandExecutor _commandExecutor;
        private readonly FolderStateMachine _stateMachine;

        // Legacy clipboard state for backward compatibility
        private List<FolderInfo> _clipboardFolders = new List<FolderInfo>();
        private bool _isCutOperation;

        #region Properties

        public bool IsCutOperation => _isCutOperation;
        public bool HasClipboardContent => _clipboardFolders.Count > 0;
        public IReadOnlyList<FolderInfo> ClipboardFolders => _clipboardFolders;

        /// <summary>
        /// Gets whether any folder operations are currently in progress
        /// </summary>
        public bool HasOperationsInProgress => GetOperationsInProgress().Any();

        /// <summary>
        /// Gets the count of operations currently in progress
        /// </summary>
        public int OperationsInProgressCount => GetOperationsInProgress().Count();

        #endregion

        #region Commands

        public ICommand UndoFolderMovementCommand { get; }
        public IAsyncRelayCommand<FolderInfo> DeleteFolderCommand { get; }
        public IAsyncRelayCommand<FolderInfo> CreateNewFolderCommand { get; }
        public IAsyncRelayCommand CancelAllOperationsCommand { get; }

        #endregion

        #region Events

        public event EventHandler<FolderOperationEventArgs> FolderOperationCompleted;
        public event EventHandler<string> StatusMessageChanged;
        public event EventHandler<CommandExecutionEventArgs> CommandStarted;
        public event EventHandler<CommandExecutionEventArgs> CommandCompleted;
        public event EventHandler<CommandExecutionEventArgs> CommandFailed;

        #endregion

        public FolderOperationsViewModel(UnifiedFolderService folderService)
        {
            _folderService = folderService ?? throw new ArgumentNullException(nameof(folderService));
            _commandExecutor = _folderService.CommandExecutor;
            _stateMachine = _folderService.StateMachine;

            // Initialize commands
            UndoFolderMovementCommand = new AsyncRelayCommand(UndoLastCommandAsync, CanUndoLastCommand);
            DeleteFolderCommand = new AsyncRelayCommand<FolderInfo>(DeleteFolderAsync, CanDeleteFolder);
            CreateNewFolderCommand = new AsyncRelayCommand<FolderInfo>(CreateNewFolderAsync, CanCreateNewFolder);
            CancelAllOperationsCommand = new AsyncRelayCommand(CancelAllOperationsAsync, () => HasOperationsInProgress);

            // Subscribe to command system events
            if (_commandExecutor != null)
            {
                _commandExecutor.CommandStarted += OnCommandStarted;
                _commandExecutor.CommandCompleted += OnCommandCompleted;
                _commandExecutor.CommandFailed += OnCommandFailed;
            }
        }

        #region Command System Event Handlers

        private void OnCommandStarted(object sender, CommandExecutionEventArgs e)
        {
            UpdateStatus($"Starting {e.Command.CommandType} operation...");
            OnPropertyChanged(nameof(HasOperationsInProgress));
            OnPropertyChanged(nameof(OperationsInProgressCount));
            CommandStarted?.Invoke(sender, e);
        }

        private void OnCommandCompleted(object sender, CommandExecutionEventArgs e)
        {
            var message = GetCompletionMessage(e.Command);
            UpdateStatus(message);

            OnPropertyChanged(nameof(HasOperationsInProgress));
            OnPropertyChanged(nameof(OperationsInProgressCount));

            // Convert to legacy event for backward compatibility
            var legacyArgs = ConvertToLegacyEventArgs(e.Command, true);
            if (legacyArgs != null)
            {
                FolderOperationCompleted?.Invoke(this, legacyArgs);
            }

            CommandCompleted?.Invoke(sender, e);
            CommandManager.InvalidateRequerySuggested();
        }

        private void OnCommandFailed(object sender, CommandExecutionEventArgs e)
        {
            var message = $"{e.Command.CommandType} operation failed: {e.Result?.Message ?? "Unknown error"}";
            UpdateStatus(message);

            OnPropertyChanged(nameof(HasOperationsInProgress));
            OnPropertyChanged(nameof(OperationsInProgressCount));

            // Show error to user
            MessageBox.Show(message, "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);

            CommandFailed?.Invoke(sender, e);
            CommandManager.InvalidateRequerySuggested();
        }

        #endregion

        #region Folder Operations Using Command Pattern

        /// <summary>
        /// Creates a new folder using the command system
        /// </summary>
        public async Task<bool> CreateNewFolderAsync(FolderInfo parentFolder)
        {
            if (parentFolder == null || !Directory.Exists(parentFolder.FolderPath))
                return false;

            try
            {
                // Check if parent folder is locked
                if (_folderService.IsFolderLocked(parentFolder.FolderPath))
                {
                    MessageBox.Show("Parent folder is currently locked by another operation.",
                        "Operation Not Allowed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                var dialog = new InputDialog("Create New Folder", "Enter folder name:", "New Folder");
                if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.InputText))
                    return false;

                var result = await _folderService.CreateFolderAsync(parentFolder.FolderPath, dialog.InputText);

                if (!result.Success)
                {
                    MessageBox.Show($"Failed to create folder: {result.Message}",
                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                return result.Success;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error creating folder: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        /// <summary>
        /// Deletes a folder using the command system
        /// </summary>
        public async Task<bool> DeleteFolderAsync(FolderInfo folder)
        {
            if (folder == null || !Directory.Exists(folder.FolderPath))
                return false;

            try
            {
                // Check if folder is locked
                if (_folderService.IsFolderLocked(folder.FolderPath))
                {
                    MessageBox.Show("Folder is currently locked by another operation.",
                        "Operation Not Allowed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                var result = MessageBox.Show(
                    $"Are you sure you want to delete the folder:\n\n{folder.FolderPath}?\n\nThis will move it to the Recycle Bin.",
                    "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                    return false;

                var commandResult = await _folderService.DeleteFolderAsync(folder.FolderPath, true);

                if (!commandResult.Success)
                {
                    MessageBox.Show($"Failed to delete folder: {commandResult.Message}",
                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                return commandResult.Success;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error deleting folder: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        /// <summary>
        /// Moves folders using the command system
        /// </summary>
        public async Task<bool> MoveFoldersAsync(IEnumerable<FolderInfo> sourceFolders, FolderInfo targetFolder)
        {
            if (sourceFolders == null || targetFolder == null)
                return false;

            var folderList = sourceFolders.Where(f => f != null && Directory.Exists(f.FolderPath)).ToList();
            if (folderList.Count == 0)
                return false;

            try
            {
                // Check for locked folders
                var lockedFolders = folderList.Where(f => _folderService.IsFolderLocked(f.FolderPath)).ToList();
                if (lockedFolders.Any())
                {
                    var folderNames = string.Join(", ", lockedFolders.Select(f => f.Name));
                    MessageBox.Show($"The following folders are currently locked: {folderNames}",
                        "Operation Not Allowed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                // Single folder move
                if (folderList.Count == 1)
                {
                    var sourceFolder = folderList[0];
                    var destinationPath = Path.Combine(targetFolder.FolderPath, sourceFolder.Name);

                    var result = await _folderService.MoveFolderAsync(sourceFolder.FolderPath, destinationPath);
                    return result.Success;
                }

                // Multiple folders - use batch operation
                var moveCommands = folderList.Select(f =>
                    new MoveFolderCommand(f.FolderPath, Path.Combine(targetFolder.FolderPath, f.Name)) as IFolderCommand).ToList();

                var batchResult = await _folderService.ExecuteBatchOperationAsync(moveCommands);
                return batchResult.Success;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error moving folders: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        /// <summary>
        /// Copies folders using the command system
        /// </summary>
        public async Task<bool> CopyFoldersAsync(IEnumerable<FolderInfo> sourceFolders, FolderInfo targetFolder)
        {
            if (sourceFolders == null || targetFolder == null)
                return false;

            var folderList = sourceFolders.Where(f => f != null && Directory.Exists(f.FolderPath)).ToList();
            if (folderList.Count == 0)
                return false;

            try
            {
                // Single folder copy
                if (folderList.Count == 1)
                {
                    var sourceFolder = folderList[0];
                    var destinationPath = Path.Combine(targetFolder.FolderPath, sourceFolder.Name);

                    var result = await _folderService.CopyFolderAsync(sourceFolder.FolderPath, destinationPath);
                    return result.Success;
                }

                // Multiple folders - use batch operation
                var copyCommands = folderList.Select(f =>
                    new CopyFolderCommand(f.FolderPath, Path.Combine(targetFolder.FolderPath, f.Name)) as IFolderCommand).ToList();

                var batchResult = await _folderService.ExecuteBatchOperationAsync(copyCommands);
                return batchResult.Success;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error copying folders: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        /// <summary>
        /// Renames a folder using the command system
        /// </summary>
        public async Task<bool> RenameFolderAsync(FolderInfo folder, string newName)
        {
            if (folder == null || string.IsNullOrWhiteSpace(newName))
                return false;

            try
            {
                // Check if folder is locked
                if (_folderService.IsFolderLocked(folder.FolderPath))
                {
                    MessageBox.Show("Folder is currently locked by another operation.",
                        "Operation Not Allowed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                var result = await _folderService.RenameFolderAsync(folder.FolderPath, newName);

                if (!result.Success)
                {
                    MessageBox.Show($"Failed to rename folder: {result.Message}",
                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                return result.Success;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error renaming folder: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        #endregion

        #region Legacy Clipboard Operations (Backward Compatibility)

        /// <summary>
        /// Cuts one or more folders to clipboard (legacy method)
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
                ? $"Cut folder '{folderList[0].Name}' to clipboard. Select a destination folder and paste."
                : $"Cut {folderList.Count} folders to clipboard. Select a destination folder and paste.";

            UpdateStatus(message);
        }

        /// <summary>
        /// Copies one or more folders to clipboard (legacy method)
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

            UpdateStatus(message);
        }

        /// <summary>
        /// Pastes clipboard content to target folder (legacy method)
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

        #region Command System Operations

        /// <summary>
        /// Undo the last executed command
        /// </summary>
        public async Task UndoLastCommandAsync()
        {
            try
            {
                var result = await _commandExecutor.UndoLastCommandAsync();

                if (result.Success)
                {
                    UpdateStatus("Last operation undone successfully.");
                }
                else
                {
                    UpdateStatus($"Failed to undo last operation: {result.Message}");
                    MessageBox.Show($"Failed to undo last operation: {result.Message}",
                        "Undo Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error during undo operation: {ex.Message}");
                MessageBox.Show($"Error during undo operation: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Cancel all operations that are currently in progress
        /// </summary>
        public async Task CancelAllOperationsAsync()
        {
            try
            {
                await _commandExecutor.CancelAllOperationsAsync();
                UpdateStatus("All operations cancelled.");
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error cancelling operations: {ex.Message}");
            }
        }

        /// <summary>
        /// Get the state of a specific folder
        /// </summary>
        public FolderState GetFolderState(FolderInfo folder)
        {
            return folder != null ? _folderService.GetFolderState(folder.FolderPath) : FolderState.Available;
        }

        /// <summary>
        /// Check if a folder is currently locked
        /// </summary>
        public bool IsFolderLocked(FolderInfo folder)
        {
            return folder != null && _folderService.IsFolderLocked(folder.FolderPath);
        }

        #endregion

        #region Command Validation Methods

        private bool CanDeleteFolder(FolderInfo folder)
        {
            return folder != null &&
                   Directory.Exists(folder.FolderPath) &&
                   !IsFolderLocked(folder);
        }

        private bool CanCreateNewFolder(FolderInfo parentFolder)
        {
            return parentFolder != null &&
                   Directory.Exists(parentFolder.FolderPath) &&
                   !IsFolderLocked(parentFolder);
        }

        private bool CanUndoLastCommand()
        {
            return _commandExecutor?.HasUndoableCommands == true;
        }

        #endregion

        #region Helper Methods

        private IEnumerable<string> GetOperationsInProgress()
        {
            // This would need to be implemented in CommandExecutor to return current operations
            // For now, return empty collection
            return Enumerable.Empty<string>();
        }

        private string GetCompletionMessage(IFolderCommand command)
        {
            return command.CommandType switch
            {
                FolderCommandType.Create => "Folder created successfully.",
                FolderCommandType.Delete => "Folder deleted successfully.",
                FolderCommandType.Move => "Folder moved successfully.",
                FolderCommandType.Copy => "Folder copied successfully.",
                FolderCommandType.Rename => "Folder renamed successfully.",
                FolderCommandType.BatchMove => "Folders moved successfully.",
                FolderCommandType.BatchCopy => "Folders copied successfully.",
                FolderCommandType.BatchDelete => "Folders deleted successfully.",
                _ => "Operation completed successfully."
            };
        }

        private FolderOperationEventArgs ConvertToLegacyEventArgs(IFolderCommand command, bool success)
        {
            // Convert command system events to legacy event args for backward compatibility
            var operation = command.CommandType switch
            {
                FolderCommandType.Create => FolderOperation.Create,
                FolderCommandType.Delete => FolderOperation.Delete,
                FolderCommandType.Move => FolderOperation.Move,
                FolderCommandType.Copy => FolderOperation.Copy,
                FolderCommandType.Rename => FolderOperation.Rename,
                _ => FolderOperation.Move // Default fallback
            };

            var affectedPaths = command.GetAffectedPaths();
            var sourcePath = affectedPaths.FirstOrDefault();
            var destinationPath = affectedPaths.Skip(1).FirstOrDefault();

            if (success)
            {
                return FolderOperationEventArgs.CreateSuccess(operation, sourcePath, destinationPath);
            }
            else
            {
                return FolderOperationEventArgs.CreateFailure(operation, sourcePath, "Operation failed");
            }
        }

        private void UpdateStatus(string message)
        {
            StatusMessageChanged?.Invoke(this, message);
        }

        #endregion

        #region IDisposable Implementation

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Unsubscribe from events
                if (_commandExecutor != null)
                {
                    _commandExecutor.CommandStarted -= OnCommandStarted;
                    _commandExecutor.CommandCompleted -= OnCommandCompleted;
                    _commandExecutor.CommandFailed -= OnCommandFailed;
                }
            }

            base.Dispose(disposing);
        }

        #endregion
    }

    #region Helper Classes for Missing Dialog

    /// <summary>
    /// Simple input dialog for getting text input from user
    /// </summary>
    public class InputDialog : Window
    {
        public string InputText { get; private set; }

        public InputDialog(string title, string prompt, string defaultValue = "")
        {
            Title = title;
            Width = 400;
            Height = 150;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var stackPanel = new StackPanel { Margin = new Thickness(10) };

            var promptLabel = new Label { Content = prompt };
            stackPanel.Children.Add(promptLabel);

            var textBox = new TextBox { Text = defaultValue, Margin = new Thickness(0, 5, 0, 10) };
            stackPanel.Children.Add(textBox);

            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

            var okButton = new Button { Content = "OK", Width = 75, Margin = new Thickness(0, 0, 5, 0) };
            okButton.Click += (s, e) => { InputText = textBox.Text; DialogResult = true; };
            buttonPanel.Children.Add(okButton);

            var cancelButton = new Button { Content = "Cancel", Width = 75 };
            cancelButton.Click += (s, e) => { DialogResult = false; };
            buttonPanel.Children.Add(cancelButton);

            stackPanel.Children.Add(buttonPanel);
            Content = stackPanel;

            textBox.Focus();
            textBox.SelectAll();
        }
    }

    #endregion
}