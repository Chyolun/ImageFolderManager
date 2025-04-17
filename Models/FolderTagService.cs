using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ImageFolderManager.Services
{
    public class FolderTagService
    {
        private const string TagFileName = ".folderTags";

        // 模拟异步读取标签
        public Task<List<string>> GetTagsForFolderAsync(string folderPath)
        {
            return Task.Run(() =>
            {
                string tagFilePath = Path.Combine(folderPath, TagFileName);
                if (!File.Exists(tagFilePath)) return new List<string>();


                try
                {
                    string content = File.ReadAllText(tagFilePath); // 同步读取文件
                    string[] parts = content.Split('|');
                    if (parts.Length > 0)
                    {
                        return parts[0].Split(new[] { '#' }, StringSplitOptions.RemoveEmptyEntries)
                                        .Select(t => t.Trim()).ToList();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error reading tags from file: {ex.Message}");
                }

                return new List<string>();
            });
        }

        // 模拟异步读取评分
        public Task<int> GetRatingForFolderAsync(string folderPath)
        {
            return Task.Run(() =>
            {
                string tagFilePath = Path.Combine(folderPath, TagFileName);
                if (!File.Exists(tagFilePath)) return 0;

                try
                {
                    string content = File.ReadAllText(tagFilePath); // 同步读取文件
                    string[] parts = content.Split('|');
                    if (parts.Length > 1 && int.TryParse(parts[1], out int rating))
                    {
                        return rating;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error reading rating from file: {ex.Message}");
                }

                return 0;
            });
        }

        // 模拟异步写入标签和评分
        public Task SetTagsAndRatingForFolderAsync(string folderPath, List<string> tags, int rating)
        {
            return Task.Run(() =>
            {
                try
                {
                    string tagFilePath = Path.Combine(folderPath, TagFileName);
                    string content = string.Join("#", tags) + "|" + rating;

                    // 同步写入文件
                    File.WriteAllText(tagFilePath, content);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error writing tags and rating to file: {ex.Message}");
                }
            });
        }
    }
}
