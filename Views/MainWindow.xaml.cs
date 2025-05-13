using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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

            // Monitor DataContext changes
            ShellTreeViewControl.DataContextChanged += (s, e) =>
            {
                Debug.WriteLine("ShellTreeViewControl DataContext changed");
                Debug.WriteLine($"Old value: {(e.OldValue != null ? e.OldValue.GetType().FullName : "null")}");
                Debug.WriteLine($"New value: {(e.NewValue != null ? e.NewValue.GetType().FullName : "null")}");
            };

            // Load default root directory if set
            LoadDefaultRootDirectoryAsync();
        }

        private async void LoadDefaultRootDirectoryAsync()
        {
            if (!string.IsNullOrEmpty(AppSettings.Instance.DefaultRootDirectory))
            {
                await ViewModel.LoadDirectoryAsync(AppSettings.Instance.DefaultRootDirectory);

                // Select the path in the shell tree view
                if (ShellTreeViewControl != null)
                {
                    ShellTreeViewControl.SelectPath(AppSettings.Instance.DefaultRootDirectory);
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

                // Select the item in the tree view
                if (ShellTreeViewControl != null)
                {
                    ShellTreeViewControl.SelectPath(folder.FolderPath);
                }

                // Just update selection without loading images
                ViewModel.SetSelectedFolderWithoutLoading(folder);
            }
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

                        // Change root directory in ShellTreeView
                        if (ShellTreeViewControl != null)
                        {
                            // Ensure UI updates happen on UI thread
                            Application.Current.Dispatcher.Invoke(() => {
                                ShellTreeViewControl.ChangeRootDirectory(newRootDir);
                            });
                        }
                        else
                        {
                            Debug.WriteLine("ShellTreeViewControl is null");
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

            // Also refresh the shell tree view
            if (ShellTreeViewControl != null)
            {
                // Refresh and restore selection if possible
                ShellTreeViewControl.RefreshTree();

                // Reselect the previously selected folder if it still exists
                if (!string.IsNullOrEmpty(currentPath) && Directory.Exists(currentPath))
                {
                    ShellTreeViewControl.SelectPath(currentPath);
                }
            }

            MessageBox.Show("All folder data has been refreshed.",
                "Refresh Complete", MessageBoxButton.OK, MessageBoxImage.Information);
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

            var selectItem = new MenuItem { Header = "Select in Tree" };
            selectItem.Click += (s, args) =>
            {
                // Select this folder in the shell tree
                if (ShellTreeViewControl != null)
                {
                    ShellTreeViewControl.SelectPath(folderInfo.FolderPath);
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
                await ViewModel.DeleteFolderCommand.ExecuteAsync(folderInfo);
                // Refresh the tree view after deletion
                if (ShellTreeViewControl != null)
                {
                    ShellTreeViewControl.RefreshTree();
                }
            };
            contextMenu.Items.Add(deleteItem);

            item.ContextMenu = contextMenu;
        }

    }
}