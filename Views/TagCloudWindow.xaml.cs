using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ImageFolderManager.ViewModels;
using ImageFolderManager.Models;
using MahApps.Metro.Controls;
using System.Globalization;
using System.Windows.Data;
using ImageFolderManager.Services;

namespace ImageFolderManager.Views
{
    /// <summary>
    /// Enhanced TagCloudWindow with category support
    /// </summary>
    public partial class TagCloudWindow : MetroWindow
    {
        private readonly MainViewModel _mainViewModel;
        private readonly Dictionary<string, Storyboard> _animationCache = new Dictionary<string, Storyboard>();
        private ICollectionView _tagItemsView;
        private TagCloudViewModel _observedViewModel;

        // Drag and drop support
        private Point _dragStartPoint;
        private bool _isDragging;
        private TagCloudItem _draggedTag;

        // Cut/paste support for tags
        private List<TagCloudItem> _cutTags = new List<TagCloudItem>();

        public MainViewModel MainViewModel => _mainViewModel;

        public static readonly DependencyProperty HasVisibleTagsProperty =
            DependencyProperty.Register(
                nameof(HasVisibleTags),
                typeof(bool),
                typeof(TagCloudWindow),
                new PropertyMetadata(true));

        public static readonly DependencyProperty EmptyStateMessageProperty =
            DependencyProperty.Register(
                nameof(EmptyStateMessage),
                typeof(string),
                typeof(TagCloudWindow),
                new PropertyMetadata("Try clearing the filter or switch to another category."));

        public bool HasVisibleTags
        {
            get => (bool)GetValue(HasVisibleTagsProperty);
            set => SetValue(HasVisibleTagsProperty, value);
        }

        public string EmptyStateMessage
        {
            get => (string)GetValue(EmptyStateMessageProperty);
            set => SetValue(EmptyStateMessageProperty, value);
        }

        public TagCloudWindow(TagCloudViewModel viewModel, MainViewModel mainViewModel)
        {
            InitializeComponent();

            DataContext = viewModel;
            _mainViewModel = mainViewModel;
            this.SizeChanged += TagCloudWindow_SizeChanged;
            this.Loaded += TagCloudWindow_Loaded;
            this.Closed += TagCloudWindow_Closed;
            this.PreviewKeyDown += TagCloudWindow_PreviewKeyDown;
        }

        private void TagCloudWindow_Loaded(object sender, RoutedEventArgs e)
        {
            AttachViewModelSubscriptions(DataContext as TagCloudViewModel);
            InitializeTagItemsView();
            ApplySortMode();
            RefreshFilterAndSummary();

            if (_observedViewModel?.TagItems != null && _observedViewModel.TagItems.Count == 0)
            {
                StatusText.Text = "No tags found yet. Add tags to folders to build your cloud.";
            }
        }

        private void TagCloudWindow_Closed(object sender, EventArgs e)
        {
            DetachViewModelSubscriptions();
        }

