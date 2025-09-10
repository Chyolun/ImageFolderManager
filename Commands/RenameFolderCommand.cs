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
        private readonly string _newName;
        private readonly string _oldName;
        private string _newPath;
        private bool _wasRenamed;

        public RenameFolderCommand(string folderPath, string newName) : base(FolderCommandType.Rename)
        {
            _folderPath = PathService.NormalizePath(folderPath);
            _newName = newName;
            _oldName = Path.GetFileName(_folderPath);
            _newPath = Path.Combine(Path.GetDirectoryName(_folderPath), _newName);
            _wasRenamed = false;
        }

        public string FolderPath => _folderPath;
        public string NewName => _newName;
        public string OldName => _oldName;
        public string OldPath => _folderPath;
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

            if (string.Equals(_oldName, _newName, StringComparison.OrdinalIgnoreCase))
                return CommandResult.CreateFailure("New name is the same as current name");

            // Check for invalid characters
            var invalidChars = Path.GetInvalidFileNameChars();
            if (_newName.IndexOfAny(invalidChars) >= 0)
                return CommandResult.CreateFailure("New name contains invalid characters");

            if (Directory.Exists(_newPath))
                return CommandResult.CreateFailure($"A folder with the name '{_newName}' already exists");

            return CommandResult.CreateSuccess("Validation passed");
        }

        protected override async Task<CommandResult> ExecuteInternalAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Run(() =>
                {
                    Directory.Move(_folderPath, _newPath);
                    _wasRenamed = true;
                }, cancellationToken);

                return CommandResult.CreateSuccess($"Renamed folder from '{_oldName}' to '{_newName}'");
            }
            catch (Exception ex)
            {
                return CommandResult.CreateFailure($"Failed to rename folder: {ex.Message}", ex);
            }
        }

        protected override async Task<CommandResult> UndoInternalAsync(CancellationToken cancellationToken)
        {
            if (!_wasRenamed)
                return CommandResult.CreateFailure("Rename operation was not executed, cannot undo");

            try
            {
                await Task.Run(() =>
                {
                    if (Directory.Exists(_newPath))
                    {
                        Directory.Move(_newPath, _folderPath);
                        _wasRenamed = false;
                    }
                }, cancellationToken);

                return CommandResult.CreateSuccess($"Undid rename operation, restored name to '{_oldName}'");
            }
            catch (Exception ex)
            {
                return CommandResult.CreateFailure($"Failed to undo rename operation: {ex.Message}", ex);
            }
        }

        public override string[] GetAffectedPaths()
        {
            return new[] { _folderPath, _newPath };
        }
    }
}
