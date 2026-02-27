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

namespace ImageFolderManager.ViewModels
{
    /// <summary>
    /// Handles all folder operations (move, copy, create, delete, rename) and
    /// owns the <see cref="UndoManager"/> that tracks reversible operations.
    /// </summary>
    public class FolderOperationsViewModel : ViewModelBase
    {
        private readonly UnifiedFolderService _folderService;

        // ── Clipboard state ───────────────────────────────────────────────
        private List<FolderInfo> _clipboardFolders = new List<FolderInfo>();
        private bool _isCutOperation;

        // ── Undo ─────────────────────────────────────────────────────────
        /// <summary>
        /// The single unified undo manager.  All operation methods push a record
        /// here when they succeed; the command delegate calls UndoLastAsync().
        /// </summary>
        public UndoManager UndoManager { get; }

        #region Properties

        public bool IsCutOperation       => _isCutOperation;
        public bool HasClipboardContent  => _clipboardFolders.Count > 0;
        public IReadOnlyList<FolderInfo> ClipboardFolders => _clipboardFolders;

        /// <summary>Convenience forwarder so callers can bind directly.</summary>
        public bool CanUndo => UndoManager.CanUndo;

        /// <summary>Short description of the next thing that will be undone.</summary>
        public string UndoDescription => UndoManager.NextUndoDescription ?? "Nothing to undo";

        #endregion

        #region Commands

        public IAsyncRelayCommand      UndoCommand          { get; }
        public IAsyncRelayCommand<FolderInfo> DeleteFolderCommand   { get; }
        public IAsyncRelayCommand<FolderInfo> CreateNewFolderCommand { get; }

        // Keep the old name as a forwarding property so existing XAML bindings
        // (UndoFolderMovementCommand) continue to work without change.
        public IAsyncRelayCommand UndoFolderMovementCommand => UndoCommand;

        #endregion

        #region Events

        public event EventHandler<FolderOperationEventArgs> FolderOperationCompleted;
        public event EventHandler<string>                   StatusMessageChanged;

        #endregion

        // ─────────────────────────────────────────────────────────────────

        public FolderOperationsViewModel(UnifiedFolderService folderService)
        {
            _folderService = folderService
                ?? throw new ArgumentNullException(nameof(folderService));

            // Create the undo manager and wire its events
            UndoManager = new UndoManager(folderService);
            UndoManager.StateChanged  += (_, __) => RefreshUndoState();
            UndoManager.StatusChanged += (_, msg) => UpdateStatus(msg);

            // Commands
            UndoCommand           = new AsyncRelayCommand(ExecuteUndoAsync, () => UndoManager.CanUndo);
            DeleteFolderCommand   = new AsyncRelayCommand<FolderInfo>(DeleteFolderAsync, CanDeleteFolder);
            CreateNewFolderCommand = new AsyncRelayCommand<FolderInfo>(CreateNewFolderAsync, CanCreateNewFolder);
        }

        // ─────────────────────────────────────────────────────────────────
        #region Undo
        // ─────────────────────────────────────────────────────────────────

        private async Task ExecuteUndoAsync()
        {
            UndoResult result = await UndoManager.UndoLastAsync();

            if (result.Success)
            {
               
                FolderOperation opType = MapUndoTypeToFolderOperation(result.OperationType);
                OnFolderOperationCompleted(new FolderOperationEventArgs
                {
                    Operation = opType,
                    SourcePath = result.PreviousPath, 
                    DestinationPath = result.RestoredPath,    
                    Success = true,
                    IsUndoOperation = true,
                    Timestamp = DateTime.Now
                });
            }

            CommandManager.InvalidateRequerySuggested();
        }

        private static FolderOperation MapUndoTypeToFolderOperation(UndoOperationType type)
        {
            switch (type)
            {
                case UndoOperationType.Move:
                case UndoOperationType.MultiMove: return FolderOperation.Move;
                case UndoOperationType.Rename: return FolderOperation.Rename;
                case UndoOperationType.Copy:
                case UndoOperationType.Create: return FolderOperation.Delete;
                default: return FolderOperation.Refresh;
            }
        }

        private void RefreshUndoState()
        {
            UndoCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(UndoDescription));
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────
        #region Create
        // ─────────────────────────────────────────────────────────────────

