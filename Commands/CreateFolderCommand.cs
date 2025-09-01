using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ImageFolderManager.Services;

namespace ImageFolderManager.Commands
{
    /// <summary>
    /// Command to create a new folder
    /// </summary>
    public class CreateFolderCommand : BaseFolderCommand
    {
        private readonly string _parentPath;
        private readonly string _folderName;
        private string _createdPath;

        public CreateFolderCommand(string parentPath, string folderName) : base(FolderCommandType.Create)
        {
            _parentPath = PathService.NormalizePath(parentPath);
            _folderName = folderName;
            _createdPath = Path.Combine(_parentPath, _folderName);
        }

        public string ParentPath => _parentPath;
        public string FolderName => _folderName;
        public string CreatedPath => _createdPath;

        protected override async Task<CommandResult> ValidateAsync(CancellationToken cancellationToken)
        {
            await Task.CompletedTask; // Make async for consistency

            if (string.IsNullOrWhiteSpace(_parentPath))
                return CommandResult.CreateFailure("Parent path cannot be empty");

            if (string.IsNullOrWhiteSpace(_folderName))
                return CommandResult.CreateFailure("Folder name cannot be empty");

            if (!Directory.Exists(_parentPath))
                return CommandResult.CreateFailure($"Parent directory does not exist: {_parentPath}");

            // Check for invalid characters
            if (_folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return CommandResult.CreateFailure("Folder name contains invalid characters");

            // Generate unique path if folder already exists
            if (Directory.Exists(_createdPath))
            {
                _createdPath = PathService.GetUniqueDirectoryPath(_parentPath, _folderName);
            }

            return CommandResult.CreateSuccess("Validation passed");
        }

        protected override async Task<CommandResult> ExecuteInternalAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(_createdPath);
                }, cancellationToken);

                LogCommand($"Created folder: {_createdPath}");
                return CommandResult.CreateSuccess($"Folder created successfully: {Path.GetFileName(_createdPath)}", _createdPath);
            }
            catch (UnauthorizedAccessException ex)
            {
                return CommandResult.CreateFailure($"Access denied creating folder: {ex.Message}", ex);
            }
            catch (DirectoryServiceException ex)
            {
                return CommandResult.CreateFailure($"Directory service error: {ex.Message}", ex);
            }
            catch (IOException ex)
            {
                return CommandResult.CreateFailure($"IO error creating folder: {ex.Message}", ex);
            }
        }

        protected override async Task<CommandResult> UndoInternalAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (Directory.Exists(_createdPath))
                {
                    await Task.Run(() =>
                    {
                        // Only delete if the folder is empty
                        if (Directory.GetFileSystemEntries(_createdPath).Length == 0)
                        {
                            Directory.Delete(_createdPath);
                        }
                        else
                        {
                            throw new InvalidOperationException("Cannot undo create operation: folder is not empty");
                        }
                    }, cancellationToken);

                    LogCommand($"Undid folder creation: {_createdPath}");
                    return CommandResult.CreateSuccess($"Folder creation undone: {Path.GetFileName(_createdPath)}");
                }
                else
                {
                    return CommandResult.CreateSuccess("Folder no longer exists (already undone)");
                }
            }
            catch (Exception ex)
            {
                return CommandResult.CreateFailure($"Failed to undo folder creation: {ex.Message}", ex);
            }
        }

        public override string[] GetAffectedPaths()
        {
            return new[] { _parentPath, _createdPath };
        }
    }
}