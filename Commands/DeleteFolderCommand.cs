using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualBasic.FileIO;
using ImageFolderManager.Services;

namespace ImageFolderManager.Commands
{
    /// <summary>
    /// Command to delete a folder (moves to recycle bin)
    /// </summary>
    public class DeleteFolderCommand : BaseFolderCommand
    {
        private readonly string _folderPath;
        private readonly bool _useRecycleBin;
        private string _parentPath;
        private bool _wasDeleted;

        public DeleteFolderCommand(string folderPath, bool useRecycleBin = true) : base(FolderCommandType.Delete)
        {
            _folderPath = PathService.NormalizePath(folderPath);
            _useRecycleBin = useRecycleBin;
            _parentPath = Path.GetDirectoryName(_folderPath);
            _wasDeleted = false;
        }

        public string FolderPath => _folderPath;
        public bool UseRecycleBin => _useRecycleBin;

        public override bool CanUndo => false; // Cannot reliably undo delete operations

        protected override async Task<CommandResult> ValidateAsync(CancellationToken cancellationToken)
        {
            await Task.CompletedTask;

            if (string.IsNullOrWhiteSpace(_folderPath))
                return CommandResult.CreateFailure("Folder path cannot be empty");

            if (!Directory.Exists(_folderPath))
                return CommandResult.CreateFailure($"Folder does not exist: {_folderPath}");

            // Check if folder is not a system folder or root drive
            if (Path.GetPathRoot(_folderPath) == _folderPath)
                return CommandResult.CreateFailure("Cannot delete root drive");

            return CommandResult.CreateSuccess("Validation passed");
        }

        protected override async Task<CommandResult> ExecuteInternalAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Run(() =>
                {
                    if (_useRecycleBin)
                    {
                        FileSystem.DeleteDirectory(_folderPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    }
                    else
                    {
                        Directory.Delete(_folderPath, true);
                    }
                    _wasDeleted = true;
                }, cancellationToken);

                LogCommand($"Deleted folder: {_folderPath} (RecycleBin: {_useRecycleBin})");
                return CommandResult.CreateSuccess($"Folder deleted: {Path.GetFileName(_folderPath)}");
            }
            catch (UnauthorizedAccessException ex)
            {
                return CommandResult.CreateFailure($"Access denied deleting folder: {ex.Message}", ex);
            }
            catch (DirectoryNotFoundException ex)
            {
                return CommandResult.CreateFailure($"Folder not found: {ex.Message}", ex);
            }
            catch (IOException ex)
            {
                return CommandResult.CreateFailure($"IO error deleting folder: {ex.Message}", ex);
            }
        }

        protected override async Task<CommandResult> UndoInternalAsync(CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            return CommandResult.CreateFailure("Delete operations cannot be undone");
        }

        public override string[] GetAffectedPaths()
        {
            return new[] { _folderPath, _parentPath };
        }
    }
}
