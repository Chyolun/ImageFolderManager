using System;
using System.Diagnostics;
using System.Threading.Tasks;
using ImageFolderManager.Commands;
using ImageFolderManager.StateMachine;
using ImageFolderManager.Services;

namespace ImageFolderManager.Services
{
    /// <summary>
    /// Helper class to manage service integration and provide a clean API for command system usage
    /// </summary>
    public static class ServiceIntegrationHelper
    {
        private static CommandSystemInitializer _commandSystemInitializer;
        private static bool _isInitialized = false;
        private static readonly object _initializationLock = new object();

        /// <summary>
        /// Initialize the command system globally for the application
        /// </summary>
        /// <returns>True if initialization was successful, false otherwise</returns>
        public static bool InitializeCommandSystem()
        {
            lock (_initializationLock)
            {
                if (_isInitialized)
                {
                    Debug.WriteLine("Command system already initialized");
                    return true;
                }

                try
                {
                    Debug.WriteLine("Initializing global command system...");

                    _commandSystemInitializer = new CommandSystemInitializer();
                    _commandSystemInitializer.Initialize();

                    _isInitialized = true;

                    Debug.WriteLine("Global command system initialized successfully");
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to initialize global command system: {ex.Message}");

                    // Clean up on failure
                    _commandSystemInitializer?.Dispose();
                    _commandSystemInitializer = null;

                    return false;
                }
            }
        }

        /// <summary>
        /// Get the global command executor instance
        /// </summary>
        /// <returns>Command executor if available, null otherwise</returns>
        public static CommandExecutor GetCommandExecutor()
        {
            lock (_initializationLock)
            {
                return _commandSystemInitializer?.CommandExecutor;
            }
        }

        /// <summary>
        /// Get the global folder state machine instance
        /// </summary>
        /// <returns>Folder state machine if available, null otherwise</returns>
        public static FolderStateMachine GetStateMachine()
        {
            lock (_initializationLock)
            {
                return _commandSystemInitializer?.StateMachine;
            }
        }

        /// <summary>
        /// Get the global path lock manager instance
        /// </summary>
        /// <returns>Path lock manager if available, null otherwise</returns>
        public static PathLockManager GetPathLockManager()
        {
            lock (_initializationLock)
            {
                return _commandSystemInitializer?.PathLockManager;
            }
        }

        /// <summary>
        /// Get the global exception handling service instance
        /// </summary>
        /// <returns>Exception handling service if available, null otherwise</returns>
        public static ExceptionHandlingService GetExceptionService()
        {
            lock (_initializationLock)
            {
                return _commandSystemInitializer?.ExceptionService;
            }
        }

        /// <summary>
        /// Check if the command system is properly initialized and available
        /// </summary>
        /// <returns>True if command system is available, false otherwise</returns>
        public static bool IsCommandSystemAvailable()
        {
            lock (_initializationLock)
            {
                return _isInitialized &&
                       _commandSystemInitializer != null &&
                       _commandSystemInitializer.CommandExecutor != null;
            }
        }

        /// <summary>
        /// Get command system status information
        /// </summary>
        /// <returns>Status information as a string</returns>
        public static string GetCommandSystemStatus()
        {
            lock (_initializationLock)
            {
                if (!_isInitialized)
                    return "Command system not initialized";

                if (_commandSystemInitializer?.CommandExecutor == null)
                    return "Command system initialization failed";

                var historyCount = _commandSystemInitializer.CommandExecutor.HistoryCount;
                return $"Command system active - {historyCount} operations in history";
            }
        }

