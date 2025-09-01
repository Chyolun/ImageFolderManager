using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ImageFolderManager.Services;

namespace ImageFolderManager.Commands
{
    /// <summary>
    /// Command that executes multiple folder commands as a single batch operation
    /// </summary>
    public class BatchOperationCommand : BaseFolderCommand
    {
        private readonly List<IFolderCommand> _commands;
        private readonly List<IFolderCommand> _executedCommands;
        private readonly object _executionLock = new object();

        public BatchOperationCommand(IEnumerable<IFolderCommand> commands) : base(FolderCommandType.BatchMove)
        {
            _commands = commands?.Where(c => c != null).ToList() ?? throw new ArgumentNullException(nameof(commands));
            _executedCommands = new List<IFolderCommand>();

            if (_commands.Count == 0)
                throw new ArgumentException("Batch operation must contain at least one command", nameof(commands));

            // Determine batch type based on the commands
            CommandType = DetermineBatchType(_commands);
        }

        public IReadOnlyList<IFolderCommand> Commands => _commands.AsReadOnly();
        public IReadOnlyList<IFolderCommand> ExecutedCommands => _executedCommands.AsReadOnly();
        public int TotalCommands => _commands.Count;
        public int ExecutedCount => _executedCommands.Count;

        public override bool CanUndo => _executedCommands.All(c => c.CanUndo);

        private FolderCommandType DetermineBatchType(List<IFolderCommand> commands)
        {
            if (commands.All(c => c.CommandType == FolderCommandType.Move))
                return FolderCommandType.BatchMove;
            if (commands.All(c => c.CommandType == FolderCommandType.Copy))
                return FolderCommandType.BatchCopy;
            if (commands.All(c => c.CommandType == FolderCommandType.Delete))
                return FolderCommandType.BatchDelete;

            // Mixed batch operation - use generic batch type
            return FolderCommandType.BatchMove; // Default to BatchMove for mixed operations
        }

