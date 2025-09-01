using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ImageFolderManager.Services;

namespace ImageFolderManager.Commands
{
    /// <summary>
    /// Base class for all folder commands with common functionality
    /// </summary>
    public abstract class BaseFolderCommand : IFolderCommand
    {
        private static int _nextCommandId = 1;
        private readonly object _executionLock = new object();

        protected BaseFolderCommand(FolderCommandType commandType)
        {
            CommandId = $"{commandType}_{Interlocked.Increment(ref _nextCommandId)}_{DateTime.Now.Ticks}";
            CommandType = commandType;
            CreatedAt = DateTime.Now;
            IsExecuted = false;
        }

        public string CommandId { get; }
        public FolderCommandType CommandType { get; }
        public virtual bool CanUndo => true;
        public bool IsExecuted { get; private set; }
        public DateTime CreatedAt { get; }
        public DateTime? ExecutedAt { get; private set; }

        public async Task<CommandResult> ExecuteAsync(CancellationToken cancellationToken = default)
        {
            lock (_executionLock)
            {
                if (IsExecuted)
                {
                    return CommandResult.CreateFailure($"Command {CommandId} has already been executed");
                }
            }

            try
            {
                // Log command start
                LogCommand($"Starting execution of {CommandType} command: {CommandId}");

                // Validate command before execution
                var validationResult = await ValidateAsync(cancellationToken);
                if (!validationResult.Success)
                {
                    return validationResult;
                }

                // Execute the actual command
                var result = await ExecuteInternalAsync(cancellationToken);

                lock (_executionLock)
                {
                    if (result.Success)
                    {
                        IsExecuted = true;
                        ExecutedAt = DateTime.Now;
                    }
                }

                // Log command completion
                LogCommand($"Command execution {(result.Success ? "completed" : "failed")}: {CommandId}");

                return result;
            }
            catch (OperationCanceledException)
            {
                LogCommand($"Command execution cancelled: {CommandId}");
                return CommandResult.CreateFailure("Operation was cancelled");
            }
            catch (Exception ex)
            {
                LogException($"Error executing command {CommandId}", ex);
                return CommandResult.CreateFailure($"Command execution failed: {ex.Message}", ex);
            }
        }

        public async Task<CommandResult> UndoAsync(CancellationToken cancellationToken = default)
        {
            if (!CanUndo)
            {
                return CommandResult.CreateFailure($"Command {CommandId} does not support undo");
            }

            if (!IsExecuted)
            {
                return CommandResult.CreateFailure($"Command {CommandId} has not been executed yet");
            }

            try
            {
                LogCommand($"Starting undo of {CommandType} command: {CommandId}");

                var result = await UndoInternalAsync(cancellationToken);

                if (result.Success)
                {
                    lock (_executionLock)
                    {
                        IsExecuted = false;
                        ExecutedAt = null;
                    }
                }

                LogCommand($"Command undo {(result.Success ? "completed" : "failed")}: {CommandId}");

                return result;
            }
            catch (OperationCanceledException)
            {
                LogCommand($"Command undo cancelled: {CommandId}");
                return CommandResult.CreateFailure("Undo operation was cancelled");
            }
            catch (Exception ex)
            {
                LogException($"Error undoing command {CommandId}", ex);
                return CommandResult.CreateFailure($"Command undo failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Abstract method for command-specific validation
        /// </summary>
        protected abstract Task<CommandResult> ValidateAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Abstract method for command-specific execution
        /// </summary>
        protected abstract Task<CommandResult> ExecuteInternalAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Abstract method for command-specific undo
        /// </summary>
        protected abstract Task<CommandResult> UndoInternalAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Abstract method to get affected paths for locking
        /// </summary>
        public abstract string[] GetAffectedPaths();

        /// <summary>
        /// Log command operations
        /// </summary>
        protected virtual void LogCommand(string message)
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] COMMAND: {message}");
        }

        /// <summary>
        /// Log exceptions with full details
        /// </summary>
        protected virtual void LogException(string context, Exception exception)
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ERROR: {context}");
            Debug.WriteLine($"Exception: {exception}");
        }
    }
}