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

        public FileOperations(FolderManagementService folderManager)
        {
            _folderManager = folderManager ?? throw new ArgumentNullException(nameof(folderManager));
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

            string newPath = Path.Combine(parentFolder.FolderPath, folderName);

            // Check if destination already exists
            if (Directory.Exists(newPath))
                throw new IOException($"A folder named '{folderName}' already exists in this location.");

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

                    // Skip root directory
                    string rootDir = AppSettings.Instance.DefaultRootDirectory;
                    if (!string.IsNullOrEmpty(rootDir) &&
                        folder.FolderPath.Equals(rootDir, StringComparison.OrdinalIgnoreCase))
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
                        _folderManager.UnwatchFolder(folder.FolderPath);

                        // Delete to recycle bin
                        FileSystem.DeleteDirectory(
                            folder.FolderPath,
                            UIOption.OnlyErrorDialogs,
                            RecycleOption.SendToRecycleBin);

                        // Brief delay to prevent UI freezing
                        await Task.Delay(10, token);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error deleting folder {folder.FolderPath}: {ex.Message}");
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

            string oldPath = folder.FolderPath;
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
            if (Directory.Exists(newPath))
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
                string targetPath = targetFolder.FolderPath;
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

                    // Skip if trying to move to itself or child folder
                    string sourcePath = sourceFolder.FolderPath;
                    if (sourcePath == targetPath ||
                        targetPath.StartsWith(sourcePath + Path.DirectorySeparatorChar))
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
                        string destinationPath = Path.Combine(targetPath, folderName);

                        // Handle name collision
                        if (Directory.Exists(destinationPath))
                        {
                            // Append number to avoid name collision
                            int counter = 1;
                            string newName = folderName;

                            while (Directory.Exists(Path.Combine(targetPath, newName)))
                            {
                                newName = $"{folderName} ({counter})";
                                counter++;
                            }

                            destinationPath = Path.Combine(targetPath, newName);
                        }

                        // Temporarily disable FileSystemWatcher
                        _folderManager.UnwatchFolder(sourcePath);
                        _folderManager.UnwatchFolder(targetPath);

                        // Move directory
                        Directory.Move(sourcePath, destinationPath);

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
                string targetPath = targetFolder.FolderPath;
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

                    // Skip if trying to copy to itself
                    string sourcePath = sourceFolder.FolderPath;
                    if (sourcePath == targetPath)
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

                        // Build destination path
                        string folderName = Path.GetFileName(sourcePath);
                        string destinationPath = Path.Combine(targetPath, folderName);

                        // Handle name collision
                        if (Directory.Exists(destinationPath))
                        {
                            // Append number to avoid name collision
                            int counter = 1;
                            string newName = folderName;

                            while (Directory.Exists(Path.Combine(targetPath, newName)))
                            {
                                newName = $"{folderName} ({counter})";
                                counter++;
                            }

                            destinationPath = Path.Combine(targetPath, newName);
                        }

                        // Create the destination directory
                        Directory.CreateDirectory(destinationPath);

                        // Copy all files and subdirectories with progress reporting
                        double copyStartProgress = (double)processed / total;
                        double copyEndProgress = (double)(processed + 1) / total;
                        CopyDirectory(sourcePath, destinationPath, progressDialog, copyStartProgress, copyEndProgress, token);

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
        private void CopyDirectory(
            string sourceDir,
            string destinationDir,
            ProgressDialog progressDialog = null,
            double progressStart = 0,
            double progressEnd = 1,
            CancellationToken cancellationToken = default)
        {
            // Get all subdirectories
            var directory = new DirectoryInfo(sourceDir);

            // Create destination directory if it doesn't exist
            if (!Directory.Exists(destinationDir))
            {
                Directory.CreateDirectory(destinationDir);
            }

            // Check if cancelled
            cancellationToken.ThrowIfCancellationRequested();

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

                CopyDirectory(
                    subDir.FullName,
                    newDestinationDir,
                    progressDialog,
                    progressStart + (progressEnd - progressStart) * (processedItems / (double)totalItems),
                    progressStart + (progressEnd - progressStart) * ((processedItems + 1) / (double)totalItems),
                    cancellationToken);
            }
        }
    }
}