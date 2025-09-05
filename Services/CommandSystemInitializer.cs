using System;
using System.Diagnostics;
using ImageFolderManager.Commands;
using ImageFolderManager.StateMachine;

namespace ImageFolderManager.Services
{
    /// <summary>
    /// Initializes and configures the command system for the application
    /// </summary>
    public class CommandSystemInitializer : IDisposable
    {
        private static bool _isInitialized = false;
        private static readonly object _initializationLock = new object();

        private PathLockManager _pathLockManager;
        private FolderStateMachine _stateMachine;
        private CommandExecutor _commandExecutor;
        private ExceptionHandlingService _exceptionService;

        public PathLockManager PathLockManager => _pathLockManager;
        public FolderStateMachine StateMachine => _stateMachine;
        public CommandExecutor CommandExecutor => _commandExecutor;
        public ExceptionHandlingService ExceptionService => _exceptionService;

        /// <summary>
        /// Initialize the command system components
        /// </summary>
        public void Initialize()
        {
            lock (_initializationLock)
            {
                if (_isInitialized)
                {
                    Debug.WriteLine("Command system already initialized");
                    return;
                }

                try
                {
                    Debug.WriteLine("Initializing command system...");

                    // Initialize core services
                    _exceptionService = new ExceptionHandlingService();
                    _pathLockManager = new PathLockManager();
                    _stateMachine = new FolderStateMachine();
                    _commandExecutor = new CommandExecutor(_pathLockManager, _stateMachine);

                    // Subscribe to global error handling
                    _commandExecutor.CommandFailed += OnCommandFailed;

                    // Configure command system settings
                    ConfigureCommandSystem();

                    _isInitialized = true;
                    Debug.WriteLine("Command system initialized successfully");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error initializing command system: {ex.Message}");
                    throw new InvalidOperationException("Failed to initialize command system", ex);
                }
            }
        }

        /// <summary>
        /// Configure command system settings and behavior
        /// </summary>
        private void ConfigureCommandSystem()
        {
            try
            {
                // Load configuration from settings if needed
                var config = CommandSystemConfiguration.LoadConfiguration();

                // Apply configuration settings
                if (config != null)
                {
                    Debug.WriteLine($"Applied command system configuration with {config.MaxConcurrentCommands} max concurrent commands");
                }

                Debug.WriteLine("Command system configuration completed");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Warning: Error configuring command system: {ex.Message}");
                // Continue with default configuration
            }
        }

        /// <summary>
        /// Handle command execution failures
        /// </summary>
        private void OnCommandFailed(object sender, CommandExecutionEventArgs e)
        {
            try
            {
                Debug.WriteLine($"Command failed: {e.Command.CommandId} - {e.Result?.Message}");

                // Log the failure
                _exceptionService?.LogCommandFailure(e.Command.CommandId, e.Command.CommandType.ToString(), e.Result?.Exception);

                // Could implement retry logic here if needed
                // Could notify user of persistent failures
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error handling command failure: {ex.Message}");
            }
        }

        /// <summary>
        /// Shutdown the command system gracefully
        /// </summary>
        public void Shutdown()
        {
            lock (_initializationLock)
            {
                if (!_isInitialized)
                    return;

                try
                {
                    Debug.WriteLine("Shutting down command system...");

                    // Unsubscribe from events
                    if (_commandExecutor != null)
                    {
                        _commandExecutor.CommandFailed -= OnCommandFailed;
                    }

                    // Dispose services in reverse order
                    _commandExecutor?.Dispose();
                    _stateMachine?.Dispose();
                    _pathLockManager?.Dispose();
                    _exceptionService?.Dispose();

                    _isInitialized = false;
                    Debug.WriteLine("Command system shut down successfully");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error shutting down command system: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Get the initialization status
        /// </summary>
        public static bool IsInitialized => _isInitialized;

        public void Dispose()
        {
            Shutdown();
        }
    }
}