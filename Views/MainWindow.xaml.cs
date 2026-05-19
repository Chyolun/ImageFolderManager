using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ImageFolderManager.Models;
using ImageFolderManager.Services;
using ImageFolderManager.ViewModels;
using ImageFolderManager.Views;
using ImageFolderManager.Controls;
using MahApps.Metro.Controls;
using MahApps.Metro.Controls.Dialogs;
using Microsoft.Web.WebView2.Wpf;
using CommunityToolkit.Mvvm.Input;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Threading;



namespace ImageFolderManager
{
    public partial class MainWindow : MetroWindow
    {

        #region property 
        public MainViewModel ViewModel => DataContext as MainViewModel;
        #endregion

        // ADD this to track which instance we're using
        private string _mainViewModelInstanceInfo;
        private readonly ObservableCollection<string> _searchSuggestionItems = new ObservableCollection<string>();
        private readonly ObservableCollection<string> _tagSuggestionItems = new ObservableCollection<string>();
        private bool _isApplyingSearchSuggestion;
        private bool _isApplyingTagSuggestion;

        public MainWindow()
        {
            InitializeComponent();
            var viewModel = new MainViewModel();
            _mainViewModelInstanceInfo = viewModel.GetInstanceInfo();
            DataContext = viewModel;
            viewModel.TagCloudRequested += MainViewModel_TagCloudRequested;
            viewModel.SetShellTreeView(ShellTreeViewControl);
            SearchSuggestionListBox.ItemsSource = _searchSuggestionItems;
            TagSuggestionListBox.ItemsSource = _tagSuggestionItems;
            AutoExpandFoldersMenuItem.IsChecked = AppSettings.Instance.AutoExpandFolders;
            this.Loaded += MainWindow_Loaded;
            this.Closing += MainWindow_Closing;
            _ = LoadDefaultRootDirectoryAsync();
        }

        // ADD THIS METHOD: Initialize debug monitoring when window is fully loaded
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {

        }