        /// <summary>
        /// Execute a folder command using the global command system
        /// </summary>
        /// <param name="command">The command to execute</param>
        /// <returns>Command result</returns>
        public static async Task<CommandResult> ExecuteCommandAsync(IFolderCommand command)
        {
            var executor = GetCommandExecutor();
            if (executor == null)
            {
                return CommandResult.CreateFailure("Command system not available");
            }

            try
            {
                return await executor.ExecuteCommandAsync(command);
            }
            catch (Exception ex)
            {
                var exceptionService = GetExceptionService();
                exceptionService?.LogException("CommandExecution", ex, $"Failed to execute command {command.CommandId}");

                return CommandResult.CreateFailure($"Command execution failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Undo the last executed command using the global command system
        /// </summary>
        /// <returns>Command result</returns>
        public static async Task<CommandResult> UndoLastCommandAsync()
        {
            var executor = GetCommandExecutor();
            if (executor == null)
            {
                return CommandResult.CreateFailure("Command system not available");
            }

            try
            {
                return await executor.UndoLastCommandAsync();
            }
            catch (Exception ex)
            {
                var exceptionService = GetExceptionService();
                exceptionService?.LogException("CommandUndo", ex, "Failed to undo last command");

                return CommandResult.CreateFailure($"Undo failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Get the current state of a folder using the global state machine
        /// </summary>
        /// <param name="folderPath">Path to the folder</param>
        /// <returns>Folder state</returns>
        public static FolderState GetFolderState(string folderPath)
        {
            var stateMachine = GetStateMachine();
            if (stateMachine == null || string.IsNullOrEmpty(folderPath))
            {
                return FolderState.Available;
            }

            return stateMachine.GetFolderState(folderPath);
        }

        /// <summary>
        /// Check if a folder can be operated on (not locked or in processing state)
        /// </summary>
        /// <param name="folderPath">Path to the folder</param>
        /// <returns>True if folder can be operated on, false otherwise</returns>
        public static bool CanOperateOnFolder(string folderPath)
        {
            if (!IsCommandSystemAvailable())
            {
                return true; // Allow all operations in legacy mode
            }

            var state = GetFolderState(folderPath);
            return state == FolderState.Available || state == FolderState.Monitoring;
        }

        /// <summary>
        /// Transition a folder to a specific state using the global state machine
        /// </summary>
        /// <param name="folderPath">Path to the folder</param>
        /// <param name="newState">Target state</param>
        /// <param name="operationId">Optional operation ID</param>
        /// <returns>True if transition was successful, false otherwise</returns>
        public static async Task<bool> TransitionFolderStateAsync(string folderPath, FolderState newState, string operationId = null)
        {
            var stateMachine = GetStateMachine();
            if (stateMachine == null)
            {
                return false;
            }

            try
            {
                return await stateMachine.TransitionStateAsync(folderPath, newState, operationId);
            }
            catch (Exception ex)
            {
                var exceptionService = GetExceptionService();
                exceptionService?.LogException("StateTransition", ex,
                    $"Failed to transition folder {folderPath} to state {newState}");

                return false;
            }
        }

        /// <summary>
        /// Get statistics about folder states from the global state machine
        /// </summary>
        /// <returns>State statistics</returns>
        public static StateStatistics GetStateStatistics()
        {
            var stateMachine = GetStateMachine();
            return stateMachine?.GetStateStatistics() ?? new StateStatistics();
        }

        /// <summary>
        /// Clear command history in the global command executor
        /// </summary>
        public static void ClearCommandHistory()
        {
            var executor = GetCommandExecutor();
            executor?.ClearHistory();
        }

        /// <summary>
        /// Get the number of commands in the global command history
        /// </summary>
        /// <returns>Number of commands in history</returns>
        public static int GetCommandHistoryCount()
        {
            var executor = GetCommandExecutor();
            return executor?.HistoryCount ?? 0;
        }

        /// <summary>
        /// Shutdown and dispose the global command system
        /// </summary>
        public static void ShutdownCommandSystem()
        {
            lock (_initializationLock)
            {
                if (_isInitialized)
                {
                    try
                    {
                        Debug.WriteLine("Shutting down global command system...");

                        _commandSystemInitializer?.Dispose();
                        _commandSystemInitializer = null;
                        _isInitialized = false;

                        Debug.WriteLine("Global command system shutdown completed");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error during command system shutdown: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Create a new folder command with automatic validation
        /// </summary>
        /// <param name="parentPath">Parent directory path</param>
        /// <param name="folderName">Name of the new folder</param>
        /// <returns>Create folder command</returns>
        public static CreateFolderCommand CreateNewFolderCommand(string parentPath, string folderName)
        {
            return new CreateFolderCommand(parentPath, folderName);
        }

        /// <summary>
        /// Create a delete folder command with automatic validation
        /// </summary>
        /// <param name="folderPath">Path to the folder to delete</param>
        /// <param name="useRecycleBin">Whether to use recycle bin</param>
        /// <returns>Delete folder command</returns>
        public static DeleteFolderCommand CreateDeleteFolderCommand(string folderPath, bool useRecycleBin = true)
        {
            return new DeleteFolderCommand(folderPath, useRecycleBin);
        }

        /// <summary>
        /// Create a rename folder command with automatic validation
        /// </summary>
        /// <param name="folderPath">Path to the folder to rename</param>
        /// <param name="newName">New name for the folder</param>
        /// <returns>Rename folder command</returns>
        public static RenameFolderCommand CreateRenameFolderCommand(string folderPath, string newName)
        {
            return new RenameFolderCommand(folderPath, newName);
        }

        /// <summary>
        /// Create a move folder command with automatic validation
        /// </summary>
        /// <param name="sourcePath">Source folder path</param>
        /// <param name="destinationPath">Destination folder path</param>
        /// <returns>Move folder command</returns>
        public static MoveFolderCommand CreateMoveFolderCommand(string sourcePath, string destinationPath)
        {
            return new MoveFolderCommand(sourcePath, destinationPath);
        }

        /// <summary>
        /// Create a copy folder command with automatic validation
        /// </summary>
        /// <param name="sourcePath">Source folder path</param>
        /// <param name="destinationPath">Destination folder path</param>
        /// <returns>Copy folder command</returns>
        public static CopyFolderCommand CreateCopyFolderCommand(string sourcePath, string destinationPath)
        {
            return new CopyFolderCommand(sourcePath, destinationPath);
        }

        /// <summary>
        /// Validate if a command can be executed based on current folder states
        /// </summary>
        /// <param name="command">Command to validate</param>
        /// <returns>Validation result with success status and message</returns>
        public static (bool Success, string Message) ValidateCommand(IFolderCommand command)
        {
            if (!IsCommandSystemAvailable())
            {
                return (true, "Command system not available - using legacy mode");
            }

            var affectedPaths = command.GetAffectedPaths();

            foreach (var path in affectedPaths)
            {
                if (!CanOperateOnFolder(path))
                {
                    var state = GetFolderState(path);
                    return (false, $"Cannot execute command: folder {path} is currently {state}");
                }
            }

            return (true, "Command can be executed");
        }
    }
}

namespace ImageFolderManager.Extensions
{
    /// <summary>
    /// Extension methods for easier command system integration
    /// </summary>
    public static class CommandSystemExtensions
    {
        /// <summary>
        /// Execute a command with automatic error handling and logging
        /// </summary>
        /// <param name="command">Command to execute</param>
        /// <returns>Command result</returns>
        public static async Task<CommandResult> ExecuteWithLoggingAsync(this IFolderCommand command)
        {
            return await ServiceIntegrationHelper.ExecuteCommandAsync(command);
        }

        /// <summary>
        /// Check if this command can be executed based on current system state
        /// </summary>
        /// <param name="command">Command to validate</param>
        /// <returns>True if command can be executed, false otherwise</returns>
        public static bool CanExecute(this IFolderCommand command)
        {
            var (success, _) = ServiceIntegrationHelper.ValidateCommand(command);
            return success;
        }

        /// <summary>
        /// Get validation message for this command
        /// </summary>
        /// <param name="command">Command to validate</param>
        /// <returns>Validation message</returns>
        public static string GetValidationMessage(this IFolderCommand command)
        {
            var (_, message) = ServiceIntegrationHelper.ValidateCommand(command);
            return message;
        }
    }
}