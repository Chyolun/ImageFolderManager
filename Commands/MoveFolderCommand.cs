using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ImageFolderManager.Services;

namespace ImageFolderManager.Commands
{
    /// <summary>
    /// Command to move a folder to a new location
    /// </summary>
    public class MoveFolderCommand : BaseFolderCommand
    {
        private readonly string _sourcePath;
        private readonly string _targetParentPath;
        private readonly string _newName;
        private string _destinationPath;
        private string _originalParentPath;

        public MoveFolderCommand(string sourcePath, string targetParentPath, string newName = null)
            : base(FolderCommandType.Move)
        {
            _sourcePath = PathService.NormalizePath(sourcePath);
            _targetParentPath = PathService.NormalizePath(targetParentPath);
            _newName = newName ?? Path.GetFileName(_sourcePath);
            _originalParentPath = Path.GetDirectoryName(_sourcePath);
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

            // Check for circular reference (moving folder into itself)
            if (PathService.IsPathWithin(_sourcePath, _targetParentPath))
                return CommandResult.CreateFailure("Cannot move folder into itself or its subfolder");

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
                    Directory.Move(_sourcePath, _destinationPath);
                }, cancellationToken);

                LogCommand($"Moved folder from {_sourcePath} to {_destinationPath}");
                return CommandResult.CreateSuccess(
                    $"Folder moved: {Path.GetFileName(_sourcePath)} → {Path.GetFileName(_destinationPath)}",
                    _destinationPath);
            }
            catch (UnauthorizedAccessException ex)
            {
                return CommandResult.CreateFailure($"Access denied moving folder: {ex.Message}", ex);
            }
            catch (DirectoryNotFoundException ex)
            {
                return CommandResult.CreateFailure($"Directory not found: {ex.Message}", ex);
            }
            catch (IOException ex)
            {
                return CommandResult.CreateFailure($"IO error moving folder: {ex.Message}", ex);
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
                        Directory.Move(_destinationPath, _sourcePath);
                    }, cancellationToken);

                    LogCommand($"Undid folder move: {_destinationPath} back to {_sourcePath}");
                    return CommandResult.CreateSuccess($"Move operation undone: {Path.GetFileName(_sourcePath)}");
                }
                else
                {
                    return CommandResult.CreateFailure("Destination folder no longer exists, cannot undo move");
                }
            }
            catch (Exception ex)
            {
                return CommandResult.CreateFailure($"Failed to undo move operation: {ex.Message}", ex);
            }
        }

        public override string[] GetAffectedPaths()
        {
            return new[] { _sourcePath, _destinationPath, _originalParentPath, _targetParentPath };
        }
    }
}