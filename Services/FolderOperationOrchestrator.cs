using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ImageFolderManager.Commands;

namespace ImageFolderManager.Services
{
    /// <summary>
    /// Unified entry point for all mutating folder operations.
    /// </summary>
    public interface IFolderOperationOrchestrator
    {
        bool HasUndoableCommands { get; }

        Task<CommandResult> ExecuteCommandAsync(IFolderCommand command, CancellationToken cancellationToken = default);
        Task<CommandResult> ExecuteBatchAsync(IEnumerable<IFolderCommand> commands, CancellationToken cancellationToken = default);
        Task<CommandResult> CreateFolderAsync(string parentPath, string folderName, CancellationToken cancellationToken = default);
        Task<CommandResult> DeleteFolderAsync(string folderPath, bool useRecycleBin = true, CancellationToken cancellationToken = default);
        Task<CommandResult> MoveFolderAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default);
        Task<CommandResult> CopyFolderAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default);
        Task<CommandResult> RenameFolderAsync(string folderPath, string newName, CancellationToken cancellationToken = default);
        Task<CommandResult> UndoLastAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Command-first orchestrator that routes all writes through the command executor.
    /// </summary>
    public sealed class FolderOperationOrchestrator : IFolderOperationOrchestrator
    {
        private readonly UnifiedFolderService _folderService;

        public FolderOperationOrchestrator(UnifiedFolderService folderService)
        {
            _folderService = folderService ?? throw new ArgumentNullException(nameof(folderService));
        }

        private CommandExecutor Executor => _folderService.CommandExecutor;

        public bool HasUndoableCommands => Executor?.HasUndoableCommands == true;

        public Task<CommandResult> CreateFolderAsync(string parentPath, string folderName, CancellationToken cancellationToken = default)
        {
            return ExecuteCommandAsync(new CreateFolderCommand(parentPath, folderName), cancellationToken);
        }

        public Task<CommandResult> DeleteFolderAsync(string folderPath, bool useRecycleBin = true, CancellationToken cancellationToken = default)
        {
            return ExecuteCommandAsync(new DeleteFolderCommand(folderPath, useRecycleBin), cancellationToken);
        }

        public Task<CommandResult> MoveFolderAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
        {
            return ExecuteCommandAsync(new MoveFolderCommand(sourcePath, destinationPath), cancellationToken);
        }

        public Task<CommandResult> CopyFolderAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
        {
            return ExecuteCommandAsync(new CopyFolderCommand(sourcePath, destinationPath), cancellationToken);
        }

        public Task<CommandResult> RenameFolderAsync(string folderPath, string newName, CancellationToken cancellationToken = default)
        {
            return ExecuteCommandAsync(new RenameFolderCommand(folderPath, newName), cancellationToken);
        }

        public async Task<CommandResult> ExecuteBatchAsync(IEnumerable<IFolderCommand> commands, CancellationToken cancellationToken = default)
        {
            var commandList = commands?.Where(c => c != null).ToList() ?? new List<IFolderCommand>();
            if (commandList.Count == 0)
            {
                return CommandResult.CreateFailure("No commands to execute");
            }

            if (commandList.Count == 1)
            {
                return await ExecuteCommandAsync(commandList[0], cancellationToken);
            }

            return await ExecuteCommandAsync(new BatchOperationCommand(commandList), cancellationToken);
        }

        public async Task<CommandResult> ExecuteCommandAsync(IFolderCommand command, CancellationToken cancellationToken = default)
        {
            if (command == null)
            {
                return CommandResult.CreateFailure("Command cannot be null");
            }

            var executor = Executor;
            if (executor != null)
            {
                var result = await executor.ExecuteCommandAsync(command, cancellationToken);
                return EnrichResultData(command, result);
            }

            // Fallback path keeps existing behavior if command system initialization fails.
            return await ExecuteFallbackAsync(command);
        }

        public async Task<CommandResult> UndoLastAsync(CancellationToken cancellationToken = default)
        {
            var executor = Executor;
            if (executor == null)
            {
                return CommandResult.CreateFailure("Undo is unavailable because command executor is not initialized");
            }

            return await executor.UndoLastCommandAsync(cancellationToken);
        }

        private async Task<CommandResult> ExecuteFallbackAsync(IFolderCommand command)
        {
            switch (command)
            {
                case CreateFolderCommand createCmd:
                {
                    bool ok = await _folderService.CreateFolderAsync(createCmd.ParentPath, createCmd.FolderName);
                    return ok
                        ? CommandResult.CreateSuccess("Folder created", createCmd.CreatedPath)
                        : CommandResult.CreateFailure("Failed to create folder");
                }
                case DeleteFolderCommand deleteCmd:
                {
                    bool ok = await _folderService.DeleteFolderAsync(deleteCmd.FolderPath, deleteCmd.UseRecycleBin);
                    return ok
                        ? CommandResult.CreateSuccess("Folder deleted", deleteCmd.FolderPath)
                        : CommandResult.CreateFailure("Failed to delete folder");
                }
                case MoveFolderCommand moveCmd:
                {
                    bool ok = await _folderService.MoveFolderAsync(moveCmd.SourcePath, moveCmd.DestinationPath);
                    return ok
                        ? CommandResult.CreateSuccess("Folder moved", moveCmd.DestinationPath)
                        : CommandResult.CreateFailure("Failed to move folder");
                }
                case CopyFolderCommand copyCmd:
                {
                    bool ok = await _folderService.CopyFolderAsync(copyCmd.SourcePath, copyCmd.DestinationPath);
                    return ok
                        ? CommandResult.CreateSuccess("Folder copied", copyCmd.DestinationPath)
                        : CommandResult.CreateFailure("Failed to copy folder");
                }
                case RenameFolderCommand renameCmd:
                {
                    bool ok = await _folderService.RenameFolderAsync(renameCmd.FolderPath, renameCmd.NewName);
                    return ok
                        ? CommandResult.CreateSuccess("Folder renamed", renameCmd.NewPath)
                        : CommandResult.CreateFailure("Failed to rename folder");
                }
                default:
                    return CommandResult.CreateFailure(
                        $"Fallback execution is not supported for command type '{command.CommandType}'");
            }
        }

        private static CommandResult EnrichResultData(IFolderCommand command, CommandResult result)
        {
            if (result == null || !result.Success || result.Data != null)
            {
                return result;
            }

            switch (command)
            {
                case CreateFolderCommand createCmd:
                    result.Data = createCmd.CreatedPath;
                    break;
                case DeleteFolderCommand deleteCmd:
                    result.Data = deleteCmd.FolderPath;
                    break;
                case MoveFolderCommand moveCmd:
                    result.Data = moveCmd.ActualDestinationPath ?? moveCmd.DestinationPath;
                    break;
                case CopyFolderCommand copyCmd:
                    result.Data = copyCmd.ActualDestinationPath ?? copyCmd.DestinationPath;
                    break;
                case RenameFolderCommand renameCmd:
                    result.Data = renameCmd.NewPath;
                    break;
            }

            return result;
        }
    }
}
