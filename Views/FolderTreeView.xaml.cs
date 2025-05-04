using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ImageFolderManager.Models;
using ImageFolderManager.ViewModels;

namespace ImageFolderManager.Views
{
    public partial class FolderTreeView : UserControl
    {
        // Event to notify when a folder is selected
        public event Action<FolderInfo> FolderSelected;

        // Reference to the main view model
        private MainViewModel ViewModel => DataContext as MainViewModel;

        // For drag and drop operations
        private Point _startPoint;
        private bool _isDragging;
        private FolderInfo _draggedItem;

        public FolderTreeView()
        {
            InitializeComponent();
        }

        // Event Handlers

        private void TreeViewItem_Expanded(object sender, RoutedEventArgs e)
        {
            if (sender is TreeViewItem item && item.DataContext is FolderInfo folder)
            {
                folder.LoadChildren();
                folder.IsExpanded = true;

                // Make sure to watch this folder and its children
                ViewModel._fileSystemWatcher.WatchFolder(folder);

                foreach (var child in folder.Children)
                {
                    if (child != null)
                    {
                        ViewModel._fileSystemWatcher.WatchFolder(child);
                    }
                }

                e.Handled = true; // Prevent event bubbling
            }
        }

        private void TreeView_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            // Get the clicked item
            var treeViewItem = FindVisualParent<TreeViewItem>((DependencyObject)e.OriginalSource);
            if (treeViewItem == null) return;

            var clickedFolder = treeViewItem.DataContext as FolderInfo;
            if (clickedFolder == null) return;

            // Create context menu
            CreateContextMenu(clickedFolder);
        }

        private void CreateContextMenu(FolderInfo folder)
        {
            if (folder == null) return;

            ContextMenu contextMenu = new ContextMenu();

            var cutItem = new MenuItem { Header = "Cut" };
            cutItem.Click += (s, e) => CutFolder(folder);
            contextMenu.Items.Add(cutItem);

            var copyItem = new MenuItem { Header = "Copy" };
            copyItem.Click += (s, e) => CopyFolder(folder);
            contextMenu.Items.Add(copyItem);

            // Add paste option if clipboard has content
            var pasteItem = new MenuItem();
            if (HasClipboardContent())
            {
                // Make it clear where the paste will happen
                pasteItem.Header = $"Paste into '{folder.Name}'";
                pasteItem.IsEnabled = true;
            }
            else
            {
                pasteItem.Header = "Paste";
                pasteItem.IsEnabled = false;
            }
            //pasteItem.IsEnabled = HasClipboardContent();
            pasteItem.Click += (s, e) => PasteFolder(folder);
            contextMenu.Items.Add(pasteItem);

            contextMenu.Items.Add(new Separator());

            var newFolderItem = new MenuItem { Header = "New Folder" };
            newFolderItem.Click += (s, e) => CreateNewFolder(folder);
            contextMenu.Items.Add(newFolderItem);

            var renameItem = new MenuItem { Header = "Rename" };
            renameItem.Click += (s, e) => RenameFolder(folder);
            contextMenu.Items.Add(renameItem);

            contextMenu.Items.Add(new Separator());

            var showItem = new MenuItem { Header = "Show in Explorer" };
            showItem.Click += (s, e) => ShowInExplorer(folder);
            contextMenu.Items.Add(showItem);

            var deleteItem = new MenuItem { Header = "Delete" };
            deleteItem.Click += (s, e) => DeleteFolder(folder);
            contextMenu.Items.Add(deleteItem);

            // Set the context menu to the treeview
            FolderTreeViewControl.ContextMenu = contextMenu;
        }

        // Drag and drop handlers

