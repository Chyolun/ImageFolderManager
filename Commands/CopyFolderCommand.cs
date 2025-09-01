using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ImageFolderManager.Services;

namespace ImageFolderManager.Commands
{
    /// <summary>
    /// Command to copy a folder to a new location
    /// </summary>
    public class CopyFolderCommand : BaseFolderCommand
    {
        private readonly string _sourcePath;
        private readonly string _targetParentPath;
        private readonly string _newName;
        private string _destinationPath;

        public CopyFolderCommand(string sourcePath, string targetParentPath, string newName = null)
            : base(FolderCommandType.Copy)
        {
            _sourcePath = PathService.NormalizePath(sourcePath);
            _targetParentPath = PathService.NormalizePath(targetParentPath);
            _newName = newName ?? Path.GetFileName(_sourcePath);
        }

        public string SourcePath => _sourcePath;
        public string TargetParentPath => _targetParentPath;
        public string NewName => _newName;
        public string DestinationPath => _destinationPath;

        protected override async Task<CommandResult> ValidateAsync(CancellationToken cancellationToken)
        {
            await Task.CompletedTask;

            if (string.IsNullOrWhiteSpace(_sourcePath))
                return CommandResult.CreateFailure("Source path cannot be empty");

            if (string.IsNullOrWhiteSpace(_targetParentPath))
                return CommandResult.CreateFailure("Target parent path cannot be empty");

            if (!Directory.Exists(_sourcePath))
                return CommandResult.CreateFailure($"Source folder does not exist: {_sourcePath}");

            if (!Directory.Exists(_targetParentPath))
                return CommandResult.CreateFailure($"Target parent folder does not exist: {_targetParentPath}");

            // Generate destination path and ensure uniqueness
            _destinationPath = PathService.GetUniqueDirectoryPath(_targetParentPath, _newName);

            return CommandResult.CreateSuccess("Validation passed");
        }

        protected override async Task<CommandResult> ExecuteInternalAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Run(() =>
                {
                    CopyDirectory(_sourcePath, _destinationPath, cancellationToken);
                }, cancellationToken);

                LogCommand($"Copied folder from {_sourcePath} to {_destinationPath}");
                return CommandResult.CreateSuccess(
                    $"Folder copied: {Path.GetFileName(_sourcePath)} → {Path.GetFileName(_destinationPath)}",
                    _destinationPath);
            }
            catch (UnauthorizedAccessException ex)
            {
                return CommandResult.CreateFailure($"Access denied copying folder: {ex.Message}", ex);
            }
            catch (DirectoryNotFoundException ex)
            {
                return CommandResult.CreateFailure($"Directory not found: {ex.Message}", ex);
            }
            catch (IOException ex)
            {
                return CommandResult.CreateFailure($"IO error copying folder: {ex.Message}", ex);
            }
        }

        protected override async Task<CommandResult> UndoInternalAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (Directory.Exists(_destinationPath))
                {
                    await Task.Run(() =>
                    {
                        Directory.Delete(_destinationPath, true);
                    }, cancellationToken);

                    LogCommand($"Undid folder copy by deleting: {_destinationPath}");
                    return CommandResult.CreateSuccess($"Copy operation undone");
                }
                else
                {
                    return CommandResult.CreateSuccess("Copied folder no longer exists (already undone)");
                }
            }
            catch (Exception ex)
            {
                return CommandResult.CreateFailure($"Failed to undo copy operation: {ex.Message}", ex);
            }
        }

        public override string[] GetAffectedPaths()
        {
            return new[] { _sourcePath, _destinationPath, _targetParentPath };
        }

        /// <summary>
        /// Recursively copy directory and all its contents
        /// </summary>
        private void CopyDirectory(string sourceDir, string targetDir, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(targetDir);

            // Copy files
            foreach (var filePath in Directory.GetFiles(sourceDir))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string fileName = Path.GetFileName(filePath);
                string destFilePath = Path.Combine(targetDir, fileName);
                File.Copy(filePath, destFilePath, true);
            }

            // Copy subdirectories
            foreach (var dirPath in Directory.GetDirectories(sourceDir))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string dirName = Path.GetFileName(dirPath);
                string destDirPath = Path.Combine(targetDir, dirName);
                CopyDirectory(dirPath, destDirPath, cancellationToken);
            }
        }
    }
}