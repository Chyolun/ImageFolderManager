using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ImageFolderManager.Models;
using ImageFolderManager.Services;
using ImageFolderManager.ViewModels;
using ImageFolderManager.Views;
using MahApps.Metro.Controls;

namespace ImageFolderManager
{
    public partial class MainWindow : MetroWindow
    {
        public MainViewModel ViewModel => DataContext as MainViewModel;

        public MainWindow()
        {
            InitializeComponent();

            // Create and set the MainViewModel
            var viewModel = new MainViewModel();
            DataContext = viewModel;

            Debug.WriteLine("MainWindow initialized");

            // Load default root directory if set
            LoadDefaultRootDirectoryAsync();
        }

        private async void LoadDefaultRootDirectoryAsync()
        {
            if (!string.IsNullOrEmpty(AppSettings.Instance.DefaultRootDirectory))
            {
                await ViewModel.LoadDirectoryAsync(AppSettings.Instance.DefaultRootDirectory);

                // Set the root directory in the FileExplorerView
                if (FileExplorerView != null)
                {
                    FileExplorerView.SetRootDirectory(AppSettings.Instance.DefaultRootDirectory);
                    FileExplorerView.SelectPath(AppSettings.Instance.DefaultRootDirectory);
                }
            }
        }

        // Modified to not load images automatically
        private void OnFolderSelected(FolderInfo folder)
        {
            Debug.WriteLine($"OnFolderSelected called with folder: {folder?.FolderPath}");

            if (ViewModel == null)
            {
                Debug.WriteLine("ERROR: ViewModel is null in OnFolderSelected");
                return;
            }

            // We don't auto-load images anymore - just update selection
            ViewModel.SetSelectedFolderWithoutLoading(folder);
        }

