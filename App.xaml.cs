using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ImageFolderManager.Services;
using ImageFolderManager.ViewModels;
using ImageFolderManager.Views;

namespace ImageFolderManager
{
    /// <summary>
    /// Application entry point with command system initialization
    /// </summary>
    public partial class App : Application
    {
        private MainViewModel _mainViewModel;
        private CommandSystemInitializer _commandSystem;

        protected override void OnStartup(StartupEventArgs e)
        {
            try
            {
                ShutdownMode = ShutdownMode.OnMainWindowClose;
                // Initialize application settings first
                InitializeApplicationSettings();

                // Setup global exception handling
                SetupGlobalExceptionHandling();

                // Initialize command system early
                InitializeCommandSystem();

                // Create and setup main window
                InitializeMainWindow();

                base.OnStartup(e);
            }
            catch (Exception ex)
            {
                HandleStartupException(ex);
            }
        }

        /// <summary>
        /// Initialize application settings and ensure directories exist
        /// </summary>
        private void InitializeApplicationSettings()
        {
            try
            {
                // Ensure application data directory exists
                var appDataPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ImageFolderManager");

                if (!Directory.Exists(appDataPath))
                {
                    Directory.CreateDirectory(appDataPath);
                }

                // Initialize AppSettings instance
                var settings = AppSettings.Instance;

                // Log startup information
                Debug.WriteLine($"Application starting at {DateTime.Now}");
                Debug.WriteLine($"Application data path: {appDataPath}");
                Debug.WriteLine($"Default root directory: {settings.DefaultRootDirectory}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error initializing application settings: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Initialize the command system for the entire application
        /// </summary>
        private void InitializeCommandSystem()
        {
            try
            {
                _commandSystem = new CommandSystemInitializer();
                _commandSystem.Initialize();

                Debug.WriteLine("Command system initialized successfully");

                // Subscribe to command system events for global handling
                _commandSystem.CommandExecutor.CommandStarted += OnGlobalCommandStarted;
                _commandSystem.CommandExecutor.CommandCompleted += OnGlobalCommandCompleted;
                _commandSystem.CommandExecutor.CommandFailed += OnGlobalCommandFailed;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to initialize command system: {ex.Message}");

                // Show warning but don't prevent application startup
                MessageBox.Show(
                    $"Warning: Failed to initialize command system. Some features may not work correctly.\n\nError: {ex.Message}",
                    "Command System Warning",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// Initialize the main window and view model
        /// </summary>
        private void InitializeMainWindow()
        {
            try
            {
                // Create main view model
                _mainViewModel = new MainViewModel();

                // Create and setup main window
                var mainWindow = new MainWindow
                {
                    DataContext = _mainViewModel
                };

                // Set as main window
                MainWindow = mainWindow;

                // Subscribe to main window events
                mainWindow.Closed += OnMainWindowClosed;

                // Show the main window
                mainWindow.Show();

                Debug.WriteLine("Main window initialized and shown");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error initializing main window: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Setup global exception handling for unhandled exceptions
        /// </summary>
        private void SetupGlobalExceptionHandling()
        {
            // Handle unhandled exceptions in UI thread
            DispatcherUnhandledException += OnDispatcherUnhandledException;

            // Handle unhandled exceptions in background threads
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

            // Handle task exceptions
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        #region Global Command System Event Handlers

        private void OnGlobalCommandStarted(object sender, Commands.CommandExecutionEventArgs e)
        {
            Debug.WriteLine($"Global: Command started - {e.Command.CommandType} ({e.Command.CommandId})");
        }

        private void OnGlobalCommandCompleted(object sender, Commands.CommandExecutionEventArgs e)
        {
            Debug.WriteLine($"Global: Command completed - {e.Command.CommandType} ({e.Command.CommandId})");
        }

        private void OnGlobalCommandFailed(object sender, Commands.CommandExecutionEventArgs e)
        {
            Debug.WriteLine($"Global: Command failed - {e.Command.CommandType} ({e.Command.CommandId}): {e.Result?.Message}");

            // Log error for debugging
            _commandSystem?.ExceptionService?.LogCommandFailure(
                e.Command.CommandId,
                e.Command.CommandType.ToString(),
                e.Result?.Exception,
                e.Result?.Message);
        }

        #endregion

        #region Exception Handlers

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            try
            {
                var message = $"An unhandled exception occurred in the UI thread: {e.Exception.Message}";
                Debug.WriteLine(message);

                // Log the exception
                _commandSystem?.ExceptionService?.LogException("DispatcherUnhandledException", e.Exception);

                // Show error to user
                var result = MessageBox.Show(
                    $"{message}\n\nWould you like to continue running the application?",
                    "Unhandled Exception",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Error);

                if (result == MessageBoxResult.Yes)
                {
                    e.Handled = true; // Continue running
                }
                else
                {
                    // Shutdown gracefully
                    Current.Shutdown(1);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in exception handler: {ex.Message}");
                // Don't mark as handled if we can't handle it properly
            }
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                var exception = e.ExceptionObject as Exception;
                var message = $"An unhandled exception occurred: {exception?.Message ?? "Unknown error"}";

                Debug.WriteLine(message);

                // Log the exception
                _commandSystem?.ExceptionService?.LogException("UnhandledException", exception);

                // Show critical error message
                MessageBox.Show(
                    $"{message}\n\nThe application will now close.",
                    "Critical Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in unhandled exception handler: {ex.Message}");
            }
            finally
            {
                // Force shutdown on critical errors
                Environment.Exit(1);
            }
        }

        private void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            try
            {
                var message = $"An unobserved task exception occurred: {e.Exception.GetBaseException().Message}";
                Debug.WriteLine(message);

                // Log the exception
                _commandSystem?.ExceptionService?.LogException("UnobservedTaskException", e.Exception);

                // Mark as observed to prevent application crash
                e.SetObserved();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in task exception handler: {ex.Message}");
            }
        }

        #endregion

        #region Application Lifecycle

        private void OnMainWindowClosed(object sender, EventArgs e)
        {
            try
            {
                Debug.WriteLine("Main window closed, performing cleanup...");

                // Cleanup main view model
                _mainViewModel?.Cleanup();

                // Cleanup command system
                CleanupCommandSystem();

                Debug.WriteLine("Cleanup completed");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during main window cleanup: {ex.Message}");
            }
            finally
            {
                // Ensure application shuts down
                Current.Shutdown();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                Debug.WriteLine("Application exiting...");

                // Final cleanup
                _mainViewModel?.Cleanup();
                CleanupCommandSystem();

                Debug.WriteLine("Application exit completed");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during application exit: {ex.Message}");
            }
            finally
            {
                base.OnExit(e);
            }
        }

        private void CleanupCommandSystem()
        {
            try
            {
                if (_commandSystem != null)
                {
                    // Unsubscribe from events
                    _commandSystem.CommandExecutor.CommandStarted -= OnGlobalCommandStarted;
                    _commandSystem.CommandExecutor.CommandCompleted -= OnGlobalCommandCompleted;
                    _commandSystem.CommandExecutor.CommandFailed -= OnGlobalCommandFailed;

                    // Dispose command system
                    _commandSystem.Dispose();
                    _commandSystem = null;

                    Debug.WriteLine("Command system cleanup completed");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error cleaning up command system: {ex.Message}");
            }
        }

        private void HandleStartupException(Exception ex)
        {
            var message = $"Failed to start application: {ex.Message}";
            Debug.WriteLine(message);

            try
            {
                MessageBox.Show(
                    $"{message}\n\nPlease check the application logs for more details.",
                    "Startup Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
                // If we can't show a message box, write to console
                Console.WriteLine(message);
            }
            finally
            {
                Environment.Exit(1);
            }
        }

        #endregion

        #region Application Info and Debugging

        /// <summary>
        /// Get information about the current application state
        /// </summary>
        public static string GetApplicationInfo()
        {
            try
            {
                var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                var framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
                var os = Environment.OSVersion;

                return $"ImageFolderManager v{version}\n" +
                       $"Framework: {framework}\n" +
                       $"OS: {os}\n" +
                       $"Started: {DateTime.Now}\n" +
                       $"Working Directory: {Environment.CurrentDirectory}";
            }
            catch (Exception ex)
            {
                return $"Error getting application info: {ex.Message}";
            }
        }

        /// <summary>
        /// Enable verbose logging for debugging
        /// </summary>
        [Conditional("DEBUG")]
        public static void EnableVerboseLogging()
        {
            Debug.WriteLine("Verbose logging enabled");

            // Add more detailed debug output
            Debug.Listeners.Add(new TextWriterTraceListener(Console.Out));
            Debug.AutoFlush = true;
        }

        #endregion
    }
}