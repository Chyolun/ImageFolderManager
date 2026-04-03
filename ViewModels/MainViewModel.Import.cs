using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using ImageFolderManager.Commands;
using ImageFolderManager.Models;
using ImageFolderManager.Services;
using ImageFolderManager.Views;
using Microsoft.WindowsAPICodePack.Dialogs;
using Application = System.Windows.Application;

namespace ImageFolderManager.ViewModels
{
    public partial class MainViewModel
    {
        /// <summary>
        /// Shows a modern dialog for selecting multiple folders for import using Windows API Code Pack
        /// </summary>
        /// <returns>List of selected folder paths, or null if cancelled</returns>
        private Task<List<string>> ShowMultiFolderSelectionDialogAsync()
        {
            return Task.FromResult(ShowMultiFolderSelectionDialog());
        }

        /// <summary>
        /// Shows a modern dialog for selecting multiple folders for import (synchronous version)
        /// Uses Windows API Code Pack's CommonOpenFileDialog for native multi-folder selection
        /// </summary>
        /// <returns>List of selected folder paths, or null if cancelled</returns>
        private List<string> ShowMultiFolderSelectionDialog()
        {
            try
            {
                // Check if Windows API Code Pack is available (Windows Vista and later)
                if (!CommonOpenFileDialog.IsPlatformSupported)
                {
                    // Fallback to the legacy method if API Code Pack is not supported
                    return ShowLegacyMultiFolderSelectionDialog();
                }

                using (var dialog = new CommonOpenFileDialog())
                {
                    // Configure dialog for folder selection
                    dialog.Title = "Select Folders to Import";
                    dialog.IsFolderPicker = true;
                    dialog.Multiselect = true;
                    dialog.AllowNonFileSystemItems = false;
                    dialog.EnsureFileExists = true;
                    dialog.EnsurePathExists = true;
                    dialog.EnsureReadOnly = false;
                    dialog.EnsureValidNames = true;
                    dialog.ShowPlacesList = true;

                    // Set initial directory if available
                    string importStartDir = null;
                    if (!string.IsNullOrEmpty(_lastImportSourceDirectory)
                        && Directory.Exists(_lastImportSourceDirectory))
                    {
                        importStartDir = _lastImportSourceDirectory;
                    }
                    else if (!string.IsNullOrEmpty(AppSettings.Instance.DefaultRootDirectory))
                    {
                        importStartDir = Directory.GetParent(
                            AppSettings.Instance.DefaultRootDirectory)?.FullName;
                    }

                    if (!string.IsNullOrEmpty(importStartDir) && Directory.Exists(importStartDir))
                    {
                        dialog.InitialDirectory = importStartDir;
                    }

                    // Show the dialog
                    CommonFileDialogResult result = dialog.ShowDialog(Application.Current.MainWindow);

                    if (result == CommonFileDialogResult.Ok)
                    {
                        // Get all selected folder paths
                        var selectedFolders = dialog.FileNames.ToList();

                        // Validate selected folders
                        var validFolders = new List<string>();
                        var invalidFolders = new List<string>();

                        foreach (var folderPath in selectedFolders)
                        {
                            if (Directory.Exists(folderPath))
                            {
                                validFolders.Add(folderPath);
                            }
                            else
                            {
                                invalidFolders.Add(folderPath);
                            }
                        }

                        // Show warning for any invalid selections
                        if (invalidFolders.Count > 0)
                        {
                            _dialogService.Show(
                                $"The following selected items are not valid folders and will be ignored:\n\n" +
                                string.Join("\n", invalidFolders.Select(f => $"- {Path.GetFileName(f)}")),
                                "Invalid Selections",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);
                        }

                        if (validFolders.Count > 0)
                        {
                            // Show success message with selection summary
                            var folderNames = validFolders.Select(Path.GetFileName).ToList();
                            var summaryMessage = validFolders.Count == 1
                                ? $"Selected folder: {folderNames[0]}"
                                : $"Selected {validFolders.Count} folders:\n- " + string.Join("\n- ", folderNames.Take(5));

                            if (validFolders.Count > 5)
                            {
                                summaryMessage += $"\n... and {validFolders.Count - 5} more folders";
                            }

                            StatusMessage = $"Selected {validFolders.Count} folder(s) for import.";

                            _lastImportSourceDirectory =
                               Directory.GetParent(validFolders[0])?.FullName
                              ?? _lastImportSourceDirectory;

                            return validFolders;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Log the error and fallback to legacy dialog
                System.Diagnostics.Debug.WriteLine($"Error using CommonOpenFileDialog: {ex.Message}");
                _dialogService.Show(
                    "Unable to use modern folder selection dialog. Falling back to legacy method.",
                    "Dialog Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                return ShowLegacyMultiFolderSelectionDialog();
            }

            return null; // User cancelled or no valid folders selected
        }

        /// <summary>
        /// Legacy fallback method for multi-folder selection using Windows Forms FolderBrowserDialog
        /// This method is kept as a fallback for older systems or when Windows API Code Pack is not available
        /// </summary>
        /// <returns>List of selected folder paths, or null if cancelled</returns>
        private List<string> ShowLegacyMultiFolderSelectionDialog()
        {
            var selectedFolders = new List<string>();

            // Use Windows Forms FolderBrowserDialog - must run on STA thread (UI thread)
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Select a folder to import (you can run this multiple times for multiple folders)";
                dialog.ShowNewFolderButton = false;

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    selectedFolders.Add(dialog.SelectedPath);

                    // Ask if user wants to select more folders
                    var result = _dialogService.Show(
                        $"Selected: {Path.GetFileName(dialog.SelectedPath)}\n\nDo you want to select additional folders for batch import?",
                        "Select More Folders?",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        // Allow selection of additional folders
                        while (true)
                        {
                            using (var additionalDialog = new System.Windows.Forms.FolderBrowserDialog())
                            {
                                additionalDialog.Description = $"Select additional folder to import ({selectedFolders.Count} already selected)";
                                additionalDialog.ShowNewFolderButton = false;

                                if (additionalDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                                {
                                    if (!selectedFolders.Contains(additionalDialog.SelectedPath, StringComparer.OrdinalIgnoreCase))
                                    {
                                        selectedFolders.Add(additionalDialog.SelectedPath);

                                        var continueResult = _dialogService.Show(
                                            $"Selected {selectedFolders.Count} folders total.\n\nSelect another folder?",
                                            "Select More Folders?",
                                            MessageBoxButton.YesNo,
                                            MessageBoxImage.Question);

                                        if (continueResult == MessageBoxResult.No)
                                            break;
                                    }
                                    else
                                    {
                                        _dialogService.Show("This folder has already been selected.",
                                            "Duplicate Selection", MessageBoxButton.OK, MessageBoxImage.Information);
                                    }
                                }
                                else
                                {
                                    break; // User cancelled additional selection
                                }
                            }
                        }
                    }
                }
            }

            return selectedFolders.Count > 0 ? selectedFolders : null;
        }

        /// <summary>
        /// Handles the import folder operation by showing the import dialog and processing the import
        /// </summary>
        public async Task ImportFolderAsync()
        {
            await ExecuteSerializedImportAsync(async operationToken =>
            {
                await ImportFolderCoreAsync(operationToken);
            });
        }

        private async Task ImportFolderCoreAsync(CancellationToken operationToken)
        {
            try
            {
                // ©¤©¤ Guard: root directory must be set ©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤
                if (string.IsNullOrEmpty(AppSettings.Instance.DefaultRootDirectory) ||
                    !Directory.Exists(AppSettings.Instance.DefaultRootDirectory))
                {
                    StatusMessage = "Please set a valid root directory first.";
                    _dialogService.Show(
                        "Please set a valid root directory in Settings before importing folders.",
                        "No Root Directory", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // ©¤©¤ Select source folders ©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤
                var sourceFolders = await ShowMultiFolderSelectionDialogAsync();

                if (sourceFolders == null || sourceFolders.Count == 0)
                {
                    StatusMessage = "Import cancelled - no folders selected.";
                    return;
                }

                // ©¤©¤ Show import configuration dialog ©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤
                var importDialog = new ImportFolderDialog(
                    sourceFolders,
                    AppSettings.Instance.DefaultRootDirectory,   // rootDirectoryPath
                    GetAllLoadedFoldersSnapshot(),                // allLoadedFolders
                    _tagService);                                 // folderTagService
                importDialog.Owner = Application.Current.MainWindow;

                if (importDialog.ShowDialog() != true)
                {
                    StatusMessage = "Import cancelled.";
                    return;
                }

                string destinationPath = importDialog.DestinationPath;

                if (string.IsNullOrEmpty(destinationPath))
                {
                    StatusMessage = "Import cancelled - no destination selected.";
                    return;
                }

                // ©¤©¤ Pre-check: resolve folder-level conflicts (UI thread) ©¤©¤
                //
                // For every source folder whose name already exists at the
                // destination we ask: Merge / Skip / Cancel All.
                // Each conflicting folder is asked independently so the user
                // can apply different decisions to different folders.
                //
                // Key   = source folder path
                // Value = true  ¡ú merge into existing destination folder
                //         false ¡ú plain copy (destination does not exist yet,
                //                 or user chose Skip ¡ª handled separately)
                var mergeDecisions = new Dictionary<string, bool>(
                    StringComparer.OrdinalIgnoreCase);

                // Tracks which source folders were skipped by the user.
                var skippedFolders = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

                bool cancelAll = false;
                int total = sourceFolders.Count;

                foreach (var sourceFolderPath in sourceFolders)
                {
                    operationToken.ThrowIfCancellationRequested();
                    string folderName = Path.GetFileName(sourceFolderPath);

                    string finalDest = (total == 1)
                        ? destinationPath
                        : Path.Combine(destinationPath, folderName);

                    if (Directory.Exists(finalDest))
                    {
                        var mergeDlg = new ImportMergeFolderDialog(folderName, finalDest)
                        {
                            Owner = Application.Current.MainWindow
                        };

                        bool? result = mergeDlg.ShowDialog();

                        if (result != true ||
                            mergeDlg.Resolution == ImportFolderMergeResolution.CancelAll)
                        {
                            cancelAll = true;
                            break;
                        }

                        if (mergeDlg.Resolution == ImportFolderMergeResolution.Skip)
                        {
                            skippedFolders.Add(sourceFolderPath);
                        }
                        else // Merge
                        {
                            mergeDecisions[sourceFolderPath] = true;
                        }
                    }
                    else
                    {
                        mergeDecisions[sourceFolderPath] = false; // plain copy
                    }
                }

                if (cancelAll)
                {
                    StatusMessage = "Import cancelled.";
                    return;
                }

                // Filter out skipped folders
                var foldersToProcess = sourceFolders
                    .Where(p => !skippedFolders.Contains(p))
                    .ToList();

                if (foldersToProcess.Count == 0)
                {
                    StatusMessage = "Import skipped ¡ª all folders were skipped.";
                    return;
                }

                // ©¤©¤ Copy / merge phase (background thread + progress dialog) ©¤
                StatusMessage = $"Importing {foldersToProcess.Count} folder(s)...";

                var progressDialog = new ProgressDialog(
                    "Importing Folders",
                    $"Importing {foldersToProcess.Count} folder(s)...");
                progressDialog.Owner = Application.Current.MainWindow;

                int processCount = foldersToProcess.Count;
                var importResults = new List<FolderImportResult>();
                using var importCts = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
                progressDialog.CancelRequested += (_, __) => importCts.Cancel();

                var importTask = Task.Run(async () =>
                {
                    int processed = 0;
                    bool importCancelledByUser = false;

                    foreach (var sourceFolderPath in foldersToProcess)
                    {
                        if (importCancelledByUser || importCts.Token.IsCancellationRequested)
                        {
                            importCancelledByUser = true;
                            break;
                        }

                        var result = new FolderImportResult
                        {
                            SourcePath = sourceFolderPath,
                            FolderName = Path.GetFileName(sourceFolderPath)
                        };

                        try
                        {
                            var progressStart = (double)processed / processCount;
                            progressDialog.UpdateProgress(
                                progressStart,
                                $"Starting import: {result.FolderName}");

                            // Determine final destination path
                            string finalDestinationPath = (sourceFolders.Count == 1)
                                ? destinationPath
                                : Path.Combine(destinationPath, result.FolderName);

                            double baseProgress = (double)processed / processCount;
                            double progressWeight = 1.0 / processCount;

                            bool shouldMerge = mergeDecisions.TryGetValue(
                                sourceFolderPath, out bool mergeFlag) && mergeFlag;

                            if (shouldMerge)
                            {
                                // ©¤©¤ Merge path ©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤
                                // Per-folder "apply to all" accumulator and
                                // cancellation flag.
                                ImportFileConflictResolution? fileApplyAll = null;
                                bool cancelImport = false;

                                int processedFiles = 0;
                                var allFiles = Directory.GetFiles(
                                    sourceFolderPath, "*",
                                    SearchOption.AllDirectories);
                                int totalFiles = allFiles.Length;

                                MergeDirectoryWithConflictHandling(
                                    sourceFolderPath,
                                    finalDestinationPath,
                                    progressDialog,
                                    baseProgress,
                                    progressWeight,
                                    ref processedFiles,
                                    totalFiles,
                                    ref fileApplyAll,
                                    ref cancelImport,
                                    importCts.Token);

                                if (cancelImport)
                                {
                                    importCancelledByUser = true;
                                    result.Success = false;
                                    result.Message = "Import cancelled by user.";
                                }
                                else
                                {
                                    result.DestinationPath = finalDestinationPath;
                                    result.Success = true;
                                    result.Message = "Merged successfully";

                                    progressDialog.UpdateProgress(
                                        (double)(processed + 1) / processCount,
                                        $"Completed: {result.FolderName}");

                                    // Remove source after successful merge
                                    try
                                    {
                                       // Directory.Delete(sourceFolderPath, recursive: true);
                                        DeleteDirectoryRobust(sourceFolderPath);
                                    }
                                    catch (Exception deleteEx)
                                    {
                                        System.Diagnostics.Debug.WriteLine(
                                            $"[Import] Failed to delete source '{sourceFolderPath}': {deleteEx.Message}");
                                        result.Message =
                                            "Merged successfully (source folder could not be removed automatically).";
                                    }
                                }
                            }
                            else
                            {
                                // ©¤©¤ Plain copy path ©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤
                                var copyResult = await CopyFolderForImportAsync(
                                    sourceFolderPath,
                                    finalDestinationPath,
                                    progressDialog,
                                    baseProgress,
                                    progressWeight,
                                    importCts.Token);

                                result.DestinationPath = copyResult.Data as string ?? finalDestinationPath;

                                if (copyResult.Success && Directory.Exists(result.DestinationPath))
                                {
                                    result.Success = true;
                                    result.Message = "Import completed successfully";

                                    progressDialog.UpdateProgress(
                                        (double)(processed + 1) / processCount,
                                        $"Completed: {result.FolderName}");

                                    try
                                    {
                                       // Directory.Delete(sourceFolderPath, recursive: true);
                                        DeleteDirectoryRobust(sourceFolderPath);
                                    }
                                    catch (Exception deleteEx)
                                    {
                                        System.Diagnostics.Debug.WriteLine(
                                            $"[Import] Failed to delete source '{sourceFolderPath}': {deleteEx.Message}");
                                        result.Message =
                                            "Imported successfully (source folder could not be removed automatically).";
                                    }
                                }
                                else
                                {
                                    result.Success = false;
                                    result.Message = string.IsNullOrWhiteSpace(copyResult.Message)
                                        ? "Import failed - destination folder not created"
                                        : copyResult.Message;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            result.Success = false;
                            result.Message = $"Import failed: {ex.Message}";
                        }

                        importResults.Add(result);
                        processed++;
                    }

                    return importResults;
                }, importCts.Token);

                // Show progress dialog (blocks UI; background thread can still
                // invoke dialogs via Dispatcher since WPF runs a nested message
                // pump inside ShowDialog).
                progressDialog.ShowDialog();

                if (progressDialog.IsCancelled && !importCts.IsCancellationRequested)
                {
                    importCts.Cancel();
                }

                List<FolderImportResult> results;
                try
                {
                    results = await importTask;
                }
                catch (OperationCanceledException)
                {
                    StatusMessage = "Import cancelled.";
                    return;
                }

                if (importCts.IsCancellationRequested)
                {
                    StatusMessage = "Import cancelled.";
                    return;
                }

                if (progressDialog.IsVisible)
                {
                    progressDialog.UpdateProgress(1.0, "Import operation completed");
                    await Task.Delay(500);
                    progressDialog.Close();
                }

                await ProcessImportResultsAsync(results);
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Import cancelled.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Import failed: {ex.Message}";
                throw;
            }
        }

        /// <summary>
        /// Merges <paramref name="sourcePath"/> into an already-existing
        /// <paramref name="destinationPath"/>, asking the user what to do
        /// whenever a file conflict is detected.
        ///
        /// <para>
        /// <paramref name="fileApplyAll"/> is a per-folder "apply to all"
        /// accumulator.  Pass it by reference so the same decision persists
        /// across recursive calls for subdirectories of the same root merge.
        /// </para>
        /// </summary>
        /// <param name="cancelImport">Set to true when the user clicks Cancel Import.</param>
        private void MergeDirectoryWithConflictHandling(
            string sourcePath,
            string destinationPath,
            ProgressDialog progressDialog,
            double baseProgress,
            double progressWeight,
            ref int processedFiles,
            int totalFiles,
            ref ImportFileConflictResolution? fileApplyAll,
            ref bool cancelImport,
            CancellationToken operationToken)
        {
            operationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(destinationPath);

            // ©¤©¤ Files ©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤
            foreach (var file in Directory.GetFiles(sourcePath))
            {
                operationToken.ThrowIfCancellationRequested();

                if (cancelImport)
                {
                    return;
                }

                string fileName = Path.GetFileName(file);
                string destFile = Path.Combine(destinationPath, fileName);

                if (File.Exists(destFile))
                {
                    ImportFileConflictResolution resolution;

                    if (fileApplyAll.HasValue)
                    {
                        resolution = fileApplyAll.Value;
                    }
                    else
                    {
                        // Must show dialog on UI thread while progress dialog is open.
                        // WPF's nested message loop (ShowDialog) allows Dispatcher.Invoke
                        // to be processed without a deadlock.
                        ImportFileConflictResolution chosen = ImportFileConflictResolution.Skip;
                        bool applyToAll = false;
                        bool userCancelled = false;

                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            var dlg = new ImportFileConflictDialog(fileName, destinationPath)
                            {
                                Owner = Application.Current.MainWindow
                            };

                            bool? result = dlg.ShowDialog();

                            if (result != true || dlg.Resolution == ImportFileConflictResolution.CancelImport)
                            {
                                userCancelled = true;
                            }
                            else
                            {
                                chosen = dlg.Resolution;
                                applyToAll = dlg.ApplyToAll;
                            }
                        });

                        if (userCancelled)
                        {
                            cancelImport = true;
                            return;
                        }

                        resolution = chosen;
                        if (applyToAll)
                            fileApplyAll = resolution;
                    }

                    if (resolution == ImportFileConflictResolution.Overwrite)
                        File.Copy(file, destFile, overwrite: true);
                    // Skip: do nothing
                }
                else
                {
                    File.Copy(file, destFile, overwrite: false);
                }

                processedFiles++;
                if (progressDialog != null && totalFiles > 0)
                {
                    double currentProgress = baseProgress +
                        (progressWeight * processedFiles / totalFiles);
                    progressDialog.UpdateProgress(
                        Math.Min(currentProgress, baseProgress + progressWeight),
                        $"Merging: {fileName}");
                }
            }

            // ©¤©¤ Subdirectories (recurse) ©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤©¤
            foreach (var subDir in Directory.GetDirectories(sourcePath))
            {
                operationToken.ThrowIfCancellationRequested();

                if (cancelImport)
                {
                    return;
                }

                string dirName = Path.GetFileName(subDir);
                string destSubDir = Path.Combine(destinationPath, dirName);

                if (progressDialog != null && totalFiles > 0)
                {
                    double currentProgress = baseProgress +
                        (progressWeight * processedFiles / totalFiles);
                    progressDialog.UpdateProgress(
                        Math.Min(currentProgress, baseProgress + progressWeight),
                        $"Merging folder: {dirName}");
                }

                MergeDirectoryWithConflictHandling(
                    subDir, destSubDir,
                    progressDialog, baseProgress, progressWeight,
                    ref processedFiles, totalFiles,
                    ref fileApplyAll, ref cancelImport,
                    operationToken);
            }
        }

        /// <summary>
        /// Import-specific wrapper that routes plain-copy operations through the
        /// unified command/orchestrator pipeline while keeping progress feedback.
        /// </summary>
        private async Task<CommandResult> CopyFolderForImportAsync(
            string sourcePath,
            string destinationPath,
            ProgressDialog progressDialog,
            double baseProgress,
            double progressWeight,
            CancellationToken operationToken)
        {
            operationToken.ThrowIfCancellationRequested();

            progressDialog?.UpdateProgress(
                baseProgress + (progressWeight * 0.1),
                $"Copying: {Path.GetFileName(sourcePath)}");

            var result = await _operationOrchestrator.CopyFolderAsync(
                sourcePath,
                destinationPath,
                operationToken);

            if (result.Success)
            {
                progressDialog?.UpdateProgress(
                    baseProgress + progressWeight,
                    $"Completed copying {Path.GetFileName(sourcePath)}");
            }

            return result;
        }

        /// <summary>
        /// Deletes a directory and all its contents, forcibly clearing ReadOnly / Hidden
        /// attributes first so that Directory.Delete does not throw UnauthorizedAccessException
        /// on metadata files (e.g. .folderTags) or other protected files.
        /// </summary>
        private static void DeleteDirectoryRobust(string path)
        {
            if (!Directory.Exists(path))
                return;

            // Strip ReadOnly / Hidden from every file so Delete cannot be blocked
            foreach (string file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    FileAttributes attrs = File.GetAttributes(file);
                    if ((attrs & (FileAttributes.ReadOnly | FileAttributes.Hidden)) != 0)
                        File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly & ~FileAttributes.Hidden);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[Import] Could not clear attributes on '{file}': {ex.Message}");
                }
            }

            Directory.Delete(path, recursive: true);
        }

        /// <summary>
        /// Processes import results and updates the application state
        /// </summary>
        /// <param name="results">List of import results</param>
        private async Task ProcessImportResultsAsync(List<FolderImportResult> results)
        {
            var successCount = results.Count(r => r.Success);
            var failureCount = results.Count(r => !r.Success);

            // Update status message
            if (failureCount == 0)
            {
                StatusMessage = $"Successfully imported {successCount} folder(s).";
            }
            else
            {
                StatusMessage = $"Import completed: {successCount} successful, {failureCount} failed.";
            }

            // Show detailed results if there were any failures
            if (failureCount > 0)
            {
                var failedFolders = results.Where(r => !r.Success).ToList();
                var errorMessage = "The following folders failed to import:\n\n";
                errorMessage += string.Join("\n", failedFolders.Select(f => $"- {f.FolderName}: {f.Message}"));

                _dialogService.Show(errorMessage, "Import Errors", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            // Refresh the folder tree to show imported folders
            await RefreshAfterImportAsync(results.Where(r => r.Success).Select(r => r.DestinationPath).ToList());
        }

        /// <summary>
        /// Refreshes the application after successful import
        /// </summary>
        /// <param name="importedPaths">List of successfully imported folder paths</param>
        private async Task RefreshAfterImportAsync(List<string> importedPaths)
        {
            try
            {
                // Refresh all folders data to include the newly imported folders
                await RefreshAllFoldersDataAsync();

                // If we have a shell tree view, refresh it
                if (_shellTreeView != null)
                {
                    await _shellTreeView.RefreshTreeFull();
                }

                // Select the first imported folder if available
                FolderInfo importedFolder = null;
                if (importedPaths.Count > 0)
                {
                    var loadedFolders = GetAllLoadedFoldersSnapshot();
                    if (loadedFolders.Count > 0)
                    {
                        importedFolder = loadedFolders.FirstOrDefault(f =>
                            importedPaths.Any(path => PathService.PathsEqual(f.FolderPath, path)));
                    }
                }

                if (importedFolder != null)
                {
                    await SetSelectedFolderAsync(importedFolder);

                    // Select in tree view if available
                    if (_shellTreeView != null)
                    {
                        _shellTreeView.SelectPath(importedFolder.FolderPath);
                    }
                }

                StatusMessage += " Folder tree refreshed.";
            }
            catch (Exception ex)
            {
                StatusMessage += $" Warning: Failed to refresh folder tree - {ex.Message}";
            }
        }
    
        private async Task ExecuteSerializedImportAsync(Func<CancellationToken, Task> operation)
        {
            await _importOperationSemaphore.WaitAsync();
            CancellationTokenSource operationCts;

            lock (_importCtsLock)
            {
                _currentImportCts?.Cancel();
                _currentImportCts?.Dispose();
                _currentImportCts = new CancellationTokenSource();
                operationCts = _currentImportCts;
            }

            try
            {
                await operation(operationCts.Token);
            }
            finally
            {
                lock (_importCtsLock)
                {
                    if (ReferenceEquals(_currentImportCts, operationCts))
                    {
                        _currentImportCts = null;
                    }
                }

                operationCts.Dispose();
                _importOperationSemaphore.Release();
            }
        }
}
}