        private void TreeView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Store the mouse position for potential drag operation
            _startPoint = e.GetPosition(null);
        }

        private void TreeView_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && !_isDragging)
            {
                Point position = e.GetPosition(null);

                // Check if the mouse has moved far enough to initiate drag
                if (Math.Abs(position.X - _startPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(position.Y - _startPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    StartDrag(e);
                }
            }
        }

        private void TreeView_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isDragging = false;
        }

        private void TreeView_DragOver(object sender, DragEventArgs e)
        {
            // Verify the data format
            if (!e.Data.GetDataPresent("FolderInfo"))
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            // Get the item under the cursor
            var targetItem = GetTreeViewItemUnderMouse(e.GetPosition(FolderTreeViewControl));
            if (targetItem == null)
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            var targetFolder = targetItem.DataContext as FolderInfo;
            var draggedItem = e.Data.GetData("FolderInfo") as FolderInfo;

            // Check if we're trying to drop into itself or its child
            if (draggedItem != null && (
                draggedItem == targetFolder ||
                targetFolder.FolderPath.StartsWith(draggedItem.FolderPath + Path.DirectorySeparatorChar)))
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            // Valid drop target
            e.Effects = DragDropEffects.Move;
            e.Handled = true;

            // Highlight the target item
            HighlightDropTarget(targetItem);
        }

        private void TreeView_Drop(object sender, DragEventArgs e)
        {
            // Clear any highlight
            ClearDropTargetHighlight();

            if (!e.Data.GetDataPresent("FolderInfo"))
                return;

            // Get the drop target
            var targetItem = GetTreeViewItemUnderMouse(e.GetPosition(FolderTreeViewControl));
            if (targetItem == null)
                return;

            var targetFolder = targetItem.DataContext as FolderInfo;
            var draggedItem = e.Data.GetData("FolderInfo") as FolderInfo;

            if (targetFolder != null && draggedItem != null)
            {
                MoveFolder(draggedItem, targetFolder);
            }
        }

        // Helper Methods

        private void StartDrag(MouseEventArgs e)
        {
            // Get the selected item for dragging
            _draggedItem = FolderTreeViewControl.SelectedItem as FolderInfo;
            if (_draggedItem == null)
                return;

            _isDragging = true;

            DataObject dragData = new DataObject("FolderInfo", _draggedItem);
            DragDrop.DoDragDrop(FolderTreeViewControl, dragData, DragDropEffects.Move);
        }

        private TreeViewItem GetTreeViewItemUnderMouse(Point mousePosition)
        {
            HitTestResult result = VisualTreeHelper.HitTest(FolderTreeViewControl, mousePosition);

            if (result != null)
            {
                DependencyObject obj = result.VisualHit;

                while (obj != null && !(obj is TreeViewItem))
                {
                    obj = VisualTreeHelper.GetParent(obj);
                }

                return obj as TreeViewItem;
            }

            return null;
        }
        private void HighlightSelectedFolder(TreeViewItem item)
        {
            // Clear previous highlights
            ClearDropTargetHighlight();

            if (item != null)
            {
                // Add a more noticeable selection highlight
                item.Background = new SolidColorBrush(Color.FromArgb(120, 0, 120, 215));

                // Ensure the item is visible (scroll if needed)
                item.BringIntoView();
            }
        }

        // Add this to the TreeView_SelectedItemChanged method in FolderTreeView.xaml.cs
        private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is FolderInfo selected)
            {
                // Find the TreeViewItem for this FolderInfo
                var treeViewItem = FindTreeViewItemForFolder(FolderTreeViewControl, selected);
                if (treeViewItem != null)
                {
                    HighlightSelectedFolder(treeViewItem);
                }

                // Notify listeners
                FolderSelected?.Invoke(selected);
            }
        }

        // Helper method to find the TreeViewItem for a folder
        private TreeViewItem FindTreeViewItemForFolder(ItemsControl parent, FolderInfo folder)
        {
            if (parent == null || folder == null) return null;

            // Search through all items
            for (int i = 0; i < parent.Items.Count; i++)
            {
                var item = parent.ItemContainerGenerator.ContainerFromIndex(i) as TreeViewItem;
                if (item == null) continue;

                // Check if this is the item we're looking for
                if (item.DataContext is FolderInfo itemFolder &&
                    itemFolder.FolderPath.Equals(folder.FolderPath, StringComparison.OrdinalIgnoreCase))
                {
                    return item;
                }

                // Recursively search children
                var childItem = FindTreeViewItemForFolder(item, folder);
                if (childItem != null)
                    return childItem;
            }

            return null;
        }

        private void HighlightDropTarget(TreeViewItem item)
        {
            // Clear previous highlights
            ClearDropTargetHighlight();

            if (item != null)
            {
                // Add drop target highlight style
                item.Background = new SolidColorBrush(Color.FromArgb(80, 0, 120, 215));
            }
        }

        private void ClearDropTargetHighlight()
        {
            // Find all TreeViewItems and clear their background
            var allItems = FindVisualChildren<TreeViewItem>(FolderTreeViewControl);
            foreach (var item in allItems)
            {
                item.Background = null;
            }
        }

        // Folder manipulation methods

        private void CutFolder(FolderInfo folder)
        {
            ViewModel.CutFolder(folder);
        }

        private void CopyFolder(FolderInfo folder)
        {
            ViewModel.CopyFolder(folder);
        }

        private bool HasClipboardContent()
        {
            return ViewModel.HasClipboardContent();
        }

        private void PasteFolder(FolderInfo targetFolder)
        {
            ViewModel.PasteFolder(targetFolder);
        }

        private async void CreateNewFolder(FolderInfo parentFolder)
        {
            await ViewModel.CreateNewFolder(parentFolder);
        }

        private async void RenameFolder(FolderInfo folder)
        {
            await ViewModel.RenameFolder(folder);
        }

        private void ShowInExplorer(FolderInfo folder)
        {
            ViewModel.ShowInExplorer(folder);
        }

        private void DeleteFolder(FolderInfo folder)
        {
            ViewModel.DeleteFolderCommand.ExecuteAsync(folder);
        }

        private void MoveFolder(FolderInfo sourceFolder, FolderInfo targetFolder)
        {
            ViewModel.MoveFolder(sourceFolder, targetFolder);
        }

        // Visual tree helper methods

        public static T FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject parentObject = VisualTreeHelper.GetParent(child);
            if (parentObject == null) return null;
            T parent = parentObject as T;
            if (parent != null) return parent;
            return FindVisualParent<T>(parentObject);
        }

        public static System.Collections.Generic.IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) yield break;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);

                if (child != null && child is T)
                    yield return (T)child;

                foreach (T descendant in FindVisualChildren<T>(child))
                    yield return descendant;
            }
        }
    }
}