using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ImageFolderManager.ViewModels;
using MahApps.Metro.Controls;

namespace ImageFolderManager.Views
{
    /// <summary>
    /// Interaction logic for TagCloudWindow.xaml
    /// </summary>
    public partial class TagCloudWindow : MetroWindow
    {
        private readonly MainViewModel _mainViewModel;

        // Expose the MainViewModel for external access
        public MainViewModel MainViewModel => _mainViewModel;

        // Cache for animations to improve performance
        private readonly Dictionary<string, Storyboard> _animationCache = new Dictionary<string, Storyboard>();

        public TagCloudWindow(TagCloudViewModel viewModel, MainViewModel mainViewModel)
        {
            InitializeComponent();

            // Set DataContext for data binding
            DataContext = viewModel;
            _mainViewModel = mainViewModel;

            // Handle window load event
            this.Loaded += (s, e) => {
                if (viewModel?.TagItems != null)
                {
                    this.Title = $"Tag Cloud - {viewModel.TagItems.Count} tags";

                    // Show empty message if no tags
                    if (viewModel.TagItems.Count == 0)
                    {
                        StatusText.Text = "No tags found. Add tags to folders to see them here.";
                    }
                }
            };

            // Handle window size changes
            this.SizeChanged += TagCloudWindow_SizeChanged;
        }

        private void TagCloudWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Ensure WrapPanel adapts to window size changes
            var scrollViewer = TagScrollViewer;
            var itemsControl = TagItemsControl;

            if (scrollViewer != null && itemsControl != null)
            {
                // Refresh layout
                itemsControl.UpdateLayout();
            }
        }

