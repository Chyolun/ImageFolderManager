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
        private readonly IDialogService _dialogService;

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

        #endregion

        #region Events

        public event EventHandler<FolderOperationEventArgs> FolderOperationCompleted;
        public event EventHandler<string>                   StatusMessageChanged;

        #endregion

        // ─────────────────────────────────────────────────────────────────

        public FolderOperationsViewModel(UnifiedFolderService folderService, IDialogService dialogService = null)
        {
            _folderService = folderService
                ?? throw new ArgumentNullException(nameof(folderService));
            _dialogService = dialogService ?? new WpfDialogService();

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
                if (result.OperationType == UndoOperationType.MultiMove
                    && result.MultiPaths.Count > 0)
                {
                    OnFolderOperationCompleted(new FolderOperationEventArgs
                    {
                        Operation = FolderOperation.Refresh,
                        SourcePath = result.MultiPaths[0].RestoredPath,   // representative path for status
                        Success = true,
                        IsUndoOperation = true,
                        Timestamp = DateTime.Now
                    });
                }
                else
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
                var dialog = new Views.CreateFolderDialog(parentFolder.FolderPath)
                {
                    Owner = Application.Current.MainWindow
                };

                if (dialog.ShowDialog() != true)
                    return;

                string folderName = dialog.FolderName;

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
                    _dialogService.Show($"Failed to create folder '{folderName}'.",
                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                _dialogService.Show($"Error creating folder: {ex.Message}",
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
                var confirm = Views.DeleteConfirmDialog.ForSingle(folder.FolderPath);
                confirm.Owner = Application.Current.MainWindow;

                if (confirm.ShowDialog() != true)
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
                    _dialogService.Show($"Failed to delete folder '{folder.Name}'.",
                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                return success;
            }
            catch (Exception ex)
            {
                _dialogService.Show($"Error deleting folder: {ex.Message}",
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

            var confirm = Views.DeleteConfirmDialog.ForMultiple(
                folderList.Select(f => f.FolderPath));
            confirm.Owner = Application.Current.MainWindow;

            if (confirm.ShowDialog() != true)
                return false;

            var progressDialog = new ProgressDialog(
                "Deleting Folders",
                $"Deleting {folderList.Count} folders...");
            progressDialog.Owner = Application.Current.MainWindow;

            bool overallSuccess = true;
            int processed = 0;
            int failedCount = 0;

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

                        if (!success)
                        {
                            overallSuccess = false;
                            Interlocked.Increment(ref failedCount);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error deleting {folder.FolderPath}: {ex.Message}");
                        overallSuccess = false;
                        Interlocked.Increment(ref failedCount);
                    }

                    Interlocked.Increment(ref processed);
                }

                Application.Current.Dispatcher.Invoke(() =>
                    progressDialog.UpdateProgress(1.0, "Delete completed"));
            });

            // Show the progress dialog (blocking the UI) first, and then wait for the task to complete
            progressDialog.ShowDialog();
            await deleteTask;

            // When the task is complete, a refresh event is triggered on the UI thread
            int successCount = folderList.Count - failedCount;
            if (successCount > 0)
            {
                OnFolderOperationCompleted(new FolderOperationEventArgs
                {
                    Operation = FolderOperation.Refresh,
                    Success = true,
                    AffectedItemCount = successCount,
                    Timestamp = DateTime.Now
                });
            }

            UpdateStatus(overallSuccess
                ? $"Deleted {folderList.Count} folders."
                : $"Deleted {successCount} of {folderList.Count} folders, {failedCount} failed.");

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
                var dialog = new Views.RenameFolderDialog(folder.Name)
                {
                    Owner = Application.Current.MainWindow
                };

                if (dialog.ShowDialog() != true)
                    return false;

                string newName = dialog.NewName;

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
                    _dialogService.Show($"Failed to rename folder to '{newName}'.",
                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _dialogService.Show($"Error renaming folder: {ex.Message}",
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
                    _dialogService.Show(
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
                    _dialogService.Show($"Failed to move folder '{sourceFolder.Name}'.",
                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                return success;
            }
            catch (Exception ex)
            {
                _dialogService.Show($"Error moving folder: {ex.Message}",
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
            var movedSources = new List<(string src, string dest)>();

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
                            bool success = await _folderService.MoveFolderAsync(folder.FolderPath, destPath);
                            if (success)
                                movedSources.Add((folder.FolderPath, destPath));  
                            else
                                overallSuccess = false;
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

            if (movedSources.Count > 0)
            {
                UndoManager.Push(UndoRecord.ForMultiMove(
                    movedSources.Select(x => x.src), targetFolder.FolderPath));
            }

            // Build destination path list for all successfully moved folders
            var sourcePaths = movedSources.Select(x => x.src).ToList();
            var destPaths = movedSources.Select(x => x.dest).ToList();

            if (destPaths.Count == 1)
            {
                // Single item — use ordinary event so existing scroll logic handles it
                OnFolderOperationCompleted(FolderOperationEventArgs.CreateSuccess(
                    FolderOperation.Move, sourcePaths[0], destPaths[0]));
            }
            else if (destPaths.Count > 1)
            {
                // Multiple items — fire ONE batch event; TreeView will center-scroll all of them
                OnFolderOperationCompleted(
                    FolderOperationEventArgs.CreateBatchMoveSuccess(sourcePaths, destPaths));
            }

            CommandManager.InvalidateRequerySuggested();
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

            // ── Conflict pre-check (on UI thread before showing progress dialog) ──
            // Build a resolution plan for every folder that would collide.
            // Key = sourceFolder.FolderPath, Value = resolved destination path (or null = skip).
            var resolutionPlan = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // "Apply-to-all" state
            bool hasApplyToAll = false;
            ConflictResolution applyToAllResolution = ConflictResolution.Skip;

            bool cancelledByUser = false;

            foreach (var sourceFolder in folderList)
            {
                string candidateDest = Path.Combine(targetFolder.FolderPath, sourceFolder.Name);

                if (!Directory.Exists(candidateDest))
                {
                    // No conflict – use as-is
                    resolutionPlan[sourceFolder.FolderPath] = candidateDest;
                    continue;
                }

                // Conflict detected ────────────────────────────────────────
                if (hasApplyToAll)
                {
                    // Apply the previously chosen resolution without showing a dialog
                    string resolved = ResolveConflictAutomatically(
                        sourceFolder.Name, targetFolder.FolderPath, applyToAllResolution);
                    resolutionPlan[sourceFolder.FolderPath] = resolved; // null = skip
                    continue;
                }

                // Show conflict dialog on UI thread
                string chosenDest = null;
                bool cancelled = false;

                Application.Current.Dispatcher.Invoke(() =>
                {
                    bool isBatch = folderList.Count > 1;
                    var dlg = new Views.CopyConflictDialog(
                        sourceFolder.Name, targetFolder.FolderPath, isBatch)
                    {
                        Owner = Application.Current.MainWindow
                    };

                    bool? result = dlg.ShowDialog();

                    if (result != true || dlg.Resolution == ConflictResolution.CancelAll)
                    {
                        cancelled = true;
                        return;
                    }

                    if (dlg.ApplyToAll)
                    {
                        hasApplyToAll = true;
                        applyToAllResolution = dlg.Resolution;
                    }

                    chosenDest = ResolveConflictFromDialog(
                        dlg.Resolution, dlg.NewFolderName,
                        sourceFolder.Name, targetFolder.FolderPath);
                });

                if (cancelled)
                {
                    cancelledByUser = true;
                    break;
                }

                resolutionPlan[sourceFolder.FolderPath] = chosenDest; // null = skip
            }

            if (cancelledByUser) return false;

            // ── Copy phase ────────────────────────────────────────────────
            // Filter to folders that are NOT skipped (resolution != null)
            var toProcess = folderList
                .Where(f => resolutionPlan.TryGetValue(f.FolderPath, out var d) && d != null)
                .ToList();

            if (toProcess.Count == 0)
            {
                UpdateStatus("Copy cancelled: all folders skipped.");
                return false;
            }

            var progressDialog = new ProgressDialog(
                "Copying Folders",
                $"Copying {toProcess.Count} folder(s)...");
            progressDialog.Owner = Application.Current.MainWindow;

            bool overallSuccess = true;
            int processed = 0;

            var copyTask = Task.Run(() =>
            {
                foreach (var sourceFolder in toProcess)
                {
                    try
                    {
                        double progress = (double)processed / toProcess.Count;
                        Application.Current.Dispatcher.Invoke(() =>
                            progressDialog.UpdateProgress(progress, $"Copying: {sourceFolder.Name}"));

                        string destPath = resolutionPlan[sourceFolder.FolderPath];

                        // Handle Overwrite: delete existing folder first
                        if (Directory.Exists(destPath))
                        {
                            Directory.Delete(destPath, recursive: true);
                        }

                        CopyDirectoryInternal(sourceFolder.FolderPath, destPath);

                        // ── Record for undo & fire TreeView event ─────────
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            UndoManager.Push(UndoRecord.ForCopy(destPath));

                            // FIX: DestinationPath must be the actual copied folder path,
                            // NOT targetFolder.FolderPath (the parent).
                            OnFolderOperationCompleted(FolderOperationEventArgs.CreateSuccess(
                                FolderOperation.Copy,
                                sourceFolder.FolderPath,   // source (informational)
                                destPath));                 // ← FIXED: real dest path
                        });
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[CopyFoldersAsync] Error copying {sourceFolder.FolderPath}: {ex.Message}");
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
                ? $"Copied {toProcess.Count} folder(s)."
                : $"Copied {processed} of {toProcess.Count} folder(s) with some errors.");

            return overallSuccess;
        }

        /// <summary>
        /// Returns the destination path based on a dialog result, or null for Skip.
        /// </summary>
        private static string ResolveConflictFromDialog(
            ConflictResolution resolution,
            string newNameFromDialog,
            string folderName,
            string parentDir)
        {
            switch (resolution)
            {
                case ConflictResolution.Skip:
                    return null;

                case ConflictResolution.Overwrite:
                    return Path.Combine(parentDir, folderName);   // same path → will be deleted first

                case ConflictResolution.Rename:
                    return Path.Combine(parentDir, newNameFromDialog);

                default:
                    return null;
            }
        }

        /// <summary>
        /// Applies an "Apply-to-all" resolution automatically (no dialog).
        /// For Rename, generates a unique name.
        /// </summary>
        private static string ResolveConflictAutomatically(
            string folderName, string parentDir, ConflictResolution resolution)
        {
            switch (resolution)
            {
                case ConflictResolution.Skip:
                    return null;

                case ConflictResolution.Overwrite:
                    return Path.Combine(parentDir, folderName);

                case ConflictResolution.Rename:
                    int idx = 1;
                    string candidate = $"{folderName} ({idx})";
                    while (Directory.Exists(Path.Combine(parentDir, candidate)))
                        candidate = $"{folderName} ({++idx})";
                    return Path.Combine(parentDir, candidate);

                default:
                    return null;
            }
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
                CopyDirectoryInternal(subDir.FullName, Path.Combine(destination, subDir.Name));
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
