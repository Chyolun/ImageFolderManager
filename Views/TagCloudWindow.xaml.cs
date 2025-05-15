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

                // Add "Add Tag" menu item - NEW
                MenuItem addTagItem = new MenuItem { Header = "Add Tag" };
                addTagItem.Click += (s, args) => AddTagToTagInput(tag);
                contextMenu.Items.Add(addTagItem);

                // Add separator
                contextMenu.Items.Add(new Separator());

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
                        if (StatusText != null)
                        {
                            StatusText.Text = $"Added tag #{tag} to tag input";
                        }
                    }
                    else
                    {
                        // Tag already exists in edit area
                        if (StatusText != null)
                        {
                            StatusText.Text = $"Tag #{tag} already exists in tag input";
                        }
                    }
                }
                else
                {
                    // MainViewModel is not available
                    if (StatusText != null)
                    {
                        StatusText.Text = "Cannot add tag: Main view model not available";
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error adding tag to tag input: {ex.Message}");
                if (StatusText != null)
                {
                    StatusText.Text = "Error adding tag to tag input";
                }
            }
        }

        private void CopyTagToClipboard(string tag)
        {
            try
            {
                // Use SetText instead of SetDataObject for better reliability
                Clipboard.SetText($"#{tag}");

                // Update status text on success
                if (StatusText != null)
                {
                    StatusText.Text = $"Copied #{tag} to clipboard";
                }
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
                    if (StatusText != null)
                    {
                        StatusText.Text = "Could not copy tag to clipboard";
                    }

                    // Use a less intrusive status message instead of a popup
                    if (StatusText != null)
                    {
                        StatusText.Foreground = new SolidColorBrush(Colors.OrangeRed);
                        StatusText.Text = "Could not copy to clipboard. Please try again.";

                        // Reset color after a delay
                        var timer = new System.Windows.Threading.DispatcherTimer();
                        timer.Interval = TimeSpan.FromSeconds(3);
                        timer.Tick += (s, args) => {
                            StatusText.Foreground = new SolidColorBrush(Colors.LightGray);
                            timer.Stop();
                        };
                        timer.Start();
                    }
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