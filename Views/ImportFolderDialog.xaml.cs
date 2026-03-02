using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Forms;
using ImageFolderManager.Models;
using ImageFolderManager.Services;
using MahApps.Metro.Controls;
using Microsoft.WindowsAPICodePack.Dialogs;

namespace ImageFolderManager.Views
{
    public partial class ImportFolderDialog : MetroWindow
    {
        private List<string> _sourceFolderPaths;
        private readonly string _rootDirectoryPath;
        private readonly List<FolderInfo> _allLoadedFolders;
        private string _detectedAuthor;
        private readonly FolderTagService _folderTagService;

        public string DestinationPath { get; private set; }
        public bool DialogConfirmed { get; private set; } = false;

        // <summary>
        /// Tracks whether the current author text was manually entered by the user
        /// or auto-detected from the folder name.
        /// </summary>
        private bool _isAuthorManuallyEdited = false;

        // Property for tags input text binding
        private string _tagInputText = string.Empty;
        public string TagInputText
        {
            get => _tagInputText;
            set
            {
                _tagInputText = value;
                // Simple property change notification for TextBox binding
                TagsTextBox.Text = value;
            }
        }

        /// <summary>
        /// Enhanced constructor for ImportFolderDialog with skip information display and height management
        /// </summary>
        /// <param name="sourceFolderPaths">Valid folder paths to import (already filtered)</param>
        /// <param name="rootDirectoryPath">Root directory path</param>
        /// <param name="allLoadedFolders">All currently loaded folders</param>
        /// <param name="skippedFolders">Optional dictionary of skipped folder names with detailed reasons including full paths</param>
        public ImportFolderDialog(
            List<string> sourceFolderPaths,
            string rootDirectoryPath,
            List<FolderInfo> allLoadedFolders,
            FolderTagService folderTagService,
            Dictionary<string, string> skippedFolders = null)
        {
            InitializeComponent();

            // Subscribe to the Loaded event for proper initialization
            this.Loaded += ImportFolderDialog_Loaded;

            _sourceFolderPaths = sourceFolderPaths;
            _rootDirectoryPath = rootDirectoryPath;
            _allLoadedFolders = allLoadedFolders;
            _folderTagService = folderTagService ?? throw new ArgumentNullException(nameof(folderTagService));

            // Show source folder(s) in the text box
            UpdateSourceFolderDisplay();

            // Update header based on folder count and show skip information if any
            UpdateHeaderWithSkipInfo(skippedFolders);

            // Extract author and analyze
            AnalyzeFolderName();

            // Set initial destination path
            RecommendDestinationPath();

            // Set up tags text box event handler
            TagsTextBox.TextChanged += TagsTextBox_TextChanged; 

            // Ensure proper initial height calculation
            this.UpdateLayout();
        }

        private void TagsTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _tagInputText = TagsTextBox.Text;
        } 

        /// <summary>
        /// Handle window loaded event for proper sizing
        /// </summary>
        private void ImportFolderDialog_Loaded(object sender, RoutedEventArgs e)
        {
            // Ensure the window size is calculated correctly after all content is loaded
            this.UpdateLayout();
            this.InvalidateMeasure();
        }

        /// <summary>
        /// Updates the header text to include information about skipped folders
        /// </summary>
        /// <param name="skippedFolders">Dictionary of skipped folder names and their skip reasons (with full paths for duplicates)</param>
        private void UpdateHeaderWithSkipInfo(Dictionary<string, string> skippedFolders)
        {
            var headerText = _sourceFolderPaths.Count > 1
                ? $"Import {_sourceFolderPaths.Count} Folders"
                : "Import Folder";

            if (skippedFolders != null && skippedFolders.Count > 0)
            {
                headerText += $" ({skippedFolders.Count} skipped)";

                // Create detailed skip information with proper formatting for paths
                var skipInfoLines = new List<string>();
                foreach (var kvp in skippedFolders)
                {
                    var folderName = kvp.Key;
                    var reason = kvp.Value;

                    // Format the line with folder name and reason
                    if (reason.StartsWith("Duplicate name - existing folder:"))
                    {
                        var existingPath = reason.Substring("Duplicate name - existing folder:".Length).Trim();
                        skipInfoLines.Add($"  └─ Existing: {existingPath}");
                    }
                    else
                    {
                        skipInfoLines.Add($"• {folderName}: {reason}");
                    }
                }

                var skipInfo = string.Join("\n", skipInfoLines);

                // Create or update a text block to show skip information
                if (SkipInfoPanel != null) // Assuming you add this panel to the XAML
                {
                    SkipInfoText.Text = $"Skipped folders:\n{skipInfo}";
                    SkipInfoPanel.Visibility = Visibility.Visible;

                    // Trigger layout update to ensure proper sizing
                    this.UpdateLayout();

                    // Force height recalculation
                    RecalculateWindowHeight();
                }
            }
            else if (SkipInfoPanel != null)
            {
                SkipInfoPanel.Visibility = Visibility.Collapsed;
            }

            HeaderText.Text = headerText;
        }

