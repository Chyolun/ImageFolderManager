using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImageFolderManager.Models;
using ImageFolderManager.Services;

namespace ImageFolderManager.ViewModels
{
    public partial class MainViewModel
    {
        /// <summary>
        /// Gets all currently loaded folders for duplicate search functionality
        /// </summary>
        /// <returns>List of all loaded FolderInfo objects</returns>
        public List<FolderInfo> GetAllLoadedFolders()
        {
            try
            {
                // Return a copy of the list to prevent external modification
                return GetAllLoadedFoldersSnapshot();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting all loaded folders: {ex.Message}");
                return new List<FolderInfo>();
            }
        }


        /// <summary>
        /// Gets the count of loaded folders
        /// </summary>
        /// <returns>Number of currently loaded folders</returns>
        public int GetLoadedFolderCount()
        {
            return GetAllLoadedFoldersSnapshot().Count;
        }

        /// <summary>
        /// Finds duplicate folder names within the current root directory with optional filtering
        /// </summary>
        /// <returns>Dictionary where key is folder name and value is list of folders with that name</returns>
        public Dictionary<string, List<FolderInfo>> FindDuplicateFolders()
        {
            try
            {
                var duplicates = new Dictionary<string, List<FolderInfo>>(StringComparer.OrdinalIgnoreCase);
                var allFolders = GetAllLoadedFolders();

                if (!allFolders.Any())
                {
                    return duplicates;
                }

                // Apply filters if enabled
                var filteredFolders = ApplyDuplicateFilters(allFolders);

                // Group folders by their name (case-insensitive)
                var folderGroups = filteredFolders
                    .Where(f => !string.IsNullOrEmpty(f.FolderPath))
                    .GroupBy(f => Path.GetFileName(f.FolderPath), StringComparer.OrdinalIgnoreCase);

                // Only keep groups with more than one folder (duplicates)
                foreach (var group in folderGroups.Where(g => g.Count() > 1))
                {
                    duplicates[group.Key] = group.ToList();
                }

                return duplicates;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error finding duplicate folders: {ex.Message}");
                return new Dictionary<string, List<FolderInfo>>();
            }
        }

        /// <summary>
        /// Applies duplicate detection filters to the folder collection
        /// </summary>
        /// <param name="folders">Collection of folders to filter</param>
        /// <returns>Filtered collection of folders</returns>
        private IEnumerable<FolderInfo> ApplyDuplicateFilters(IEnumerable<FolderInfo> folders)
        {
            if (!AppSettings.Instance.EnableDuplicateFilters)
            {
                return folders;
            }

            var filteredFolders = folders.Where(folder =>
            {
                if (string.IsNullOrEmpty(folder.FolderPath))
                    return false;

                var folderName = Path.GetFileName(folder.FolderPath);

                if (string.IsNullOrEmpty(folderName))
                    return false;

                // Apply minimum length filter
                if (folderName.Length < AppSettings.Instance.MinFolderNameLength)
                {
                    System.Diagnostics.Debug.WriteLine($"Filtered out folder '{folderName}' - below minimum length ({AppSettings.Instance.MinFolderNameLength})");
                    return false;
                }

                // Apply exclusion list filter
                if (AppSettings.Instance.IsFolderNameExcluded(folderName))
                {
                    System.Diagnostics.Debug.WriteLine($"Filtered out folder '{folderName}' - in exclusion list");
                    return false;
                }

                return true;
            });

            return filteredFolders;
        }

        /// <summary>
        /// Gets duplicate folder statistics with filter information
        /// </summary>
        /// <returns>Tuple containing (total folders, filtered folders, duplicate groups count, total duplicate folders)</returns>
        public (int totalFolders, int filteredFolders, int duplicateGroups, int duplicateFolders) GetDuplicateStatsWithFilters()
        {
            try
            {
                var allFolders = GetAllLoadedFolders();
                var filteredFolders = ApplyDuplicateFilters(allFolders).ToList();
                var duplicates = FindDuplicateFolders();

                int totalFolders = allFolders.Count;
                int filteredCount = filteredFolders.Count;
                int duplicateGroups = duplicates.Count;
                int duplicateFolders = duplicates.Values.Sum(list => list.Count);

                return (totalFolders, filteredCount, duplicateGroups, duplicateFolders);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error getting duplicate stats with filters: {ex.Message}");
                return (0, 0, 0, 0);
            }
        }

        /// <summary>
        /// Gets duplicate folder statistics (legacy method for backward compatibility)
        /// </summary>
        /// <returns>Tuple containing (total folders, duplicate groups count, total duplicate folders)</returns>
        public (int totalFolders, int duplicateGroups, int duplicateFolders) GetDuplicateStats()
        {
            var stats = GetDuplicateStatsWithFilters();
            return (stats.totalFolders, stats.duplicateGroups, stats.duplicateFolders);
        }
    }
}
