using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ImageFolderManager.Models;
using ImageFolderManager.ViewModels;
using Microsoft.WindowsAPICodePack.Dialogs;

namespace ImageFolderManager.Views
{
    /// <summary>
    /// A simplified file explorer that uses Windows native components
    /// </summary>
    public partial class FileExplorerView : UserControl, INotifyPropertyChanged
    {
        // Event to notify when a folder is selected
        public event Action<FolderInfo> FolderSelected;

        private MainViewModel ViewModel => DataContext as MainViewModel;
        private string _rootDirectory;
        private FolderInfo _selectedFolder;

        /// <summary>
        /// Currently selected folder
        /// </summary>
        public FolderInfo SelectedFolder
        {
            get => _selectedFolder;
            set
            {
                if (_selectedFolder != value)
                {
                    _selectedFolder = value;
                    OnPropertyChanged();

                    // Notify any listeners about the folder selection
                    FolderSelected?.Invoke(_selectedFolder);
                }
            }
        }

        public FileExplorerView()
        {
            InitializeComponent();
            DataContext = this;
        }

        // Initialize component manually for this example
        private void InitializeComponent()
        {
            // Create a simple panel with buttons for folder operations
            var grid = new Grid();

            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Add title
            var titleBlock = new TextBlock
            {
                Text = "Folder Navigation",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(5),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White)
            };
            Grid.SetRow(titleBlock, 0);
            grid.Children.Add(titleBlock);

            // Create a StackPanel for the buttons
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(5)
            };
            Grid.SetRow(buttonPanel, 1);

            // Add buttons for directory operations
            var browseButton = new Button
            {
                Content = "Browse Folders...",
                Margin = new Thickness(0, 5, 0, 5),
                Padding = new Thickness(10, 5, 10, 5)
            };
            browseButton.Click += BrowseButton_Click;
            buttonPanel.Children.Add(browseButton);

            var openParentButton = new Button
            {
                Content = "Open Parent Directory",
                Margin = new Thickness(0, 5, 0, 5),
                Padding = new Thickness(10, 5, 10, 5)
            };
            openParentButton.Click += OpenParentButton_Click;
            buttonPanel.Children.Add(openParentButton);

            var createFolderButton = new Button
            {
                Content = "Create New Folder...",
                Margin = new Thickness(0, 5, 0, 5),
                Padding = new Thickness(10, 5, 10, 5)
            };
            createFolderButton.Click += CreateFolderButton_Click;
            buttonPanel.Children.Add(createFolderButton);

            var deleteButton = new Button
            {
                Content = "Delete Selected Folder",
                Margin = new Thickness(0, 5, 0, 5),
                Padding = new Thickness(10, 5, 10, 5)
            };
            deleteButton.Click += DeleteButton_Click;
            buttonPanel.Children.Add(deleteButton);

            var renameButton = new Button
            {
                Content = "Rename Selected Folder",
                Margin = new Thickness(0, 5, 0, 5),
                Padding = new Thickness(10, 5, 10, 5)
            };
            renameButton.Click += RenameButton_Click;
            buttonPanel.Children.Add(renameButton);

            var exploreButton = new Button
            {
                Content = "Show in Explorer",
                Margin = new Thickness(0, 5, 0, 5),
                Padding = new Thickness(10, 5, 10, 5)
            };
            exploreButton.Click += ExploreButton_Click;
            buttonPanel.Children.Add(exploreButton);

