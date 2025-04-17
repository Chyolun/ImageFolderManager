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
        public string FolderPath { get; set; }
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

                var subDirs = Directory.GetDirectories(FolderPath);
                foreach (var dir in subDirs)
                {
                    Children.Add(new FolderInfo(dir, this));
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


        private async Task<BitmapImage> LoadOrCacheThumbnailAsync(string filePath)
        {
            string cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ImageFolderManager", "Cache");
            Directory.CreateDirectory(cacheDir);

            string thumbPath = Path.Combine(cacheDir, Path.GetFileName(filePath) + ".thumb.jpg");

            if (File.Exists(thumbPath))
            {
                return LoadBitmapImage(thumbPath);
            }

            // 生成缩略图并缓存
            try
            {
                var decoder = BitmapDecoder.Create(new Uri(filePath), BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                var resized = new TransformedBitmap(frame, new System.Windows.Media.ScaleTransform(0.2, 0.2));

                BitmapEncoder encoder = new JpegBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(resized));
                using var fs = new FileStream(thumbPath, FileMode.Create);
                encoder.Save(fs);

                return LoadBitmapImage(thumbPath);
            }
            catch
            {
                return null;
            }
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

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }
    }
}
