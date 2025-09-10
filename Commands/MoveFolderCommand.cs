using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ImageFolderManager.Services;

namespace ImageFolderManager.Commands
{
    /// <summary>
    /// Command to move a folder from one location to another
    /// </summary>
    public class MoveFolderCommand : BaseFolderCommand
    {
        private readonly string _sourcePath;
        private readonly string _destinationPath;
        private string _actualDestinationPath;
        private bool _wasMoved;

        public MoveFolderCommand(string sourcePath, string destinationPath) : base(FolderCommandType.Move)
        {
            _sourcePath = PathService.NormalizePath(sourcePath);
            _destinationPath = PathService.NormalizePath(destinationPath);
            _wasMoved = false;
        }

        public string SourcePath => _sourcePath;
        public string DestinationPath => _destinationPath;
        public string ActualDestinationPath => _actualDestinationPath;

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

            if (_destinationPath.StartsWith(_sourcePath, StringComparison.OrdinalIgnoreCase))
                return CommandResult.CreateFailure("Cannot move folder into itself");

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
                    Directory.Move(_sourcePath, _actualDestinationPath);
                    _wasMoved = true;
                }, cancellationToken);

                return CommandResult.CreateSuccess($"Moved folder from {_sourcePath} to {_actualDestinationPath}");
            }
            catch (Exception ex)
            {
                return CommandResult.CreateFailure($"Failed to move folder: {ex.Message}", ex);
            }
        }

        protected override async Task<CommandResult> UndoInternalAsync(CancellationToken cancellationToken)
        {
            if (!_wasMoved)
                return CommandResult.CreateFailure("Move operation was not executed, cannot undo");

            try
            {
                await Task.Run(() =>
                {
                    if (Directory.Exists(_actualDestinationPath))
                    {
                        Directory.Move(_actualDestinationPath, _sourcePath);
                        _wasMoved = false;
                    }
                }, cancellationToken);

                return CommandResult.CreateSuccess($"Undid move operation, restored folder to {_sourcePath}");
            }
            catch (Exception ex)
            {
                return CommandResult.CreateFailure($"Failed to undo move operation: {ex.Message}", ex);
            }
        }

        public override string[] GetAffectedPaths()
        {
            return new[] { _sourcePath, _destinationPath };
        }
    }
}
