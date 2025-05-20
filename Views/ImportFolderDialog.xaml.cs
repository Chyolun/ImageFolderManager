using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using ImageFolderManager.Models;
using ImageFolderManager.Services;
using MahApps.Metro.Controls;

namespace ImageFolderManager.Views
{
    public partial class ImportFolderDialog : MetroWindow
    {
        private List<string> _sourceFolderPaths;
        private readonly string _rootDirectoryPath;
        private readonly List<FolderInfo> _allLoadedFolders;
        private string _detectedAuthor;

        public string DestinationPath { get; private set; }
        public bool DialogConfirmed { get; private set; } = false;

        public ImportFolderDialog(List<string> sourceFolderPaths, string rootDirectoryPath, List<FolderInfo> allLoadedFolders)
        {
            InitializeComponent();

            _sourceFolderPaths = sourceFolderPaths;
            _rootDirectoryPath = rootDirectoryPath;
            _allLoadedFolders = allLoadedFolders;

            // Show source folder(s) in the text box
            UpdateSourceFolderDisplay();

            // Update header based on folder count
            if (_sourceFolderPaths.Count > 1)
            {
                HeaderText.Text = $"Import {_sourceFolderPaths.Count} Folders";
            }

            // Extract author and analyze
            AnalyzeFolderName();

            // Set initial destination path
            RecommendDestinationPath();
        }

        private void UpdateSourceFolderDisplay()
        {
            if (_sourceFolderPaths.Count == 1)
            {
                SourceFolderText.Text = _sourceFolderPaths[0];
            }
            else if (_sourceFolderPaths.Count > 1)
            {
                // Show the first folder and indicate there are more
                string firstFolder = _sourceFolderPaths[0];
                SourceFolderText.Text = $"{firstFolder} (and {_sourceFolderPaths.Count - 1} more...)";
            }
        }

        private void AnalyzeFolderName()
        {
            // For multiple folders, analyze the first one for author detection
            string folderName = Path.GetFileName(_sourceFolderPaths[0]);
            _detectedAuthor = ExtractAuthorFromFolderName(folderName);

            // Set the author in the textbox for editing
            AuthorTextBox.Text = _detectedAuthor;
        }

        private string ExtractAuthorFromFolderName(string folderName)
        {
            // Try to extract author from square brackets [Author]
            var bracketMatch = Regex.Match(folderName, @"\[(.*?)\]");
            if (bracketMatch.Success)
            {
                return bracketMatch.Groups[1].Value.Trim();
            }

            // Try to extract author from the part before a dash
            // Check for common patterns: "Author - Title" or similar
            var dashMatch = Regex.Match(folderName, @"^(.*?)\s*-");
            if (dashMatch.Success)
            {
                return dashMatch.Groups[1].Value.Trim();
            }

            // If no patterns matched, return empty string
            return string.Empty;
        }

        private void RecommendDestinationPath()
        {
            string folderName = Path.GetFileName(_sourceFolderPaths[0]);
            string recommendedPath = _rootDirectoryPath;
            string author = AuthorTextBox.Text.Trim();

            // Only try to find an author-based path if the author field is not empty
            if (!string.IsNullOrEmpty(author))
            {
                // Find folders at the top level that contain the author name
                var topLevelFolders = _allLoadedFolders
                    .Where(f => {
                        // Get folder name
                        string folderNameOnly = Path.GetFileName(f.FolderPath);

                        // Check if this is a direct child of the root directory
                        string parentPath = Directory.GetParent(f.FolderPath)?.FullName;
                        bool isTopLevel = parentPath != null &&
                                         PathService.PathsEqual(parentPath, _rootDirectoryPath);

                        // Check if folder name contains the author
                        bool containsAuthor = folderNameOnly.IndexOf(author, StringComparison.OrdinalIgnoreCase) >= 0;

                        return isTopLevel && containsAuthor;
                    })
                    .OrderBy(f => f.FolderPath.Length) // Prefer shorter paths
                    .ToList();

                if (topLevelFolders.Count > 0)
                {
                    // Use the first matching top-level folder as destination
                    recommendedPath = topLevelFolders.First().FolderPath;

                    // Update status text with info about the match
                    StatusText.Text = $"Found author folder: {Path.GetFileName(recommendedPath)}";
                }
                else
                {
                    // If no existing author folder, suggest creating one
                    string authorFolderName = $"[{author}]";
                    recommendedPath = Path.Combine(_rootDirectoryPath, authorFolderName);

                    // Check if this directory already exists
                    if (!Directory.Exists(recommendedPath))
                    {
                        StatusText.Text = $"No existing author folder found. A new folder '{authorFolderName}' will be created.";
                    }
                }
            }

            // For multiple folders, we just use the destination directory without folder name
            if (_sourceFolderPaths.Count > 1)
            {
                DestinationPathTextBox.Text = recommendedPath;
            }
            else
            {
                // For single folder import, include the folder name in the path
                string finalPath = Path.Combine(recommendedPath, folderName);

                // Check if the destination already exists, if so, create a unique name
                if (Directory.Exists(finalPath))
                {
                    finalPath = PathService.GetUniqueDirectoryPath(recommendedPath, folderName);
                    StatusText.Text += " A folder with the same name already exists, a unique name will be created.";
                }

                DestinationPathTextBox.Text = finalPath;
            }
        }

