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
        public enum ImportTransferMode
        {
            Copy,
            Move
        }

        private List<string> _sourceFolderPaths;
        private readonly string _rootDirectoryPath;
        private readonly List<FolderInfo> _allLoadedFolders;
        private string _detectedAuthor;

        public string DestinationPath { get; private set; }
        public List<string> TagsToApply { get; private set; } = new List<string>();
        public ImportTransferMode TransferMode { get; private set; } = ImportTransferMode.Copy;
        public bool DialogConfirmed { get; private set; } = false;

        /// <summary>
        /// Tracks whether the current author text was manually entered by the user
        /// or auto-detected from the folder name.
        /// </summary>
        private bool _isAuthorManuallyEdited = false;

        /// <summary>
        /// When true, the base-path textbox is being updated programmatically by
        /// RecommendDestinationPath(), so DestinationPathTextBox_TextChanged should
        /// not trigger a recalculation (which would cause an infinite loop).
        /// </summary>
        private bool _suppressBasePathChanged = false;

        /// <summary>
        /// True when the base path in DestinationPathTextBox was set by the user
        /// (via Explore or direct typing), meaning the auto-recommendation should no
        /// longer overwrite it.  Reset to false when the Author field changes.
        /// </summary>
        private bool _isBasePathManuallySet = false;

        /// <summary>
        /// When set, we already found an existing [author] folder and placed it as the
        /// base path.  In that case we do NOT append "[author]" a second time.
        /// </summary>
        private bool _existingAuthorFolderFound = false;

        // ── Tag binding ───────────────────────────────────────────────────────────
        private string _tagInputText = string.Empty;
        public string TagInputText
        {
            get => _tagInputText;
            set
            {
                _tagInputText = value;
                TagsTextBox.Text = value;
            }
        }

        // ═════════════════════════════════════════════════════════════════════════
        //  Constructor
        // ═════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Enhanced constructor for ImportFolderDialog with skip information display
        /// and height management.
        /// </summary>
        public ImportFolderDialog(
            List<string> sourceFolderPaths,
            string rootDirectoryPath,
            List<FolderInfo> allLoadedFolders,
            Dictionary<string, string> skippedFolders = null)
        {
            InitializeComponent();

            this.Loaded += ImportFolderDialog_Loaded;

            _sourceFolderPaths = sourceFolderPaths;
            _rootDirectoryPath = rootDirectoryPath;
            _allLoadedFolders = allLoadedFolders;

            // Show source folder(s) in the text box
            UpdateSourceFolderDisplay();

            // Update header and show skip information if any
            UpdateHeaderWithSkipInfo(skippedFolders);

            // Extract author from folder name
            AnalyzeFolderName();

            // Compute the initial recommended base path and final path preview
            RecommendDestinationPath();

            // Wire up tag text box
            TagsTextBox.TextChanged += TagsTextBox_TextChanged;

            this.UpdateLayout();
        }

        private void TagsTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _tagInputText = TagsTextBox.Text;
        }

        private void ImportFolderDialog_Loaded(object sender, RoutedEventArgs e)
        {
            this.UpdateLayout();
            this.InvalidateMeasure();
        }

        // ═════════════════════════════════════════════════════════════════════════
        //  Path recommendation — core logic
        // ═════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Computes the recommended BASE path and writes it into DestinationPathTextBox,
        /// then calls UpdateFinalPathPreview() so the real import target is always visible.
        ///
        /// BASE PATH semantics:
        ///   • Existing author folder found  → basePath = that folder
        ///                                     final     = basePath \ folderName
        ///   • No existing author folder     → basePath = root  (or user-chosen dir)
        ///                                     final     = basePath \ [author] \ folderName
        ///   • No author at all              → basePath = root
        ///                                     final     = basePath \ folderName
        ///
        /// When the user has already manually set the base path (_isBasePathManuallySet),
        /// we do NOT overwrite DestinationPathTextBox — we only refresh the final preview.
        /// </summary>
        private void RecommendDestinationPath()
        {
            string author = AuthorTextBox?.Text?.Trim() ?? string.Empty;

            if (!_isBasePathManuallySet)
            {
                // ── Auto-recommend the base path ─────────────────────────────────
                string recommendedBase = _rootDirectoryPath;
                _existingAuthorFolderFound = false;

                if (!string.IsNullOrEmpty(author))
                {
                    string existingFolder = FindAuthorFolder(author, _isAuthorManuallyEdited);

                    if (existingFolder != null)
                    {
                        // Use the existing author folder directly as the base.
                        recommendedBase = existingFolder;
                        _existingAuthorFolderFound = true;
                        StatusText.Text = $"Found existing author folder: {Path.GetFileName(existingFolder)}";
                    }
                    else
                    {
                        // No existing folder — base stays at root; [author] will be inserted
                        // between base and folderName when building the final path.
                        recommendedBase = _rootDirectoryPath;
                        _existingAuthorFolderFound = false;
                        StatusText.Text = $"No existing [{author}] folder found. It will be created automatically.";
                    }
                }
                else
                {
                    StatusText.Text = string.Empty;
                }

                // Write base path without triggering the "manual" flag
                _suppressBasePathChanged = true;
                DestinationPathTextBox.Text = recommendedBase;
                _suppressBasePathChanged = false;
            }
            else
            {
                // User already chose a custom base path.
                // Re-evaluate _existingAuthorFolderFound so the final path stays correct
                // (the author text may have changed even if the base path did not).
                if (!string.IsNullOrEmpty(author))
                {
                    string existingFolder = FindAuthorFolder(author, _isAuthorManuallyEdited);
                    // If the user's chosen base IS the existing author folder, honour that.
                    string currentBase = DestinationPathTextBox.Text?.Trim() ?? string.Empty;
                    _existingAuthorFolderFound = existingFolder != null &&
                        PathService.PathsEqual(currentBase, existingFolder);

                    if (!_existingAuthorFolderFound)
                        StatusText.Text = $"Base path set by user. [{author}] will be created inside it.";
                }
            }

            // Always refresh the final-path preview label
            UpdateFinalPathPreview();
        }

        /// <summary>
        /// Computes the real import destination from the current base path, author,
        /// and source folder name(s), then updates the preview label in the UI.
        ///
        /// Rules:
        ///   single-folder import:
        ///     _existingAuthorFolderFound → basePath \ folderName
        ///     author present, no existing → basePath \ [author] \ folderName
        ///     no author                  → basePath \ folderName
        ///
        ///   multi-folder import:
        ///     _existingAuthorFolderFound → basePath   (each folder copied inside)
        ///     author present, no existing → basePath \ [author]
        ///     no author                  → basePath
        /// </summary>
        private void UpdateFinalPathPreview()
        {
            if (DestinationPathTextBox == null) return;

            string basePath = DestinationPathTextBox.Text?.Trim() ?? string.Empty;
            string author   = AuthorTextBox?.Text?.Trim() ?? string.Empty;
            string folderName = Path.GetFileName(_sourceFolderPaths[0]);

            string finalPath = ComputeFinalDestination(basePath, author, folderName);

            // Show / hide the preview border
            if (FinalPathPreviewBorder != null)
            {
                if (!string.IsNullOrEmpty(finalPath) && finalPath != basePath)
                {
                    FinalPathPreviewText.Text = finalPath;
                    FinalPathPreviewBorder.Visibility = Visibility.Visible;
                }
                else
                {
                    FinalPathPreviewBorder.Visibility = Visibility.Collapsed;
                }
            }
        }

        private string ComputeFinalDestination(string basePath, string author, string folderName)
        {
            if (string.IsNullOrEmpty(basePath))
                return string.Empty;

            if (_sourceFolderPaths.Count == 1)
            {
                // ── Single folder ─────────────────────────────────────────────────
                if (!string.IsNullOrEmpty(author) && !_existingAuthorFolderFound)
                {
                    // basePath \ [author] \ folderName
                    return Path.Combine(basePath, $"[{author}]", folderName);
                }
                else
                {
                    // basePath \ folderName  (existing author folder OR no author)
                    return Path.Combine(basePath, folderName);
                }
            }
            else
            {
                // ── Multiple folders ─────────────────────────────────────────────
                if (!string.IsNullOrEmpty(author) && !_existingAuthorFolderFound)
                    return Path.Combine(basePath, $"[{author}]");
                else
                    return basePath;
            }
        }

        // ═════════════════════════════════════════════════════════════════════════
        //  Event handlers — Author / Base path changes
        // ═════════════════════════════════════════════════════════════════════════

        private void AuthorTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _isAuthorManuallyEdited = true;

            // Changing the author resets the "user has manually chosen a base path" flag,
            // so the recommendation can update the base path to match the new author.
            _isBasePathManuallySet = false;

            RecommendDestinationPath();
        }

        /// <summary>
        /// Fired whenever DestinationPathTextBox content changes.
        /// If the change was made by the user (not by RecommendDestinationPath),
        /// mark the base path as manually set and refresh only the final-path preview.
        /// </summary>
        private void DestinationPathTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressBasePathChanged)
                return;

            // User edited the base path directly → lock it in and recompute final preview
            _isBasePathManuallySet = true;

            // Re-evaluate whether the new base happens to be an existing author folder
            string author = AuthorTextBox?.Text?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(author))
            {
                string existingFolder = FindAuthorFolder(author, _isAuthorManuallyEdited);
                string currentBase    = DestinationPathTextBox.Text?.Trim() ?? string.Empty;
                _existingAuthorFolderFound = existingFolder != null &&
                    PathService.PathsEqual(currentBase, existingFolder);

                StatusText.Text = _existingAuthorFolderFound
                    ? $"Found existing author folder: {Path.GetFileName(currentBase)}"
                    : $"Base path set by user. [{author}] will be created inside it.";
            }

            UpdateFinalPathPreview();
        }

        // ═════════════════════════════════════════════════════════════════════════
        //  Explore button — lets user pick an arbitrary base path
        // ═════════════════════════════════════════════════════════════════════════

        private void ExploreButton_Click(object sender, RoutedEventArgs e)
        {
            string currentText = DestinationPathTextBox.Text?.Trim() ?? string.Empty;
            string startPath   = _rootDirectoryPath;

            if (!string.IsNullOrEmpty(currentText))
            {
                // Start browser at the current base path if it exists, else at root
                string candidate = Directory.Exists(currentText)
                    ? currentText
                    : (Directory.Exists(Path.GetDirectoryName(currentText) ?? string.Empty)
                        ? Path.GetDirectoryName(currentText)
                        : _rootDirectoryPath);
                if (!string.IsNullOrEmpty(candidate))
                    startPath = candidate;
            }

            if (!CommonOpenFileDialog.IsPlatformSupported)
            {
                ExploreButton_LegacyFallback(startPath);
                return;
            }

            using (var dialog = new CommonOpenFileDialog())
            {
                dialog.Title          = "Select the base folder for this import";
                dialog.IsFolderPicker = true;
                dialog.Multiselect    = false;
                dialog.AllowNonFileSystemItems = false;
                dialog.EnsurePathExists = true;
                dialog.InitialDirectory = startPath;

                if (dialog.ShowDialog(this) != CommonFileDialogResult.Ok)
                    return;

                // Write the chosen path; DestinationPathTextBox_TextChanged will handle the rest
                DestinationPathTextBox.Text = dialog.FileName;
                StatusText.Text = "Base path set by user.";
            }
        }

        private void ExploreButton_LegacyFallback(string startPath)
        {
            var dialog = new FolderBrowserDialog
            {
                Description  = "Select the base folder for this import",
                SelectedPath = Directory.Exists(startPath) ? startPath : _rootDirectoryPath
            };

            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return;

            DestinationPathTextBox.Text = dialog.SelectedPath;
            StatusText.Text = "Base path set by user.";
        }

        // ═════════════════════════════════════════════════════════════════════════
        //  Import / Cancel
        // ═════════════════════════════════════════════════════════════════════════

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            string basePath = DestinationPathTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(basePath))
            {
                System.Windows.MessageBox.Show("Please specify a base path.", "Missing Information",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string author     = AuthorTextBox.Text.Trim();
            string folderName = Path.GetFileName(_sourceFolderPaths[0]);

            // Compute the real destination using the same rules as the preview
            string finalDestination = ComputeFinalDestination(basePath, author, folderName);

            if (string.IsNullOrEmpty(finalDestination))
            {
                System.Windows.MessageBox.Show("Could not determine a valid destination path.", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Parse tags
            List<string> tagsToApply = new List<string>();
            if (!string.IsNullOrWhiteSpace(_tagInputText))
            {
                try
                {
                    tagsToApply = TagHelper.ParseTags(_tagInputText).ToList();
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Error parsing tags: {ex.Message}", "Tag Parse Error",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            TransferMode = MoveModeRadioButton?.IsChecked == true
                ? ImportTransferMode.Move
                : ImportTransferMode.Copy;

            if (_sourceFolderPaths.Count > 1)
            {
                // Multiple folders: finalDestination is the target directory
                try
                {
                    if (!Directory.Exists(finalDestination))
                        Directory.CreateDirectory(finalDestination);
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
                // Single folder checks
                if (PathService.PathsEqual(_sourceFolderPaths[0], finalDestination))
                {
                    System.Windows.MessageBox.Show("Source and destination folders are the same.",
                        "Cannot Import", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (PathService.IsPathWithin(_sourceFolderPaths[0], finalDestination))
                {
                    System.Windows.MessageBox.Show("Cannot import a folder into itself or its subfolder.",
                        "Invalid Destination", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Ensure parent directories exist (e.g. basePath\[author])
                string parentDir = Path.GetDirectoryName(finalDestination);
                if (!Directory.Exists(parentDir))
                {
                    var result = System.Windows.MessageBox.Show(
                        $"Destination directory '{parentDir}' does not exist. Create it?",
                        "Create Directory", MessageBoxButton.YesNo, MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        try   { Directory.CreateDirectory(parentDir); }
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

            DestinationPath = finalDestination;
            TagsToApply = tagsToApply;
            DialogConfirmed = true;
            if (tagsToApply.Count > 0)
                StatusText.Text += $" Tags prepared: {string.Join(" ", tagsToApply.Select(t => $"#{t}"))}";

            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogConfirmed = false;
            DialogResult    = false;
        }

        // ═════════════════════════════════════════════════════════════════════════
        //  Author detection helpers
        // ═════════════════════════════════════════════════════════════════════════

        private void AnalyzeFolderName()
        {
            string folderName  = Path.GetFileName(_sourceFolderPaths[0]);
            _detectedAuthor    = ExtractAuthorFromFolderName(folderName);
            AuthorTextBox.Text = _detectedAuthor;
        }

        private string ExtractAuthorFromFolderName(string folderName)
        {
            var bracketMatch = Regex.Match(folderName, @"\[(.*?)\]");
            if (bracketMatch.Success)
                return bracketMatch.Groups[1].Value.Trim();

            var parenMatch = Regex.Match(folderName, @"^\((.*?)\)");
            if (parenMatch.Success)
                return parenMatch.Groups[1].Value.Trim();

            var dashMatch = Regex.Match(folderName, @"^(.+?)\s+-\s+");
            if (dashMatch.Success)
                return dashMatch.Groups[1].Value.Trim();

            var spaceMatch = Regex.Match(folderName, @"^(\S+)\s+\S");
            if (spaceMatch.Success)
                return spaceMatch.Groups[1].Value.Trim();

            return string.Empty;
        }

        /// <summary>
        /// Searches for an existing author folder.
        /// Manual input  → strict exact match on "[author]" bracket folder name.
        /// Auto-detected → broader substring search at root level.
        /// </summary>
        private string FindAuthorFolder(string author, bool isManualInput)
        {
            if (string.IsNullOrEmpty(author))
                return null;

            if (isManualInput)
            {
                string bracketFolderName = $"[{author}]";

                var match = _allLoadedFolders.FirstOrDefault(f =>
                {
                    bool isUnderRoot  = PathService.IsPathWithin(_rootDirectoryPath, f.FolderPath);
                    bool nameMatches  = string.Equals(
                        Path.GetFileName(f.FolderPath), bracketFolderName,
                        StringComparison.OrdinalIgnoreCase);
                    return isUnderRoot && nameMatches;
                });

                if (match != null)
                    return match.FolderPath;

                string directPath = Path.Combine(_rootDirectoryPath, bracketFolderName);
                if (Directory.Exists(directPath))
                    return directPath;

                return null;
            }
            else
            {
                var topLevelFolders = _allLoadedFolders
                    .Where(f =>
                    {
                        string folderNameOnly = Path.GetFileName(f.FolderPath);
                        string parentPath     = Directory.GetParent(f.FolderPath)?.FullName;
                        bool isTopLevel       = parentPath != null &&
                                                PathService.PathsEqual(parentPath, _rootDirectoryPath);
                        bool containsAuthor   = folderNameOnly.IndexOf(
                            author, StringComparison.OrdinalIgnoreCase) >= 0;
                        return isTopLevel && containsAuthor;
                    })
                    .OrderBy(f => f.FolderPath.Length)
                    .ToList();

                return topLevelFolders.FirstOrDefault()?.FolderPath;
            }
        }

        // ═════════════════════════════════════════════════════════════════════════
        //  UI helpers
        // ═════════════════════════════════════════════════════════════════════════

        private void UpdateSourceFolderDisplay()
        {
            if (_sourceFolderPaths.Count == 1)
            {
                SourceFolderText.Text = _sourceFolderPaths[0];
            }
            else
            {
                SourceFolderText.Text =
                    $"{_sourceFolderPaths[0]} (and {_sourceFolderPaths.Count - 1} more valid folders...)";
            }
        }

        private void UpdateHeaderWithSkipInfo(Dictionary<string, string> skippedFolders)
        {
            var headerText = _sourceFolderPaths.Count > 1
                ? $"Import {_sourceFolderPaths.Count} Folders"
                : "Import Folder";

            if (skippedFolders != null && skippedFolders.Count > 0)
            {
                headerText += $" ({skippedFolders.Count} skipped)";

                var skipInfoLines = new List<string>();
                foreach (var kvp in skippedFolders)
                {
                    if (kvp.Value.StartsWith("Duplicate name - existing folder:"))
                    {
                        var existingPath = kvp.Value.Substring("Duplicate name - existing folder:".Length).Trim();
                        skipInfoLines.Add($"• {kvp.Key}");
                        skipInfoLines.Add($"  └─ Existing: {existingPath}");
                    }
                    else
                    {
                        skipInfoLines.Add($"• {kvp.Key}: {kvp.Value}");
                    }
                }

                if (SkipInfoPanel != null)
                {
                    SkipInfoText.Text = $"Skipped folders:\n{string.Join("\n", skipInfoLines)}";
                    SkipInfoPanel.Visibility = Visibility.Visible;
                    this.UpdateLayout();
                    RecalculateWindowHeight();
                }
            }
            else if (SkipInfoPanel != null)
            {
                SkipInfoPanel.Visibility = Visibility.Collapsed;
            }

            HeaderText.Text = headerText;
        }

        private void RecalculateWindowHeight()
        {
            this.SizeToContent = SizeToContent.Manual;
            this.UpdateLayout();
            this.SizeToContent = SizeToContent.Height;

            if (this.ActualHeight < this.MinHeight) this.Height = this.MinHeight;
            else if (this.ActualHeight > this.MaxHeight) this.Height = this.MaxHeight;
        }

        // ═════════════════════════════════════════════════════════════════════════
        //  Tag application (legacy helper, no longer used)
        // ═════════════════════════════════════════════════════════════════════════

        [Obsolete("Tag application is now handled by MainViewModel after successful import results.")]
        private Task ApplyTagsToImportedFoldersAsync(List<string> tags)
        {
            return Task.CompletedTask;
        }
    }
}
