using System;
using System.Diagnostics;
using System.Windows;
using ImageFolderManager.Services;

namespace ImageFolderManager
{
    /// <summary>
    /// Enhanced App.xaml.cs with Command System initialization
    /// </summary>
    public partial class App : Application
    {
        private bool _commandSystemInitialized = false;

        protected override void OnStartup(StartupEventArgs e)
        {
            // Initialize command system early in application lifecycle
            InitializeCommandSystem();

            // Set up global exception handling
            SetupGlobalExceptionHandling();

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Cleanup command system on application exit
            CleanupCommandSystem();

            base.OnExit(e);
        }

        /// <summary>
        /// Initialize the global command system for the application
        /// </summary>
        private void InitializeCommandSystem()
        {
            try
            {
                Debug.WriteLine("Initializing command system during application startup...");

                _commandSystemInitialized = ServiceIntegrationHelper.InitializeCommandSystem();

                if (_commandSystemInitialized)
                {
                    Debug.WriteLine("Command system initialized successfully");

                    // Log initial status
                    var status = ServiceIntegrationHelper.GetCommandSystemStatus();
                    Debug.WriteLine($"Command system status: {status}");
                }
                else
                {
                    Debug.WriteLine("Command system initialization failed - application will run in legacy mode");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during command system initialization: {ex.Message}");
                _commandSystemInitialized = false;
            }
        }

        /// <summary>
        /// Setup global exception handling with command system integration
        /// </summary>
        private void SetupGlobalExceptionHandling()
        {
            // Handle unhandled exceptions in UI thread
            this.DispatcherUnhandledException += (sender, args) =>
            {
                HandleUnhandledException(args.Exception, "UI Thread");
                args.Handled = true;
            };

            // Handle unhandled exceptions in background threads
            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
            {
                HandleUnhandledException(args.ExceptionObject as Exception, "Background Thread");
            };
        }

        /// <summary>
        /// Handle unhandled exceptions with command system logging
        /// </summary>
        private void HandleUnhandledException(Exception exception, string context)
        {
            try
            {
                Debug.WriteLine($"Unhandled exception in {context}: {exception}");

                // Log to command system if available
                if (_commandSystemInitialized)
                {
                    var exceptionService = ServiceIntegrationHelper.GetExceptionService();
                    exceptionService?.LogException(context, exception, "Unhandled application exception");
                }

                // Show user-friendly error message
                var message = $"An unexpected error occurred in {context}.\n\n" +
                             $"Error: {exception?.Message}\n\n" +
                             $"The application will continue running, but some features may not work correctly.";

                MessageBox.Show(message, "Application Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception logException)
            {
                // Last resort - at least log to debug output
                Debug.WriteLine($"Error handling exception: {logException}");
                Debug.WriteLine($"Original exception: {exception}");
            }
        }

        /// <summary>
        /// Cleanup command system on application shutdown
        /// </summary>
        private void CleanupCommandSystem()
        {
            if (_commandSystemInitialized)
            {
                try
                {
                    Debug.WriteLine("Shutting down command system...");

                    // Get final statistics before shutdown
                    var stats = ServiceIntegrationHelper.GetStateStatistics();
                    var historyCount = ServiceIntegrationHelper.GetCommandHistoryCount();

                    Debug.WriteLine($"Final statistics - History: {historyCount} commands, States: {stats.TotalCount} folders");

                    // Shutdown command system
                    ServiceIntegrationHelper.ShutdownCommandSystem();

                    Debug.WriteLine("Command system shutdown completed");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error during command system cleanup: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Get application-wide command system status
        /// </summary>
        public static bool IsCommandSystemEnabled()
        {
            return ServiceIntegrationHelper.IsCommandSystemAvailable();
        }

        /// <summary>
        /// Get application-wide command system status message
        /// </summary>
        public static string GetCommandSystemStatusMessage()
        {
            return ServiceIntegrationHelper.GetCommandSystemStatus();
        }
    }
}