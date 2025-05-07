using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ImageFolderManager.Models;
using ImageFolderManager.Views;
using Microsoft.VisualBasic.FileIO;

namespace ImageFolderManager.Services
{
    /// <summary>
    /// Provides centralized file system operations with consistent error handling and progress reporting
    /// </summary>
    public class FileOperations
    {
        private readonly FolderManagementService _folderManager;
        private readonly FolderTagService _tagService;

        public FileOperations(FolderManagementService folderManager)
        {
            _folderManager = folderManager ?? throw new ArgumentNullException(nameof(folderManager));
            _tagService = new FolderTagService();
        }

        /// <summary>
        /// Creates a new folder within the specified parent folder
        /// </summary>
        public async Task<FolderInfo> CreateFolderAsync(FolderInfo parentFolder, string folderName)
        {
            if (parentFolder == null)
                throw new ArgumentNullException(nameof(parentFolder));

            if (string.IsNullOrWhiteSpace(folderName))
                throw new ArgumentException("Folder name cannot be empty.", nameof(folderName));

            // Check for invalid characters
            if (folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("The folder name contains invalid characters.", nameof(folderName));

            // Normalize parent path
            string parentPath = PathService.NormalizePath(parentFolder.FolderPath);

            // Verify parent directory exists
            if (!PathService.DirectoryExists(parentPath))
                throw new DirectoryNotFoundException($"Parent directory does not exist: {parentPath}");

            // Get a unique path for the new folder to avoid name collisions
            string newPath = PathService.GetUniqueDirectoryPath(parentPath, folderName);
            if (string.IsNullOrEmpty(newPath))
                throw new IOException($"Failed to generate a unique path for the new folder: {folderName}");

            try
            {
                // Create the directory
                Directory.CreateDirectory(newPath);

                // Refresh the parent node to show the new folder
                parentFolder.LoadChildren();

                // Start watching the parent folder to detect changes
                _folderManager.WatchFolder(parentFolder);

                // Create a FolderInfo for the new folder
                var newFolder = new FolderInfo(newPath, parentFolder);

                // Watch the new folder
                _folderManager.WatchFolder(newFolder);

                // Invalidate path cache for the new folder
                PathService.InvalidatePathCache(parentPath, false);

                return newFolder;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error creating folder: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Deletes a folder or multiple folders to the recycle bin
        /// </summary>
        public async Task DeleteFoldersAsync(List<FolderInfo> folders, ProgressDialog progressDialog = null, CancellationToken cancellationToken = default)
        {
            if (folders == null || folders.Count == 0)
                throw new ArgumentException("No folders specified for deletion.", nameof(folders));

            try
            {
                int total = folders.Count;
                int processed = 0;
                bool isOwningProgressDialog = false;

                // Create progress dialog if not provided
                if (progressDialog == null && total > 1)
                {
                    progressDialog = new ProgressDialog(
                        "Deleting Folders",
                        $"Deleting {total} folders...");

                    progressDialog.Owner = Application.Current.MainWindow;
                    isOwningProgressDialog = true;
                }

                // Create cancellation token source if needed
                using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    progressDialog?.GetCancellationToken() ?? CancellationToken.None);

                var token = linkedTokenSource.Token;

                foreach (var folder in folders)
                {
                    // Check for cancellation
                    token.ThrowIfCancellationRequested();

                    // Normalize folder path
                    string folderPath = PathService.NormalizePath(folder.FolderPath);

                    // Skip root directory
                    string rootDir = PathService.NormalizePath(AppSettings.Instance.DefaultRootDirectory);
                    if (!string.IsNullOrEmpty(rootDir) && PathService.PathsEqual(folderPath, rootDir))
                    {
                        processed++;
                        progressDialog?.UpdateProgress((double)processed / total, $"Skipping root directory");
                        continue;
                    }

                    try
                    {
                        // Update progress
                        if (progressDialog != null)
                        {
                            double progress = (double)processed / total;
                            progressDialog.UpdateProgress(progress, $"Deleting {processed + 1} of {total}: {folder.Name}");
                        }

                        // Stop watching this folder before deletion
                        _folderManager.UnwatchFolder(folderPath);

                        // Make sure the folder exists before deletion
                        if (PathService.DirectoryExists(folderPath))
                        {
                            // Delete to recycle bin
                            FileSystem.DeleteDirectory(
                                folderPath,
                                UIOption.OnlyErrorDialogs,
                                RecycleOption.SendToRecycleBin);

                            // Invalidate path cache
                            PathService.InvalidatePathCache(folderPath, true);
                        }

                        // Brief delay to prevent UI freezing
                        await Task.Delay(10, token);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error deleting folder {folderPath}: {ex.Message}");
                        // Continue with other folders
                    }

                    processed++;
                }

                // Update final progress
                if (progressDialog != null)
                {
                    progressDialog.UpdateProgress(1.0, "Delete completed");

                    // Only close the dialog if we created it
                    if (isOwningProgressDialog)
                    {
                        await Task.Delay(500, CancellationToken.None); // Brief delay to show completion
                        progressDialog.CloseDialog();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("Delete operation was canceled");
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in delete operation: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Renames a folder to a new name
        /// </summary>
        public async Task<string> RenameFolderAsync(FolderInfo folder, string newName)
        {
            if (folder == null)
                throw new ArgumentNullException(nameof(folder));

            if (string.IsNullOrWhiteSpace(newName))
                throw new ArgumentException("New folder name cannot be empty.", nameof(newName));

            string oldPath = PathService.NormalizePath(folder.FolderPath);
            string oldName = Path.GetFileName(oldPath);

            // Don't do anything if the name is the same
            if (newName == oldName)
                return oldPath;

            string parentPath = Path.GetDirectoryName(oldPath);

            // Check for invalid characters
            if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("The folder name contains invalid characters.", nameof(newName));

            // Construct new path
            string newPath = Path.Combine(parentPath, newName);

            // Check if destination already exists
            if (PathService.DirectoryExists(newPath))
                throw new IOException($"A folder named '{newName}' already exists in this location.");

            try
            {
                // Stop watching the folder before renaming
                _folderManager.UnwatchFolder(oldPath);

                // Rename the directory
                Directory.Move(oldPath, newPath);

                // Update the folder path in the data model
                folder.FolderPath = newPath;

                // Start watching the renamed folder
                _folderManager.WatchFolder(folder);

                // Invalidate path cache
                PathService.InvalidatePathCache(oldPath, true);
                PathService.InvalidatePathCache(parentPath, false);

                // Move folder tags to the new location
                await _tagService.MoveFolderTagsAsync(oldPath, newPath);

                return newPath;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error renaming folder: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Moves folders to a target folder with progress reporting
        /// </summary>
        public async Task MoveFoldersAsync(List<FolderInfo> sourceFolders, FolderInfo targetFolder, ProgressDialog progressDialog = null, CancellationToken cancellationToken = default)
        {
            if (sourceFolders == null || sourceFolders.Count == 0)
                throw new ArgumentException("No source folders specified.", nameof(sourceFolders));

            if (targetFolder == null)
                throw new ArgumentNullException(nameof(targetFolder));

            try
            {
                string targetPath = PathService.NormalizePath(targetFolder.FolderPath);

                // Verify target exists
                if (!PathService.DirectoryExists(targetPath))
                    throw new DirectoryNotFoundException($"Target directory not found: {targetPath}");

                int total = sourceFolders.Count;
                int processed = 0;
                bool isOwningProgressDialog = false;

                // Create progress dialog if not provided
                if (progressDialog == null && total > 1)
                {
                    progressDialog = new ProgressDialog(
                        "Moving Folders",
                        $"Moving {total} folders...");

                    progressDialog.Owner = Application.Current.MainWindow;
                    isOwningProgressDialog = true;
                }

                // Create cancellation token source if needed
                using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    progressDialog?.GetCancellationToken() ?? CancellationToken.None);

                var token = linkedTokenSource.Token;

                foreach (var sourceFolder in sourceFolders)
                {
                    // Check for cancellation
                    token.ThrowIfCancellationRequested();

                    // Get normalized source path
                    string sourcePath = PathService.NormalizePath(sourceFolder.FolderPath);

                    // Skip if trying to move to itself or child folder
                    if (PathService.PathsEqual(sourcePath, targetPath) ||
                        PathService.IsPathWithin(sourcePath, targetPath))
                    {
                        processed++;
                        continue;
                    }

                    try
                    {
                        // Update progress
                        if (progressDialog != null)
                        {
                            double progress = (double)processed / total;
                            progressDialog.UpdateProgress(progress, $"Moving {processed + 1} of {total}: {sourceFolder.Name}");
                        }

                        // Build destination path
                        string folderName = Path.GetFileName(sourcePath);
                        string destinationPath = PathService.GetUniqueDirectoryPath(targetPath, folderName);

                        if (string.IsNullOrEmpty(destinationPath))
                        {
                            Debug.WriteLine($"Failed to generate a unique destination path for {sourcePath}");
                            processed++;
                            continue;
                        }

                        // Temporarily disable FileSystemWatcher
                        _folderManager.UnwatchFolder(sourcePath);
                        _folderManager.UnwatchFolder(targetPath);

                        // Move directory
                        Directory.Move(sourcePath, destinationPath);

                        // Move folder tags to the new location
                        await _tagService.MoveFolderTagsAsync(sourcePath, destinationPath);

                        // Invalidate path caches
                        PathService.InvalidatePathCache(sourcePath, true);
                        PathService.InvalidatePathCache(targetPath, false);

                        // Create FolderInfo for the new location
                        var movedFolder = new FolderInfo(destinationPath, targetFolder);

                        // Re-enable file monitoring
                        _folderManager.WatchFolder(targetFolder);
                        _folderManager.WatchFolder(movedFolder);

                        // Also watch source parent if available
                        if (sourceFolder.Parent != null)
                        {
                            _folderManager.WatchFolder(sourceFolder.Parent);
                        }

                        // Brief delay to prevent UI freezing
                        await Task.Delay(50, token);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error moving folder {sourceFolder.FolderPath}: {ex.Message}");
                        // Continue with other folders
                    }

                    processed++;
                }

                // Update final progress
                if (progressDialog != null)
                {
                    progressDialog.UpdateProgress(1.0, "Move completed");

                    // Only close the dialog if we created it
                    if (isOwningProgressDialog)
                    {
                        await Task.Delay(500, CancellationToken.None); // Brief delay to show completion
                        progressDialog.CloseDialog();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("Move operation was canceled");
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in move operation: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Copies folders to a target folder with progress reporting
        /// </summary>
        public async Task CopyFoldersAsync(List<FolderInfo> sourceFolders, FolderInfo targetFolder, ProgressDialog progressDialog = null, CancellationToken cancellationToken = default)
        {
            if (sourceFolders == null || sourceFolders.Count == 0)
                throw new ArgumentException("No source folders specified.", nameof(sourceFolders));

            if (targetFolder == null)
                throw new ArgumentNullException(nameof(targetFolder));

            try
            {
                string targetPath = PathService.NormalizePath(targetFolder.FolderPath);

                // Verify target exists
                if (!PathService.DirectoryExists(targetPath))
                    throw new DirectoryNotFoundException($"Target directory not found: {targetPath}");

                int total = sourceFolders.Count;
                int processed = 0;
                bool isOwningProgressDialog = false;

                // Create progress dialog if not provided
                if (progressDialog == null && total > 1)
                {
                    progressDialog = new ProgressDialog(
                        "Copying Folders",
                        $"Copying {total} folders...");

                    progressDialog.Owner = Application.Current.MainWindow;
                    isOwningProgressDialog = true;
                }

                // Create cancellation token source if needed
                using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    progressDialog?.GetCancellationToken() ?? CancellationToken.None);

                var token = linkedTokenSource.Token;

                foreach (var sourceFolder in sourceFolders)
                {
                    // Check for cancellation
                    token.ThrowIfCancellationRequested();

                    // Get normalized source path
                    string sourcePath = PathService.NormalizePath(sourceFolder.FolderPath);

                    // Skip if trying to copy to itself
                    if (PathService.PathsEqual(sourcePath, targetPath))
                    {
                        processed++;
                        continue;
                    }

                    try
                    {
                        // Update progress
                        if (progressDialog != null)
                        {
                            double progress = (double)processed / total;
                            progressDialog.UpdateProgress(progress, $"Copying {processed + 1} of {total}: {sourceFolder.Name}");
                        }

                        // Generate a unique destination path
                        string folderName = Path.GetFileName(sourcePath);
                        string destinationPath = PathService.GetUniqueDirectoryPath(targetPath, folderName);

                        if (string.IsNullOrEmpty(destinationPath))
                        {
                            Debug.WriteLine($"Failed to generate a unique destination path for {sourcePath}");
                            processed++;
                            continue;
                        }

                        // Create the destination directory
                        Directory.CreateDirectory(destinationPath);

                        // Copy all files and subdirectories with progress reporting
                        double copyStartProgress = (double)processed / total;
                        double copyEndProgress = (double)(processed + 1) / total;
                        await CopyDirectoryAsync(
                            sourcePath,
                            destinationPath,
                            progressDialog,
                            copyStartProgress,
                            copyEndProgress,
                            token);

                        // Copy folder tags to the new location
                        await _tagService.CopyFolderTagsAsync(sourcePath, destinationPath);

                        // Create FolderInfo for the new copy
                        var newFolder = new FolderInfo(destinationPath, targetFolder);

                        // Watch the new folder
                        _folderManager.WatchFolder(newFolder);

                        // Brief delay to prevent UI freezing
                        await Task.Delay(50, token);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error copying folder {sourceFolder.FolderPath}: {ex.Message}");
                        // Continue with other folders
                    }

                    processed++;
                }

                // Update final progress
                if (progressDialog != null)
                {
                    progressDialog.UpdateProgress(1.0, "Copy completed");

                    // Only close the dialog if we created it
                    if (isOwningProgressDialog)
                    {
                        await Task.Delay(500, CancellationToken.None); // Brief delay to show completion
                        progressDialog.CloseDialog();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("Copy operation was canceled");
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in copy operation: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Recursively copies a directory and its contents
        /// </summary>
        private async Task CopyDirectoryAsync(
            string sourceDir,
            string destinationDir,
            ProgressDialog progressDialog = null,
            double progressStart = 0,
            double progressEnd = 1,
            CancellationToken cancellationToken = default)
        {
            // Normalize paths
            sourceDir = PathService.NormalizePath(sourceDir);
            destinationDir = PathService.NormalizePath(destinationDir);

            // Check if source directory exists
            if (!PathService.DirectoryExists(sourceDir))
                return;

            // Create destination directory if it doesn't exist
            if (!PathService.DirectoryExists(destinationDir))
            {
                Directory.CreateDirectory(destinationDir);
            }

            // Check if cancelled
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // Get directory info
                var directory = new DirectoryInfo(sourceDir);

                // Calculate total items for progress reporting
                int totalItems = 0;
                int processedItems = 0;

                if (progressDialog != null)
                {
                    // Count files and subdirectories
                    totalItems = directory.GetFiles().Length + directory.GetDirectories().Length;
                    if (totalItems == 0) totalItems = 1; // Prevent division by zero
                }

                // Copy all files
                foreach (FileInfo file in directory.GetFiles())
                {
                    // Check if cancelled
                    cancellationToken.ThrowIfCancellationRequested();

                    string targetFilePath = Path.Combine(destinationDir, file.Name);
                    file.CopyTo(targetFilePath, true);

                    // Update progress
                    if (progressDialog != null)
                    {
                        processedItems++;
                        double progress = progressStart + (progressEnd - progressStart) * (processedItems / (double)totalItems);
                        progressDialog.UpdateProgress(progress, $"Copying: {file.Name}");
                    }
                }

                // Process subdirectories recursively
                foreach (DirectoryInfo subDir in directory.GetDirectories())
                {
                    // Check if cancelled
                    cancellationToken.ThrowIfCancellationRequested();

                    string newDestinationDir = Path.Combine(destinationDir, subDir.Name);

                    // Update progress
                    if (progressDialog != null)
                    {
                        processedItems++;
                        double progress = progressStart + (progressEnd - progressStart) * (processedItems / (double)totalItems);
                        progressDialog.UpdateProgress(progress, $"Copying folder: {subDir.Name}");
                    }

                    await CopyDirectoryAsync(
                        subDir.FullName,
                        newDestinationDir,
                        progressDialog,
                        progressStart + (progressEnd - progressStart) * (processedItems / (double)totalItems),
                        progressStart + (progressEnd - progressStart) * ((processedItems + 1) / (double)totalItems),
                        cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error copying directory {sourceDir} to {destinationDir}: {ex.Message}");
                throw;
            }
        }
    }
}