        public async Task CreateNewFolderAsync(FolderInfo parentFolder)
        {
            if (parentFolder == null || !Directory.Exists(parentFolder.FolderPath))
                return;

            try
            {
                string folderName = Microsoft.VisualBasic.Interaction.InputBox(
                    "Enter folder name:", "Create New Folder", "New Folder");

                if (string.IsNullOrWhiteSpace(folderName))
                    return;

                bool success = await _folderService.CreateFolderAsync(
                    parentFolder.FolderPath, folderName);

                if (success)
                {
                    string newPath = Path.Combine(parentFolder.FolderPath, folderName);

                    // ── Record for undo ───────────────────────────────────
                    UndoManager.Push(UndoRecord.ForCreate(newPath));

                    UpdateStatus($"Created folder '{folderName}'.");
                    OnFolderOperationCompleted(FolderOperationEventArgs.CreateSuccess(
                        FolderOperation.Create, newPath));
                }
                else
                {
                    MessageBox.Show($"Failed to create folder '{folderName}'.",
                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error creating folder: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────
        #region Delete
        // ─────────────────────────────────────────────────────────────────

        public async Task<bool> DeleteFolderAsync(FolderInfo folder)
        {
            if (folder == null || !Directory.Exists(folder.FolderPath))
                return false;

            try
            {
                var result = MessageBox.Show(
                    $"Are you sure you want to delete:\n\n{folder.FolderPath}\n\nThis will move it to the Recycle Bin.",
                    "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                    return false;

                // Note: Delete is intentionally NOT added to the undo stack.
                // The folder is in the Recycle Bin and the user can restore it
                // manually.  Adding it here would give false confidence since
                // we cannot programmatically restore from the Recycle Bin.
                bool success = await _folderService.DeleteFolderAsync(folder.FolderPath, useRecycleBin: true);

                if (success)
                {
                    UpdateStatus($"Moved '{folder.Name}' to Recycle Bin.");
                    OnFolderOperationCompleted(FolderOperationEventArgs.CreateSuccess(
                        FolderOperation.Delete, folder.FolderPath));

                    CommandManager.InvalidateRequerySuggested();
                }
                else
                {
                    MessageBox.Show($"Failed to delete folder '{folder.Name}'.",
                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                return success;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error deleting folder: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        public async Task<bool> DeleteFoldersAsync(IEnumerable<FolderInfo> folders)
        {
            if (folders == null) return false;

            var folderList = folders
                .Where(f => f != null && Directory.Exists(f.FolderPath))
                .ToList();

            if (folderList.Count == 0) return false;

            if (folderList.Count == 1)
                return await DeleteFolderAsync(folderList[0]);

            var result = MessageBox.Show(
                $"Are you sure you want to delete {folderList.Count} folders?\nThey will be moved to the Recycle Bin.",
                "Confirm Delete Multiple", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return false;

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
                        Application.Current.Dispatcher.Invoke(() =>
                            progressDialog.UpdateProgress(progress, $"Deleting: {folder.Name}"));

                        bool success = await _folderService.DeleteFolderAsync(
                            folder.FolderPath, useRecycleBin: true);

                        if (!success) overallSuccess = false;

                        Application.Current.Dispatcher.Invoke(() =>
                            OnFolderOperationCompleted(FolderOperationEventArgs.CreateSuccess(
                                FolderOperation.Delete, folder.FolderPath)));
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error deleting {folder.FolderPath}: {ex.Message}");
                        overallSuccess = false;
                    }

                    processed++;
                }

                Application.Current.Dispatcher.Invoke(() =>
                    progressDialog.UpdateProgress(1.0, "Delete completed"));
            });

            progressDialog.ShowDialog();
            await deleteTask;

            UpdateStatus(overallSuccess
                ? $"Deleted {folderList.Count} folders."
                : $"Deleted {processed} folders with some errors.");

            return overallSuccess;
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────
        #region Rename
        // ─────────────────────────────────────────────────────────────────

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

                string oldPath = folder.FolderPath;
                bool success = await _folderService.RenameFolderAsync(oldPath, newName);

                if (success)
                {
                    string newPath = Path.Combine(
                        Path.GetDirectoryName(oldPath) ?? string.Empty, newName);

                    // ── Record for undo ───────────────────────────────────
                    UndoManager.Push(UndoRecord.ForRename(oldPath, newPath));

                    UpdateStatus($"Renamed '{folder.Name}' → '{newName}'.");
                    OnFolderOperationCompleted(FolderOperationEventArgs.CreateSuccess(
                        FolderOperation.Rename, oldPath, newPath));
                    return true;
                }
                else
                {
                    MessageBox.Show($"Failed to rename folder to '{newName}'.",
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

        #endregion

        // ─────────────────────────────────────────────────────────────────
        #region Clipboard (Cut / Copy)
        // ─────────────────────────────────────────────────────────────────

        public void CutFolders(IEnumerable<FolderInfo> folders)
        {
            var list = folders?.Where(f => f != null).ToList();
            if (list == null || list.Count == 0) return;

            _clipboardFolders = list;
            _isCutOperation   = true;
            RaiseClipboardProperties();

            UpdateStatus(list.Count == 1
                ? $"Cut '{list[0].Name}' to clipboard."
                : $"Cut {list.Count} folders to clipboard.");
        }

        public void CopyFolders(IEnumerable<FolderInfo> folders)
        {
            var list = folders?.Where(f => f != null).ToList();
            if (list == null || list.Count == 0) return;

            _clipboardFolders = list;
            _isCutOperation   = false;
            RaiseClipboardProperties();

            UpdateStatus(list.Count == 1
                ? $"Copied '{list[0].Name}' to clipboard."
                : $"Copied {list.Count} folders to clipboard.");
        }

        public void ClearClipboard()
        {
            _clipboardFolders.Clear();
            _isCutOperation = false;
            RaiseClipboardProperties();
            UpdateStatus("Clipboard cleared.");
        }

        private void RaiseClipboardProperties()
        {
            OnPropertyChanged(nameof(HasClipboardContent));
            OnPropertyChanged(nameof(IsCutOperation));
            OnPropertyChanged(nameof(ClipboardFolders));
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────
        #region Paste
        // ─────────────────────────────────────────────────────────────────

        public async Task<bool> PasteFoldersAsync(FolderInfo targetFolder)
        {
            if (targetFolder == null || !HasClipboardContent) return false;

            bool success;

            if (_isCutOperation)
                success = await MoveFoldersAsync(_clipboardFolders, targetFolder);
            else
                success = await CopyFoldersAsync(_clipboardFolders, targetFolder);

            if (success && _isCutOperation)
                ClearClipboard();

            CommandManager.InvalidateRequerySuggested();
            return success;
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────
        #region Move
        // ─────────────────────────────────────────────────────────────────

        public async Task<bool> MoveFoldersAsync(
            IEnumerable<FolderInfo> sourceFolders, FolderInfo targetFolder)
        {
            if (sourceFolders == null || targetFolder == null) return false;

            var list = sourceFolders
                .Where(f => f != null && Directory.Exists(f.FolderPath))
                .ToList();

            if (list.Count == 0) return false;

            return list.Count == 1
                ? await MoveSingleFolderAsync(list[0], targetFolder)
                : await MoveMultipleFoldersAsync(list, targetFolder);
        }

        private async Task<bool> MoveSingleFolderAsync(
            FolderInfo sourceFolder, FolderInfo targetFolder)
        {
            try
            {
                string destPath = Path.Combine(targetFolder.FolderPath, sourceFolder.Name);

                if (Directory.Exists(destPath))
                {
                    MessageBox.Show(
                        $"A folder named '{sourceFolder.Name}' already exists in the destination.",
                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                bool success = await _folderService.MoveFolderAsync(
                    sourceFolder.FolderPath, destPath);

                if (success)
                {
                    // ── Record for undo ───────────────────────────────────
                    UndoManager.Push(UndoRecord.ForMove(sourceFolder.FolderPath, destPath));

                    UpdateStatus($"Moved '{sourceFolder.Name}' → '{targetFolder.Name}'.");
                    OnFolderOperationCompleted(FolderOperationEventArgs.CreateSuccess(
                        FolderOperation.Move, sourceFolder.FolderPath, destPath));
                }
                else
                {
                    MessageBox.Show($"Failed to move folder '{sourceFolder.Name}'.",
                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                return success;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error moving folder: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private async Task<bool> MoveMultipleFoldersAsync(
            List<FolderInfo> folderList, FolderInfo targetFolder)
        {
            var progressDialog = new ProgressDialog(
                "Moving Folders",
                $"Moving {folderList.Count} folders...");
            progressDialog.Owner = Application.Current.MainWindow;

            bool overallSuccess = true;
            int  processed      = 0;

            // Collect successful source paths for a single MultiMove undo record
            var movedSources = new List<string>();

            var moveTask = Task.Run(async () =>
            {
                foreach (var folder in folderList)
                {
                    try
                    {
                        double progress = (double)processed / folderList.Count;
                        Application.Current.Dispatcher.Invoke(() =>
                            progressDialog.UpdateProgress(progress, $"Moving: {folder.Name}"));

                        string destPath = Path.Combine(targetFolder.FolderPath, folder.Name);

                        if (Directory.Exists(destPath))
                        {
                            Debug.WriteLine($"[Move] Skipped '{folder.Name}' — already exists at destination.");
                            overallSuccess = false;
                        }
                        else
                        {
                            bool success = await _folderService.MoveFolderAsync(
                                folder.FolderPath, destPath);

                            if (success)
                            {
                                movedSources.Add(folder.FolderPath);
                                Application.Current.Dispatcher.Invoke(() =>
                                    OnFolderOperationCompleted(FolderOperationEventArgs.CreateSuccess(
                                        FolderOperation.Move, folder.FolderPath, destPath)));
                            }
                            else
                            {
                                overallSuccess = false;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error moving {folder.FolderPath}: {ex.Message}");
                        overallSuccess = false;
                    }

                    processed++;
                }

                Application.Current.Dispatcher.Invoke(() =>
                    progressDialog.UpdateProgress(1.0, "Move completed"));
            });

            progressDialog.ShowDialog();
            await moveTask;

            // ── Record for undo (one MultiMove record for all successes) ──
            if (movedSources.Count > 0)
            {
                UndoManager.Push(UndoRecord.ForMultiMove(movedSources, targetFolder.FolderPath));
            }

            UpdateStatus(overallSuccess
                ? $"Moved {folderList.Count} folders."
                : $"Moved {movedSources.Count} of {folderList.Count} folders (some errors).");

            return overallSuccess;
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────
        #region Copy
        // ─────────────────────────────────────────────────────────────────

        public async Task<bool> CopyFoldersAsync(
            IEnumerable<FolderInfo> sourceFolders, FolderInfo targetFolder)
        {
            if (sourceFolders == null || targetFolder == null) return false;

            var folderList = sourceFolders
                .Where(f => f != null && Directory.Exists(f.FolderPath))
                .ToList();

            if (folderList.Count == 0) return false;

            var progressDialog = new ProgressDialog(
                "Copying Folders",
                $"Copying {folderList.Count} folders...");
            progressDialog.Owner = Application.Current.MainWindow;

            bool overallSuccess = true;
            int  processed      = 0;

            var copyTask = Task.Run(async () =>
            {
                foreach (var sourceFolder in folderList)
                {
                    try
                    {
                        double progress = (double)processed / folderList.Count;
                        Application.Current.Dispatcher.Invoke(() =>
                            progressDialog.UpdateProgress(progress, $"Copying: {sourceFolder.Name}"));

                        string destPath = Path.Combine(targetFolder.FolderPath, sourceFolder.Name);

                        if (Directory.Exists(destPath))
                        {
                            // Auto-rename copy to avoid collision
                            int idx = 1;
                            string baseName = sourceFolder.Name;
                            while (Directory.Exists(destPath))
                            {
                                destPath = Path.Combine(
                                    targetFolder.FolderPath, $"{baseName} ({idx++})");
                            }
                        }

                        CopyDirectoryInternal(sourceFolder.FolderPath, destPath);

                        // ── Record for undo ───────────────────────────────
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            UndoManager.Push(UndoRecord.ForCopy(destPath));
                            OnFolderOperationCompleted(FolderOperationEventArgs.CreateSuccess(
                                FolderOperation.Copy, sourceFolder.FolderPath, destPath));
                        });
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error copying {sourceFolder.FolderPath}: {ex.Message}");
                        overallSuccess = false;
                    }

                    processed++;
                }

                Application.Current.Dispatcher.Invoke(() =>
                    progressDialog.UpdateProgress(1.0, "Copy completed"));
            });

            progressDialog.ShowDialog();
            await copyTask;

            UpdateStatus(overallSuccess
                ? $"Copied {folderList.Count} folders."
                : $"Copied {processed} of {folderList.Count} folders with some errors.");

            return overallSuccess;
        }

        /// <summary>Recursive directory copy (runs on a thread-pool thread).</summary>
        private void CopyDirectoryInternal(string source, string destination)
        {
            var dir = new DirectoryInfo(source);
            if (!dir.Exists)
                throw new DirectoryNotFoundException($"Source not found: {source}");

            Directory.CreateDirectory(destination);

            foreach (var file in dir.GetFiles())
                file.CopyTo(Path.Combine(destination, file.Name), overwrite: false);

            foreach (var subDir in dir.GetDirectories())
                CopyDirectoryInternal(subDir.FullName,
                    Path.Combine(destination, subDir.Name));
        }

        #endregion

        // ─────────────────────────────────────────────────────────────────
        #region Command CanExecute predicates
        // ─────────────────────────────────────────────────────────────────

        private bool CanDeleteFolder(FolderInfo folder) =>
            folder != null && Directory.Exists(folder.FolderPath);

        private bool CanCreateNewFolder(FolderInfo parent) =>
            parent != null && Directory.Exists(parent.FolderPath);

        #endregion

        // ─────────────────────────────────────────────────────────────────
        #region Helpers
        // ─────────────────────────────────────────────────────────────────

        private void UpdateStatus(string message) =>
            StatusMessageChanged?.Invoke(this, message);

        private void OnFolderOperationCompleted(FolderOperationEventArgs e) =>
            FolderOperationCompleted?.Invoke(this, e);

        #endregion
    }
}
