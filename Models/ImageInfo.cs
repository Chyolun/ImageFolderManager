using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace ImageFolderManager.Models
{
    public class ImageInfo : INotifyPropertyChanged
    {
        public string FilePath { get; set; }

        // 自动从路径获取文件名
        public string FileName => Path.GetFileName(FilePath);

        private BitmapImage _thumbnail;
        public BitmapImage Thumbnail
        {
            get => _thumbnail;
            set
            {
                _thumbnail = value;
                OnPropertyChanged();
            }
        }

        // 异步加载缩略图（带缓存）
        public async Task LoadThumbnailAsync()
        {
            var thumbnail = await ImageCache.LoadThumbnailAsync(FilePath);
            Thumbnail = thumbnail;  // 设置缩略图，触发 OnPropertyChanged
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }

    }
}
