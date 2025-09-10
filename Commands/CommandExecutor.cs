using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ImageFolderManager.Services;
using ImageFolderManager.StateMachine;

namespace ImageFolderManager.Commands
{
    /// <summary>
    /// Executor service that manages command execution with path-level locking and state management
    /// </summary>
    public class CommandExecutor : IDisposable
    {
        private readonly PathLockManager _lockManager;
        private readonly FolderStateMachine _stateMachine;
        private readonly Stack<IFolderCommand> _commandHistory;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _runningCommands;
        private readonly SemaphoreSlim _executorSemaphore;
        private readonly object _historyLock = new object();
        private bool _disposed = false;

        // Events for command lifecycle
        public event EventHandler<CommandExecutionEventArgs> CommandStarted;
        public event EventHandler<CommandExecutionEventArgs> CommandCompleted;
        public event EventHandler<CommandExecutionEventArgs> CommandFailed;

        public CommandExecutor(PathLockManager lockManager, FolderStateMachine stateMachine)
        {
            _lockManager = lockManager ?? throw new ArgumentNullException(nameof(lockManager));
            _stateMachine = stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));
            _commandHistory = new Stack<IFolderCommand>();
            _runningCommands = new ConcurrentDictionary<string, TaskCompletionSource<bool>>();
            _executorSemaphore = new SemaphoreSlim(Environment.ProcessorCount, Environment.ProcessorCount);
        }

        /// <summary>
        /// Execute a command with full concurrency control and state management
        /// </summary>
        public async Task<CommandResult> ExecuteCommandAsync(IFolderCommand command, CancellationToken cancellationToken = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(CommandExecutor));

            if (command == null)
                throw new ArgumentNullException(nameof(command));

            // Check if this command is already running
            if (_runningCommands.ContainsKey(command.CommandId))
            {
                return CommandResult.CreateFailure("Command is already running");
            }

            var tcs = new TaskCompletionSource<bool>();
            if (!_runningCommands.TryAdd(command.CommandId, tcs))
            {
                return CommandResult.CreateFailure("Failed to register command for execution");
            }

            try
            {
                await _executorSemaphore.WaitAsync(cancellationToken);

                try
                {
                    return await ExecuteCommandInternalAsync(command, cancellationToken);
                }
                finally
                {
                    _executorSemaphore.Release();
                }
            }
            finally
            {
                _runningCommands.TryRemove(command.CommandId, out _);
                tcs.SetResult(true);
            }
        }

        /// <summary>
        /// Gets whether there are commands that can be undone
        /// </summary>
        public bool HasUndoableCommands
        {
            get
            {
                lock (_historyLock)
                {
                    return _commandHistory.Count > 0 && _commandHistory.Peek().CanUndo;
                }
            }
        }
        private async Task<CommandResult> ExecuteCommandInternalAsync(IFolderCommand command, CancellationToken cancellationToken)
        {
            var affectedPaths = command.GetAffectedPaths();
            PathLockToken lockToken = null;

            try
            {
                // Fire command started event
                CommandStarted?.Invoke(this, new CommandExecutionEventArgs(command, CommandExecutionPhase.Started));

                // Acquire locks for all affected paths
                lockToken = await _lockManager.AcquireLocksAsync(affectedPaths, cancellationToken);

                // Update folder states to "Processing"
                foreach (var path in affectedPaths.Where(p => !string.IsNullOrEmpty(p)))
                {
                    await _stateMachine.TransitionStateAsync(path, FolderState.Processing);
                }

                // Execute the command
                var result = await command.ExecuteAsync(cancellationToken);

                if (result.Success)
                {
                    // Add to command history for undo support
                    if (command.CanUndo)
                    {
                        lock (_historyLock)
                        {
                            _commandHistory.Push(command);

                            // Limit history size to prevent memory issues
                            if (_commandHistory.Count > 100)
                            {
                                var oldestCommands = _commandHistory.Skip(100).ToArray();
                                _commandHistory.Clear();
                                foreach (var cmd in oldestCommands.Reverse())
                                {
                                    _commandHistory.Push(cmd);
                                }
                            }
                        }
                    }

                    // Update folder states based on command type
                    await UpdateFolderStatesAfterCommand(command, affectedPaths);

                    // Fire command completed event
                    CommandCompleted?.Invoke(this, new CommandExecutionEventArgs(command, CommandExecutionPhase.Completed, result));
                }
                else
                {
                    // Revert folder states to "Available" on failure
                    foreach (var path in affectedPaths.Where(p => !string.IsNullOrEmpty(p)))
                    {
                        await _stateMachine.TransitionStateAsync(path, FolderState.Available);
                    }

                    // Fire command failed event
                    CommandFailed?.Invoke(this, new CommandExecutionEventArgs(command, CommandExecutionPhase.Failed, result));
                }

                return result;
            }
            catch (Exception ex)
            {
                LogException($"Error executing command {command.CommandId}", ex);

                // Revert folder states on exception
                try
                {
                    foreach (var path in affectedPaths.Where(p => !string.IsNullOrEmpty(p)))
                    {
                        await _stateMachine.TransitionStateAsync(path, FolderState.Available);
                    }
                }
                catch (Exception stateEx)
                {
                    LogException($"Error reverting folder states after command failure", stateEx);
                }

                var failureResult = CommandResult.CreateFailure($"Command execution failed: {ex.Message}", ex);
                CommandFailed?.Invoke(this, new CommandExecutionEventArgs(command, CommandExecutionPhase.Failed, failureResult));

                return failureResult;
            }
            finally
            {
                // Always release the locks
                lockToken?.Dispose();
            }
        }

        private async Task UpdateFolderStatesAfterCommand(IFolderCommand command, string[] affectedPaths)
        {
            try
            {
                switch (command.CommandType)
                {
                    case FolderCommandType.Create:
                        // New folders are available
                        foreach (var path in affectedPaths)
                        {
                            if (System.IO.Directory.Exists(path))
                                await _stateMachine.TransitionStateAsync(path, FolderState.Available);
                        }
                        break;

                    case FolderCommandType.Delete:
                        // Deleted folders are removed from state machine
                        foreach (var path in affectedPaths)
                        {
                            await _stateMachine.RemoveFolderAsync(path);
                        }
                        break;

                    case FolderCommandType.Move:
                    case FolderCommandType.Rename:
                        // Update paths in state machine and set as available
                        foreach (var path in affectedPaths)
                        {
                            if (System.IO.Directory.Exists(path))
                                await _stateMachine.TransitionStateAsync(path, FolderState.Available);
                        }
                        break;

                    default:
                        // For other operations, just mark as available
                        foreach (var path in affectedPaths)
                        {
                            if (System.IO.Directory.Exists(path))
                                await _stateMachine.TransitionStateAsync(path, FolderState.Available);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                LogException("Error updating folder states after command", ex);
            }
        }

        /// <summary>
        /// Cancel all currently running operations
        /// </summary>
        public async Task CancelAllOperationsAsync()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(CommandExecutor));

            var runningCommands = _runningCommands.Keys.ToList();

            if (runningCommands.Count == 0)
                return;

            Debug.WriteLine($"Cancelling {runningCommands.Count} running operations");

            // Note: This is a simplified implementation
            // In a real implementation, you'd need to pass cancellation tokens to commands
            // and implement proper cancellation support in each command

            foreach (var commandId in runningCommands)
            {
                if (_runningCommands.TryGetValue(commandId, out var tcs))
                {
                    tcs.TrySetCanceled();
                }
            }

            // Wait a short time for operations to cancel gracefully
            await Task.Delay(1000);

            // Force remove any remaining running commands
            foreach (var commandId in runningCommands)
            {
                _runningCommands.TryRemove(commandId, out _);
            }
        }

        /// <summary>
        /// Undo the last executed command
        /// </summary>
        public async Task<CommandResult> UndoLastCommandAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(CommandExecutor));

            IFolderCommand lastCommand;
            lock (_historyLock)
            {
                if (_commandHistory.Count == 0)
                {
                    return CommandResult.CreateFailure("No commands to undo");
                }

                lastCommand = _commandHistory.Pop();
            }

            if (!lastCommand.CanUndo)
            {
                return CommandResult.CreateFailure($"Command {lastCommand.CommandId} cannot be undone");
            }

            // Execute undo with the same locking mechanism
            var affectedPaths = lastCommand.GetAffectedPaths();
            PathLockToken lockToken = null;

            try
            {
                await _executorSemaphore.WaitAsync(cancellationToken);

                lockToken = await _lockManager.AcquireLocksAsync(affectedPaths, cancellationToken);

                foreach (var path in affectedPaths.Where(p => !string.IsNullOrEmpty(p)))
                {
                    await _stateMachine.TransitionStateAsync(path, FolderState.Processing);
                }

                var result = await lastCommand.UndoAsync(cancellationToken);

                if (result.Success)
                {
                    await UpdateFolderStatesAfterCommand(lastCommand, affectedPaths);
                }
                else
                {
                    foreach (var path in affectedPaths.Where(p => !string.IsNullOrEmpty(p)))
                    {
                        await _stateMachine.TransitionStateAsync(path, FolderState.Available);
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                LogException($"Error undoing command {lastCommand.CommandId}", ex);
                return CommandResult.CreateFailure($"Undo failed: {ex.Message}", ex);
            }
            finally
            {
                lockToken?.Dispose();
                _executorSemaphore.Release();
            }
        }

        /// <summary>
        /// Get the count of commands in history
        /// </summary>
        public int HistoryCount
        {
            get
            {
                lock (_historyLock)
                {
                    return _commandHistory.Count;
                }
            }
        }

        /// <summary>
        /// Clear command history
        /// </summary>
        public void ClearHistory()
        {
            lock (_historyLock)
            {
                _commandHistory.Clear();
            }
        }

        private void LogException(string context, Exception exception)
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] EXECUTOR ERROR: {context}");
            Debug.WriteLine($"Exception: {exception}");
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _executorSemaphore?.Dispose();
                _disposed = true;
            }
        }
    }


    /// <summary>
    /// Event args for command execution events
    /// </summary>
    public class CommandExecutionEventArgs : EventArgs
    {
        public IFolderCommand Command { get; }
        public CommandExecutionPhase Phase { get; }
        public CommandResult Result { get; }

        public CommandExecutionEventArgs(IFolderCommand command, CommandExecutionPhase phase, CommandResult result = null)
        {
            Command = command;
            Phase = phase;
            Result = result;
        }
    }

    public enum CommandExecutionPhase
    {
        Started,
        Completed,
        Failed
    }
}