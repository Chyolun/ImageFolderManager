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
        private readonly string _destinationPath;
        private string _actualDestinationPath;
        private bool _wasCopied;

        public CopyFolderCommand(string sourcePath, string destinationPath) : base(FolderCommandType.Copy)
        {
            _sourcePath = PathService.NormalizePath(sourcePath);
            _destinationPath = PathService.NormalizePath(destinationPath);
            _wasCopied = false;
        }

        public string SourcePath => _sourcePath;
        public string DestinationPath => _destinationPath;
        public string ActualDestinationPath => _actualDestinationPath;

        public override bool CanUndo => true; // Copy can be undone by deleting the copied folder

        protected override async Task<CommandResult> ValidateAsync(CancellationToken cancellationToken)
        {
            await Task.CompletedTask;

            if (string.IsNullOrWhiteSpace(_sourcePath))
                return CommandResult.CreateFailure("Source path cannot be empty");

            if (string.IsNullOrWhiteSpace(_destinationPath))
                return CommandResult.CreateFailure("Destination path cannot be empty");

            if (!Directory.Exists(_sourcePath))
                return CommandResult.CreateFailure($"Source folder does not exist: {_sourcePath}");

            var destinationParent = Path.GetDirectoryName(_destinationPath);
            if (!Directory.Exists(destinationParent))
                return CommandResult.CreateFailure($"Destination parent directory does not exist: {destinationParent}");

            if (string.Equals(_sourcePath, _destinationPath, StringComparison.OrdinalIgnoreCase))
                return CommandResult.CreateFailure("Source and destination paths are the same");

            // Generate unique destination path if target already exists
            _actualDestinationPath = PathService.GetUniqueDirectoryPath(
                Path.GetDirectoryName(_destinationPath),
                Path.GetFileName(_destinationPath));

            return CommandResult.CreateSuccess("Validation passed");
        }

        protected override async Task<CommandResult> ExecuteInternalAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Run(() =>
                {
                    CopyDirectory(_sourcePath, _actualDestinationPath, true, cancellationToken);
                    _wasCopied = true;
                }, cancellationToken);

                return CommandResult.CreateSuccess($"Copied folder from {_sourcePath} to {_actualDestinationPath}");
            }
            catch (OperationCanceledException)
            {
                // Cleanup partial copy on cancellation
                try
                {
                    if (Directory.Exists(_actualDestinationPath))
                    {
                        Directory.Delete(_actualDestinationPath, true);
                    }
                }
                catch
                {
                    // Ignore cleanup errors
                }
                throw;
            }
            catch (Exception ex)
            {
                return CommandResult.CreateFailure($"Failed to copy folder: {ex.Message}", ex);
            }
        }

        protected override async Task<CommandResult> UndoInternalAsync(CancellationToken cancellationToken)
        {
            if (!_wasCopied)
                return CommandResult.CreateFailure("Copy operation was not executed, cannot undo");

            try
            {
                await Task.Run(() =>
                {
                    if (Directory.Exists(_actualDestinationPath))
                    {
                        Directory.Delete(_actualDestinationPath, true);
                        _wasCopied = false;
                    }
                }, cancellationToken);

                return CommandResult.CreateSuccess($"Undid copy operation, deleted copied folder at {_actualDestinationPath}");
            }
            catch (Exception ex)
            {
                return CommandResult.CreateFailure($"Failed to undo copy operation: {ex.Message}", ex);
            }
        }

        public override string[] GetAffectedPaths()
        {
            return new[] { _sourcePath, _destinationPath };
        }

        /// <summary>
        /// Recursively copy directory contents
        /// </summary>
        private static void CopyDirectory(string sourceDir, string destinationDir, bool recursive, CancellationToken cancellationToken)
        {
            var dir = new DirectoryInfo(sourceDir);

            if (!dir.Exists)
                throw new DirectoryNotFoundException($"Source directory not found: {dir.FullName}");

            DirectoryInfo[] dirs = dir.GetDirectories();
            Directory.CreateDirectory(destinationDir);

            // Copy files
            foreach (FileInfo file in dir.GetFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();

                string targetFilePath = Path.Combine(destinationDir, file.Name);
                file.CopyTo(targetFilePath);
            }

            // Copy subdirectories if recursive
            if (recursive)
            {
                foreach (DirectoryInfo subDir in dirs)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string newDestinationDir = Path.Combine(destinationDir, subDir.Name);
                    CopyDirectory(subDir.FullName, newDestinationDir, true, cancellationToken);
                }
            }
        }
    }
}