        private void TagCloudWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                TagFilterTextBox.Focus();
                TagFilterTextBox.SelectAll();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape && !string.IsNullOrEmpty(TagFilterTextBox.Text))
            {
                TagFilterTextBox.Clear();
                e.Handled = true;
            }
        }

        private void AttachViewModelSubscriptions(TagCloudViewModel viewModel)
        {
            if (ReferenceEquals(_observedViewModel, viewModel))
                return;

            DetachViewModelSubscriptions();
            _observedViewModel = viewModel;

            if (_observedViewModel == null)
                return;

            _observedViewModel.PropertyChanged += ObservedViewModel_PropertyChanged;

            if (_observedViewModel.TagItems != null)
            {
                _observedViewModel.TagItems.CollectionChanged += TagItems_CollectionChanged;
            }

            if (_observedViewModel.Categories != null)
            {
                foreach (var category in _observedViewModel.Categories)
                {
                    category.PropertyChanged += Category_PropertyChanged;
                }

                _observedViewModel.Categories.CollectionChanged += Categories_CollectionChanged;
            }
        }

        private void DetachViewModelSubscriptions()
        {
            if (_observedViewModel == null)
                return;

            _observedViewModel.PropertyChanged -= ObservedViewModel_PropertyChanged;

            if (_observedViewModel.TagItems != null)
            {
                _observedViewModel.TagItems.CollectionChanged -= TagItems_CollectionChanged;
            }

            if (_observedViewModel.Categories != null)
            {
                foreach (var category in _observedViewModel.Categories)
                {
                    category.PropertyChanged -= Category_PropertyChanged;
                }

                _observedViewModel.Categories.CollectionChanged -= Categories_CollectionChanged;
            }

            _observedViewModel = null;
        }

        private void InitializeTagItemsView()
        {
            if (!(FindResource("TagItemsViewSource") is CollectionViewSource collectionViewSource))
                return;

            _tagItemsView = collectionViewSource.View;
            if (_tagItemsView == null)
                return;

            _tagItemsView.Filter -= TagItemsFilter;
            _tagItemsView.Filter += TagItemsFilter;
        }

        private bool TagItemsFilter(object item)
        {
            if (!(item is TagCloudItem tagItem))
                return false;

            string filter = TagFilterTextBox?.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(filter))
                return true;

            return tagItem.Tag.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   tagItem.Category.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ApplySortMode()
        {
            if (_tagItemsView == null)
                return;

            using (_tagItemsView.DeferRefresh())
            {
                _tagItemsView.SortDescriptions.Clear();

                int sortIndex = SortModeComboBox?.SelectedIndex ?? 0;
                switch (sortIndex)
                {
                    case 1:
                        _tagItemsView.SortDescriptions.Add(new SortDescription(nameof(TagCloudItem.Tag), ListSortDirection.Ascending));
                        break;
                    case 2:
                        _tagItemsView.SortDescriptions.Add(new SortDescription(nameof(TagCloudItem.Tag), ListSortDirection.Descending));
                        break;
                    default:
                        _tagItemsView.SortDescriptions.Add(new SortDescription(nameof(TagCloudItem.Count), ListSortDirection.Descending));
                        _tagItemsView.SortDescriptions.Add(new SortDescription(nameof(TagCloudItem.Tag), ListSortDirection.Ascending));
                        break;
                }
            }
        }

        private void RefreshFilterAndSummary()
        {
            _tagItemsView?.Refresh();
            UpdateSummaryPanel();
            UpdateEmptyState();
            UpdateWindowTitle();
        }

        private void UpdateSummaryPanel()
        {
            var viewModel = DataContext as TagCloudViewModel;
            if (viewModel == null)
                return;

            int totalTags = viewModel.Categories?.Sum(category => category.TagCount) ?? 0;
            int totalCategories = viewModel.Categories?.Count ?? 0;
            int visibleCount = _tagItemsView?.Cast<object>().Count() ?? viewModel.TagItems?.Count ?? 0;
            int selectedCategoryCount = viewModel.SelectedCategory?.TagCount ?? 0;
            string categoryName = viewModel.SelectedCategory?.Name ?? "All Categories";

            HeaderSummaryText.Text = $"{totalTags} tag references across {totalCategories} categories";
            SelectedCategorySummaryText.Text =
                $"Viewing {categoryName}. Showing {visibleCount} tag{(visibleCount == 1 ? string.Empty : "s")} out of {selectedCategoryCount}.";
            VisibleTagStatsText.Text = $"{visibleCount} shown";
            FilterHintText.Text = string.IsNullOrWhiteSpace(TagFilterTextBox?.Text)
                ? "Ctrl+F to focus filter"
                : $"Filter active: \"{TagFilterTextBox.Text.Trim()}\"";
        }

        private void UpdateEmptyState()
        {
            int visibleCount = _tagItemsView?.Cast<object>().Count() ?? 0;
            HasVisibleTags = visibleCount > 0;

            if (HasVisibleTags)
                return;

            EmptyStateMessage = string.IsNullOrWhiteSpace(TagFilterTextBox?.Text)
                ? "This category is empty right now. Add tags or drag a tag here to start organizing."
                : "No tags match the current filter. Clear the filter or switch to another category.";
        }

        private void ObservedViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TagCloudViewModel.SelectedCategory) ||
                e.PropertyName == nameof(TagCloudViewModel.TagItems) ||
                e.PropertyName == nameof(TagCloudViewModel.Categories))
            {
                Dispatcher.InvokeAsync(RefreshFilterAndSummary);
            }
        }

        private void TagItems_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            Dispatcher.InvokeAsync(RefreshFilterAndSummary);
        }

        private void Categories_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (TagCategory category in e.OldItems)
                {
                    category.PropertyChanged -= Category_PropertyChanged;
                }
            }

            if (e.NewItems != null)
            {
                foreach (TagCategory category in e.NewItems)
                {
                    category.PropertyChanged += Category_PropertyChanged;
                }
            }

            Dispatcher.InvokeAsync(UpdateWindowTitle);
            Dispatcher.InvokeAsync(UpdateSummaryPanel);
        }

        private void Category_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TagCategory.TagCount) ||
                e.PropertyName == nameof(TagCategory.Name) ||
                e.PropertyName == nameof(TagCategory.DisplayName))
            {
                Dispatcher.InvokeAsync(UpdateWindowTitle);
                Dispatcher.InvokeAsync(UpdateSummaryPanel);
            }
        }

        private void TagCloudWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Ensure WrapPanel adapts to window size changes
            var scrollViewer = FindName("TagScrollViewer") as ScrollViewer;
            var itemsControl = FindName("TagItemsControl") as ItemsControl;

            if (scrollViewer != null && itemsControl != null)
            {
                itemsControl.UpdateLayout();
            }
        }

        private void UpdateWindowTitle()
        {
            if (DataContext is TagCloudViewModel viewModel)
            {
                int totalTags = 0;
                int totalCategories = viewModel.Categories?.Count ?? 0;

                // Count total tags across all categories
                foreach (var category in viewModel.Categories ?? Enumerable.Empty<TagCategory>())
                {
                    totalTags += category.TagCount;
                }

                this.Title = $"Tag Cloud - {totalTags} tags in {totalCategories} categories";
            }
        }

        private void TagFilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            RefreshFilterAndSummary();
        }

        private void ClearFilterButton_Click(object sender, RoutedEventArgs e)
        {
            TagFilterTextBox.Clear();
            TagFilterTextBox.Focus();
        }

        private void SortModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplySortMode();
            RefreshFilterAndSummary();
        }

        #region Tag Button Events

        private void TagButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is TagCloudItem tag)
            {
                e.Handled = true;
                AnimateTagSelection(button);
                StatusText.Text = $"Selected #{tag.Tag}. Right-click for actions, or drag it to another category.";
            }
        }


        private void TagButton_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is Button button)
            {
                e.Handled = true;
                  
                // Extract tag information
                TagCloudItem tagItem = null;

                if (button.Tag is TagCloudItem item)
                {
                    tagItem = item;
                }
                else if (button.Tag is string tag)
                {
                    // Convert string tag to TagCloudItem for compatibility with ShowTagContextMenu
                    var parsed = TagHelper.ParseTagWithCategory(tag);
                    tagItem = new TagCloudItem
                    {
                        Tag = parsed?.TagName?? tag,
                        Category = parsed?.Category ?? "Default",
                        Count = 1, // Default count
                        FontSize = 12 // Default font size
                    };
                }

                // Call the existing ShowTagContextMenu method
                if (tagItem != null)
                {
                    ShowTagContextMenu(button, tagItem);
                }
            }
        }

        private void TagButton_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                _dragStartPoint = e.GetPosition(null);
                _isDragging = false;

                if (sender is Button button && button.Tag is TagCloudItem tag)
                {
                    _draggedTag = tag;
                }
            }
        }

        private void TagButton_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && _draggedTag != null && !_isDragging)
            {
                Point currentPosition = e.GetPosition(null);

                if (Math.Abs(currentPosition.X - _dragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(currentPosition.Y - _dragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    StartTagDrag(sender as Button);
                }
            }
        }

        private void TagButton_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _isDragging = false;
            _draggedTag = null;
        }

        #endregion

        #region Category Tab Events

        private void CategoryTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0 && e.AddedItems[0] is TagCategory category)
            {
                StatusText.Text = $"Viewing category: {category.Name} ({category.TagCount} tags)";
                RefreshFilterAndSummary();
            }
        }

        private void TabHeader_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent("TagCloudItem") && sender is StackPanel panel)
            {
                var targetCategory = panel.DataContext as TagCategory;
                var draggedTag = e.Data.GetData("TagCloudItem") as TagCloudItem;

                if (targetCategory != null && draggedTag != null)
                {
                    _ = MoveTagToCategoryAsync(draggedTag, targetCategory.Name);
                    ClearDropHighlight(panel);
                }
            }
        }

        private void TabHeader_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent("TagCloudItem"))
            {
                e.Effects = DragDropEffects.Move;
                ShowDropHighlight(sender as StackPanel);
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
        }

        private void TabHeader_DragLeave(object sender, DragEventArgs e)
        {
            ClearDropHighlight(sender as StackPanel);
        }

        private void TabHeader_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is StackPanel panel && panel.DataContext is TagCategory category)
            {
                ShowCategoryContextMenu(panel, category);
            }
        }

        private void CloseTabButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is TagCategory category)
            {
                if (category.Name != "Uncategorized")
                {
                    _ = CloseCategoryTabAsync(category);
                }
            }
        }

        #endregion

        #region Category Content Events

        private void CategoryContent_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent("TagCloudItem"))
            {
                var draggedTag = e.Data.GetData("TagCloudItem") as TagCloudItem;
                var viewModel = DataContext as TagCloudViewModel;

                if (draggedTag != null && viewModel?.SelectedCategory != null)
                {
                    _ = MoveTagToCategoryAsync(draggedTag, viewModel.SelectedCategory.Name);
                }
            }

            ClearCategoryDropHighlight(sender as Border);
        }

        private void CategoryContent_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent("TagCloudItem"))
            {
                e.Effects = DragDropEffects.Move;
                ShowCategoryDropHighlight(sender as Border);
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
        }

        private void CategoryContent_DragLeave(object sender, DragEventArgs e)
        {
            ClearCategoryDropHighlight(sender as Border);
        }

        #endregion

        #region Button Click Events

        private void AddCategoryButton_Click(object sender, RoutedEventArgs e)
        {
            string categoryName = Microsoft.VisualBasic.Interaction.InputBox(
                "Enter new category name:",
                "Add Category",
                "");

            if (!string.IsNullOrWhiteSpace(categoryName))
            {
                var viewModel = DataContext as TagCloudViewModel;
                viewModel?.AddCategory(categoryName.Trim());

                StatusText.Text = $"Added new category: {categoryName.Trim()}";
                RefreshFilterAndSummary();
            }
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                StatusText.Text = "Refreshing tag cloud...";

                if (DataContext is TagCloudViewModel viewModel)
                {
                    viewModel.InvalidateCache();
                    if (_mainViewModel != null)
                    {
                        await _mainViewModel.UpdateTagCloudAsync();
                    }
                    RefreshFilterAndSummary();
                    StatusText.Text = "Tag cloud refreshed";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error refreshing tag cloud: {ex.Message}", "Error",
                               MessageBoxButton.OK, MessageBoxImage.Error);
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
                MessageBox.Show($"Error closing window: {ex.Message}", "Error",
                               MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Drag and Drop Implementation

        private void StartTagDrag(Button button)
        {
            if (_draggedTag == null) return;

            _isDragging = true;

            // Create drag data
            DataObject dragData = new DataObject("TagCloudItem", _draggedTag);

            // Start drag operation
            DragDropEffects result = DragDrop.DoDragDrop(button, dragData, DragDropEffects.Move);

            // Clean up
            _isDragging = false;
            _draggedTag = null;
        }

        private void ShowDropHighlight(StackPanel panel)
        {
            if (panel != null)
            {
                panel.Background = TryFindResource("App.Brush.SuccessSoft") as Brush
                    ?? new SolidColorBrush(Color.FromArgb(80, 60, 177, 121));
            }
        }

        private void ClearDropHighlight(StackPanel panel)
        {
            if (panel != null)
            {
                panel.Background = Brushes.Transparent;
            }
        }

        private void ShowCategoryDropHighlight(Border border)
        {
            if (border != null)
            {
                border.Tag = "DragOver";
            }
        }

        private void ClearCategoryDropHighlight(Border border)
        {
            if (border != null)
            {
                border.Tag = null;
            }
        }

        #endregion

        #region Context Menus
        private void ShowTagContextMenu(Button button, TagCloudItem tag)
        {
            var contextMenu = new ContextMenu();

            // Add "Search" menu item
            var searchItem = new MenuItem { Header = "Search" };
            searchItem.Click += (s, args) => SearchTag(tag);
            contextMenu.Items.Add(searchItem);

            contextMenu.Items.Add(new Separator());
            // Add "Search" menu item
            var addItem = new MenuItem { Header = "Add tag to editbox" };
            addItem.Click += (s, args) => AddTagToTagInput(tag.Tag);
            contextMenu.Items.Add(addItem);

            // Add "Copy to Clipboard" menu item
            var copyItem = new MenuItem { Header = "Copy to Clipboard" };
            copyItem.Click += (s, args) => CopyTagToClipboard(tag.Tag);
            contextMenu.Items.Add(copyItem);

            // Add "Move to Category" submenu
            var moveToItem = new MenuItem { Header = "Move to Category" };
            var viewModel = DataContext as TagCloudViewModel;

            if (viewModel?.Categories != null)
            {
                foreach (var category in viewModel.Categories)
                {
                    if (category.Name != tag.Category)
                    {
                        var categoryItem = new MenuItem { Header = category.Name };
                        categoryItem.Click += (s, args) => _ = MoveTagToCategoryAsync(tag, category.Name);
                        moveToItem.Items.Add(categoryItem);
                    }
                }
            }
            contextMenu.Items.Add(moveToItem);

            // Add "Rename Tag" menu item
            var renameItem = new MenuItem { Header = "Rename Tag" };
            renameItem.Click += (s, args) => _ = ShowRenameTagDialogAsync(tag.Tag);
            contextMenu.Items.Add(renameItem);

            contextMenu.Items.Add(new Separator());
            // Add "Delete Tag" menu item (new)
            var deleteItem = new MenuItem { Header = "Delete Tag" };
            deleteItem.Click += (s, args) => _ = DeleteTagPromptAsync(tag.Tag);
            contextMenu.Items.Add(deleteItem);

            // Show context menu
            contextMenu.PlacementTarget = button;
            contextMenu.IsOpen = true;
        }



        private void ShowCategoryContextMenu(StackPanel panel, TagCategory category)
        {
            var contextMenu = new ContextMenu();

            // Add "Rename Category" menu item (not for Uncategorized)
            if (category.Name != "Uncategorized")
            {
                var renameItem = new MenuItem { Header = "Rename Category" };
                renameItem.Click += (s, args) => _ = RenameCategoryDialogAsync(category);
                contextMenu.Items.Add(renameItem);

                contextMenu.Items.Add(new Separator());

                // Add "Delete Category" menu item
                var deleteItem = new MenuItem { Header = "Delete Category" };
                deleteItem.Click += (s, args) => _ = CloseCategoryTabAsync(category);
                contextMenu.Items.Add(deleteItem);
            }

            // Show context menu
            contextMenu.PlacementTarget = panel;
            contextMenu.IsOpen = true;
        }

        #endregion

        #region Tag Operations

        private async Task MoveTagToCategoryAsync(TagCloudItem tag, string newCategory)
        {
            if (tag.Category == newCategory) return;

            try
            {
                string oldCategory = tag.Category;

                if (_mainViewModel != null)
                {
                    await _mainViewModel.MoveTagToCategoryAsync(
                        tag.Tag,
                        oldCategory,
                        newCategory,
                        _mainViewModel.GetAllIndexedFolderPaths());
                }
                else
                {
                    var viewModel = DataContext as TagCloudViewModel;
                    if (viewModel != null)
                    {
                        await viewModel.MoveTagToCategoryAsync(tag, newCategory);
                    }
                }

                StatusText.Text = $"Moved tag '{tag.Tag}' from '{oldCategory}' to '{newCategory}'";
                RefreshFilterAndSummary();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error moving tag to category: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                StatusText.Text = "Error moving tag to category";
            }
        }

        private void AddTagToTagInput(string tag)
        {
            try
            {
                if (_mainViewModel != null)
                {
                    string currentText = _mainViewModel.TagInputText ?? string.Empty;

                    if (!currentText.Contains($"#{tag}"))
                    {
                        if (!string.IsNullOrWhiteSpace(currentText) && !currentText.EndsWith(" "))
                        {
                            currentText += " ";
                        }

                        currentText += $"#{tag}";
                        _mainViewModel.TagInputText = currentText;
                        StatusText.Text = $"Added tag #{tag} to tag input";
                    }
                    else
                    {
                        StatusText.Text = $"Tag #{tag} already exists in tag input";
                    }
                }
                else
                {
                    StatusText.Text = "Cannot add tag: Main view model not available";
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error adding tag to tag input: {ex.Message}");
                StatusText.Text = "Error adding tag to tag input";
            }
        }


        private void SearchTag(TagCloudItem tag)
        {
            if (_mainViewModel != null)
            {
                _mainViewModel.SearchText = $"#{tag.Tag}";
                _mainViewModel.SearchCommand.Execute(null);
                StatusText.Text = $"Searching for #{tag.Tag} in category '{tag.Category}'";
            }
        }

        private void CopyTagToClipboard(string tag)
        {
            try
            {
                Clipboard.SetText($"#{tag}");
                StatusText.Text = $"Copied #{tag} to clipboard";
            }
            catch (Exception)
            {
                StatusText.Text = "Could not copy tag to clipboard";
            }
        }

        private async Task ShowRenameTagDialogAsync(string currentTag)
        {
            try
            {
                // Get the original tag's category from the currently selected tag item
                string category = "Uncategorized";

                // Find the actual tag item that was clicked to get exact category
                var tagItem = DataContext is TagCloudViewModel viewModel
                    ? viewModel.TagItems.FirstOrDefault(t => t.Tag.Equals(currentTag, StringComparison.OrdinalIgnoreCase))
                    : null;

                if (tagItem != null)
                {
                    category = tagItem.Category;
                }

                var dialog = new RenameTagDialog(currentTag);
                dialog.Owner = this;

                bool? dialogResult = dialog.ShowDialog();

                if (dialogResult == true && !string.IsNullOrEmpty(dialog.NewTag))
                {
                    // Get the new tag name from the dialog
                    string newTag = dialog.NewTag;

                    // Always preserve the original category
                    string newTagWithCategory = $"{category}::{newTag}";

                    // Update status
                    StatusText.Text = $"Renaming tag '{currentTag}' to '{newTag}'...";

                    // Get all indexed folder paths from the UnifiedFolderService via MainViewModel
                    var folderPaths = _mainViewModel.GetAllIndexedFolderPaths();

                    // Call the MainViewModel's RenameTag method with the folder paths and preserved category
                    await _mainViewModel.RenameTag(currentTag, newTagWithCategory, folderPaths);

                    // Update status
                    StatusText.Text = $"Tag renamed to '{newTag}' (category: {category})";

                    // Trigger refresh
                    RefreshButton_Click(null, null);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error showing rename dialog: {ex.Message}");
                MessageBox.Show($"Error renaming tag: {ex.Message}", "Error",
                               MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task DeleteTagPromptAsync(string tag)
        {
            if (string.IsNullOrEmpty(tag))
                return;

            // Ask for confirmation
            var result = MessageBox.Show(
                $"Are you sure you want to delete the tag '{tag}' from all folders?\nThis action cannot be undone.",
                "Delete Tag",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    StatusText.Text = $"Deleting tag '{tag}' from all folders...";

                    if (_mainViewModel != null)
                    {
                        // Get all folder paths
                        var folderPaths = _mainViewModel.GetAllIndexedFolderPaths();

                        // Call the service to delete the tag
                        await _mainViewModel.DeleteTagFromAllFoldersAsync(tag, folderPaths);
                        StatusText.Text = $"Tag '{tag}' has been deleted from all folders.";
                        RefreshFilterAndSummary();
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error deleting tag: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    StatusText.Text = "Error deleting tag.";
                }
            }
        }


        #endregion

        #region Category Operations

        private async Task RenameCategoryDialogAsync(TagCategory category)
        {
            if (category.Name == "Uncategorized")
            {
                MessageBox.Show("The 'Uncategorized' category cannot be renamed.",
                    "Operation Not Allowed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            string originalName = category.Name; // Store the original name before it changes

            string newName = Microsoft.VisualBasic.Interaction.InputBox(
                "Enter new category name:",
                "Rename Category",
                category.Name);

            if (!string.IsNullOrWhiteSpace(newName) && newName != category.Name)
            {
                try
                {
                    if (_mainViewModel != null)
                    {
                        await _mainViewModel.RenameCategoryAsync(
                            category.Name,
                            newName,
                            _mainViewModel.GetAllIndexedFolderPaths());
                    }
                    else
                    {
                        var viewModel = DataContext as TagCloudViewModel;
                        viewModel?.RenameCategory(category.Name, newName);
                    }

                    StatusText.Text = $"Category renamed from '{originalName}' to '{newName}'"; // Use originalName
                    RefreshFilterAndSummary();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error renaming category: {ex.Message}",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);

                    StatusText.Text = "Error renaming category";
                }
            }
        }

        private async Task CloseCategoryTabAsync(TagCategory category)
        {
            if (category.Name == "Uncategorized")
            {
                MessageBox.Show("The 'Uncategorized' category cannot be deleted.",
                    "Operation Not Allowed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show(
                $"Are you sure you want to delete the category '{category.Name}'?\n\n" +
                $"All tags in this category will be moved to 'Uncategorized'.",
                "Delete Category",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    if (_mainViewModel != null)
                    {
                        await _mainViewModel.DeleteCategoryAsync(
                            category.Name,
                            _mainViewModel.GetAllIndexedFolderPaths());
                    }
                    else
                    {
                        var viewModel = DataContext as TagCloudViewModel;
                        viewModel?.DeleteCategory(category.Name);
                    }

                    StatusText.Text = $"Deleted category '{category.Name}'. Tags moved to 'Uncategorized'";
                    RefreshFilterAndSummary();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error deleting category: {ex.Message}",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);

                    StatusText.Text = "Error deleting category";
                }
            }
        }
        #endregion

        #region Animation


        private void AnimateTagSelection(Button button)
        {
            if (button?.Tag is TagCloudItem tag)
            {
                string buttonTag = tag.FullTagIdentifier;

                if (!_animationCache.TryGetValue(buttonTag, out var storyboard))
                {
                    storyboard = new Storyboard();

                    ScaleTransform scaleTransform = new ScaleTransform(1, 1);
                    button.RenderTransform = scaleTransform;
                    button.RenderTransformOrigin = new Point(0.5, 0.5);

                    DoubleAnimation scaleXAnimation = new DoubleAnimation
                    {
                        From = 1.0,
                        To = 1.3,
                        Duration = TimeSpan.FromMilliseconds(150),
                        AutoReverse = true
                    };
                    Storyboard.SetTarget(scaleXAnimation, button);
                    Storyboard.SetTargetProperty(scaleXAnimation, new PropertyPath("RenderTransform.ScaleX"));

                    DoubleAnimation scaleYAnimation = new DoubleAnimation
                    {
                        From = 1.0,
                        To = 1.3,
                        Duration = TimeSpan.FromMilliseconds(150),
                        AutoReverse = true
                    };
                    Storyboard.SetTarget(scaleYAnimation, button);
                    Storyboard.SetTargetProperty(scaleYAnimation, new PropertyPath("RenderTransform.ScaleY"));

                    storyboard.Children.Add(scaleXAnimation);
                    storyboard.Children.Add(scaleYAnimation);

                    _animationCache[buttonTag] = storyboard;
                }

                foreach (Timeline timeline in storyboard.Children)
                {
                    if (timeline is DoubleAnimation animation)
                    {
                        Storyboard.SetTarget(animation, button);
                    }
                }

                storyboard.Begin();
            }
        }

        #endregion
    }

    /// <summary>
    /// Converter to hide close button for "Uncategorized" category
    /// </summary>
    public class CategoryNameToVisibilityConverter : IValueConverter
    {
        public static readonly CategoryNameToVisibilityConverter Instance = new CategoryNameToVisibilityConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string categoryName && categoryName == "Uncategorized")
            {
                return Visibility.Collapsed;
            }
            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class InverseBooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return boolValue ? Visibility.Collapsed : Visibility.Visible;
            }

            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
