using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using ImageFolderManager.Models;
using ImageFolderManager.Services;
using Microsoft.Win32;
using CommunityToolkit.Mvvm.ComponentModel;
using System.IO;
using System.Windows;
using MessageBox = System.Windows.MessageBox;
using Microsoft.VisualBasic.FileIO;

namespace ImageFolderManager.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        public ObservableCollection<FolderInfo> RootFolders { get; set; } = new();
        public ObservableCollection<ImageInfo> Images { get; set; } = new();
        public string DisplayTagLine => string.Join(" ", FolderTags.Select(tag => $"#{tag}"));
       
        private FolderInfo _selectedFolder;

        private FileSystemWatcherService _fileSystemWatcher;

        private readonly FolderService _folderService =  new FolderService();
        public FolderInfo SelectedFolder
        {
            get => _selectedFolder;
            set
            {
                if (_selectedFolder != value)
                {
                    _selectedFolder = value;
                    OnPropertyChanged();
                   
                }
            }
        }
        public async Task SetSelectedFolderAsync(FolderInfo folder)
        {
            SelectedFolder = folder;
            await LoadImagesForSelectedFolderAsync();
        }

        private FolderInfo _selectedSearchResult;
        public FolderInfo SelectedSearchResult
        {
            get => _selectedSearchResult;
            set
            {
              
                if (value != null)
                {
                    SelectedFolder = value;
                    _ = LoadImagesForSelectedFolderAsync();
                }
            }
        }

        public ObservableCollection<string> FolderTags { get; set; } = new();

        public ObservableCollection<StarModel> Stars { get; set; }
        public ICommand SetRatingCommand { get; }
        public ICommand SaveTagsCommand { get; }
        public ICommand SetRootDirectoryCommand { get; }
        public ICommand SearchCommand { get; }
        public IRelayCommand<FolderInfo> ShowInExplorerCommand { get; }
        public IAsyncRelayCommand<FolderInfo> DeleteFolderCommand { get; }


        private int _rating;
        public int Rating
        {
            get => _rating;
            set
            {
                _rating = value;
                UpdateStars();
                OnPropertyChanged();
            }
        }

        private string _searchText;
        public string SearchText
        {
            get => _searchText;
            set
            {
                _searchText = value;
                OnPropertyChanged();
            }
        }

        private readonly FolderTagService _tagService = new FolderTagService();
        public MainViewModel()
        {
            Stars = new ObservableCollection<StarModel>();
            SetRatingCommand = new RelayCommand(param => Rating = (int)param);

            // 这里改为使用 AsyncRelayCommand
            SaveTagsCommand = new AsyncRelayCommand(SaveFolderTagsAsync);

            SetRootDirectoryCommand = new RelayCommand(param => SetRootDirectory());
            SearchCommand = new RelayCommand(param => PerformSearch());

            ShowInExplorerCommand = new RelayCommand<FolderInfo>(ShowInExplorer);
            DeleteFolderCommand = new AsyncRelayCommand<FolderInfo>(DeleteFolderAsync);
            _fileSystemWatcher = new FileSystemWatcherService(_folderService, HandleFileSystemEvent);

            UpdateStars();
          
        }

        private void UpdateStars()
        {
            Stars.Clear();
            for (int i = 1; i <= 5; i++)
            {
                Stars.Add(new StarModel
                {
                    Value = i,
                    Symbol = i <= Rating ? "★" : "☆"
                });
            }
        }

        public void FolderExpanded(FolderInfo folder)
        {
            folder.LoadChildren();
            folder.IsExpanded = true;

            // Start watching this folder for changes
            _fileSystemWatcher.WatchFolder(folder);

            // Also watch all immediate child folders
            foreach (var child in folder.Children)
            {
                _fileSystemWatcher.WatchFolder(child);
            }
        }


        public async Task LoadImagesForSelectedFolderAsync()
        {
            Images.Clear();
            FolderTags.Clear();

            if (SelectedFolder == null)
                return;

            var path = SelectedFolder.FolderPath;

            // 加载评分和标签
            Rating = await _tagService.GetRatingForFolderAsync(path);
            var tags = await _tagService.GetTagsForFolderAsync(path);
            foreach (var tag in tags)
            {
                FolderTags.Add(tag);
            }

            // 异步加载缩略图并添加到 Images 集合中
            var loadedImages = await SelectedFolder.LoadImagesAsync();
            foreach (var img in loadedImages)
            {
                Images.Add(img);  // 在加载完图片后更新 UI
                Debug.WriteLine($"✅ 添加图片：{img.FileName}, 缩略图是否为空: {img.Thumbnail == null}");
            }

            OnPropertyChanged(nameof(DisplayTagLine));
        }

        private string _tagInputText;
        public string TagInputText
        {
            get => _tagInputText;
            set
            {
                _tagInputText = value;
                OnPropertyChanged();
            }
        }

        public async Task SaveFolderTagsAsync()
        {
            if (SelectedFolder == null)
                return;

            if (string.IsNullOrWhiteSpace(TagInputText))
                return; // 🚫 空内容，跳过保存

            // 把输入框里的内容按 "#" 分割后保存
            FolderTags.Clear();
            if (!string.IsNullOrWhiteSpace(TagInputText))
            {
                var parts = TagInputText.Split(new[] { '#' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var tag in parts)
                {
                    var trimmed = tag.Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                        FolderTags.Add(trimmed);
                }
            }

            // 异步保存到本地
            await _tagService.SetTagsAndRatingForFolderAsync(
                SelectedFolder.FolderPath,
                new List<string>(FolderTags),
                Rating
            );

            // 清空输入框
            TagInputText = string.Empty;

            // 通知 UI 更新展示
            OnPropertyChanged(nameof(DisplayTagLine));
            UpdateFolderMetadataInAllLoadedFolders(SelectedFolder.FolderPath, new List<string>(FolderTags), Rating);
        }

        //设置根目录
        private async void SetRootDirectory()
        {
            var dialog = new FolderBrowserDialog();
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                // Stop watching previous folders
                _fileSystemWatcher.UnwatchAllFolders();

                var path = dialog.SelectedPath;
                var folders = await _folderService.LoadFoldersRecursivelyAsync(path);

                _allLoadedFolders = folders;

                RootFolders.Clear();
                var root = folders.FirstOrDefault(f => f.FolderPath == path);
                if (root != null)
                {
                    root.LoadChildren(); // 初始化展开用
                    RootFolders.Add(root);

                    // Start watching the root folder
                    _fileSystemWatcher.WatchFolder(root);
                }
            }
        }

        public void ShowInExplorer(FolderInfo folder)
        {
            if (folder == null) return;
            if (Directory.Exists(folder.FolderPath))
            {
                System.Diagnostics.Process.Start("explorer.exe", folder.FolderPath);
            }
        }

        public async Task DeleteFolderAsync(FolderInfo folder)
        {
            if (folder == null) return;

            var result = MessageBox.Show(
                $"Are you sure you want to delete the folder:\n\n{folder.FolderPath}?",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            try
            {
                // Stop watching this folder before deletion
                _fileSystemWatcher.UnwatchFolder(folder.FolderPath);

                // 删除到回收站
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                    folder.FolderPath,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

                // Remove this folder and all subfolders from _allLoadedFolders
                RemoveFolderAndSubfoldersFromAllLoaded(folder.FolderPath);

                // 从搜索结果中移除
                RemoveFolderAndSubfoldersFromSearchResults(folder.FolderPath);

                // 从树结构中移除
                RemoveFolder(folder);

                // 自动选中其父节点
                if (folder.Parent != null)
                {
                    SelectedFolder = folder.Parent;
                    await LoadImagesForSelectedFolderAsync();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to delete folder:\n{ex.Message}");
            }
        }

        // Remove folder and all its subfolders from _allLoadedFolders list
        private void RemoveFolderAndSubfoldersFromAllLoaded(string folderPath)
        {
            // Remove the folder itself and all subfolders that start with this path
            _allLoadedFolders.RemoveAll(f => f.FolderPath == folderPath ||
                                            f.FolderPath.StartsWith(folderPath + Path.DirectorySeparatorChar));
        }

        // Remove folder and all its subfolders from search results
        private void RemoveFolderAndSubfoldersFromSearchResults(string folderPath)
        {
            // Create a temporary list to store items to remove (can't modify collection during enumeration)
            var itemsToRemove = SearchResultFolders
                .Where(f => f.FolderPath == folderPath ||
                           f.FolderPath.StartsWith(folderPath + Path.DirectorySeparatorChar))
                .ToList();

            // Remove all found items from the search results
            foreach (var item in itemsToRemove)
            {
                SearchResultFolders.Remove(item);
            }
        }

        public void RemoveFolder(FolderInfo folder)
        {
            if (folder == null) return;

            if (RootFolders.Contains(folder))
            {
                RootFolders.Remove(folder);
            }
            else if (folder.Parent != null)
            {
                // If parent is available, remove directly from parent's children
                folder.Parent.Children.Remove(folder);
            }
            else
            {
                // Fallback: search through the tree recursively
                RemoveFromChildren(RootFolders, folder);
            }
        }

        private void RemoveFromChildren(ObservableCollection<FolderInfo> folders, FolderInfo target)
        {
            foreach (var folder in folders)
            {
                if (folder == null) continue; // 避免对 null 进行访问

                if (folder.Children != null && folder.Children.Contains(target))
                {
                    folder.Children.Remove(target);
                    return;
                }

                if (folder.Children != null)
                {
                    RemoveFromChildren(folder.Children, target);
                }
            }
        }

        private void UpdateFolderMetadataInAllLoadedFolders(string folderPath, List<string> newTags, int newRating)
        {
            var folder = _allLoadedFolders.FirstOrDefault(f => f.FolderPath == folderPath);
            if (folder != null) 
            {
                folder.Tags = new ObservableCollection<string>(newTags);
                folder.Rating = newRating;
            }
        }
        public ObservableCollection<FolderInfo> SearchResultFolders { get; set; } = new();

        private List<FolderInfo> _allLoadedFolders = new();
        //搜索功能实现
        private void PerformSearch()
        {
            if (string.IsNullOrWhiteSpace(SearchText)) return;


            SearchResultFolders.Clear();


            var input = SearchText.Trim();
            var parts = input.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries);
            bool hasValidSearch = parts.Any(part => part.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                               .Any(cond => cond.StartsWith("#") || cond.StartsWith("*")));
            if (!hasValidSearch) return; // 如果没有有效的搜索条件，直接返回不执行搜索

            // 每一组 part 是 AND 的一部分，里面可能含有多个 OR 的内容
            var andGroups = new List<Func<FolderInfo, bool>>();

            foreach (var part in parts)
            {
                var conditions = part.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var orConditions = new List<Func<FolderInfo, bool>>();

                foreach (var cond in conditions)
                {
                    if (cond.StartsWith("#"))
                    {
                        var tag = cond.Substring(1).Trim().ToLowerInvariant();
                        orConditions.Add(folder => folder.Tags.Any(t => t.ToLowerInvariant().Contains(tag)));
                    }
                    else if (cond.StartsWith("*"))
                    {
                        // 支持 *>=N, *<=N, *=N, *>N, *<N
                        string pattern = cond.Substring(1).Trim(); // 去掉*
                        if (pattern.StartsWith(">="))
                        {
                            if (int.TryParse(pattern.Substring(2), out int value))
                                orConditions.Add(folder => folder.Rating >= value);
                        }
                        else if (pattern.StartsWith("<="))
                        {
                            if (int.TryParse(pattern.Substring(2), out int value))
                                orConditions.Add(folder => folder.Rating <= value);
                        }
                        else if (pattern.StartsWith("="))
                        {
                            if (int.TryParse(pattern.Substring(1), out int value))
                                orConditions.Add(folder => folder.Rating == value);
                        }
                        else if (pattern.StartsWith(">"))
                        {
                            if (int.TryParse(pattern.Substring(1), out int value))
                                orConditions.Add(folder => folder.Rating > value);
                        }
                        else if (pattern.StartsWith("<"))
                        {
                            if (int.TryParse(pattern.Substring(1), out int value))
                                orConditions.Add(folder => folder.Rating < value);
                        }
                    }
                }

                // 构建 OR 条件组
                if (orConditions.Count > 0)
                {
                    andGroups.Add(folder => orConditions.Any(cond => cond(folder)));
                }
            }

            foreach (var folder in _allLoadedFolders)
            {
                bool matches = andGroups.All(cond => cond(folder));
                if (matches)
                {
                    SearchResultFolders.Add(folder);
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private async void HandleFileSystemEvent(FolderInfo folder, FileSystemEventArgs e, WatcherChangeTypes changeType)
        {
            if (folder == null) return;

            // Get the path information
            string changedPath = e.FullPath;
            bool isDirectory = Directory.Exists(changedPath);

            switch (changeType)
            {
                case WatcherChangeTypes.Created:
                    if (isDirectory)
                    {
                        // A new directory was created
                        var newFolder = await _folderService.CreateFolderInfoWithoutImagesAsync(changedPath);
                        if (newFolder != null)
                        {
                            newFolder.Parent = folder; // Set parent relationship
                            folder.Children.Add(newFolder);

                            // Add to _allLoadedFolders for search functionality
                            _allLoadedFolders.Add(newFolder);

                            // If we're viewing this folder, refresh the UI
                            if (SelectedFolder == folder)
                            {
                                await SetSelectedFolderAsync(folder);
                            }

                            // Update search results if needed
                            if (!string.IsNullOrEmpty(SearchText))
                            {
                                PerformSearch();
                            }
                        }
                    }
                    else
                    {
                        // A new file was created
                        if (SelectedFolder == folder)
                        {
                            await LoadImagesForSelectedFolderAsync();
                        }
                    }
                    break;

                case WatcherChangeTypes.Deleted:
                    if (isDirectory)
                    {
                        // A directory was deleted
                        var deletedFolder = folder.Children.FirstOrDefault(f => f.FolderPath == changedPath);
                        if (deletedFolder != null)
                        {
                            // Stop watching this folder
                            _fileSystemWatcher.UnwatchFolder(changedPath);

                            // Remove from tree
                            folder.Children.Remove(deletedFolder);

                            // Remove folder and all subfolders from _allLoadedFolders
                            RemoveFolderAndSubfoldersFromAllLoaded(changedPath);

                            // Remove from search results
                            RemoveFolderAndSubfoldersFromSearchResults(changedPath);
                        }
                    }
                    else
                    {
                        // A file was deleted
                        if (SelectedFolder == folder)
                        {
                            await LoadImagesForSelectedFolderAsync();
                        }
                    }
                    break;

                case WatcherChangeTypes.Renamed:
                    if (e is RenamedEventArgs renamedArgs)
                    {
                        if (isDirectory)
                        {
                            // A directory was renamed
                            var renamedFolder = folder.Children.FirstOrDefault(f => f.FolderPath == renamedArgs.OldFullPath);
                            if (renamedFolder != null)
                            {
                                // Update the path
                                string oldPath = renamedFolder.FolderPath;
                                renamedFolder.FolderPath = renamedArgs.FullPath;

                                // Stop watching old path and start watching new path
                                _fileSystemWatcher.UnwatchFolder(oldPath);
                                _fileSystemWatcher.WatchFolder(renamedFolder);

                                // Force refresh of the folder
                                var index = folder.Children.IndexOf(renamedFolder);
                                folder.Children.RemoveAt(index);
                                folder.Children.Insert(index, renamedFolder);

                                // Update in _allLoadedFolders
                                UpdateFolderPathsInAllLoadedFolders(oldPath, renamedArgs.FullPath);

                                // Update search results if needed
                                if (!string.IsNullOrEmpty(SearchText))
                                {
                                    PerformSearch();
                                }
                            }
                        }
                        else
                        {
                            // A file was renamed
                            if (SelectedFolder == folder)
                            {
                                await LoadImagesForSelectedFolderAsync();
                            }
                        }
                    }
                    break;

                case WatcherChangeTypes.Changed:
                    // For most changes to directories, we don't need to do anything
                    // But for changes to files, reload if the current folder is selected
                    if (!isDirectory && SelectedFolder == folder)
                    {
                        await LoadImagesForSelectedFolderAsync();
                    }
                    break;
            }
        }

        // Helper method to update folder paths in _allLoadedFolders after rename
        private void UpdateFolderPathsInAllLoadedFolders(string oldPath, string newPath)
        {
            foreach (var folder in _allLoadedFolders.ToList())
            {
                if (folder.FolderPath == oldPath)
                {
                    folder.FolderPath = newPath;
                }
                else if (folder.FolderPath.StartsWith(oldPath + Path.DirectorySeparatorChar))
                {
                    // Update subfolders as well
                    folder.FolderPath = newPath + folder.FolderPath.Substring(oldPath.Length);
                }
            }
        }


    }

    public class StarModel
    {
        public int Value { get; set; }
        public string Symbol { get; set; }
    }


    public class RelayCommand : ICommand
    {
        private readonly Action<object> _execute;
        private readonly Predicate<object> _canExecute;

        public RelayCommand(Action<object> execute, Predicate<object> canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter) => _canExecute == null || _canExecute(parameter);

        public void Execute(object parameter) => _execute(parameter);

        public event EventHandler CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
    }
}
