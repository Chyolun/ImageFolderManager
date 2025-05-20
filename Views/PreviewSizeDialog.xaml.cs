using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using ImageFolderManager.Models;
using ImageFolderManager.Services;
using MahApps.Metro.Controls;

namespace ImageFolderManager.Views
{
    public partial class PreviewSizeDialog : MetroWindow
    {
        public int SelectedWidth { get; private set; }
        public int SelectedHeight { get; private set; }
        public int SelectedMaxCacheSize { get; private set; }
        public int SelectedThreadCount { get; private set; }
        public bool DialogResult { get; private set; } = false;

        private bool _isCalculatingCacheSize = false;

        public PreviewSizeDialog()
        {
            InitializeComponent();

            // Load current settings
            WidthUpDown.Value = AppSettings.Instance.PreviewWidth;
            HeightUpDown.Value = AppSettings.Instance.PreviewHeight;
            MaxCacheSizeUpDown.Value = AppSettings.Instance.MaxCacheSize;
            ThreadCountUpDown.Value = AppSettings.Instance.ParallelThreadCount;

            // Calculate cache folder size
            CalculateCacheSizeAsync();
        }

        private async void CalculateCacheSizeAsync()
        {
            if (_isCalculatingCacheSize)
                return;

            _isCalculatingCacheSize = true;
            CacheSizeText.Text = "Calculating...";

            try
            {
                string cacheFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ImageFolderManager", "Cache");

                await Task.Run(() =>
                {
                    long totalSize = 0;

                    // Check main cache folder
                    if (Directory.Exists(cacheFolder))
                    {
                        var directory = new DirectoryInfo(cacheFolder);
                        foreach (var file in directory.GetFiles("*", SearchOption.AllDirectories))
                        {
                            totalSize += file.Length;
                        }
                    }

                 
                    // Convert to appropriate size format
                    string sizeText = FormatSize(totalSize);

                    // Update UI on the UI thread
                    Dispatcher.Invoke(() =>
                    {
                        CacheSizeText.Text = sizeText;
                    });
                });
            }
            catch (Exception ex)
            {
                CacheSizeText.Text = "Error calculating size";
                System.Diagnostics.Debug.WriteLine($"Error calculating cache size: {ex.Message}");
            }
            finally
            {
                _isCalculatingCacheSize = false;
            }
        }

        private string FormatSize(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int counter = 0;
            decimal number = bytes;
            while (Math.Round(number / 1024) >= 1)
            {
                number = number / 1024;
                counter++;
            }
            return $"{number:n1} {suffixes[counter]}";
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            SelectedWidth = (int)WidthUpDown.Value;
            SelectedHeight = (int)HeightUpDown.Value;
            SelectedMaxCacheSize = (int)MaxCacheSizeUpDown.Value;
            SelectedThreadCount = (int)ThreadCountUpDown.Value;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private async void ClearCache_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "Are you sure you want to clear the thumbnail cache? This will delete all cached thumbnails and they will be regenerated when needed.",
                "Clear Cache",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    ImageCache.ClearCache();
                    MessageBox.Show("Cache cleared successfully.", "Cache Cleared", MessageBoxButton.OK, MessageBoxImage.Information);

                    // Recalculate cache size
                    await Task.Delay(500); // Small delay to ensure files are deleted
                    CalculateCacheSizeAsync();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error clearing cache: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }
}