            // Add path display
            var pathDisplayBlock = new TextBlock
            {
                Text = "Selected Path:",
                Margin = new Thickness(0, 15, 0, 5),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White)
            };
            buttonPanel.Children.Add(pathDisplayBlock);

            var selectedPathBlock = new TextBlock
            {
                Margin = new Thickness(0, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.LightGreen)
            };
            selectedPathBlock.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("SelectedFolder.FolderPath")
            {
                Source = this,
                TargetNullValue = "No folder selected"
            });
            buttonPanel.Children.Add(selectedPathBlock);

            // Add a separator
            var separator = new Separator
            {
                Margin = new Thickness(0, 10, 0, 10),
            };
            buttonPanel.Children.Add(separator);

            // Add current root directory display
            var rootDirBlock = new TextBlock
            {
                Text = "Root Directory:",
                Margin = new Thickness(0, 5, 0, 5),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White)
            };
            buttonPanel.Children.Add(rootDirBlock);

            var rootPathBlock = new TextBlock
            {
                Margin = new Thickness(0, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.LightBlue)
            };
            rootPathBlock.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("RootDirectory")
            {
                Source = this,
                TargetNullValue = "No root directory set"
            });
            buttonPanel.Children.Add(rootPathBlock);

            var setRootButton = new Button
            {
                Content = "Set Root Directory...",
                Margin = new Thickness(0, 5, 0, 5),
                Padding = new Thickness(10, 5, 10, 5)
            };
            setRootButton.Click += SetRootButton_Click;
            buttonPanel.Children.Add(setRootButton);

            grid.Children.Add(buttonPanel);

            // Set the content of the UserControl
            this.Content = grid;
        }

        /// <summary>
        /// Gets or sets the root directory used for browsing
        /// </summary>
        public string RootDirectory
        {
            get => _rootDirectory;
            set
            {
                if (_rootDirectory != value)
                {
                    _rootDirectory = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// Opens the folder browser dialog to select a folder
        /// </summary>
        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Use the Common File Dialog from Windows API Code Pack for a modern folder picker
                var dialog = new CommonOpenFileDialog
                {
                    Title = "Select Folder",
                    IsFolderPicker = true,
                    InitialDirectory = SelectedFolder?.FolderPath ?? RootDirectory,
                    AddToMostRecentlyUsedList = true,
                    AllowNonFileSystemItems = false,
                    DefaultDirectory = SelectedFolder?.FolderPath ?? RootDirectory,
                    EnsureFileExists = true,
                    EnsurePathExists = true,
                    EnsureReadOnly = false,
                    EnsureValidNames = true,
                    Multiselect = false,
                    ShowPlacesList = true
                };

                if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    string selectedPath = dialog.FileName;

                    // Check if path is within root directory if a root is set
                    if (!string.IsNullOrEmpty(RootDirectory) &&
                        !selectedPath.StartsWith(RootDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        MessageBox.Show("The selected folder must be within the root directory.",
                            "Invalid Selection", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    // Create FolderInfo for the selected path
                    var folderInfo = new FolderInfo(selectedPath);

                    // Update the selected folder
                    SelectedFolder = folderInfo;

                    // If using the MainViewModel, tell it to load the selected folder
                    if (ViewModel != null)
                    {
                        ViewModel.SetSelectedFolderAsync(folderInfo);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error browsing folders: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Opens the parent directory of the currently selected folder
        /// </summary>
        private void OpenParentButton_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedFolder == null || string.IsNullOrEmpty(SelectedFolder.FolderPath))
            {
                MessageBox.Show("No folder is currently selected.",
                    "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                string parentPath = Path.GetDirectoryName(SelectedFolder.FolderPath);

                // Check if parent path is valid and exists
                if (string.IsNullOrEmpty(parentPath) || !Directory.Exists(parentPath))
                {
                    MessageBox.Show("The parent directory does not exist or cannot be accessed.",
                        "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Check if path is within root directory if a root is set
                if (!string.IsNullOrEmpty(RootDirectory) &&
                    !parentPath.StartsWith(RootDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("Cannot navigate above the root directory.",
                        "Navigation Restricted", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Create FolderInfo for the parent path
                var parentFolder = new FolderInfo(parentPath);

                // Update the selected folder
                SelectedFolder = parentFolder;

                // If using the MainViewModel, tell it to load the parent folder
                if (ViewModel != null)
                {
                    ViewModel.SetSelectedFolderAsync(parentFolder);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error navigating to parent directory: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Opens the selected folder in Windows Explorer
        /// </summary>
        private void ExploreButton_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedFolder == null || string.IsNullOrEmpty(SelectedFolder.FolderPath))
            {
                MessageBox.Show("No folder is currently selected.",
                    "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                // Check if directory exists
                if (!Directory.Exists(SelectedFolder.FolderPath))
                {
                    MessageBox.Show("The selected directory does not exist.",
                        "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Open in Windows Explorer
                Process.Start("explorer.exe", SelectedFolder.FolderPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening Explorer: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Creates a new folder within the selected folder
        /// </summary>
        private void CreateFolderButton_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedFolder == null || string.IsNullOrEmpty(SelectedFolder.FolderPath))
            {
                MessageBox.Show("No parent folder is selected to create a new folder.",
                    "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                // Check if directory exists
                if (!Directory.Exists(SelectedFolder.FolderPath))
                {
                    MessageBox.Show("The selected directory does not exist.",
                        "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Prompt for folder name
                string folderName = Microsoft.VisualBasic.Interaction.InputBox(
                    "Enter name for new folder:",
                    "Create New Folder",
                    "New Folder");

                if (string.IsNullOrWhiteSpace(folderName))
                    return;  // User cancelled

                // Check for invalid characters
                if (folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    MessageBox.Show("The folder name contains invalid characters.",
                        "Invalid Name", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Create new path
                string newFolderPath = Path.Combine(SelectedFolder.FolderPath, folderName);

                // Check if already exists
                if (Directory.Exists(newFolderPath))
                {
                    MessageBox.Show($"A folder named '{folderName}' already exists.",
                        "Folder Exists", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Create the directory
                Directory.CreateDirectory(newFolderPath);

                // Create FolderInfo for the new folder
                var newFolder = new FolderInfo(newFolderPath);

                // If using MainViewModel, notify about folder creation
                if (ViewModel != null)
                {
                    // Refresh folder list
                    ViewModel.RefreshAllFoldersDataAsync();
                }

                MessageBox.Show($"Folder '{folderName}' created successfully.",
                    "Folder Created", MessageBoxButton.OK, MessageBoxImage.Information);

                // Select the new folder
                SelectedFolder = newFolder;
                if (ViewModel != null)
                {
                    ViewModel.SetSelectedFolderAsync(newFolder);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error creating folder: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Deletes the selected folder
        /// </summary>
        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedFolder == null || string.IsNullOrEmpty(SelectedFolder.FolderPath))
            {
                MessageBox.Show("No folder is selected to delete.",
                    "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                // Confirm deletion
                var result = MessageBox.Show(
                    $"Are you sure you want to delete folder '{SelectedFolder.Name}'?\nThis will move it to the Recycle Bin.",
                    "Confirm Delete",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                    return;

                // Check if it's the root directory
                if (!string.IsNullOrEmpty(RootDirectory) &&
                    SelectedFolder.FolderPath.Equals(RootDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("Cannot delete the root directory.",
                        "Delete Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Remember parent path for selection after deletion
                string parentPath = Path.GetDirectoryName(SelectedFolder.FolderPath);

                // Check if directory exists
                if (!Directory.Exists(SelectedFolder.FolderPath))
                {
                    MessageBox.Show("The selected directory does not exist.",
                        "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Delete using FileSystem to send to recycle bin
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                    SelectedFolder.FolderPath,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

                // If using MainViewModel, notify about deletion
                if (ViewModel != null)
                {
                    ViewModel.RefreshAllFoldersDataAsync();
                }

                // Select parent folder
                if (!string.IsNullOrEmpty(parentPath) && Directory.Exists(parentPath))
                {
                    var parentFolder = new FolderInfo(parentPath);
                    SelectedFolder = parentFolder;

                    if (ViewModel != null)
                    {
                        ViewModel.SetSelectedFolderAsync(parentFolder);
                    }
                }
                else
                {
                    SelectedFolder = null;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error deleting folder: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Renames the selected folder
        /// </summary>
        private void RenameButton_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedFolder == null || string.IsNullOrEmpty(SelectedFolder.FolderPath))
            {
                MessageBox.Show("No folder is selected to rename.",
                    "Information", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                // Check if it's the root directory
                if (!string.IsNullOrEmpty(RootDirectory) &&
                    SelectedFolder.FolderPath.Equals(RootDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("Cannot rename the root directory.",
                        "Rename Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Prompt for new name
                string currentName = SelectedFolder.Name;
                string newName = Microsoft.VisualBasic.Interaction.InputBox(
                    "Enter new name for the folder:",
                    "Rename Folder",
                    currentName);

                if (string.IsNullOrWhiteSpace(newName) || newName == currentName)
                    return;  // User cancelled or didn't change the name

                // Check for invalid characters
                if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    MessageBox.Show("The folder name contains invalid characters.",
                        "Invalid Name", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Get parent directory
                string parentPath = Path.GetDirectoryName(SelectedFolder.FolderPath);
                if (string.IsNullOrEmpty(parentPath))
                {
                    MessageBox.Show("Cannot determine parent directory.",
                        "Rename Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Create new path
                string newPath = Path.Combine(parentPath, newName);

                // Check if already exists
                if (Directory.Exists(newPath))
                {
                    MessageBox.Show($"A folder named '{newName}' already exists in this location.",
                        "Folder Exists", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Rename the directory
                Directory.Move(SelectedFolder.FolderPath, newPath);

                // Create FolderInfo for the renamed folder
                var renamedFolder = new FolderInfo(newPath);

                // If using MainViewModel, notify about rename
                if (ViewModel != null)
                {
                    ViewModel.RefreshAllFoldersDataAsync();
                }

                // Select the renamed folder
                SelectedFolder = renamedFolder;
                if (ViewModel != null)
                {
                    ViewModel.SetSelectedFolderAsync(renamedFolder);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error renaming folder: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Sets the root directory for browsing
        /// </summary>
        private void SetRootButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Use the Common File Dialog to select a folder
                var dialog = new CommonOpenFileDialog
                {
                    Title = "Select Root Directory",
                    IsFolderPicker = true,
                    InitialDirectory = RootDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    AddToMostRecentlyUsedList = true,
                    AllowNonFileSystemItems = false,
                    EnsureFileExists = true,
                    EnsurePathExists = true,
                    EnsureReadOnly = false,
                    EnsureValidNames = true,
                    Multiselect = false,
                    ShowPlacesList = true
                };

                if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
                {
                    string selectedPath = dialog.FileName;

                    // Update root directory
                    RootDirectory = selectedPath;

                    // If using the MainViewModel, update its root directory
                    if (ViewModel != null)
                    {
                        ViewModel.LoadDirectoryAsync(RootDirectory);
                    }

                    // Update the selected folder to the root
                    var rootFolder = new FolderInfo(RootDirectory);
                    SelectedFolder = rootFolder;

                    // If using the MainViewModel, tell it to load the root folder
                    if (ViewModel != null)
                    {
                        ViewModel.SetSelectedFolderAsync(rootFolder);
                    }

                    // Update AppSettings
                    ImageFolderManager.Services.AppSettings.Instance.DefaultRootDirectory = RootDirectory;
                    ImageFolderManager.Services.AppSettings.Instance.Save();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error setting root directory: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Updates the root directory from outside the control
        /// </summary>
        public void SetRootDirectory(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                return;

            RootDirectory = path;

            // Create a folder info for the root
            var rootFolder = new FolderInfo(path);

            // If there's no selection, select the root
            if (SelectedFolder == null)
            {
                SelectedFolder = rootFolder;
            }
        }

        /// <summary>
        /// Selects a specific path
        /// </summary>
        public void SelectPath(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                return;

            // Check if path is within root directory if root is set
            if (!string.IsNullOrEmpty(RootDirectory) &&
                !path.StartsWith(RootDirectory, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("The selected path must be within the root directory.",
                    "Selection Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Create FolderInfo for the path
            var folder = new FolderInfo(path);

            // Update the selected folder
            SelectedFolder = folder;

            // If using the MainViewModel, tell it to load the selected folder
            if (ViewModel != null)
            {
                ViewModel.SetSelectedFolderAsync(folder);
            }
        }

        /// <summary>
        /// Property changed event implementation
        /// </summary>
        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}