using System;
using System.Threading;
using System.Threading.Tasks;

namespace ImageFolderManager.Commands
{
    /// <summary>
    /// Interface for folder commands with async execution and undo support
    /// </summary>
    public interface IFolderCommand
    {
        /// <summary>
        /// Unique identifier for the command instance
        /// </summary>
        string CommandId { get; }

        /// <summary>
        /// Command type for categorization and logging
        /// </summary>
        FolderCommandType CommandType { get; }

        /// <summary>
        /// Whether this command supports undo operation
        /// </summary>
        bool CanUndo { get; }

        /// <summary>
        /// Whether this command has been executed
        /// </summary>
        bool IsExecuted { get; }

        /// <summary>
        /// Timestamp when the command was created
        /// </summary>
        DateTime CreatedAt { get; }

        /// <summary>
        /// Timestamp when the command was executed (null if not executed)
        /// </summary>
        DateTime? ExecutedAt { get; }

        /// <summary>
        /// Execute the command asynchronously
        /// </summary>
        /// <param name="cancellationToken">Cancellation token for the operation</param>
        /// <returns>Command result containing success status and details</returns>
        Task<CommandResult> ExecuteAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Undo the command asynchronously
        /// </summary>
        /// <param name="cancellationToken">Cancellation token for the operation</param>
        /// <returns>Command result containing success status and details</returns>
        Task<CommandResult> UndoAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Get paths that will be affected by this command (for locking purposes)
        /// </summary>
        /// <returns>Array of paths that need to be locked during execution</returns>
        string[] GetAffectedPaths();
    }

    /// <summary>
    /// Types of folder commands
    /// </summary>
    public enum FolderCommandType
    {
        Create,
        Delete,
        Rename,
        Move,
        Copy,
        BatchMove,
        BatchCopy,
        BatchDelete
    }

    /// <summary>
    /// Result of command execution
    /// </summary>
    public class CommandResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public Exception Exception { get; set; }
        public object Data { get; set; }

        public static CommandResult CreateSuccess(string message = null, object data = null)
        {
            return new CommandResult
            {
                Success = true,
                Message = message,
                Data = data
            };
        }

        public static CommandResult CreateFailure(string message, Exception exception = null)
        {
            return new CommandResult
            {
                Success = false,
                Message = message,
                Exception = exception
            };
        }
    }
}