        // ADD THIS METHOD: Cleanup debug monitoring when window is closing
        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.TagCloudRequested -= MainViewModel_TagCloudRequested;
            }
        }

        private void MainViewModel_TagCloudRequested(object sender, EventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(ShowTagCloudWindow);
                return;
            }

            ShowTagCloudWindow();
        }

        private async Task LoadDefaultRootDirectoryAsync()
        {
            if (!string.IsNullOrEmpty(AppSettings.Instance.DefaultRootDirectory))
            {
                await ViewModel.LoadDirectoryAsync(AppSettings.Instance.DefaultRootDirectory);

                // Select the path in the shell tree view
                //if (ShellTreeViewControl != null)
                //{
                //    ShellTreeViewControl.SelectPath(AppSettings.Instance.DefaultRootDirectory);
                //}
            }
        }

        // Modified to not load images automatically
        private void OnFolderSelected(FolderInfo folder)
        {
            if (ViewModel == null)
            {
                Debug.WriteLine("ERROR: ViewModel is null in OnFolderSelected");
                return;
            }
            ClearImageSelection();
            // We don't auto-load images anymore - just update selection
            ViewModel.SetSelectedFolderWithoutLoading(folder);
        }




        private T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);

                if (child is T found)
                {
                    System.Diagnostics.Debug.WriteLine($"Found {typeof(T).Name} in visual tree");
                    return found;
                }

                var childOfChild = FindVisualChild<T>(child);
                if (childOfChild != null)
                    return childOfChild;
            }
            return null;
        }


        // Handle selection changed in search results
        // Modified to not load images automatically
        private void SearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var listBox = sender as ListBox;
            if (listBox?.SelectedItem is FolderInfo folder)
            {
                Debug.WriteLine($"SearchResults_SelectionChanged with folder: {folder.FolderPath}");

                //// Select the item in the tree view
                //if (ShellTreeViewControl != null)
                //{
                //    ShellTreeViewControl.SelectPath(folder.FolderPath);
                //}

                // Just update selection without loading images
                ClearImageSelection();
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
            ShellTreeViewControl?.SelectPath(folderInfo.FolderPath);
            e.Handled = true;
        }

        private void SearchResultListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var listBox = sender as ListBox;
            var element = e.OriginalSource as DependencyObject;
            if (listBox == null || element == null)
                return;

            var item = FindVisualParent<ListBoxItem>(element);
            if (item == null)
                return;

            if (!item.IsSelected)
            {
                listBox.SelectedItems.Clear();
                item.IsSelected = true;
            }

            item.Focus();
        }

        private async void ImportFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Disable the menu item during the operation to prevent multiple simultaneous imports
                var menuItem = sender as MenuItem;
                if (menuItem != null)
                {
                    menuItem.IsEnabled = false;
                }

                // Check if ViewModel is available
                if (ViewModel == null)
                {
                    MessageBox.Show("Could not perform import: ViewModel is not available.",
                        "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Call the import functionality
                await ViewModel.ImportFolderAsync();
            }
            catch (Exception ex)
            {
                // Log the exception details for debugging
                System.Diagnostics.Debug.WriteLine($"ImportFolder_Click error: {ex}");

                // Show user-friendly error message
                MessageBox.Show($"An unexpected error occurred during folder import:\n\n{ex.Message}",
                    "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // Re-enable the menu item
                var menuItem = sender as MenuItem;
                if (menuItem != null)
                {
                    menuItem.IsEnabled = true;
                }
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

                        // Change root directory in ShellTreeView
                        if (ShellTreeViewControl != null)
                        {
                            await ShellTreeViewControl.ChangeRootDirectoryAsync(newRootDir);
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

        private async void AutoExpandFolders_Click(object sender, RoutedEventArgs e)
        {
            bool enabled = AutoExpandFoldersMenuItem.IsChecked;
            AppSettings.Instance.AutoExpandFolders = enabled;
            ViewModel.StatusMessage = enabled
                ? "Auto-expand enabled for first-level folders."
                : "Auto-expand disabled.";

            if (ShellTreeViewControl != null)
            {
                await ShellTreeViewControl.RefreshTreeFull();
            }
        }

        private void RecentRootDirectories_SubmenuOpened(object sender, RoutedEventArgs e)
        {
            RecentRootDirectoriesMenuItem.Items.Clear();

            var recentFolders = AppSettings.Instance.RecentFolders?
                .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();

            if (recentFolders.Count == 0)
            {
                RecentRootDirectoriesMenuItem.Items.Add(new MenuItem
                {
                    Header = "(No recent directories)",
                    IsEnabled = false
                });
                return;
            }

            foreach (var path in recentFolders)
            {
                var menuItem = new MenuItem
                {
                    Header = path,
                    ToolTip = path
                };

                menuItem.Click += async (_, __) =>
                {
                    await SwitchRootDirectoryAsync(path);
                };

                RecentRootDirectoriesMenuItem.Items.Add(menuItem);
            }
        }

        private async Task SwitchRootDirectoryAsync(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            {
                MessageBox.Show("The selected directory no longer exists.",
                    "Directory Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            AppSettings.Instance.DefaultRootDirectory = rootPath;
            AppSettings.Instance.AddRecentFolder(rootPath);

            await ViewModel.LoadDirectoryAsync(rootPath);
        }

        private async void PreviewSize_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Views.PreviewSizeDialog();
            dialog.Owner = this;
            dialog.ShowDialog();

            if (dialog.PreviewDialogResult)
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
                _ = ViewModel.LoadImagesForSelectedFolderAsync();
                return;
            }

            if (sender is Image imageElement && imageElement.DataContext is ImageInfo selectedImage)
            {
                SetSelectedImage(selectedImage);
            }

            // Open in internal image viewer on double-click
            if (e.ClickCount == 2 && sender is Image img && img.Tag is string filePath)
            {
                try
                {
                    var imagePaths = ViewModel.Images
                        .Select(x => x.FilePath)
                        .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                        .ToList();

                    if (imagePaths.Count == 0)
                    {
                        return;
                    }

                    var selectedIndex = imagePaths.FindIndex(path =>
                        string.Equals(path, filePath, StringComparison.OrdinalIgnoreCase));

                    if (selectedIndex < 0)
                    {
                        selectedIndex = 0;
                    }

                    var viewerWindow = new ImageViewerWindow(imagePaths, selectedIndex)
                    {
                        Owner = this
                    };
                    viewerWindow.ShowDialog();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Unable to open image viewer: {ex.Message}");
                }
            }
        }

        private void SetSelectedImage(ImageInfo selectedImage)
        {
            if (ViewModel?.Images == null || selectedImage == null)
            {
                return;
            }

            foreach (var image in ViewModel.Images)
            {
                image.IsSelected = ReferenceEquals(image, selectedImage);
            }
        }

        private void ClearImageSelection()
        {
            if (ViewModel?.Images == null)
            {
                return;
            }

            foreach (var image in ViewModel.Images)
            {
                image.IsSelected = false;
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
                await ShellTreeViewControl.RefreshTreeFull();

                // Reselect the previously selected folder if it still exists
                if (!string.IsNullOrEmpty(currentPath) && Directory.Exists(currentPath))
                {
                    ShellTreeViewControl.SelectPath(currentPath);
                }
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

                    // Then perform the actual collapsing in the tree view
                    if (ViewModel?.SelectedFolder != null)
                    {
                        string selectedPath = ViewModel.SelectedFolder.FolderPath;
                        string parentPath = Path.GetDirectoryName(selectedPath);

                        if (!string.IsNullOrEmpty(parentPath))
                        {
                            ShellTreeViewControl.CollapseDirectory(parentPath);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error collapsing parent directory: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);

                Debug.WriteLine($"Error in CollapseParentDirectory_Click: {ex.Message}");
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                IAsyncRelayCommand cmd = ViewModel?.UndoCommand;
                if (cmd != null && cmd.CanExecute(null))
                {
                    _ = ExecuteUndoSafeAsync(cmd);
                    e.Handled = true;
                }
            }
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                OpenFindBar();
                e.Handled = true;
            }

        }

        #region Search And Tag Autocomplete

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isApplyingSearchSuggestion)
                return;

            UpdateSearchSuggestions();
        }

        private void SearchTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!SearchSuggestionPopup.IsOpen || _searchSuggestionItems.Count == 0)
                return;

            if (e.Key == Key.Down)
            {
                int nextIndex = Math.Min(_searchSuggestionItems.Count - 1, SearchSuggestionListBox.SelectedIndex + 1);
                SearchSuggestionListBox.SelectedIndex = nextIndex;
                SearchSuggestionListBox.ScrollIntoView(SearchSuggestionListBox.SelectedItem);
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                int nextIndex = Math.Max(0, SearchSuggestionListBox.SelectedIndex - 1);
                SearchSuggestionListBox.SelectedIndex = nextIndex;
                SearchSuggestionListBox.ScrollIntoView(SearchSuggestionListBox.SelectedItem);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter || e.Key == Key.Tab)
            {
                if (ApplySelectedSearchSuggestion())
                {
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Escape)
            {
                HideSearchSuggestionPopup();
                e.Handled = true;
            }
        }

        private void SearchSuggestionListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            ApplySelectedSearchSuggestion();
        }

        private bool ApplySelectedSearchSuggestion()
        {
            if (!(SearchSuggestionListBox.SelectedItem is string selectedSuggestion) ||
                string.IsNullOrWhiteSpace(selectedSuggestion))
            {
                return false;
            }

            ReplaceSearchToken(selectedSuggestion);
            return true;
        }

        private void ReplaceSearchToken(string replacement)
        {
            if (SearchTextBox == null)
                return;

            string text = SearchTextBox.Text ?? string.Empty;
            var (start, end, _) = GetTokenBoundsByWhitespace(text, SearchTextBox.CaretIndex);
            string newText = text.Substring(0, start) + replacement + text.Substring(end);

            _isApplyingSearchSuggestion = true;
            SearchTextBox.Text = newText;
            SearchTextBox.CaretIndex = start + replacement.Length;
            _isApplyingSearchSuggestion = false;

            HideSearchSuggestionPopup();
        }

        private void UpdateSearchSuggestions()
        {
            if (SearchTextBox == null)
                return;

            string text = SearchTextBox.Text ?? string.Empty;
            var (_, _, token) = GetTokenBoundsByWhitespace(text, SearchTextBox.CaretIndex);

            IEnumerable<string> suggestions = Enumerable.Empty<string>();
            if (token.StartsWith("#"))
            {
                suggestions = BuildTagSearchSuggestions(token.Substring(1));
            }
            else if (token.StartsWith("@"))
            {
                suggestions = BuildFolderSearchSuggestions(token.Substring(1));
            }
            else if (token.StartsWith("*"))
            {
                suggestions = BuildRatingSearchSuggestions(token.Substring(1));
            }
            else
            {
                HideSearchSuggestionPopup();
                return;
            }

            var topSuggestions = suggestions.Take(24).ToList();
            SetSuggestionItems(_searchSuggestionItems, topSuggestions);

            bool shouldOpen = topSuggestions.Count > 0 && SearchTextBox.IsKeyboardFocusWithin;
            SearchSuggestionPopup.IsOpen = shouldOpen;
            SearchSuggestionListBox.SelectedIndex = shouldOpen ? 0 : -1;
        }

        private IEnumerable<string> BuildTagSearchSuggestions(string fragment)
        {
            var candidates = GetCategoryTagSuggestionValues()
                .Select(v => $"#{v}")
                .ToList();

            if (candidates.Count == 0)
            {
                candidates.Add("#Category::Tag");
            }

            return RankSuggestions(candidates, $"#{fragment}", 64);
        }

        private IEnumerable<string> BuildFolderSearchSuggestions(string fragment)
        {
            var folderNames = (ViewModel?.GetAllIndexedFolderPaths() ?? new List<string>())
                .Select(path => Path.GetFileName(path))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name => $"@{name}")
                .ToList();

            if (folderNames.Count == 0)
            {
                folderNames.Add("@folderName");
            }

            return RankSuggestions(folderNames, $"@{fragment}", 64);
        }

        private IEnumerable<string> BuildRatingSearchSuggestions(string fragment)
        {
            string[] ratingTemplates =
            {
                "*>=5", "*>=4", "*>=3", "*=5", "*=4", "*<=2", "*<3", "*>3"
            };
            return RankSuggestions(ratingTemplates, $"*{fragment}", 16);
        }

        private void TagsTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isApplyingTagSuggestion)
                return;

            UpdateTagSuggestions();
        }

        private void TagsTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!TagSuggestionPopup.IsOpen || _tagSuggestionItems.Count == 0)
                return;

            if (e.Key == Key.Down)
            {
                int nextIndex = Math.Min(_tagSuggestionItems.Count - 1, TagSuggestionListBox.SelectedIndex + 1);
                TagSuggestionListBox.SelectedIndex = nextIndex;
                TagSuggestionListBox.ScrollIntoView(TagSuggestionListBox.SelectedItem);
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                int nextIndex = Math.Max(0, TagSuggestionListBox.SelectedIndex - 1);
                TagSuggestionListBox.SelectedIndex = nextIndex;
                TagSuggestionListBox.ScrollIntoView(TagSuggestionListBox.SelectedItem);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter || e.Key == Key.Tab)
            {
                if (ApplySelectedTagSuggestion())
                {
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Escape)
            {
                HideTagSuggestionPopup();
                e.Handled = true;
            }
        }

        private void TagSuggestionListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            ApplySelectedTagSuggestion();
        }

        private bool ApplySelectedTagSuggestion()
        {
            if (!(TagSuggestionListBox.SelectedItem is string selectedSuggestion) ||
                string.IsNullOrWhiteSpace(selectedSuggestion))
            {
                return false;
            }

            ReplaceTagFragment(selectedSuggestion);
            return true;
        }

        private void ReplaceTagFragment(string replacement)
        {
            if (TagsTextBox == null)
                return;

            string text = TagsTextBox.Text ?? string.Empty;
            if (!TryGetTagFragmentBounds(text, TagsTextBox.CaretIndex, out int valueStart, out int valueEnd, out _))
                return;

            string newText = text.Substring(0, valueStart) + replacement + text.Substring(valueEnd);

            _isApplyingTagSuggestion = true;
            TagsTextBox.Text = newText;
            TagsTextBox.CaretIndex = valueStart + replacement.Length;
            _isApplyingTagSuggestion = false;

            HideTagSuggestionPopup();
        }

        private void UpdateTagSuggestions()
        {
            if (TagsTextBox == null)
                return;

            string text = TagsTextBox.Text ?? string.Empty;
            if (!TryGetTagFragmentBounds(text, TagsTextBox.CaretIndex, out _, out _, out string fragment))
            {
                HideTagSuggestionPopup();
                return;
            }

            var suggestions = RankSuggestions(GetCategoryTagSuggestionValues(), fragment, 24).ToList();
            SetSuggestionItems(_tagSuggestionItems, suggestions);

            bool shouldOpen = suggestions.Count > 0 && TagsTextBox.IsKeyboardFocusWithin;
            TagSuggestionPopup.IsOpen = shouldOpen;
            TagSuggestionListBox.SelectedIndex = shouldOpen ? 0 : -1;
        }

        private List<string> GetCategoryTagSuggestionValues()
        {
            var suggestionSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (ViewModel?.TagManagement?.TagCloud != null)
            {
                var tagCloud = ViewModel.TagManagement.TagCloud;
                var categories = tagCloud.Categories?
                    .Select(c => c.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToList() ?? new List<string>();

                foreach (var category in categories)
                {
                    if (!category.Equals("Uncategorized", StringComparison.OrdinalIgnoreCase))
                    {
                        suggestionSet.Add($"{category}::");
                    }

                    var tags = tagCloud.GetTagsInCategory(category)
                        .Select(t => t.Tag)
                        .Where(t => !string.IsNullOrWhiteSpace(t))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    foreach (var tag in tags)
                    {
                        suggestionSet.Add(tag);
                        if (!category.Equals("Uncategorized", StringComparison.OrdinalIgnoreCase))
                        {
                            suggestionSet.Add($"{category}::{tag}");
                        }
                    }
                }
            }

            foreach (var tag in ViewModel?.FolderTags ?? new ObservableCollection<string>())
            {
                if (!string.IsNullOrWhiteSpace(tag))
                    suggestionSet.Add(tag.Trim());
            }

            foreach (var tagDisplayInfo in ViewModel?.TagDisplayItems ?? new ObservableCollection<TagDisplayInfo>())
            {
                if (tagDisplayInfo == null || string.IsNullOrWhiteSpace(tagDisplayInfo.TagName))
                    continue;

                suggestionSet.Add(tagDisplayInfo.TagName.Trim());
                if (!string.IsNullOrWhiteSpace(tagDisplayInfo.Category) &&
                    !tagDisplayInfo.Category.Equals("Uncategorized", StringComparison.OrdinalIgnoreCase))
                {
                    suggestionSet.Add($"{tagDisplayInfo.Category.Trim()}::{tagDisplayInfo.TagName.Trim()}");
                }
            }

            if (suggestionSet.Count == 0)
            {
                suggestionSet.Add("Category::Tag");
            }

            return suggestionSet
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static IEnumerable<string> RankSuggestions(IEnumerable<string> rawSuggestions, string typedFragment, int take)
        {
            var source = rawSuggestions?.Where(s => !string.IsNullOrWhiteSpace(s))
                ?? Enumerable.Empty<string>();

            string fragment = typedFragment?.Trim() ?? string.Empty;

            var filtered = source
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(s => string.IsNullOrEmpty(fragment) ||
                            s.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => string.IsNullOrEmpty(fragment) ||
                              s.StartsWith(fragment, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(s => s, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(1, take));

            return filtered;
        }

        private static (int start, int end, string token) GetTokenBoundsByWhitespace(string text, int caretIndex)
        {
            string value = text ?? string.Empty;
            int caret = Math.Max(0, Math.Min(caretIndex, value.Length));

            int start = caret;
            while (start > 0 && !char.IsWhiteSpace(value[start - 1]))
            {
                start--;
            }

            int end = caret;
            while (end < value.Length && !char.IsWhiteSpace(value[end]))
            {
                end++;
            }

            string token = value.Substring(start, end - start);
            return (start, end, token);
        }

        private static bool TryGetTagFragmentBounds(
            string text,
            int caretIndex,
            out int valueStart,
            out int valueEnd,
            out string fragment)
        {
            valueStart = 0;
            valueEnd = 0;
            fragment = string.Empty;

            string value = text ?? string.Empty;
            if (value.Length == 0)
                return false;

            int caret = Math.Max(0, Math.Min(caretIndex, value.Length));
            int hashIndex = value.LastIndexOf('#', Math.Max(0, caret - 1));
            if (hashIndex < 0)
                return false;

            valueStart = hashIndex + 1;
            while (valueStart < value.Length &&
                   valueStart < caret &&
                   char.IsWhiteSpace(value[valueStart]))
            {
                valueStart++;
            }

            int nextHash = value.IndexOf('#', caret);
            valueEnd = nextHash >= 0 ? nextHash : value.Length;
            fragment = value.Substring(valueStart, Math.Max(0, valueEnd - valueStart)).Trim();
            return true;
        }

        private static void SetSuggestionItems(ObservableCollection<string> target, IEnumerable<string> values)
        {
            target.Clear();
            foreach (var value in values)
            {
                target.Add(value);
            }
        }

        private void HideSearchSuggestionPopup()
        {
            SearchSuggestionPopup.IsOpen = false;
            SearchSuggestionListBox.SelectedIndex = -1;
            _searchSuggestionItems.Clear();
        }

        private void HideTagSuggestionPopup()
        {
            TagSuggestionPopup.IsOpen = false;
            TagSuggestionListBox.SelectedIndex = -1;
            _tagSuggestionItems.Clear();
        }

        #endregion

        #region Find Bar

        private List<string> _findResults = new List<string>();
        private int _findIndex = -1;
        private DispatcherTimer _findDebounceTimer;
        private const int FindDebounceDelayMs = 600;
        private CancellationTokenSource _findOperationCts;
        private int _findRequestId;

        /// <summary>Menu item click - Edit -> Find</summary>
        private void Find_Click(object sender, RoutedEventArgs e) => OpenFindBar();

        private void OpenFindBar()
        {
            if (_findDebounceTimer == null)
            {
                _findDebounceTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(FindDebounceDelayMs)
                };
                _findDebounceTimer.Tick += FindDebounceTimer_Tick;
            }
            FindBar.Visibility = Visibility.Visible;
            FindTextBox.Focus();
            FindTextBox.SelectAll();
        }

        private void FindClose_Click(object sender, RoutedEventArgs e) => CloseFindBar();

        private void CloseFindBar()
        {
            _findDebounceTimer?.Stop();
            _findOperationCts?.Cancel();
            _findOperationCts?.Dispose();
            _findOperationCts = null;
            FindBar.Visibility = Visibility.Collapsed;
            ShellTreeViewControl.Focus();
        }

        private void FindDebounceTimer_Tick(object sender, EventArgs e)
        {
            _findDebounceTimer.Stop();
            _ = RunFindAsync(FindTextBox.Text, forward: true, resetIndex: true);
        }

        /// <summary>Re-run search whenever the user types.</summary>
        private void FindTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {

            if (string.IsNullOrWhiteSpace(FindTextBox.Text))
            {
                _findDebounceTimer?.Stop();
                _findOperationCts?.Cancel();
                _findOperationCts?.Dispose();
                _findOperationCts = null;
                _findResults.Clear();
                _findIndex = -1;
                FindNoMatchLabel.Visibility = Visibility.Collapsed;
                FindMatchLabel.Text = "";
                return;
            }

            _findDebounceTimer?.Stop();
            _findDebounceTimer?.Start();
        }

        private void FindNext_Click(object sender, RoutedEventArgs e)
            => _ = RunFindAsync(FindTextBox.Text, forward: true, resetIndex: false);

        private void FindPrev_Click(object sender, RoutedEventArgs e)
            => _ = RunFindAsync(FindTextBox.Text, forward: false, resetIndex: false);

        private void FindTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                bool backward = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
                _ = RunFindAsync(FindTextBox.Text, forward: !backward, resetIndex: false);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CloseFindBar();
                e.Handled = true;
            }
        }

        /// <summary>Core search + navigation logic.</summary>
        private async Task<List<string>> FindFoldersWithIndexFirstAsync(string keyword, CancellationToken cancellationToken)
        {
            var indexedPaths = ViewModel?.GetAllIndexedFolderPaths();
            if (indexedPaths != null && indexedPaths.Count > 0)
            {
                var indexedMatches = indexedPaths
                    .Where(path =>
                        !string.IsNullOrWhiteSpace(path) &&
                        PathService.DirectoryExists(path) &&
                        Path.GetFileName(path).IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path =>
                        path.Split(
                            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                            StringSplitOptions.RemoveEmptyEntries).Length)
                    .ThenBy(path => Path.GetFileName(path), WindowsNaturalStringComparer.Instance)
                    .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (indexedMatches.Count > 0)
                    return indexedMatches;
            }

            return await ShellTreeViewControl.FindFoldersByNameAsync(keyword, cancellationToken);
        }

        /// <summary>Core search + navigation logic.</summary>
        private async Task RunFindAsync(string keyword, bool forward, bool resetIndex)
        {
            // Reset UI hints
            FindNoMatchLabel.Visibility = Visibility.Collapsed;
            FindMatchLabel.Text = "";

            int requestId = Interlocked.Increment(ref _findRequestId);
            _findOperationCts?.Cancel();
            _findOperationCts?.Dispose();
            _findOperationCts = new CancellationTokenSource();
            var cancellationToken = _findOperationCts.Token;

            string trimmedKeyword = keyword?.Trim();
            if (string.IsNullOrWhiteSpace(trimmedKeyword))
            {
                _findResults.Clear();
                _findIndex = -1;
                return;
            }

            try
            {
                string currentPath = (_findIndex >= 0 && _findIndex < _findResults.Count)
                    ? _findResults[_findIndex]
                    : null;

                var refreshedResults = await FindFoldersWithIndexFirstAsync(trimmedKeyword, cancellationToken);

                if (requestId != _findRequestId)
                    return;

                _findResults = refreshedResults;

                if (_findResults.Count == 0)
                {
                    _findIndex = -1;
                }
                else if (resetIndex)
                {
                    _findIndex = 0;
                }
                else
                {
                    int anchorIndex = -1;
                    if (!string.IsNullOrEmpty(currentPath))
                    {
                        anchorIndex = _findResults.FindIndex(p => PathService.PathsEqual(p, currentPath));
                    }

                    if (anchorIndex < 0)
                    {
                        anchorIndex = forward ? -1 : 0;
                    }

                    _findIndex = forward
                        ? (anchorIndex + 1) % _findResults.Count
                        : (anchorIndex - 1 + _findResults.Count) % _findResults.Count;
                }

                if (_findResults.Count == 0)
                {
                    FindNoMatchLabel.Text = "No results";
                    FindNoMatchLabel.Visibility = Visibility.Visible;
                    return;
                }

                int attempts = _findResults.Count;
                bool navigated = false;

                while (attempts-- > 0 && _findResults.Count > 0)
                {
                    if (_findIndex < 0 || _findIndex >= _findResults.Count)
                        _findIndex = 0;

                    string target = _findResults[_findIndex];
                    if (!PathService.DirectoryExists(target))
                    {
                        _findResults.RemoveAt(_findIndex);
                        if (_findResults.Count == 0)
                        {
                            _findIndex = -1;
                            break;
                        }

                        if (_findIndex >= _findResults.Count)
                            _findIndex = 0;

                        continue;
                    }

                    navigated = await ShellTreeViewControl.NavigateToPathAsync(
                        target,
                        cancellationToken,
                        promptToChangeRoot: false,
                        centerInView: true);
                    if (navigated)
                        break;

                    // If navigation failed, drop this stale/unreachable entry and continue
                    _findResults.RemoveAt(_findIndex);
                    if (_findResults.Count == 0)
                    {
                        _findIndex = -1;
                        break;
                    }

                    if (_findIndex >= _findResults.Count)
                        _findIndex = 0;
                }

                if (requestId != _findRequestId)
                    return;

                if (!navigated || _findResults.Count == 0 || _findIndex < 0)
                {
                    FindNoMatchLabel.Text = "No results";
                    FindNoMatchLabel.Visibility = Visibility.Visible;
                    FindMatchLabel.Text = "";
                    return;
                }

                FindMatchLabel.Text = $"{_findIndex + 1} / {_findResults.Count}";
            }
            catch (OperationCanceledException)
            {
                // Ignored - a newer request superseded this one
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RunFindAsync error: {ex.Message}");
                FindNoMatchLabel.Text = "No results";
                FindNoMatchLabel.Visibility = Visibility.Visible;
            }
        }
        #endregion

        /// <summary>
        /// Safely executes the undo command and surfaces any exception as a
        /// MessageBox rather than crashing the UI thread.
        /// </summary>
        private async Task ExecuteUndoSafeAsync(CommunityToolkit.Mvvm.Input.IAsyncRelayCommand cmd)
        {
            try
            {
                await cmd.ExecuteAsync(null);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainWindow] Undo failed: {ex.Message}");
                MessageBox.Show($"Undo failed: {ex.Message}",
                    "Undo Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SearchResultListBox_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var listBox = sender as ListBox;
            if (listBox == null)
                return;

            var element = e.OriginalSource as FrameworkElement;
            var item = element != null ? FindVisualParent<ListBoxItem>(element) : null;

            var selectedFolders = GetSelectedSearchResultFolders(listBox);

            if (selectedFolders.Count == 0 && item?.DataContext is FolderInfo fallbackFolder)
            {
                selectedFolders.Add(fallbackFolder);
            }

            if (selectedFolders.Count == 0)
            {
                e.Handled = true;
                return;
            }

            var contextMenu = new ContextMenu();

            if (selectedFolders.Count == 1)
            {
                var folderInfo = selectedFolders[0];

                var loadImagesItem = new MenuItem { Header = "Load Images" };
                loadImagesItem.Click += async (s, args) =>
                {
                    await ViewModel.SetSelectedFolderAsync(folderInfo);
                };
                contextMenu.Items.Add(loadImagesItem);

                contextMenu.Items.Add(new Separator());

                var selectItem = new MenuItem { Header = "Select in Tree" };
                selectItem.Click += (s, args) =>
                {
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
                    if (ShellTreeViewControl != null)
                    {
                        await ShellTreeViewControl.RefreshTreeFull();
                    }
                };
                contextMenu.Items.Add(deleteItem);
            }
            else
            {
                var batchTagsItem = new MenuItem { Header = "Batch Tags..." };
                batchTagsItem.Click += async (s, args) =>
                {
                    await ViewModel.BatchUpdateTags(selectedFolders);
                };
                contextMenu.Items.Add(batchTagsItem);
            }

            listBox.ContextMenu = contextMenu;
        }

        private static List<FolderInfo> GetSelectedSearchResultFolders(ListBox listBox)
        {
            if (listBox == null)
                return new List<FolderInfo>();

            return listBox.SelectedItems
                .OfType<FolderInfo>()
                .Where(folder => folder != null)
                .Distinct()
                .ToList();
        }

        /// <summary>
        /// Handles Tools > Smart Author Classification click.
        /// Organizes non-[author] top-level folders into [author]/[author]folder format.
        /// </summary>
        private async void SmartClassifyFolders_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            if (menuItem != null)
            {
                menuItem.IsEnabled = false;
            }

            try
            {
                if (ViewModel == null)
                {
                    MessageBox.Show(
                        "ViewModel is not available.",
                        "Smart Classification",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                string rootDirectory = ViewModel.CurrentRootDirectory;
                if (string.IsNullOrWhiteSpace(rootDirectory))
                {
                    rootDirectory = AppSettings.Instance.DefaultRootDirectory;
                }

                if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
                {
                    MessageBox.Show(
                        "Please set a valid root directory first.",
                        "Smart Classification",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                if (ViewModel.IsIndexing)
                {
                    var proceed = MessageBox.Show(
                        "Folder indexing is in progress. Smart classification can continue, but results may be incomplete.\n\nContinue anyway?",
                        "Indexing In Progress",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    if (proceed != MessageBoxResult.Yes)
                    {
                        return;
                    }
                }

                await ViewModel.SmartClassifyRootFoldersByAuthorAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SmartClassifyFolders_Click error: {ex}");
                MessageBox.Show(
                    $"An error occurred during smart classification:\n\n{ex.Message}",
                    "Smart Classification Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                if (menuItem != null)
                {
                    menuItem.IsEnabled = true;
                }
            }
        }

        private async void AutoAssortment_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            if (menuItem != null)
            {
                menuItem.IsEnabled = false;
            }

            try
            {
                if (ViewModel == null)
                {
                    MessageBox.Show(
                        "ViewModel is not available.",
                        "Auto Assortment",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                string rootDirectory = ViewModel.CurrentRootDirectory;
                if (string.IsNullOrWhiteSpace(rootDirectory))
                {
                    rootDirectory = AppSettings.Instance.DefaultRootDirectory;
                }

                if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
                {
                    MessageBox.Show(
                        "Please set a valid root directory first.",
                        "Auto Assortment",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                string selectedSourceDirectory = PromptForAutoAssortmentSourceDirectory(rootDirectory);
                if (string.IsNullOrWhiteSpace(selectedSourceDirectory))
                {
                    return;
                }

                if (ViewModel.IsIndexing)
                {
                    var proceed = MessageBox.Show(
                        "Folder indexing is in progress. Auto assortment can continue, but the author folder list may be incomplete.\n\nContinue anyway?",
                        "Indexing In Progress",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    if (proceed != MessageBoxResult.Yes)
                    {
                        return;
                    }
                }

                AutoAssortmentPlan plan;
                try
                {
                    var service = new AutoAssortmentService();
                    plan = await Task.Run(() => service.BuildPlan(rootDirectory, selectedSourceDirectory));
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Failed to analyze folders:\n\n{ex.Message}",
                        "Auto Assortment",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                if (plan.Items.Count == 0)
                {
                    MessageBox.Show(
                        "The selected folder has no child folders to classify.",
                        "Auto Assortment",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                if (plan.AuthorTargets.Count == 0)
                {
                    MessageBox.Show(
                        "No [author] folders were found under the current root directory.",
                        "Auto Assortment",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                var dialog = new AutoAssortmentDialog(plan)
                {
                    Owner = this
                };

                if (dialog.ShowDialog() != true)
                {
                    return;
                }

                await ViewModel.AutoAssortFoldersAsync(
                    rootDirectory,
                    selectedSourceDirectory,
                    dialog.SelectedMoves);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AutoAssortment_Click error: {ex}");
                MessageBox.Show(
                    $"An error occurred during auto assortment:\n\n{ex.Message}",
                    "Auto Assortment Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                if (menuItem != null)
                {
                    menuItem.IsEnabled = true;
                }
            }
        }

        private string PromptForAutoAssortmentSourceDirectory(string rootDirectory)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select the unassorted folder under the current root directory",
                SelectedPath = rootDirectory,
                ShowNewFolderButton = false
            };

            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            {
                return null;
            }

            string selectedPath = PathService.NormalizePath(dialog.SelectedPath);
            string normalizedRoot = PathService.NormalizePath(rootDirectory);

            if (!PathService.IsPathWithin(normalizedRoot, selectedPath) ||
                PathService.PathsEqual(normalizedRoot, selectedPath))
            {
                MessageBox.Show(
                    "Please select a subfolder inside the current root directory.",
                    "Auto Assortment",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return null;
            }

            return selectedPath;
        }

        /// <summary>
        /// Handles Tools > Compression click.
        /// Prompts the user with ImageCompressionDialog, then compresses
        /// all images in the currently selected folder to WebP.
        /// </summary>
        private async void Compression_Click(object sender, RoutedEventArgs e)
        {
            var folder = ViewModel?.SelectedFolder;
            if (folder == null)
            {
                MessageBox.Show(
                    "Please select a folder first.",
                    "No Folder Selected",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }


            var dialog = new ImageCompressionDialog(folder.FolderPath, folder.Name) { Owner = this };
            if (dialog.ShowDialog() != true)
                return;

            int quality = dialog.Quality;
            bool deleteOrig = dialog.DeleteSourceFiles;

            var menuItem = sender as MenuItem;
            if (menuItem != null) menuItem.IsEnabled = false;
            ViewModel.StatusMessage = $"Compressing images in '{folder.Name}'...";

            var metroWindow = this as MahApps.Metro.Controls.MetroWindow;
            var progressCtrl = await metroWindow.ShowProgressAsync(
                "Compressing Images",
                $"Converting images in '{folder.Name}' to WebP...",
                isCancelable: true);
            progressCtrl.SetIndeterminate();

            try
            {
                var service = new ImageCompressionService();
                var cts = new System.Threading.CancellationTokenSource();
                progressCtrl.Canceled += (s, args) => cts.Cancel();

                var progressReporter = new Progress<double>(v =>
                {
                    progressCtrl.SetProgress(v);
                    progressCtrl.SetMessage(
                        $"Converting images in '{folder.Name}' to WebP... {(int)(v * 100)}%");
                });

                var result = await service.CompressImagesAsync(
                    folder.FolderPath, quality, deleteOrig, progressReporter, cts.Token);

                await progressCtrl.CloseAsync();

                if (result.TotalFiles == 0)
                {
                    ViewModel.StatusMessage = "No supported images found in the selected folder.";
                    MessageBox.Show(
                        "No supported image files were found in the selected folder.",
                        "Compression", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                ViewModel.StatusMessage = result.Summary;

                string msg = result.Summary;
                if (result.FailedFiles > 0)
                {
                    msg += $"\n\nFailed files ({result.FailedFiles}):\n"
                         + string.Join("\n", result.Errors.Take(10));
                    if (result.Errors.Count > 10)
                        msg += $"\n...and {result.Errors.Count - 10} more.";
                }

                MessageBox.Show(msg, "Compression Complete", MessageBoxButton.OK,
                    result.FailedFiles > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

                // Refresh thumbnails so new .webp files appear in the preview panel
                await ViewModel.LoadImagesForSelectedFolderAsync();
            }
            catch (OperationCanceledException)
            {
                await progressCtrl.CloseAsync();
                ViewModel.StatusMessage = "Compression cancelled.";
            }
            catch (Exception ex)
            {
                await progressCtrl.CloseAsync();
                ViewModel.StatusMessage = $"Compression failed: {ex.Message}";
                MessageBox.Show($"An error occurred during compression:\n\n{ex.Message}",
                    "Compression Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (menuItem != null) menuItem.IsEnabled = true;
            }
        }

        private void TagsCloud_Click(object sender, RoutedEventArgs e)
        {
            ShowTagCloudWindow();
        }

        private void ShowTagCloudWindow()
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

            if (ViewModel?.TagManagement?.TagCloud == null)
                return;

            // Create the tag cloud window - fixed property access
            var tagCloudWindow = new TagCloudWindow(ViewModel.TagManagement.TagCloud, ViewModel);

            // Set owner but don't make it modal - use Show() instead of ShowDialog()
            tagCloudWindow.Owner = this;
            tagCloudWindow.Show();
        }

        private void FindDuplicateFolders_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Disable menu item temporarily to prevent multiple dialogs
                var menuItem = sender as MenuItem;
                if (menuItem != null)
                {
                    menuItem.IsEnabled = false;
                }

                // Check if a root directory is set
                if (string.IsNullOrEmpty(AppSettings.Instance.DefaultRootDirectory))
                {
                    MessageBox.Show(
                        "Please set a root directory first before searching for duplicate folders.\n\n" +
                        "You can set the root directory from Settings -> Root Directory.",
                        "No Root Directory",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                // Check if the root directory exists
                if (!Directory.Exists(AppSettings.Instance.DefaultRootDirectory))
                {
                    MessageBox.Show(
                        $"The root directory does not exist:\n{AppSettings.Instance.DefaultRootDirectory}\n\n" +
                        "Please update the root directory in Settings.",
                        "Root Directory Not Found",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                // Check if indexing is in progress
                if (ViewModel.IsIndexing)
                {
                    var result = MessageBox.Show(
                        "Folder indexing is currently in progress. The search may not include all folders.\n\n" +
                        "Do you want to continue anyway?",
                        "Indexing In Progress",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.No)
                    {
                        return;
                    }
                }

                // Create and show the duplicate folders dialog
                var duplicateFoldersDialog = new DuplicateFoldersDialog(ViewModel)
                {
                    Owner = this
                };

                duplicateFoldersDialog.ShowDialog();
            }
            catch (Exception ex)
            {
                // Log the exception details for debugging
                System.Diagnostics.Debug.WriteLine($"FindDuplicateFolders_Click error: {ex}");

                // Show user-friendly error message
                MessageBox.Show(
                    $"An error occurred while opening the duplicate folders dialog:\n\n{ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                // Re-enable the menu item
                var menuItem = sender as MenuItem;
                if (menuItem != null)
                {
                    menuItem.IsEnabled = true;
                }
            }
        }

        private void UserGuide_Click(object sender, RoutedEventArgs e)
        {
            _ = ShowHelpWindowAsync("User Guide", GetHelpContent());
        }

        private async Task ShowHelpWindowAsync(string title, string content)
        {
            var helpWindow = new Window
            {
                Title = title,
                Width = 900,
                Height = 700,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Icon = this.Icon
            };

            var webView = new WebView2();
            webView.NavigationCompleted += (s, e) =>
            {
                webView.ExecuteScriptAsync(@"
            document.documentElement.style.overflowY = 'scroll';
            document.documentElement.style.scrollbarBaseColor = '#2D2D30';
            ");
            };

            string tempFile = Path.Combine(Path.GetTempPath(), "HelpDocumentation.html");
            File.WriteAllText(tempFile, content);

            helpWindow.Content = webView;
            helpWindow.Show();

            await webView.EnsureCoreWebView2Async();
            webView.CoreWebView2.Navigate(tempFile);
        }


        private string GetHelpContent()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "ImageFolderManager.Resources.HelpDocumentation.html";

            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8)) // 
            {
                return reader.ReadToEnd();
            }
        }

        private void About_Click(object sender, RoutedEventArgs e)
        {
            var metroWindow = this as MahApps.Metro.Controls.MetroWindow;

            metroWindow.ShowMessageAsync("About Image Folder Manager",
                "Image Folder Manager v1.0\n\n" +
                "A powerful WPF application for managing and organizing large collections of image folders.\n\n" +
                "Features:\n" +
                "- Advanced tagging system with categories\n" +
                "- Smart search functionality\n" +
                "- Tag cloud visualization\n" +
                "- Rating system (1-5 stars)\n" +
                "- Real-time folder monitoring\n" +
                "- Modern MVVM architecture\n\n" +
                "Built with .NET Framework 4.8 and WPF\n"
               );
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





}
