using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ImageFolderManager.ViewModels;
using System.Windows.Threading;
using System.Collections.Generic;

namespace ImageFolderManager.Views
{
    public partial class TagCloudControl : UserControl
    {
        // Cache for animations
        private readonly Dictionary<string, Storyboard> _animationCache = new Dictionary<string, Storyboard>();

        // Event to notify parent about tag rename requests
        public event Action<string, string> TagRenamed;

        // Debounce timer for rapid UI updates
        private DispatcherTimer _updateTimer;

        public TagCloudControl()
        {
            InitializeComponent();

            // Setup timer for debounced updates
            _updateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _updateTimer.Tick += (s, e) =>
            {
                _updateTimer.Stop();
                RefreshVisualState();
            };

            // Listen for collection changes
            Loaded += (s, e) =>
            {
                if (DataContext is TagCloudViewModel viewModel)
                {
                    viewModel.TagItems.CollectionChanged += (sender, args) =>
                    {
                        // Debounce UI updates
                        _updateTimer.Stop();
                        _updateTimer.Start();
                    };
                }
            };
        }

        /// <summary>
        /// Refresh the visual state of the control
        /// </summary>
        private void RefreshVisualState()
        {
            // This method can be used to update virtualization or layout optimization
            // if needed in the future
        }

        private void TagButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string tag)
            {
                // Get the MainViewModel
                if (Window.GetWindow(this) is MainWindow mainWindow &&
                    mainWindow.DataContext is MainViewModel viewModel)
                {
                    e.Handled = true;

                    // Set the search text to search for this tag
                    viewModel.SearchText = $"#{tag}";

                    // Execute search
                    viewModel.SearchCommand.Execute(null);

                    // Visual feedback for tag selection
                    AnimateTagSelection(button);
                }
            }
        }

        private void TagButton_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is Button button && button.Tag is string tag)
            {
                e.Handled = true;

                // Create context menu
                var contextMenu = new ContextMenu();

                // Add "Rename Tag" menu item
                var renameItem = new MenuItem { Header = "Rename Tag" };
                renameItem.Click += (s, args) => ShowRenameTagDialog(tag);
                contextMenu.Items.Add(renameItem);

                // Add "Add to Folder" menu item
                var addToFolderItem = new MenuItem { Header = "Add to Folder" };
                addToFolderItem.Click += (s, args) => AddTagToFolder(tag);
                contextMenu.Items.Add(addToFolderItem);

                // Show context menu
                contextMenu.IsOpen = true;
            }
        }

        private async void ShowRenameTagDialog(string currentTag)
        {
            try
            {
                var dialog = new RenameTagDialog(currentTag);
                dialog.Owner = Window.GetWindow(this);

                // Use nullable boolean comparison
                bool? dialogResult = dialog.ShowDialog();

                // Check if dialogResult is true (not null and not false)
                if (dialogResult == true)
                {
                    // If user confirmed the rename
                    if (!string.IsNullOrEmpty(dialog.NewTag))
                    {
                        // Get main window and view model
                        if (Window.GetWindow(this) is MainWindow mainWindow &&
                            mainWindow.DataContext is MainViewModel viewModel)
                        {
                            // Execute tag rename in the view model
                            await viewModel.RenameTag(currentTag, dialog.NewTag);

                            // Notify any subscribers
                            TagRenamed?.Invoke(currentTag, dialog.NewTag);

                            // Force tag cloud to refresh
                            if (DataContext is TagCloudViewModel tagCloudViewModel)
                            {
                                tagCloudViewModel.InvalidateCache();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error showing rename dialog: {ex.Message}");
            }
        }

        private void AddTagToFolder(string tag)
        {
            // Get main window and view model
            if (Window.GetWindow(this) is MainWindow mainWindow &&
                mainWindow.DataContext is MainViewModel viewModel)
            {
                // Add the tag to the current tag input text
                string currentText = viewModel.TagInputText ?? string.Empty;

                // Determine if we need to add a separator
                if (!string.IsNullOrWhiteSpace(currentText))
                {
                    // Check if the current text already ends with # or whitespace
                    if (!currentText.EndsWith("#") && !char.IsWhiteSpace(currentText[currentText.Length - 1]))
                    {
                        currentText += " ";
                    }

                    // Add the tag with # prefix
                    currentText += $"#{tag}";
                }
                else
                {
                    // If the text box is empty, just add the tag with # prefix
                    currentText = $"#{tag}";
                }

                // Update the view model
                viewModel.TagInputText = currentText;

                // Optional: Focus on the tags text box
                if (mainWindow.FindName("TagsTextBox") is TextBox tagsTextBox)
                {
                    tagsTextBox.Focus();
                    tagsTextBox.CaretIndex = tagsTextBox.Text.Length;
                }
            }
        }

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
            // Use a different approach to iterate through children
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

        // Clean up on unload
        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            _updateTimer.Stop();
            _animationCache.Clear();

            if (DataContext is TagCloudViewModel viewModel)
            {
                // Unsubscribe from events if needed
            }
        }
    }
}