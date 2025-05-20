using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.WindowsAPICodePack.Shell;

namespace ImageFolderManager.Services
{
    /// <summary>
    /// Centralized service for all path-related operations to reduce redundancy and improve performance
    /// </summary>
    public static class PathService
    {
        #region Path Normalization and Comparison

        /// <summary>
        /// Normalizes a file system path by trimming trailing separators and handling case sensitivity
        /// </summary>
        /// <param name="path">The path to normalize</param>
        /// <returns>Normalized path or null if input is null</returns>
        public static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        /// <summary>
        /// Compares two paths for equality, accounting for normalization and case sensitivity
        /// </summary>
        /// <param name="path1">First path</param>
        /// <param name="path2">Second path</param>
        /// <returns>True if paths are equivalent</returns>
        public static bool PathsEqual(string path1, string path2)
        {
            if (path1 == null && path2 == null)
                return true;

            if (path1 == null || path2 == null)
                return false;

            return string.Equals(
                NormalizePath(path1),
                NormalizePath(path2),
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Checks if a child path is within a parent path
        /// </summary>
        /// <param name="parentPath">The parent directory path</param>
        /// <param name="childPath">The potential child path to check</param>
        /// <returns>True if childPath is within parentPath</returns>
        public static bool IsPathWithin(string parentPath, string childPath)
        {
            if (string.IsNullOrEmpty(parentPath) || string.IsNullOrEmpty(childPath))
                return false;

            parentPath = NormalizePath(parentPath);
            childPath = NormalizePath(childPath);

            return childPath.StartsWith(parentPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   PathsEqual(parentPath, childPath);
        }

        #endregion

        #region Path Validation with Caching

        // Cache for directory existence checks to reduce file system calls
        private static readonly ConcurrentDictionary<string, Tuple<bool, DateTime>> _directoryExistsCache =
            new ConcurrentDictionary<string, Tuple<bool, DateTime>>(StringComparer.OrdinalIgnoreCase);

        // Cache expiration time
        private static readonly TimeSpan _cacheExpiration = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Checks if a directory exists with caching to reduce file system calls
        /// </summary>
        /// <param name="path">Directory path to check</param>
        /// <param name="bypassCache">Whether to bypass the cache and check directly</param>
        /// <returns>True if directory exists</returns>
        public static bool DirectoryExists(string path, bool bypassCache = false)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            path = NormalizePath(path);

            // Use cache unless bypassed
            if (!bypassCache)
            {
                if (_directoryExistsCache.TryGetValue(path, out var cachedResult))
                {
                    // Check if cache entry is still valid
                    if (DateTime.Now - cachedResult.Item2 < _cacheExpiration)
                    {
                        return cachedResult.Item1;
                    }
                }
            }

            // Check file system and update cache
            bool exists = Directory.Exists(path);
            _directoryExistsCache[path] = new Tuple<bool, DateTime>(exists, DateTime.Now);
            return exists;
        }

        /// <summary>
        /// Checks if a directory has any subdirectories with caching
        /// </summary>
        /// <param name="path">Path to check for subdirectories</param>
        /// <returns>True if subdirectories exist</returns>
        public static bool DirectoryHasSubdirectories(string path)
        {
            if (!DirectoryExists(path))
                return false;

            try
            {
                // Use GetDirectories with TopDirectoryOnly for better performance
                var dirs = Directory.GetDirectories(path, "*", SearchOption.TopDirectoryOnly);
                return dirs.Length > 0;
            }
            catch (UnauthorizedAccessException)
            {
                // For unauthorized directories, assume they might have subdirectories
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking for subdirectories in {path}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Invalidates path cache entries when paths change
        /// </summary>
        /// <param name="path">Path to invalidate from cache</param>
        /// <param name="recursive">Whether to invalidate child paths as well</param>
        public static void InvalidatePathCache(string path, bool recursive = false)
        {
            if (string.IsNullOrEmpty(path))
                return;

            path = NormalizePath(path);

            // Remove exact path match
            _directoryExistsCache.TryRemove(path, out _);

            // Remove child paths if recursive
            if (recursive)
            {
                foreach (var cachedPath in _directoryExistsCache.Keys)
                {
                    if (IsPathWithin(path, cachedPath))
                    {
                        _directoryExistsCache.TryRemove(cachedPath, out _);
                    }
                }
            }
        }

        #endregion

        #region Shell Object Path Handling

        /// <summary>
        /// Gets a file system path from a Shell object
        /// </summary>
        /// <param name="shellObject">The Shell object</param>
        /// <returns>File system path or null if not applicable</returns>
        public static string GetPathFromShellObject(ShellObject shellObject)
        {
            if (shellObject == null)
                return null;

            try
            {
                if (shellObject.IsFileSystemObject)
                {
                    return NormalizePath(shellObject.ParsingName);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error getting path from ShellObject: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Builds a relative path from one path to another
        /// </summary>
        /// <param name="fromPath">Base path</param>
        /// <param name="toPath">Target path</param>
        /// <returns>Relative path from base to target</returns>
        public static string GetRelativePath(string fromPath, string toPath)
        {
            if (string.IsNullOrEmpty(fromPath) || string.IsNullOrEmpty(toPath))
                return toPath;

            fromPath = NormalizePath(fromPath);
            toPath = NormalizePath(toPath);

            try
            {
                Uri fromUri = new Uri(fromPath + Path.DirectorySeparatorChar);
                Uri toUri = new Uri(toPath);

                if (fromUri.Scheme != toUri.Scheme)
                    return toPath;

                Uri relativeUri = fromUri.MakeRelativeUri(toUri);
                string relativePath = Uri.UnescapeDataString(relativeUri.ToString());

                return relativePath.Replace('/', Path.DirectorySeparatorChar);
            }
            catch
            {
                return toPath;
            }
        }

        #endregion

        #region Path Generation and Management

        /// <summary>
        /// Generates a unique folder path by appending a number if needed
        /// </summary>
        /// <param name="targetDirectory">Target directory</param>
        /// <param name="folderName">Desired folder name</param>
        /// <returns>Unique path that doesn't exist yet</returns>
        public static string GetUniqueDirectoryPath(string targetDirectory, string folderName)
        {
            if (string.IsNullOrEmpty(targetDirectory) || string.IsNullOrEmpty(folderName))
                return null;

            targetDirectory = NormalizePath(targetDirectory);
            string destinationPath = Path.Combine(targetDirectory, folderName);

            // If path doesn't exist yet, return it directly
            if (!DirectoryExists(destinationPath))
                return destinationPath;

            // Otherwise, append a number to make it unique
            int counter = 1;
            string newName = folderName;

            while (DirectoryExists(Path.Combine(targetDirectory, newName)))
            {
                newName = $"{folderName} ({counter})";
                counter++;
            }

            return Path.Combine(targetDirectory, newName);
        }

        /// <summary>
        /// Creates a content-based hash for a file path, useful for caching
        /// </summary>
        /// <param name="filePath">Path to the file</param>
        /// <returns>Hash string based on file content and metadata</returns>
        public static string CreateFileContentHash(string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                    return null;

                var fileInfo = new FileInfo(filePath);

                // Create a simple hash based on file size and last write time
                // For a more sophisticated implementation, include file content sampling
                return $"{fileInfo.Length}_{fileInfo.LastWriteTimeUtc.Ticks}";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error creating file hash: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Cleanup and Maintenance

        /// <summary>
        /// Clears all cached path data
        /// </summary>
        public static void ClearPathCache()
        {
            _directoryExistsCache.Clear();
        }

        /// <summary>
        /// Performs maintenance on path caches, removing expired entries
        /// </summary>
        public static void CleanupPathCache()
        {
            var now = DateTime.Now;
            var expiredKeys = new List<string>();

            // Find expired cache entries
            foreach (var entry in _directoryExistsCache)
            {
                if (now - entry.Value.Item2 > _cacheExpiration)
                {
                    expiredKeys.Add(entry.Key);
                }
            }

            // Remove expired entries
            foreach (var key in expiredKeys)
            {
                _directoryExistsCache.TryRemove(key, out _);
            }
        }

        #endregion
    }
}