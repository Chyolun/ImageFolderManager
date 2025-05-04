using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
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
            // Before starting a recursive scan, disable caching in the tag service
            // for a fresh read of all tag files
            _tagService.EnableCaching = false;

            var result = new List<FolderInfo>();

            try
            {
                // Clear any existing cache
                _tagService.ClearCache();

                await TraverseDirectoriesAsync(rootPath, null, result);
            }
            finally
            {
                // Re-enable caching after the scan is complete
                _tagService.EnableCaching = true;
            }

            return result;
        }

        private async Task TraverseDirectoriesAsync(string path, FolderInfo parent, List<FolderInfo> result)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            try
            {
                // Create the folder info
                var folder = new FolderInfo(path, parent);

                // Get fresh tag data directly from the file system
                folder.Tags = new ObservableCollection<string>(await _tagService.GetTagsForFolderAsync(path));
                folder.Rating = await _tagService.GetRatingForFolderAsync(path);

                // Add to results
                result.Add(folder);

                // Process subdirectories
                string[] subDirectories;
                try
                {
                    subDirectories = Directory.GetDirectories(path);
                }
                catch (UnauthorizedAccessException)
                {
                    // Skip directories we can't access
                    return;
                }

                // Process each subdirectory
                foreach (var subDir in subDirectories)
                {
                    await TraverseDirectoriesAsync(subDir, folder, result);
                }
            }
            catch (Exception ex)
            {
                // Log error but continue processing other directories
                Console.WriteLine($"Error processing directory {path}: {ex.Message}");
            }
        }
    }
}