        /// <summary>
        /// Handles tag button click event
        /// </summary>
        private void TagButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string tag)
            {
                e.Handled = true;

                // Set search text and execute search
                if (_mainViewModel != null)
                {
                    _mainViewModel.SearchText = $"#{tag}";
                    _mainViewModel.SearchCommand.Execute(null);

                    // Visual feedback for tag selection
                    AnimateTagSelection(button);

                    // Update status message
                    StatusText.Text = $"Searching for #{tag}";
                }
            }
        }

        /// <summary>
        /// Handles right-click on tag buttons
        /// </summary>
        private void TagButton_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is Button button && button.Tag is string tag)
            {
                e.Handled = true;

                // Create context menu
                var contextMenu = new ContextMenu();

                // Add "Add Tag" menu item - for adding to the tag input field
                var addTagItem = new MenuItem { Header = "Add Tag" };
                addTagItem.Click += (s, args) => AddTagToTagInput(tag);
                contextMenu.Items.Add(addTagItem);

                // Add separator
                contextMenu.Items.Add(new Separator());

                // Add "Rename Tag" menu item
                var renameItem = new MenuItem { Header = "Rename Tag" };
                renameItem.Click += (s, args) => ShowRenameTagDialog(tag);
                contextMenu.Items.Add(renameItem);

                // Add "Copy Tag" menu item
                var copyItem = new MenuItem { Header = "Copy Tag" };
                copyItem.Click += (s, args) => CopyTagToClipboard(tag);
                contextMenu.Items.Add(copyItem);

                // Show context menu
                contextMenu.PlacementTarget = button;
                contextMenu.IsOpen = true;
            }
        }

        /// <summary>
        /// Adds the selected tag to the tag input field in the main window
        /// </summary>
        private void AddTagToTagInput(string tag)
        {
            try
            {
                // Get the MainViewModel
                if (_mainViewModel != null)
                {
                    // Check if TagInputText already has content
                    string currentText = _mainViewModel.TagInputText ?? string.Empty;

                    // Check if tag is already in the edit area
                    if (!currentText.Contains($"#{tag}"))
                    {
                        // Add space if needed and append the tag
                        if (!string.IsNullOrWhiteSpace(currentText) && !currentText.EndsWith(" "))
                        {
                            currentText += " ";
                        }

                        // Add the tag with # prefix
                        currentText += $"#{tag}";

                        // Update the TagInputText property
                        _mainViewModel.TagInputText = currentText;

                        // Update status
                        StatusText.Text = $"Added tag #{tag} to tag input";
                    }
                    else
                    {
                        // Tag already exists in edit area
                        StatusText.Text = $"Tag #{tag} already exists in tag input";
                    }
                }
                else
                {
                    // MainViewModel is not available
                    StatusText.Text = "Cannot add tag: Main view model not available";
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error adding tag to tag input: {ex.Message}");
                StatusText.Text = "Error adding tag to tag input";
            }
        }

        /// <summary>
        /// Copies the tag to clipboard
        /// </summary>
        private void CopyTagToClipboard(string tag)
        {
            try
            {
                // Use SetText instead of SetDataObject for better reliability
                Clipboard.SetText($"#{tag}");

                // Update status text on success
                StatusText.Text = $"Copied #{tag} to clipboard";
            }
            catch (Exception ex)
            {
                // Even with an exception, the clipboard operation might still succeed
                // Let's check if the text is actually in the clipboard
                bool clipboardContainsTag = false;

                try
                {
                    // Attempt to read the clipboard on the UI thread
                    Application.Current.Dispatcher.Invoke(() => {
                        if (Clipboard.ContainsText())
                        {
                            string clipboardText = Clipboard.GetText();
                            clipboardContainsTag = clipboardText == $"#{tag}";
                        }
                    });
                }
                catch
                {
                    // Ignore errors in this check
                }

                // If the clipboard doesn't contain our text, show an error
                if (!clipboardContainsTag)
                {
                    StatusText.Text = "Could not copy tag to clipboard";

                    // Use a more noticeable color for error messages
                    StatusText.Foreground = new SolidColorBrush(Colors.OrangeRed);

                    // Reset color after a delay
                    var timer = new System.Windows.Threading.DispatcherTimer();
                    timer.Interval = TimeSpan.FromSeconds(3);
                    timer.Tick += (s, args) => {
                        StatusText.Foreground = new SolidColorBrush(Colors.LightGray);
                        timer.Stop();
                    };
                    timer.Start();
                }
                else
                {
                    // The clipboard operation actually succeeded despite the exception
                    StatusText.Text = $"Copied #{tag} to clipboard";
                }
            }
        }

        /// <summary>
        /// Shows the rename tag dialog
        /// </summary>
        private async void ShowRenameTagDialog(string currentTag)
        {
            try
            {
                var dialog = new RenameTagDialog(currentTag);
                dialog.Owner = this;

                bool? dialogResult = dialog.ShowDialog();

                if (dialogResult == true && !string.IsNullOrEmpty(dialog.NewTag))
                {
                    // Update status
                    StatusText.Text = $"Renaming tag '{currentTag}' to '{dialog.NewTag}'...";

                    // Execute the rename operation
                    await _mainViewModel.RenameTag(currentTag, dialog.NewTag);

                    // Update status
                    StatusText.Text = $"Tag renamed from '{currentTag}' to '{dialog.NewTag}'";

                    // Trigger refresh
                    RefreshButton_Click(null, null);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error showing rename dialog: {ex.Message}");
                MessageBox.Show($"Error renaming tag: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Animates tag selection for visual feedback
        /// </summary>
        private void AnimateTagSelection(Button button)
        {
            string buttonTag = button.Tag as string;

            // Try to get from cache first
            if (!_animationCache.TryGetValue(buttonTag, out var storyboard))
            {
                // Create a storyboard for animation
                storyboard = new Storyboard();

                // Scale animation for button
                ScaleTransform scaleTransform = new ScaleTransform(1, 1);
                button.RenderTransform = scaleTransform;
                button.RenderTransformOrigin = new Point(0.5, 0.5);

                // Create the animation for ScaleX
                DoubleAnimation scaleXAnimation = new DoubleAnimation
                {
                    From = 1.0,
                    To = 1.3,
                    Duration = TimeSpan.FromMilliseconds(150),
                    AutoReverse = true
                };
                Storyboard.SetTarget(scaleXAnimation, button);
                Storyboard.SetTargetProperty(scaleXAnimation, new PropertyPath("RenderTransform.ScaleX"));

                // Create the animation for ScaleY
                DoubleAnimation scaleYAnimation = new DoubleAnimation
                {
                    From = 1.0,
                    To = 1.3,
                    Duration = TimeSpan.FromMilliseconds(150),
                    AutoReverse = true
                };
                Storyboard.SetTarget(scaleYAnimation, button);
                Storyboard.SetTargetProperty(scaleYAnimation, new PropertyPath("RenderTransform.ScaleY"));

                // Add animations to storyboard
                storyboard.Children.Add(scaleXAnimation);
                storyboard.Children.Add(scaleYAnimation);

                // Cache for later reuse
                _animationCache[buttonTag] = storyboard;
            }

            // Ensure target is set correctly (could have changed)
            foreach (Timeline timeline in storyboard.Children)
            {
                if (timeline is DoubleAnimation animation)
                {
                    Storyboard.SetTarget(animation, button);
                }
            }

            // Start the animation
            storyboard.Begin();
        }

        /// <summary>
        /// Handles the refresh button click event
        /// </summary>
        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Update status text
                StatusText.Text = "Refreshing tag cloud...";

                // Get the TagCloudViewModel from DataContext
                if (DataContext is TagCloudViewModel viewModel)
                {
                    viewModel.InvalidateCache();

                    // Refresh the tag cloud asynchronously
                    _mainViewModel?.UpdateTagCloudAsync().ContinueWith(_ => {
                        // Update UI on the UI thread
                        this.Dispatcher.Invoke(() => {
                            StatusText.Text = "Tag cloud refreshed";

                            // Update title with new count
                            if (viewModel.TagItems != null)
                            {
                                this.Title = $"Tag Cloud - {viewModel.TagItems.Count} tags";
                            }
                        });
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error refreshing tag cloud: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Handles the close button click event
        /// </summary>
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                this.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error closing window: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