        // Handle selection changed in search results
        // Modified to not load images automatically
        private void SearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0 && e.AddedItems[0] is FolderInfo folder)
            {
                Debug.WriteLine($"SearchResults_SelectionChanged with folder: {folder.FolderPath}");

                // Update selection in FileExplorerView
                if (FileExplorerView != null)
                {
                    FileExplorerView.SelectPath(folder.FolderPath);
                }

                // Just update selection without loading images
                ViewModel.SetSelectedFolderWithoutLoading(folder);
            }
        }

        private void SearchResults_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var listBox = sender as ListBox;
            if (listBox == null) return;
            var folderInfo = listBox.SelectedItem as FolderInfo;
            if (folderInfo == null) return;

            _ = ViewModel.SetSelectedFolderAsync(folderInfo);

            // Update selection in FileExplorerView
            FileExplorerView?.SelectPath(folderInfo.FolderPath);

            e.Handled = true;
        }

        private async void ImportFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Check if ViewModel is available
                if (ViewModel != null)
                {
                    await ViewModel.ImportFolderAsync();
                }
                else
                {
                    MessageBox.Show("Could not perform import: ViewModel is not available.",
                        "Operation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error importing folder: {ex.Message}",
                    "Import Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        // Menu event handlers
        private async void RootDirectory_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Store old root directory
                string oldRootDir = AppSettings.Instance.DefaultRootDirectory;

                // Show folder browser dialog to select new root directory
                await ViewModel.SetDefaultRootDirectoryAsync();

                // If root directory changed
                string newRootDir = AppSettings.Instance.DefaultRootDirectory;
                if (!string.IsNullOrEmpty(newRootDir) &&
                    !string.Equals(oldRootDir, newRootDir, StringComparison.OrdinalIgnoreCase))
                {
                    // Check if directory exists
                    if (Directory.Exists(newRootDir))
                    {
                        Debug.WriteLine($"Changing root directory to: {newRootDir}");

                        // Update FileExplorerView with new root directory
                        if (FileExplorerView != null)
                        {
                            // Ensure UI updates happen on UI thread
                            Application.Current.Dispatcher.Invoke(() => {
                                FileExplorerView.SetRootDirectory(newRootDir);
                            });
                        }
                        else
                        {
                            Debug.WriteLine("FileExplorerView is null");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error setting root directory: {ex.Message}");
                MessageBox.Show($"Error setting root directory: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void PreviewSize_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Views.PreviewSizeDialog();
            dialog.Owner = this;
            dialog.ShowDialog();

            if (dialog.DialogResult)
            {
                await ViewModel.SetPreviewSize(
                    dialog.SelectedWidth,
                    dialog.SelectedHeight,
                    dialog.SelectedMaxCacheSize,
                    dialog.SelectedThreadCount);

                MessageBox.Show(
                    "Performance settings updated. Settings that affect thumbnails will clear the cache.",
                    "Settings Updated",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }

        // Handle image click - now make sure we're loading thumbnails when user 
        // interacts with the images panel
        private void Image_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Check if images are loaded; if not, load them
            if (ViewModel.Images.Count == 0 && ViewModel.SelectedFolder != null)
            {
                // If this is the first time user clicks in the images area, load images
                ViewModel.LoadImagesForSelectedFolderAsync();
                return;
            }

            // Regular image handling for double-click
            if (e.ClickCount == 2 && sender is Image img && img.Tag is string filePath)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Unable to open image: {ex.Message}");
                }
            }
        }

        private static T FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject parentObject = VisualTreeHelper.GetParent(child);
            if (parentObject == null) return null;
            T parent = parentObject as T;
            if (parent != null) return parent;
            return FindVisualParent<T>(parentObject);
        }

        private async void RefreshAll_Click(object sender, RoutedEventArgs e)
        {
            // Get the current selected folder before refresh
            string currentPath = ViewModel.SelectedFolder?.FolderPath;

            // Refresh all data from the file system
            await ViewModel.RefreshAllFoldersDataAsync();

            // Reselect the previously selected folder if it still exists
            if (!string.IsNullOrEmpty(currentPath) && Directory.Exists(currentPath))
            {
                FileExplorerView?.SelectPath(currentPath);
            }

            MessageBox.Show("All folder data has been refreshed.",
                "Refresh Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// Event handler for the "Collapse Parent Directory" menu item
        /// </summary>
        private void CollapseParentDirectory_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Execute the command if available
                if (ViewModel?.CollapseParentDirectoryCommand?.CanExecute(null) == true)
                {
                    // First call the ViewModel method (for status updates)
                    ViewModel.CollapseParentDirectoryCommand.Execute(null);

                    // With the FileExplorerView, we don't need the tree collapse functionality
                    // but we can navigate to the parent folder
                    if (ViewModel?.SelectedFolder != null)
                    {
                        string selectedPath = ViewModel.SelectedFolder.FolderPath;
                        string parentPath = Path.GetDirectoryName(selectedPath);

                        if (!string.IsNullOrEmpty(parentPath))
                        {
                            FileExplorerView?.SelectPath(parentPath);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error navigating to parent directory: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);

                Debug.WriteLine($"Error in CollapseParentDirectory_Click: {ex.Message}");
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            // Handle Ctrl+Z for undo
            if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                if (ViewModel != null && ViewModel.UndoFolderMovementCommand.CanExecute(null))
                {
                    ViewModel.UndoFolderMovementCommand.Execute(null);
                    e.Handled = true;
                }
            }
        }

        private void SearchResultListBox_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var listBox = sender as ListBox;
            if (listBox == null) return;

            var element = e.OriginalSource as FrameworkElement;
            if (element == null) return;

            var item = FindVisualParent<ListBoxItem>(element);
            if (item == null) return;

            var folderInfo = item.DataContext as FolderInfo;
            if (folderInfo == null) return;

            var contextMenu = new ContextMenu();

            var loadImagesItem = new MenuItem { Header = "Load Images" };
            loadImagesItem.Click += (s, args) =>
            {
                ViewModel.SetSelectedFolderAsync(folderInfo);
            };
            contextMenu.Items.Add(loadImagesItem);

            contextMenu.Items.Add(new Separator());

            var selectItem = new MenuItem { Header = "Select in Explorer" };
            selectItem.Click += (s, args) =>
            {
                // Select this folder in the file explorer
                if (FileExplorerView != null)
                {
                    FileExplorerView.SelectPath(folderInfo.FolderPath);
                }
            };
            contextMenu.Items.Add(selectItem);

            var showItem = new MenuItem { Header = "Show in Explorer" };
            showItem.Click += (s, args) =>
            {
                ViewModel.ShowInExplorer(folderInfo);
            };
            contextMenu.Items.Add(showItem);

            var deleteItem = new MenuItem { Header = "Delete" };
            deleteItem.Click += async (s, args) =>
            {
                await ViewModel.DeleteFolderAsync(folderInfo);
            };
            contextMenu.Items.Add(deleteItem);

            item.ContextMenu = contextMenu;
        }

        private void TagsCloud_Click(object sender, RoutedEventArgs e)
        {
            // Check if there is already an open TagCloudWindow
            foreach (Window window in Application.Current.Windows)
            {
                if (window is TagCloudWindow existingWindow)
                {
                    // If found, activate it and bring it to front
                    existingWindow.Activate();
                    existingWindow.Focus();
                    return;
                }
            }

            // Create the tag cloud window
            var tagCloudWindow = new TagCloudWindow(ViewModel.TagCloud, ViewModel);

            // Set owner but don't make it modal - use Show() instead of ShowDialog()
            tagCloudWindow.Owner = this;
            tagCloudWindow.Show();
        }
    }

    public class EnhancedTagCloudButton : Button
    {
        // Add custom properties for more control over appearance
        public double InitialFontSize { get; set; }
        public string TagText { get; set; }
        public int Count { get; set; }

        public EnhancedTagCloudButton()
        {
            // Apply advanced styling
            this.Background = Brushes.Transparent;
            this.Foreground = Brushes.White;
            this.BorderThickness = new Thickness(0);
            this.Padding = new Thickness(8, 4, 8, 4);
            this.Margin = new Thickness(3);
            this.Cursor = Cursors.Hand;

            // Set corner radius via template
            ControlTemplate template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "border";
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));

            var contentPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
            contentPresenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            contentPresenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);

            border.AppendChild(contentPresenter);
            template.VisualTree = border;

            // Add triggers for mouse over and pressed states
            var mouseOverTrigger = new Trigger { Property = IsMouseOverProperty, Value = true };
            mouseOverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(60, 100, 100, 240)), "border"));
            mouseOverTrigger.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1), "border"));
            template.Triggers.Add(mouseOverTrigger);

            var pressedTrigger = new Trigger { Property = IsPressedProperty, Value = true };
            pressedTrigger.Setters.Add(new Setter(RenderTransformProperty, new ScaleTransform(0.95, 0.95)));
            pressedTrigger.Setters.Add(new Setter(RenderTransformOriginProperty, new Point(0.5, 0.5)));
            template.Triggers.Add(pressedTrigger);

            this.Template = template;
        }
    }

    public class CountToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int count)
            {
                return count > 0 ? Visibility.Collapsed : Visibility.Visible;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class RatingToStarsDisplayConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
                return "☆☆☆☆☆";  // Default: all empty stars

            // Get the rating value (should be between 0-5)
            if (!int.TryParse(value.ToString(), out int rating))
                return "☆☆☆☆☆";

            // Ensure rating is within range
            rating = Math.Max(0, Math.Min(5, rating));

            // Build the star string efficiently
            StringBuilder stars = new StringBuilder(5);

            // Add filled stars
            for (int i = 0; i < rating; i++)
            {
                stars.Append('★');
            }

            // Add empty stars
            for (int i = rating; i < 5; i++)
            {
                stars.Append('☆');
            }

            return stars.ToString();
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converter that transforms a collection of tags into a formatted string
    /// with an optional fallback message when no tags are present
    /// </summary>
    public class TagsToStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
                return "No tags";

            var tags = value as System.Collections.ObjectModel.ObservableCollection<string>;
            if (tags == null || tags.Count == 0)
                return "No tags";

            // Format tags with # prefix
            return string.Join(" ", tags.Select(tag => $"#{tag}"));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converter that returns visibility based on whether tags exist
    /// </summary>
    public class HasTagsToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
                return System.Windows.Visibility.Collapsed;

            var tags = value as System.Collections.ObjectModel.ObservableCollection<string>;
            return (tags != null && tags.Count > 0)
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}