        private void AuthorTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // When author is changed by user, update the recommended path
            RecommendDestinationPath();
        }

        private void ExploreButton_Click(object sender, RoutedEventArgs e)
        {
            // Show folder browser dialog to select destination
            var dialog = new FolderBrowserDialog
            {
                Description = "Select destination folder",
                SelectedPath = DestinationPathTextBox.Text
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                string selectedPath = dialog.SelectedPath;

                // Ensure the selected path is within the root directory
                if (!PathService.IsPathWithin(_rootDirectoryPath, selectedPath))
                {
                    System.Windows.MessageBox.Show(
                        "Please select a folder within the root directory.",
                        "Invalid Selection",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                // For single folder, append the folder name
                if (_sourceFolderPaths.Count == 1)
                {
                    string folderName = Path.GetFileName(_sourceFolderPaths[0]);
                    string finalPath = Path.Combine(selectedPath, folderName);

                    // Check for uniqueness
                    if (Directory.Exists(finalPath))
                    {
                        finalPath = PathService.GetUniqueDirectoryPath(selectedPath, folderName);
                    }

                    DestinationPathTextBox.Text = finalPath;
                }
                else
                {
                    // For multiple folders, just use the directory path
                    DestinationPathTextBox.Text = selectedPath;
                }

                StatusText.Text = "Custom destination selected.";
            }
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            string destinationPath = DestinationPathTextBox.Text?.Trim();

            if (string.IsNullOrEmpty(destinationPath))
            {
                System.Windows.MessageBox.Show("Please specify a destination path.", "Missing Information",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // For multiple folders, ensure destinationPath is just a directory
            if (_sourceFolderPaths.Count > 1)
            {
                // Verify the path exists or can be created
                try
                {
                    if (!Directory.Exists(destinationPath))
                    {
                        Directory.CreateDirectory(destinationPath);
                    }
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Cannot create destination directory: {ex.Message}",
                        "Invalid Destination", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            else
            {
                // For single folder import, check source and destination

                // Check if source and destination are the same
                if (PathService.PathsEqual(_sourceFolderPaths[0], destinationPath))
                {
                    System.Windows.MessageBox.Show("Source and destination folders are the same.", "Cannot Import",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Check if trying to import into itself or its subfolder
                if (PathService.IsPathWithin(_sourceFolderPaths[0], destinationPath))
                {
                    System.Windows.MessageBox.Show("Cannot import a folder into itself or its subfolder.",
                        "Invalid Destination", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Ensure destination parent directory exists
                string parentDirectory = Path.GetDirectoryName(destinationPath);
                if (!Directory.Exists(parentDirectory))
                {
                    // Ask if we should create the parent directory
                    var result = System.Windows.MessageBox.Show(
                        $"Destination directory '{parentDirectory}' does not exist. Create it?",
                        "Create Directory", MessageBoxButton.YesNo, MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        try
                        {
                            Directory.CreateDirectory(parentDirectory);
                        }
                        catch (Exception ex)
                        {
                            System.Windows.MessageBox.Show($"Failed to create directory: {ex.Message}",
                                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            return;
                        }
                    }
                    else
                    {
                        return;
                    }
                }
            }

            // Set result and close dialog
            DestinationPath = destinationPath;
            DialogConfirmed = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogConfirmed = false;
            Close();
        }
    }
}