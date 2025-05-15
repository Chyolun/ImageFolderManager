using System;
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

        // Expose the MainViewModel for the responsive tag cloud control to access
        public MainViewModel MainViewModel => _mainViewModel;

        public TagCloudWindow(TagCloudViewModel viewModel, MainViewModel mainViewModel)
        {
            InitializeComponent();

            // Set the DataContext for the whole window
            this.DataContext = viewModel;
            _mainViewModel = mainViewModel;

            // Set title with tag count
            this.Loaded += (s, e) => {
                if (viewModel.TagItems != null)
                {
                    this.Title = $"Tag Cloud - {viewModel.TagItems.Count} tags";
                }
            };
        }

        private void TagItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string tag)
            {
                e.Handled = true;

                // Set search text and execute search
                if (_mainViewModel != null)
                {
                    _mainViewModel.SearchText = $"#{tag}";
                    _mainViewModel.SearchCommand.Execute(null);

                    // Animate the tag button for visual feedback
                    AnimateTagButton(button);
                }
            }
        }

        private void AnimateTagButton(Button button)
        {
            // Simple scale animation for feedback
            ScaleTransform scaleTransform = new ScaleTransform(1, 1);
            button.RenderTransform = scaleTransform;
            button.RenderTransformOrigin = new Point(0.5, 0.5);

            // Create animations
            DoubleAnimation scaleXAnimation = new DoubleAnimation
            {
                From = 1.0,
                To = 1.3,
                Duration = TimeSpan.FromMilliseconds(150),
                AutoReverse = true
            };
            DoubleAnimation scaleYAnimation = new DoubleAnimation
            {
                From = 1.0,
                To = 1.3,
                Duration = TimeSpan.FromMilliseconds(150),
                AutoReverse = true
            };

            // Apply animations
            scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnimation);
            scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnimation);
        }

        private void TagItem_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is Button button && button.Tag is string tag)
            {
                e.Handled = true;

                // Create context menu
                ContextMenu contextMenu = new ContextMenu();

                // Add "Rename Tag" menu item
                MenuItem renameItem = new MenuItem { Header = "Rename Tag" };
                renameItem.Click += (s, args) => ShowRenameTagDialog(tag);
                contextMenu.Items.Add(renameItem);

                // Add "Copy Tag" menu item
                MenuItem copyItem = new MenuItem { Header = "Copy Tag" };
                copyItem.Click += (s, args) => CopyTagToClipboard(tag);
                contextMenu.Items.Add(copyItem);

                // Show the context menu
                contextMenu.PlacementTarget = button;
                contextMenu.IsOpen = true;
            }
        }

        private void CopyTagToClipboard(string tag)
        {
            try
            {
                // Use TextDataFormat.Text explicitly for better compatibility
                Clipboard.SetDataObject($"#{tag}", true);

                // Update status text on success
                if (StatusText != null)
                {
                    StatusText.Text = $"Copied #{tag} to clipboard";
                }
            }
            catch (Exception ex)
            {
                // Even though an exception occurred, the clipboard operation might still succeed
                // because Windows may retry clipboard operations internally

                // Check if clipboard now contains our text despite the exception
                bool clipboardContainsTag = false;
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        string clipboardText = Clipboard.GetText();
                        clipboardContainsTag = clipboardText == $"#{tag}";
                    }
                }
                catch
                {
                    // Ignore errors in this check
                }

                // If the clipboard doesn't contain our text, show an error
                if (!clipboardContainsTag)
                {
                    if (StatusText != null)
                    {
                        StatusText.Text = "Could not copy tag to clipboard";
                    }

                    MessageBox.Show(
                        "Could not copy tag to clipboard. Please try again in a moment.",
                        "Clipboard Operation Failed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    // The clipboard operation actually succeeded despite the exception
                    if (StatusText != null)
                    {
                        StatusText.Text = $"Copied #{tag} to clipboard";
                    }
                }
            }
        }

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
                    if (StatusText != null)
                    {
                        StatusText.Text = $"Renaming tag '{currentTag}' to '{dialog.NewTag}'...";
                    }

                    // Execute the rename operation
                    await _mainViewModel.RenameTag(currentTag, dialog.NewTag);

                    // Update status
                    if (StatusText != null)
                    {
                        StatusText.Text = $"Tag renamed from '{currentTag}' to '{dialog.NewTag}'";
                    }

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

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Update status text
                if (StatusText != null)
                {
                    StatusText.Text = "Refreshing tag cloud...";
                }

                // Get the TagCloudViewModel from DataContext
                if (this.DataContext is TagCloudViewModel viewModel)
                {
                    viewModel.InvalidateCache();

                    // Refresh the tag cloud asynchronously
                    _mainViewModel?.UpdateTagCloudAsync().ContinueWith(_ => {
                        // Update UI on the UI thread
                        this.Dispatcher.Invoke(() => {
                            if (StatusText != null)
                            {
                                StatusText.Text = "Tag cloud refreshed";
                            }

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