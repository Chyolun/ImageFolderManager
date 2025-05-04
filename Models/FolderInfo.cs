using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace ImageFolderManager.Models
{
    public class FolderInfo : INotifyPropertyChanged
    {
        private string _folderPath;
        public string FolderPath
        {
            get => _folderPath;
            set
            {
                if (_folderPath != value)
                {
                    _folderPath = value;
                    OnPropertyChanged();
                    // Also notify that Name property has changed since it depends on FolderPath
                    OnPropertyChanged(nameof(Name));
                }
            }
        }

        public FolderInfo Parent { get; set; }

        private ObservableCollection<FolderInfo> _children;
        public ObservableCollection<FolderInfo> Children
        {
            get => _children;
            set
            {
                _children = value;
                OnPropertyChanged();
            }
        }

        private ObservableCollection<ImageInfo> _images;
        public ObservableCollection<ImageInfo> Images
        {
            get => _images;
            set
            {
                _images = value;
                OnPropertyChanged();
            }
        }

        private ObservableCollection<string> _tags = new();
        public ObservableCollection<string> Tags
        {
            get => _tags;
            set
            {
                _tags = value;
                OnPropertyChanged();
            }
        }

        private int _rating;
        public int Rating
        {
            get => _rating;
            set
            {
                _rating = value;
                OnPropertyChanged();
            }
        }

        public string Name => Path.GetFileName(FolderPath);
        public FolderInfo() { }

        public FolderInfo(string path, FolderInfo parent = null)
        {
            FolderPath = path;
            Parent = parent;
            Tags = new ObservableCollection<string>();
            Children = new ObservableCollection<FolderInfo>();
            Images = new ObservableCollection<ImageInfo>();

            // 添加一个 dummy 子项用于懒加载
            if (HasSubfolders(path))
            {
                Children.Add(null);
            }
        }

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                _isExpanded = value;
                OnPropertyChanged(); // 通知 UI 更新绑定
            }
        }

        public void LoadChildren()
        {
            if (Children.Count == 1 && Children[0] == null)
            {
                Children.Clear();

                try
                {
                    var subDirs = Directory.GetDirectories(FolderPath);
                    foreach (var dir in subDirs)
                    {
                        var child = new FolderInfo(dir, this);
                        Children.Add(child);
                    }
                }
                catch (Exception ex)
                {
                    // Log error but don't crash
                    System.Diagnostics.Debug.WriteLine($"Error loading children for {FolderPath}: {ex.Message}");
                }
            }
            else if (Children.Count == 0)
            {
                // If Children is empty, try loading anyway
                try
                {
                    var subDirs = Directory.GetDirectories(FolderPath);
                    foreach (var dir in subDirs)
                    {
                        var child = new FolderInfo(dir, this);
                        Children.Add(child);
                    }
                }
                catch (Exception ex)
                {
                    // Log error but don't crash
                    System.Diagnostics.Debug.WriteLine($"Error loading children for {FolderPath}: {ex.Message}");
                }
            }
        }

        public async Task<List<ImageInfo>> LoadImagesAsync()
        {
            var loadedImages = new List<ImageInfo>();
            if (!Directory.Exists(FolderPath)) return loadedImages;

            string[] extensions = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".wbep" };
            try
            {
                var files = Directory.GetFiles(FolderPath);
                foreach (var file in files)
                {
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (Array.Exists(extensions, e => e == ext))
                    {
                        var image = new ImageInfo
                        {
                            FilePath = file
                        };
                        await image.LoadThumbnailAsync();  // 异步加载缩略图
                        loadedImages.Add(image);
                    }
                }
            }
            catch
            {
                // 忽略错误
            }
            return loadedImages;
        }


        private BitmapImage LoadBitmapImage(string path)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }


        private BitmapImage LoadThumbnail(string path)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(path);
                bitmap.DecodePixelWidth = 150;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private bool HasSubfolders(string path)
        {
            try
            {
                return Directory.GetDirectories(path).Length > 0;
            }
            catch
            {
                return false;
            }
        }
        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propName = null)
         {
             PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
         }
    }
}
