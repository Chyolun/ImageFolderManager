using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ImageFolderManager.Models;
using ImageFolderManager.Services;
using ImageFolderManager.ViewModels;
using MahApps.Metro.Controls;
using Microsoft.VisualBasic.FileIO;

namespace ImageFolderManager.Views
{
    /// <summary>
    /// Dialog for displaying and managing duplicate folder names within the root directory
    /// </summary>
    public partial class DuplicateFoldersDialog : MetroWindow
    {
        private readonly MainViewModel _mainViewModel;
        private List<DuplicateFolderGroup> _duplicateGroups;

        /// <summary>
        /// Represents a group of folders with the same name
        /// </summary>
        public class DuplicateFolderGroup
        {
            public string FolderName { get; set; }
            public List<FolderInfo> Folders { get; set; } = new List<FolderInfo>();
            public int Count => Folders.Count;
        }

        public DuplicateFoldersDialog(MainViewModel mainViewModel)
        {
            InitializeComponent();
            _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));

            this.Loaded += DuplicateFoldersDialog_Loaded;
        }

        private async void DuplicateFoldersDialog_Loaded(object sender, RoutedEventArgs e)
        {
            await FindAndDisplayDuplicates();
        }

        /// <summary>
        /// Finds duplicate folders and displays them in the UI with filter information
        /// </summary>
        private async Task FindAndDisplayDuplicates()
        {
            try
            {
                StatusText.Text = "Searching for duplicate folders...";
                DuplicateGroupsPanel.Children.Clear();

                // Get statistics with filter information
                var stats = _mainViewModel.GetDuplicateStatsWithFilters();

                if (stats.totalFolders == 0)
                {
                    ShowNoFoldersMessage();
                    return;
                }

                // Get actual duplicate groups
                var duplicateGroups = _mainViewModel.FindDuplicateFolders()
                    .Select(kvp => new DuplicateFolderGroup
                    {
                        FolderName = kvp.Key,
                        Folders = kvp.Value
                    })
                    .OrderByDescending(g => g.Count)
                    .ThenBy(g => g.FolderName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                _duplicateGroups = duplicateGroups;

                // Update summary with filter information
                UpdateSummaryWithFilters(stats);

                if (duplicateGroups.Count == 0)
                {
                    ShowNoDuplicatesMessage();
                    return;
                }

                // Display duplicate groups
                foreach (var group in duplicateGroups)
                {
                    CreateDuplicateGroupUI(group);
                }

                StatusText.Text = $"Found {duplicateGroups.Count} duplicate folder groups";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Error occurred while searching for duplicates";
                MessageBox.Show($"Error finding duplicates: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Updates the summary display with filter information
        /// </summary>
        private void UpdateSummaryWithFilters((int totalFolders, int filteredFolders, int duplicateGroups, int duplicateFolders) stats)
        {
            // Main summary
            SummaryText.Text = stats.duplicateGroups == 0
                ? "No duplicate folder names found"
                : $"Found {stats.duplicateGroups} duplicate folder names ({stats.duplicateFolders} total folders)";

            // Total folders count
            TotalFoldersText.Text = $"Total folders: {stats.totalFolders}";

            // Filter information
            if (AppSettings.Instance.EnableDuplicateFilters)
            {
                var filtered = stats.totalFolders - stats.filteredFolders;
                FilterInfoText.Text = $"Filters enabled • {filtered} folders excluded • {stats.filteredFolders} folders checked";
                FilterInfoText.Foreground = Brushes.LightBlue;
                FilterSettingsButton.Visibility = Visibility.Visible;
            }
            else
            {
                FilterInfoText.Text = "Filters disabled • All folders checked";
                FilterInfoText.Foreground = Brushes.Gray;
                FilterSettingsButton.Visibility = Visibility.Visible;
            }
        }

        /// <summary>
        /// Creates UI elements for a duplicate folder group
        /// </summary>
        private void CreateDuplicateGroupUI(DuplicateFolderGroup group)
        {
            // Group container
            var groupBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(40, 100, 149, 237)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(100, 100, 149, 237)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Margin = new Thickness(0, 0, 0, 15),
                Padding = new Thickness(15)
            };

            var groupPanel = new StackPanel();

            // Group header
            var headerPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 10)
            };

            var folderIcon = new TextBlock
            {
                Text = "📁",
                FontSize = 16,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            var headerText = new TextBlock
            {
                Text = $"\"{group.FolderName}\" ({group.Count} duplicates)",
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            };

            headerPanel.Children.Add(folderIcon);
            headerPanel.Children.Add(headerText);
            groupPanel.Children.Add(headerPanel);

            // Folder list
            foreach (var folder in group.Folders)
            {
                CreateFolderItemUI(folder, groupPanel);
            }

            groupBorder.Child = groupPanel;
            DuplicateGroupsPanel.Children.Add(groupBorder);
        }

        /// <summary>
        /// Creates UI for an individual folder item
        /// </summary>
        private void CreateFolderItemUI(FolderInfo folder, StackPanel parent)
        {
            var itemPanel = new Grid
            {
                Margin = new Thickness(20, 5, 0, 5)
            };

            // Define columns
            itemPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            itemPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Path text with folder size
            var pathWithSize = GetFolderPathWithSize(folder);
            var pathText = new TextBlock
            {
                Text = pathWithSize,
                Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };

            Grid.SetColumn(pathText, 0);
            itemPanel.Children.Add(pathText);

            // Action buttons
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Show in Explorer button
            var explorerButton = new Button
            {
                Content = "📂",
                Width = 46,
                Height = 30,
                Margin = new Thickness(1, 1, 5, 1),
                ToolTip = "Show in Explorer",
                Style = FindResource("MahApps.Styles.Button.MetroSquare") as Style
            };
            explorerButton.Click += (s, e) => ShowFolderInExplorer(folder);

            // Delete button
            var deleteButton = new Button
            {
                Content = "🗑️",
                Width = 46,
                Height = 30,
                ToolTip = "Delete Folder",
                Style = FindResource("MahApps.Styles.Button.MetroSquare") as Style,
                Background = new SolidColorBrush(Color.FromArgb(100, 220, 20, 60)) // Semi-transparent red
            };
            deleteButton.Click += async (s, e) => await DeleteFolder(folder);

            buttonPanel.Children.Add(explorerButton);
            buttonPanel.Children.Add(deleteButton);

            Grid.SetColumn(buttonPanel, 1);
            itemPanel.Children.Add(buttonPanel);

            parent.Children.Add(itemPanel);
        }

        /// <summary>
        /// Gets folder path with size information
        /// </summary>
        private string GetFolderPathWithSize(FolderInfo folder)
        {
            try
            {
                var sizeInfo = GetFolderSizeInfo(folder.FolderPath);
                return $"{folder.FolderPath} ({sizeInfo})";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting folder size for {folder.FolderPath}: {ex.Message}");
                return $"{folder.FolderPath} (Size: Unknown)";
            }
        }

        /// <summary>
        /// Gets formatted folder size information
        /// </summary>
        private string GetFolderSizeInfo(string folderPath)
        {
            try
            {
                if (!Directory.Exists(folderPath))
                {
                    return "Folder not found";
                }

                long totalSize = 0;
                int fileCount = 0;
                int folderCount = 0;

                // Calculate folder size and counts
                var directoryInfo = new DirectoryInfo(folderPath);

                // Count files and calculate size
                var files = directoryInfo.GetFiles("*", System.IO.SearchOption.AllDirectories);
                fileCount = files.Length;
                totalSize = files.Sum(file => file.Length);

                // Count subdirectories
                folderCount = directoryInfo.GetDirectories("*", System.IO.SearchOption.AllDirectories).Length;

                // Format size
                string sizeText = FormatFileSize(totalSize);

                return $"{sizeText}, {fileCount} files, {folderCount} folders";
            }
            catch (UnauthorizedAccessException)
            {
                return "Access denied";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error calculating folder size: {ex.Message}");
                return "Size calculation failed";
            }
        }

        /// <summary>
        /// Formats file size in human readable format
        /// </summary>
        private string FormatFileSize(long bytes)
        {
            if (bytes == 0) return "0 B";

            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int suffixIndex = 0;
            double size = bytes;

            while (size >= 1024 && suffixIndex < suffixes.Length - 1)
            {
                size /= 1024;
                suffixIndex++;
            }

            return $"{size:F1} {suffixes[suffixIndex]}";
        }

        /// <summary>
        /// Shows the folder in Windows Explorer
        /// </summary>
        private void ShowFolderInExplorer(FolderInfo folder)
        {
            try
            {
                _mainViewModel.ShowInExplorer(folder);
                StatusText.Text = $"Opened folder in Explorer: {folder.Name}";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error opening Explorer: {ex.Message}";
            }
        }

        /// <summary>
        /// Deletes the specified folder after confirmation
        /// </summary>
        private async Task DeleteFolder(FolderInfo folder)
        {
            try
            {
                // Confirmation dialog
                var result = MessageBox.Show(
                    $"Are you sure you want to move the following folder to Recycle Bin?\n\n" +
                    $"Path: {folder.FolderPath}\n\n" +
                    "The folder will be moved to Recycle Bin and can be restored if needed.",
                    "Move Folder to Recycle Bin",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question,
                    MessageBoxResult.No);

                if (result != MessageBoxResult.Yes)
                {
                    return;
                }

                // Check if folder exists
                if (!Directory.Exists(folder.FolderPath))
                {
                    StatusText.Text = "Folder no longer exists";
                    MessageBox.Show("The folder no longer exists and may have already been deleted.",
                        "Folder Not Found", MessageBoxButton.OK, MessageBoxImage.Information);

                    // Force refresh the folder data and display
                    await RefreshFolderDataAndDisplay();
                    return;
                }

                StatusText.Text = $"Moving folder to Recycle Bin: {folder.Name}...";

                // Try to use MainViewModel's delete command first (if it supports recycle bin)
                bool deletedByMainViewModel = false;
                if (_mainViewModel.DeleteFolderCommand != null && _mainViewModel.DeleteFolderCommand.CanExecute(folder))
                {
                    try
                    {
                        await _mainViewModel.DeleteFolderCommand.ExecuteAsync(folder);
                        deletedByMainViewModel = true;
                        StatusText.Text = $"Successfully moved folder to Recycle Bin: {folder.Name}";
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"MainViewModel delete command failed: {ex.Message}");
                        // Continue to fallback method
                    }
                }

                // Fallback: Direct deletion to recycle bin using VisualBasic FileSystem
                if (!deletedByMainViewModel)
                {
                    try
                    {
                        FileSystem.DeleteDirectory(
                            folder.FolderPath,
                            UIOption.OnlyErrorDialogs,     // Show only error dialogs, not confirmation
                            RecycleOption.SendToRecycleBin, // Send to recycle bin instead of permanent delete
                            UICancelOption.DoNothing);      // Don't allow user to cancel the operation

                        StatusText.Text = $"Successfully moved folder to Recycle Bin: {folder.Name}";
                    }
                    catch (OperationCanceledException)
                    {
                        StatusText.Text = "Operation was cancelled by user";
                        return; // Don't refresh if user cancelled
                    }
                    catch (DirectoryNotFoundException)
                    {
                        StatusText.Text = "Folder not found";
                        MessageBox.Show(
                            $"The folder was not found. It may have already been deleted:\n{folder.FolderPath}",
                            "Folder Not Found",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        StatusText.Text = "Access denied - cannot delete folder";
                        MessageBox.Show(
                            $"Access denied. You don't have permission to delete this folder:\n{folder.FolderPath}\n\n" +
                            "Please run the application as administrator or check folder permissions.",
                            "Access Denied",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                        return; // Don't refresh if deletion failed
                    }
                    catch (IOException ex)
                    {
                        StatusText.Text = "Failed to delete folder - I/O error";
                        MessageBox.Show(
                            $"Failed to delete the folder due to an I/O error:\n{ex.Message}\n\n" +
                            "The folder may be in use by another application or contain files that are currently open.",
                            "Delete Failed",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                        return; // Don't refresh if deletion failed
                    }
                    catch (Exception ex)
                    {
                        StatusText.Text = $"Error moving folder to Recycle Bin: {ex.Message}";
                        System.Diagnostics.Debug.WriteLine($"Error deleting folder {folder.FolderPath}: {ex}");
                        MessageBox.Show(
                            $"An unexpected error occurred while moving the folder to Recycle Bin:\n{ex.Message}",
                            "Delete Error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                        return; // Don't refresh if deletion failed
                    }
                }

                // Force refresh the folder data and display after successful deletion
                await RefreshFolderDataAndDisplay();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Unexpected error: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"Unexpected error in DeleteFolder: {ex}");
                MessageBox.Show(
                    $"An unexpected error occurred:\n{ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Forces a refresh of folder data from MainViewModel and updates the display
        /// </summary>
        private async Task RefreshFolderDataAndDisplay()
        {
            try
            {
                StatusText.Text = "Refreshing folder data...";

                // Force MainViewModel to refresh its folder data
                await _mainViewModel.RefreshAllFoldersDataAsync();

                // Now refresh our display with the updated data
                await FindAndDisplayDuplicates();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error refreshing data: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"Error in RefreshFolderDataAndDisplay: {ex}");
            }
        }

        /// <summary>
        /// Updates the summary information
        /// </summary>
        private void UpdateSummary(int duplicateGroupCount, int totalFolderCount)
        {
            SummaryText.Text = $"Found {duplicateGroupCount} duplicate folder names";
            TotalFoldersText.Text = $"Total folders: {totalFolderCount}";
        }

        /// <summary>
        /// Shows message when no folders are loaded
        /// </summary>
        private void ShowNoFoldersMessage()
        {
            var messageText = new TextBlock
            {
                Text = "No folders are currently loaded.\nPlease set a root directory and wait for indexing to complete.",
                FontSize = 14,
                Foreground = Brushes.Orange,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(20)
            };

            DuplicateGroupsPanel.Children.Add(messageText);
            UpdateSummary(0, 0);
            StatusText.Text = "No folders loaded";
        }

        /// <summary>
        /// Shows message when no duplicates are found
        /// </summary>
        private void ShowNoDuplicatesMessage()
        {
            var messagePanel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(20)
            };

            var iconText = new TextBlock
            {
                Text = "✅",
                FontSize = 48,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 15)
            };

            var messageText = new TextBlock
            {
                Text = "No duplicate folder names found!",
                FontSize = 16,
                Foreground = Brushes.LightGreen,
                HorizontalAlignment = HorizontalAlignment.Center,
                FontWeight = FontWeights.SemiBold
            };

            var subText = new TextBlock
            {
                Text = "All folder names in your root directory are unique.",
                FontSize = 12,
                Foreground = Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 5, 0, 0)
            };

            messagePanel.Children.Add(iconText);
            messagePanel.Children.Add(messageText);
            messagePanel.Children.Add(subText);

            DuplicateGroupsPanel.Children.Add(messagePanel);
            StatusText.Text = "No duplicates found";
        }

        /// <summary>
        /// Handles filter settings button click
        /// </summary>
        private async void FilterSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var settingsDialog = new DuplicateFilterSettingsDialog
                {
                    Owner = this
                };

                if (settingsDialog.ShowDialog() == true)
                {
                    // Refresh the duplicate search with new filter settings
                    await FindAndDisplayDuplicates();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening filter settings: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Refreshes the duplicate search
        /// </summary>
        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            await FindAndDisplayDuplicates();
        }

        /// <summary>
        /// Closes the dialog
        /// </summary>
        private void Close_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}