        /// <summary>
        /// Enhanced method to update source folder display with skip information
        /// </summary>
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
                SourceFolderText.Text = $"{firstFolder} (and {_sourceFolderPaths.Count - 1} more valid folders...)";
            }
        }


        /// <summary>
        /// Method to manually trigger height recalculation when content changes
        /// </summary>
        private void RecalculateWindowHeight()
        {
            // Force the window to recalculate its size
            this.SizeToContent = SizeToContent.Manual;
            this.UpdateLayout();
            this.SizeToContent = SizeToContent.Height;

            // Ensure minimum and maximum height constraints are respected
            if (this.ActualHeight < this.MinHeight)
            {
                this.Height = this.MinHeight;
            }
            else if (this.ActualHeight > this.MaxHeight)
            {
                this.Height = this.MaxHeight;
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

        /// <summary>
        /// Searches for an existing author folder in the root directory.
        /// When the user manually types an author name "xxx", this method first looks
        /// for a folder named "[xxx]" (bracket format) at the root level.
        /// Falls back to a broader substring search for auto-detected authors.
        /// </summary>
        /// <param name="author">The author name to search for</param>
        /// <param name="isManualInput">
        /// True if the author was manually typed by the user (searches strictly for "[author]" bracket folder);
        /// false if it was auto-detected from the folder name (uses broader substring search).
        /// </param>
        /// <returns>The matched folder path, or null if not found</returns>
        private string FindAuthorFolder(string author, bool isManualInput)
        {
            if (string.IsNullOrEmpty(author))
                return null;

            if (isManualInput)
            {
                string bracketFolderName = $"[{author}]";

                // Search all loaded folders under root at any depth, match by folder name
                var match = _allLoadedFolders.FirstOrDefault(f =>
                {
                    // Must be somewhere within the root directory (any depth)
                    bool isUnderRoot = PathService.IsPathWithin(_rootDirectoryPath, f.FolderPath);
                    // Folder name must exactly match [author] (case-insensitive)
                    bool nameMatches = string.Equals(
                        Path.GetFileName(f.FolderPath),
                        bracketFolderName,
                        StringComparison.OrdinalIgnoreCase);
                    return isUnderRoot && nameMatches;
                });

                if (match != null)
                    return match.FolderPath;

                // Fallback: check filesystem directly at root level
                // (covers the case where the folder exists but hasn't been loaded yet)
                string directPath = Path.Combine(_rootDirectoryPath, bracketFolderName);
                if (Directory.Exists(directPath))
                    return directPath;

                return null;
            }
            else
            {
                // For auto-detected authors, use the original broader substring search
                var topLevelFolders = _allLoadedFolders
                    .Where(f =>
                    {
                        string folderNameOnly = Path.GetFileName(f.FolderPath);
                        string parentPath = Directory.GetParent(f.FolderPath)?.FullName;
                        bool isTopLevel = parentPath != null &&
                                         PathService.PathsEqual(parentPath, _rootDirectoryPath);
                        bool containsAuthor = folderNameOnly.IndexOf(author, StringComparison.OrdinalIgnoreCase) >= 0;
                        return isTopLevel && containsAuthor;
                    })
                    .OrderBy(f => f.FolderPath.Length) // Prefer shorter (more specific) paths
                    .ToList();

                return topLevelFolders.FirstOrDefault()?.FolderPath;
            }
        }

        private void RecommendDestinationPath()
        {
            string folderName = Path.GetFileName(_sourceFolderPaths[0]);
            string recommendedPath = _rootDirectoryPath;
            string author = AuthorTextBox.Text.Trim();

            if (!string.IsNullOrEmpty(author))
            {
                string existingFolder = FindAuthorFolder(author, _isAuthorManuallyEdited);

                if (existingFolder != null)
                {
                    recommendedPath = existingFolder;
                    StatusText.Text = $"Found author folder: {Path.GetFileName(recommendedPath)}";
                }
                else
                {
                    // No existing folder found — suggest creating a bracketed one
                    string authorFolderName = _isAuthorManuallyEdited
                        ? $"[{author}]"                 // Manual input always uses [author] format
                        : $"[{author}]";                // Auto-detected also defaults to bracket format
                    recommendedPath = Path.Combine(_rootDirectoryPath, authorFolderName);

                    StatusText.Text = Directory.Exists(recommendedPath)
                        ? $"Found author folder: {authorFolderName}"
                        : $"No existing author folder found. '{authorFolderName}' will be created automatically.";
                }
            }

            // For multiple folders, use the destination directory without appending a folder name
            if (_sourceFolderPaths.Count > 1)
            {
                DestinationPathTextBox.Text = recommendedPath;
            }
            else
            {
                // For single folder import, append the source folder name to the destination
                string finalPath = Path.Combine(recommendedPath, folderName);

                // If destination already exists, generate a unique name
                if (Directory.Exists(finalPath))
                {
                    finalPath = PathService.GetUniqueDirectoryPath(recommendedPath, folderName);
                    StatusText.Text += " A folder with the same name already exists; a unique name will be used.";
                }

                DestinationPathTextBox.Text = finalPath;
            }
        }


        private void AuthorTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // When author is changed by user, update the recommended path
            _isAuthorManuallyEdited = true;
            RecommendDestinationPath();
        }

        /// <summary>
        /// Opens a folder browser dialog starting from the currently recommended destination path.
        /// If the recommended path or its parent doesn't exist yet (e.g. a planned [author] folder),
        /// the browser falls back to the root directory.
        /// </summary>
        private void ExploreButton_Click(object sender, RoutedEventArgs e)
        {
            // Determine the browse start directory:
            // Use the parent of the recommended path if it exists; otherwise fall back to root.
            string currentText = DestinationPathTextBox.Text?.Trim() ?? string.Empty;
            string startPath = _rootDirectoryPath;

            if (!string.IsNullOrEmpty(currentText))
            {
                string parent = Path.GetDirectoryName(currentText);
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                    startPath = parent;
            }

            // Use CommonOpenFileDialog for native Explorer-style browsing with proper scroll positioning
            if (!CommonOpenFileDialog.IsPlatformSupported)
            {
                // Fallback to FolderBrowserDialog on unsupported platforms
                ExploreButton_LegacyFallback(startPath);
                return;
            }

            using (var dialog = new CommonOpenFileDialog())
            {
                dialog.Title = "Select destination folder";
                dialog.IsFolderPicker = true;
                dialog.Multiselect = false;
                dialog.AllowNonFileSystemItems = false;
                dialog.EnsurePathExists = false; // Allow navigating to not-yet-created folders' parents
                dialog.InitialDirectory = startPath; // Explorer scrolls to this location automatically

                if (dialog.ShowDialog(this) != CommonFileDialogResult.Ok)
                    return;

                string selectedPath = dialog.FileName;

                if (!PathService.IsPathWithin(_rootDirectoryPath, selectedPath))
                {
                    System.Windows.MessageBox.Show(
                        "Please select a folder within the root directory.",
                        "Invalid Selection",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                if (_sourceFolderPaths.Count == 1)
                {
                    string folderName = Path.GetFileName(_sourceFolderPaths[0]);
                    string finalPath = Path.Combine(selectedPath, folderName);
                    if (Directory.Exists(finalPath))
                        finalPath = PathService.GetUniqueDirectoryPath(selectedPath, folderName);
                    DestinationPathTextBox.Text = finalPath;
                }
                else
                {
                    DestinationPathTextBox.Text = selectedPath;
                }

                StatusText.Text = "Custom destination selected.";
            }
        }

        /// <summary>
        /// Legacy fallback for ExploreButton when CommonOpenFileDialog is not available.
        /// FolderBrowserDialog does not support scroll positioning.
        /// </summary>
        private void ExploreButton_LegacyFallback(string startPath)
        {
            var dialog = new FolderBrowserDialog
            {
                Description = "Select destination folder",
                SelectedPath = startPath
            };

            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return;

            string selectedPath = dialog.SelectedPath;

            if (!PathService.IsPathWithin(_rootDirectoryPath, selectedPath))
            {
                System.Windows.MessageBox.Show(
                    "Please select a folder within the root directory.",
                    "Invalid Selection",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (_sourceFolderPaths.Count == 1)
            {
                string folderName = Path.GetFileName(_sourceFolderPaths[0]);
                string finalPath = Path.Combine(selectedPath, folderName);
                if (Directory.Exists(finalPath))
                    finalPath = PathService.GetUniqueDirectoryPath(selectedPath, folderName);
                DestinationPathTextBox.Text = finalPath;
            }
            else
            {
                DestinationPathTextBox.Text = selectedPath;
            }

            StatusText.Text = "Custom destination selected.";
        }

        private async void Import_Click(object sender, RoutedEventArgs e)
        {
            string destinationPath = DestinationPathTextBox.Text?.Trim();

            if (string.IsNullOrEmpty(destinationPath))
            {
                System.Windows.MessageBox.Show("Please specify a destination path.", "Missing Information",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Parse tags from input
            List<string> tagsToApply = new List<string>();
            if (!string.IsNullOrWhiteSpace(_tagInputText))
            {
                try
                {
                    var parsedTags = TagHelper.ParseTags(_tagInputText);
                    tagsToApply = parsedTags.ToList();
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Error parsing tags: {ex.Message}", "Tag Parse Error",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
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
                    System.Windows.MessageBox.Show("Source and destination folders are the same.",
                        "Cannot Import", MessageBoxButton.OK, MessageBoxImage.Warning);
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

            // Set dialog result to indicate successful confirmation
            DestinationPath = destinationPath;
            DialogConfirmed = true;

            // Apply tags to imported folders if any tags were specified
            if (tagsToApply.Count > 0)
            {
                // Start the tag application task in background
                // Don't await it to allow the dialog to close immediately
                System.Threading.Tasks.Task.Run(async () => await ApplyTagsToImportedFoldersAsync(tagsToApply));
                StatusText.Text += $" Tags will be applied: {string.Join(" ", tagsToApply.Select(t => $"#{t}"))}";
            }
            DialogResult = true; // This is the key fix - explicitly set DialogResult to true
        }

        /// <summary>
        /// Applies the specified tags to the imported folders after they are copied
        /// </summary>
        /// <param name="tags">Tags to apply to the imported folders</param>
        private async Task ApplyTagsToImportedFoldersAsync(List<string> tags)
        {
            if (tags == null || tags.Count == 0 || string.IsNullOrEmpty(DestinationPath))
                return;

            try
            {
                // Determine the folders that will be created after import
                List<string> targetFolderPaths = new List<string>();

                if (_sourceFolderPaths.Count == 1)
                {
                    // For single folder import, the destination path is the target folder
                    targetFolderPaths.Add(DestinationPath);
                }
                else
                {
                    // For multiple folders, each source folder will be copied as a subfolder
                    foreach (var sourcePath in _sourceFolderPaths)
                    {
                        string folderName = Path.GetFileName(sourcePath);
                        string targetPath = Path.Combine(DestinationPath, folderName);
                        targetFolderPaths.Add(targetPath);
                    }
                }

                // Wait for folders to be created and then apply tags
                await System.Threading.Tasks.Task.Run(async () =>
                {
                    const int maxWaitTimeSeconds = 60; // Maximum wait time
                    const int checkIntervalMs = 500; // Check every 500ms
                    int totalWaitTime = 0;

                    // Wait for all target folders to exist
                    while (totalWaitTime < maxWaitTimeSeconds * 1000)
                    {
                        bool allFoldersExist = targetFolderPaths.All(path => Directory.Exists(path));

                        if (allFoldersExist)
                        {
                            // All folders exist, now apply tags
                            foreach (var targetPath in targetFolderPaths)
                            {
                                try
                                {
                                    // Apply tags with rating 0 (no rating specified during import)
                                    await _folderTagService.SetTagsAndRatingForFolderAsync(targetPath, tags, 0);
                                    System.Diagnostics.Debug.WriteLine($"Tags applied to folder: {targetPath}");
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"Error applying tags to folder {targetPath}: {ex.Message}");
                                }
                            }

                            System.Diagnostics.Debug.WriteLine($"Successfully applied tags to {targetFolderPaths.Count} imported folders");
                            return; // Success, exit the method
                        }

                        // Wait a bit before checking again
                        await System.Threading.Tasks.Task.Delay(checkIntervalMs);
                        totalWaitTime += checkIntervalMs;
                    }

                    // Timeout occurred
                    System.Diagnostics.Debug.WriteLine("Timeout waiting for imported folders to be created. Tags not applied.");
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error applying tags to imported folders: {ex.Message}");
            }
        }


        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogConfirmed = false;
            DialogResult = false; // Explicitly set DialogResult to false for consistency
        }

    }
}