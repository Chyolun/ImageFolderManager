using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using ImageFolderManager.Services;
using ImageFolderManager.ViewModels;

namespace ImageFolderManager
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnExit(ExitEventArgs e)
        {
            // Clear path cache
            PathService.ClearPathCache();

            // Enhanced cleanup for main view model with indexing service
            if (Application.Current.MainWindow?.DataContext is MainViewModel viewModel)
            {
                try
                {
                    viewModel.Cleanup();
                    Debug.WriteLine("Application cleanup completed successfully");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error during application cleanup: {ex.Message}");
                }
            }

            base.OnExit(e);
        }
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Initialize application settings
            InitializeAppSettings();
        }
        

        private void InitializeAppSettings()
        {
            try
            {
                // Just access the instance to trigger loading of settings
                var settings = AppSettings.Instance;

                // Ensure cache directory exists
                string cacheDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ImageFolderManager",
                    "Cache");

                if (!Directory.Exists(cacheDir))
                {
                    Directory.CreateDirectory(cacheDir);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error initializing application settings: {ex.Message}",
                    "Initialization Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }
}