using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ImageFolderManager.ViewModels;

namespace ImageFolderManager.Views
{
    /// <summary>
    /// Interaction logic for ResponsiveTagCloudControl.xaml
    /// </summary>
    public partial class ResponsiveTagCloudControl : UserControl
    {
        // Event to notify parent about tag rename requests
        public event Action<string, string> TagRenamed;

        // Cache for animations
        private readonly Dictionary<string, Storyboard> _animationCache = new Dictionary<string, Storyboard>();

        public ResponsiveTagCloudControl()
        {
            InitializeComponent();

            // Handle size changes to ensure tags rewrap correctly
            this.SizeChanged += ResponsiveTagCloudControl_SizeChanged;
        }

        private void ResponsiveTagCloudControl_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // When the control size changes, ensure the WrapPanel adapts
            // This is automatically handled by the WrapPanel but we could add
            // additional logic here if needed
        }

        private void TagButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string tag)
            {
                // Get the MainViewModel
                if (Window.GetWindow(this) is TagCloudWindow tagCloudWindow &&
                    tagCloudWindow.DataContext is TagCloudViewModel viewModel)
                {
                    e.Handled = true;

                    // Notify for search by tag
                    // We'll handle this in the parent window
                    SearchForTag(tag);

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

                // Add "Copy Tag" menu item
                var copyItem = new MenuItem { Header = "Copy Tag" };
                copyItem.Click += (s, args) => CopyTagToClipboard(tag);
                contextMenu.Items.Add(copyItem);

                // Show context menu
                contextMenu.IsOpen = true;
            }
        }

        private void CopyTagToClipboard(string tag)
        {
            try
            {
                Clipboard.SetText($"#{tag}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error copying tag: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SearchForTag(string tag)
        {
            // Get the parent window
            var parentWindow = Window.GetWindow(this);
            if (parentWindow is TagCloudWindow tagCloudWindow && tagCloudWindow.MainViewModel != null)
            {
                // Set the search text to search for this tag and execute search
                tagCloudWindow.MainViewModel.SearchText = $"#{tag}";
                tagCloudWindow.MainViewModel.SearchCommand.Execute(null);

                // Optionally close the tag cloud window after search
                // parentWindow.Close();
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
                        // Get parent window
                        if (Window.GetWindow(this) is TagCloudWindow tagCloudWindow &&
                            tagCloudWindow.DataContext is TagCloudViewModel viewModel)
                        {
                            // Notify any subscribers
                            TagRenamed?.Invoke(currentTag, dialog.NewTag);

                            // Force tag cloud to refresh
                            viewModel.InvalidateCache();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error showing rename dialog: {ex.Message}");
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
    }
}