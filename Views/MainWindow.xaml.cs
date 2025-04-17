using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ImageFolderManager.Models;
using ImageFolderManager.ViewModels;
using MahApps.Metro.Controls; // 添加这行

namespace ImageFolderManager
{
    public partial class MainWindow : MetroWindow // 修改这里
    {
        public MainViewModel ViewModel => DataContext as MainViewModel;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainViewModel();
        }

        private void TreeViewItem_Expanded(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is TreeViewItem item && item.DataContext is FolderInfo folder)
            {
                ViewModel.FolderExpanded(folder);
                folder.LoadChildren();
                e.Handled = true; // 防止事件继续冒泡
            }
        }

        private async void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is FolderInfo selected)
            {
                await ViewModel.SetSelectedFolderAsync(selected);
            }
        }
        private void AddTag_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.FolderTags.Add(string.Empty);
        }

        private async Task SaveTags_Click(object sender, RoutedEventArgs e)
        {
           await ViewModel.SaveFolderTagsAsync();
        }

        private void TreeView_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var treeViewItem = WPFHelper.FindVisualParent<TreeViewItem>((DependencyObject)e.OriginalSource);
            if (treeViewItem != null)
            {
                var folderInfo = treeViewItem.DataContext as FolderInfo;
                if (folderInfo != null)
                {
                    // 构建右键菜单
                    var contextMenu = new ContextMenu();

                    //// 添加"Show in Explorer"菜单项
                    var showItem = new MenuItem { Header = "Show in Explorer" };
                    showItem.Click += (s, args) =>
                    {
                        ((MainViewModel)DataContext).ShowInExplorer(folderInfo);
                    };
                    contextMenu.Items.Add(showItem);

                    // 添加"Delete"菜单项
                    var deleteItem = new MenuItem { Header = "Delete" };
                    deleteItem.Click += async (s, args) => {
                        await ((MainViewModel)DataContext).DeleteFolderCommand.ExecuteAsync(folderInfo);
                    };
                    contextMenu.Items.Add(deleteItem);

                    // 设置ContextMenu
                    treeViewItem.ContextMenu = contextMenu;
                }
            }
        }
        private void SearchResultListBox_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var listBox = sender as ListBox;
            if (listBox == null) return;

            var element = e.OriginalSource as FrameworkElement;
            if (element == null) return;

            var item = WPFHelper.FindVisualParent<ListBoxItem>(element);
            if (item == null) return;

            var folderInfo = item.DataContext as FolderInfo;
            if (folderInfo == null) return;

            var contextMenu = new ContextMenu();

            var showItem = new MenuItem { Header = "Show in Explorer" };
            showItem.Click += (s, args) =>
            {
                ((MainViewModel)DataContext).ShowInExplorer(folderInfo);
            };
            contextMenu.Items.Add(showItem);

            var deleteItem = new MenuItem { Header = "Delete" };
            deleteItem.Click += async (s, args) =>
            {
                await ((MainViewModel)DataContext).DeleteFolderCommand.ExecuteAsync(folderInfo);
            };
            contextMenu.Items.Add(deleteItem);

            item.ContextMenu = contextMenu;
        }


        // WPFHelper中的辅助方法
        public static class WPFHelper
        {
            public static T FindVisualParent<T>(DependencyObject child) where T : DependencyObject
            {
                DependencyObject parentObject = VisualTreeHelper.GetParent(child);
                if (parentObject == null) return null;
                T parent = parentObject as T;
                if (parent != null) return parent;
                return FindVisualParent<T>(parentObject);
            }
        }
        private void Image_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2 && sender is Image img && img.Tag is string filePath)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"无法打开图片：{ex.Message}");
                }
            }

        }
    }
}
