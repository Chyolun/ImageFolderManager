using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ImageFolderManager.Services;

namespace ImageFolderManager.Commands
{
    /// <summary>
    /// Command to rename a folder
    /// </summary>
    public class RenameFolderCommand : BaseFolderCommand
    {
        private readonly string _folderPath;
        private readonly string _oldName;
        private readonly string _newName;
        private string _newPath;
        private readonly string _parentPath;

        public RenameFolderCommand(string folderPath, string newName) : base(FolderCommandType.Rename)
        {
            _folderPath = PathService.NormalizePath(folderPath);
            _oldName = Path.GetFileName(_folderPath);
            _newName = newName;
            _parentPath = Path.GetDirectoryName(_folderPath);
        }

        public string FolderPath => _folderPath;
        public string OldName => _oldName;
        public string NewName => _newName;
        public string NewPath => _newPath;

        protected override async Task<CommandResult> ValidateAsync(CancellationToken cancellationToken)
        {
            await Task.CompletedTask;

            if (string.IsNullOrWhiteSpace(_folderPath))
                return CommandResult.CreateFailure("Folder path cannot be empty");

            if (string.IsNullOrWhiteSpace(_newName))
                return CommandResult.CreateFailure("New name cannot be empty");

            if (!Directory.Exists(_folderPath))
                return CommandResult.CreateFailure($"Folder does not exist: {_folderPath}");

            // Check for invalid characters
            if (_newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return CommandResult.CreateFailure("New name contains invalid characters");

            // Same name check
            if (string.Equals(_oldName, _newName, StringComparison.OrdinalIgnoreCase))
                return CommandResult.CreateFailure("New name is the same as current name");

            // Generate new path and check for conflicts
            _newPath = PathService.GetUniqueDirectoryPath(_parentPath, _newName);

            return CommandResult.CreateSuccess("Validation passed");
        }

        protected override async Task<CommandResult> ExecuteInternalAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Run(() =>
                {
                    Directory.Move(_folderPath, _newPath);
                }, cancellationToken);

                LogCommand($"Renamed folder from {_oldName} to {Path.GetFileName(_newPath)}");
                return CommandResult.CreateSuccess(
                    $"Folder renamed: {_oldName} → {Path.GetFileName(_newPath)}",
                    _newPath);
            }
            catch (UnauthorizedAccessException ex)
            {
                return CommandResult.CreateFailure($"Access denied renaming folder: {ex.Message}", ex);
            }
            catch (DirectoryNotFoundException ex)
            {
                return CommandResult.CreateFailure($"Folder not found: {ex.Message}", ex);
            }
            catch (IOException ex)
            {
                return CommandResult.CreateFailure($"IO error renaming folder: {ex.Message}", ex);
            }
        }

        protected override async Task<CommandResult> UndoInternalAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (Directory.Exists(_newPath))
                {
                    await Task.Run(() =>
                    {
                        Directory.Move(_newPath, _folderPath);
                    }, cancellationToken);

                    LogCommand($"Undid folder rename: {Path.GetFileName(_newPath)} back to {_oldName}");
                    return CommandResult.CreateSuccess($"Rename operation undone: {_oldName}");
                }
                else
                {
                    return CommandResult.CreateFailure("Renamed folder no longer exists, cannot undo rename");
                }
            }
            catch (Exception ex)
            {
                return CommandResult.CreateFailure($"Failed to undo rename operation: {ex.Message}", ex);
            }
        }

        public override string[] GetAffectedPaths()
        {
            return new[] { _folderPath, _newPath, _parentPath };
        }
    }
}
