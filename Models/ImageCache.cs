using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace ImageFolderManager.Models
{
    public static class ImageCache
    {
        private static readonly ConcurrentDictionary<string, BitmapImage> _thumbnailCache = new();



        private static string _cacheFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ThumbCache");

        public static async Task<BitmapImage> LoadThumbnailAsync(string path)
        {
            if (_thumbnailCache.TryGetValue(path, out var cachedImage))
                return cachedImage;

            // 优先从磁盘缓存中读取
            string thumbPath = GetThumbnailCachePath(path);
            if (File.Exists(thumbPath))
            {
                var bitmap = await LoadImageFromFileAsync(thumbPath);
                _thumbnailCache[path] = bitmap;
                return bitmap;
            }

            return await Task.Run(() =>
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

                    _thumbnailCache[path] = bitmap;

                    // 保存到磁盘缓存
                    SaveThumbnailToDisk(bitmap, thumbPath);

                    return bitmap;
                }
                catch
                {
                    return null;
                }
            });
        }

        private static string GetThumbnailCachePath(string originalPath)
        {
            if (!Directory.Exists(_cacheFolder))
                Directory.CreateDirectory(_cacheFolder);

            string hash = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(originalPath))
                .Replace("=", "").Replace("/", "_").Replace("+", "-"); // 安全路径
            return Path.Combine(_cacheFolder, $"{hash}.jpg");
        }

        private static async Task<BitmapImage> LoadImageFromFileAsync(string filePath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.StreamSource = stream;
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.EndInit();
                        bitmap.Freeze();
                        return bitmap;
                    }
                }
                catch
                {
                    return null;
                }
            });
        }

        private static void SaveThumbnailToDisk(BitmapImage image, string filePath)
        {
            try
            {
                var encoder = new JpegBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    encoder.Save(stream);
                }
            }
            catch
            {
                // 忽略错误
            }
        }
    }
}