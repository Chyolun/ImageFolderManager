using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using ImageFolderManager.Models;

namespace ImageFolderManager.Services
{
    public class FolderService
    {
        private readonly FolderTagService _tagService = new();
        private readonly string _thumbnailCachePath = Path.Combine(Path.GetTempPath(), "ImageFolderManager", "thumbnails");

        public async Task<FolderInfo> LoadRootFolderAsync(string path)
        {
            var root = await CreateFolderInfoWithoutImagesAsync(path);
            await LoadSubfoldersAsync(root);
            return root;
        }

        public async Task LoadSubfoldersAsync(FolderInfo parent)
        {
            try
            {
                var subDirs = Directory.GetDirectories(parent.FolderPath);
                foreach (var dir in subDirs)
                {
                    var child = await CreateFolderInfoWithoutImagesAsync(dir);
                    parent.Children.Add(child);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading folder '{parent.FolderPath}': {ex.Message}");
            }
        }


        public async Task<FolderInfo> CreateFolderInfoWithoutImagesAsync(string path, bool loadImages = true)
        {
            var folder = new FolderInfo
            {
                FolderPath = path,
                Children = new ObservableCollection<FolderInfo>(),
                Images = new ObservableCollection<ImageInfo>(),
                Tags = new ObservableCollection<string>(await _tagService.GetTagsForFolderAsync(path)),
                Rating = await _tagService.GetRatingForFolderAsync(path)
            };

            if (loadImages)
            {
                _ = LoadImagesAsync(folder);
            }

            return folder;
        }

        public async Task LoadImagesAsync(FolderInfo folder)
        {
            var supportedExtensions = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif" };

            if (!Directory.Exists(folder.FolderPath)) return;

            var images = new List<ImageInfo>();

            foreach (var file in Directory.GetFiles(folder.FolderPath))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (Array.Exists(supportedExtensions, e => e == ext))
                {
                    var imageInfo = new ImageInfo { FilePath = file };
                    await imageInfo.LoadThumbnailAsync();
                    images.Add(imageInfo);
                }
            }

            App.Current.Dispatcher.Invoke(() =>
            {
                folder.Images.Clear();
                foreach (var img in images)
                {
                    folder.Images.Add(img);
                }
            });
        }

        public async Task<List<FolderInfo>> LoadFoldersRecursivelyAsync(string rootPath)
        {

            var result = new List<FolderInfo>();
            async Task Traverse(string path)
            {
                var folder = new FolderInfo(path);

                // 防止 null 报错：如果 tags 为 null，使用空 List<string>
                var tags = await _tagService.GetTagsForFolderAsync(path) ?? new List<string>();
                folder.Tags = new ObservableCollection<string>(tags);

                // 防止 rating 不存在的情况：默认 0
                folder.Rating = await _tagService.GetRatingForFolderAsync(path);

                result.Add(folder);

                foreach (var sub in Directory.GetDirectories(path))
                {
                    await Traverse(sub);
                }
            }

            await Traverse(rootPath);
            return result;
        }
    }
}