        protected override async Task<CommandResult> ValidateAsync(CancellationToken cancellationToken)
        {
            var invalidCommands = new List<string>();

            foreach (var command in _commands)
            {
                try
                {
                    // We can't directly call ValidateAsync on the command since it's protected
                    // Instead, we'll do basic validation here
                    var affectedPaths = command.GetAffectedPaths();
                    if (affectedPaths == null || affectedPaths.Length == 0)
                    {
                        invalidCommands.Add($"{command.CommandId}: No affected paths defined");
                        continue;
                    }

                    // Check for already executed commands
                    if (command.IsExecuted)
                    {
                        invalidCommands.Add($"{command.CommandId}: Command already executed");
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    invalidCommands.Add($"{command.CommandId}: Validation error - {ex.Message}");
                }

                if (cancellationToken.IsCancellationRequested)
                    return CommandResult.CreateFailure("Batch validation cancelled");
            }

            if (invalidCommands.Count > 0)
            {
                return CommandResult.CreateFailure($"Batch validation failed:\n{string.Join("\n", invalidCommands)}");
            }

            // Check for path conflicts between commands
            var allPaths = _commands.SelectMany(c => c.GetAffectedPaths()).ToList();
            var duplicatePaths = allPaths.GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (duplicatePaths.Count > 0)
            {
                return CommandResult.CreateFailure($"Batch contains conflicting paths: {string.Join(", ", duplicatePaths)}");
            }

            return CommandResult.CreateSuccess("Batch validation passed");
        }

        protected override async Task<CommandResult> ExecuteInternalAsync(CancellationToken cancellationToken)
        {
            var results = new List<CommandResult>();
            var failedCommands = new List<string>();

            try
            {
                LogCommand($"Starting batch execution of {_commands.Count} commands");

                foreach (var command in _commands)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        LogCommand("Batch execution cancelled");
                        break;
                    }

                    try
                    {
                        var result = await command.ExecuteAsync(cancellationToken);
                        results.Add(result);

                        lock (_executionLock)
                        {
                            if (result.Success)
                            {
                                _executedCommands.Add(command);
                                LogCommand($"Batch command executed successfully: {command.CommandId}");
                            }
                            else
                            {
                                failedCommands.Add($"{command.CommandId}: {result.Message}");
                                LogCommand($"Batch command failed: {command.CommandId} - {result.Message}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        var errorResult = CommandResult.CreateFailure($"Command execution error: {ex.Message}", ex);
                        results.Add(errorResult);
                        failedCommands.Add($"{command.CommandId}: {ex.Message}");
                        LogException($"Error executing command {command.CommandId} in batch", ex);
                    }
                }

                // Determine overall result
                int successCount = results.Count(r => r.Success);
                int totalCount = _commands.Count;

                if (successCount == totalCount)
                {
                    LogCommand($"Batch execution completed successfully: {successCount}/{totalCount} commands");
                    return CommandResult.CreateSuccess(
                        $"Batch operation completed successfully: {successCount} commands executed",
                        new BatchResult { SuccessCount = successCount, TotalCount = totalCount, Results = results });
                }
                else if (successCount > 0)
                {
                    LogCommand($"Batch execution partially completed: {successCount}/{totalCount} commands");
                    return CommandResult.CreateSuccess(
                        $"Batch operation partially completed: {successCount}/{totalCount} commands executed\nFailures:\n{string.Join("\n", failedCommands)}",
                        new BatchResult { SuccessCount = successCount, TotalCount = totalCount, Results = results });
                }
                else
                {
                    LogCommand($"Batch execution failed: 0/{totalCount} commands completed");
                    return CommandResult.CreateFailure(
                        $"Batch operation failed: No commands were executed successfully\nFailures:\n{string.Join("\n", failedCommands)}");
                }
            }
            catch (Exception ex)
            {
                LogException("Error in batch execution", ex);
                return CommandResult.CreateFailure($"Batch execution error: {ex.Message}", ex);
            }
        }

        protected override async Task<CommandResult> UndoInternalAsync(CancellationToken cancellationToken)
        {
            var undoResults = new List<CommandResult>();
            var failedUndos = new List<string>();

            try
            {
                LogCommand($"Starting batch undo for {_executedCommands.Count} commands");

                // Undo commands in reverse order
                var commandsToUndo = _executedCommands.ToList();
                commandsToUndo.Reverse();

                foreach (var command in commandsToUndo)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        LogCommand("Batch undo cancelled");
                        break;
                    }

                    try
                    {
                        if (command.CanUndo)
                        {
                            var result = await command.UndoAsync(cancellationToken);
                            undoResults.Add(result);

                            if (result.Success)
                            {
                                LogCommand($"Batch undo executed successfully: {command.CommandId}");
                            }
                            else
                            {
                                failedUndos.Add($"{command.CommandId}: {result.Message}");
                                LogCommand($"Batch undo failed: {command.CommandId} - {result.Message}");
                            }
                        }
                        else
                        {
                            failedUndos.Add($"{command.CommandId}: Command does not support undo");
                            LogCommand($"Batch undo skipped (not supported): {command.CommandId}");
                        }
                    }
                    catch (Exception ex)
                    {
                        var errorResult = CommandResult.CreateFailure($"Undo error: {ex.Message}", ex);
                        undoResults.Add(errorResult);
                        failedUndos.Add($"{command.CommandId}: {ex.Message}");
                        LogException($"Error undoing command {command.CommandId} in batch", ex);
                    }
                }

                // Clear executed commands list since we attempted to undo them
                lock (_executionLock)
                {
                    _executedCommands.Clear();
                }

                // Determine overall undo result
                int successCount = undoResults.Count(r => r.Success);
                int totalCount = commandsToUndo.Count;

                if (successCount == totalCount)
                {
                    LogCommand($"Batch undo completed successfully: {successCount}/{totalCount} commands");
                    return CommandResult.CreateSuccess($"Batch undo completed successfully: {successCount} commands undone");
                }
                else if (successCount > 0)
                {
                    LogCommand($"Batch undo partially completed: {successCount}/{totalCount} commands");
                    return CommandResult.CreateSuccess(
                        $"Batch undo partially completed: {successCount}/{totalCount} commands undone\nFailures:\n{string.Join("\n", failedUndos)}");
                }
                else
                {
                    LogCommand($"Batch undo failed: 0/{totalCount} commands undone");
                    return CommandResult.CreateFailure(
                        $"Batch undo failed: No commands were undone successfully\nFailures:\n{string.Join("\n", failedUndos)}");
                }
            }
            catch (Exception ex)
            {
                LogException("Error in batch undo", ex);
                return CommandResult.CreateFailure($"Batch undo error: {ex.Message}", ex);
            }
        }

        public override string[] GetAffectedPaths()
        {
            try
            {
                return _commands
                    .SelectMany(c => c.GetAffectedPaths())
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception ex)
            {
                LogException("Error getting affected paths for batch command", ex);
                return new string[0];
            }
        }
    }

    /// <summary>
    /// Result data for batch operations
    /// </summary>
    public class BatchResult
    {
        public int SuccessCount { get; set; }
        public int TotalCount { get; set; }
        public List<CommandResult> Results { get; set; }

        public double SuccessRate => TotalCount > 0 ? (double)SuccessCount / TotalCount : 0.0;
        public bool IsPartialSuccess => SuccessCount > 0 && SuccessCount < TotalCount;
        public bool IsCompleteSuccess => SuccessCount == TotalCount && TotalCount > 0;
        public bool IsCompleteFailure => SuccessCount == 0;
    }
}