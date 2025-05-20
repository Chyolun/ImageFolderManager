using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.WindowsAPICodePack.Shell;

namespace ImageFolderManager.Services
{
    /// <summary>
    /// Unified path handling service
    /// </summary>
    public static class PathService
    {
        #region Path Normalization and Comparison

        private static readonly Dictionary<string, bool> _pathCache =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _cacheLock = new object();

        /// <summary>
        /// Normalizes a file system path
        /// </summary>
        public static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        /// <summary>
        /// Compares two paths for equality
        /// </summary>
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

        #region Directory Existence Checks

        /// <summary>
        /// Checks if a directory exists
        /// </summary>
        public static bool DirectoryExists(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            path = NormalizePath(path);
            return Directory.Exists(path);
        }

        /// <summary>
        /// Checks if a directory has subdirectories
        /// </summary>
        public static bool DirectoryHasSubdirectories(string path)
        {
            if (!DirectoryExists(path))
                return false;

            try
            {
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

        #endregion

        #region Shell Object Handling

        /// <summary>
        /// Gets a file system path from a Shell object
        /// </summary>
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

        #endregion

        #region Path Generation and Management

        /// <summary>
        /// Generates a unique folder path by appending a number if needed
        /// </summary>
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
        /// Creates a content-based hash for a file path
        /// </summary>
        public static string CreateFileContentHash(string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                    return null;

                var fileInfo = new FileInfo(filePath);

                // Create a simple hash based on file size and last write time
                return $"{fileInfo.Length}_{fileInfo.LastWriteTimeUtc.Ticks}";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error creating file hash: {ex.Message}");
                return null;
            }
        }

        /// </summary>
        /// <param name="path">Path to invalidate</param>
        /// <param name="recursive">Whether to invalidate child paths</param>
        public static void InvalidatePathCache(string path, bool recursive = false)
        {
            if (string.IsNullOrEmpty(path))
                return;

            path = NormalizePath(path);

            lock (_cacheLock)
            {
                // For direct path
                if (_pathCache.ContainsKey(path))
                    _pathCache.Remove(path);

                // For recursive invalidation
                if (recursive)
                {
                    var keysToRemove = _pathCache.Keys
                        .Where(key => IsPathWithin(path, key))
                        .ToList();

                    foreach (var key in keysToRemove)
                    {
                        _pathCache.Remove(key);
                    }
                }
            }
        }

        /// <summary>
        /// Clears the entire path cache
        /// </summary>
        public static void ClearPathCache()
        {
            lock (_cacheLock)
            {
                _pathCache.Clear();
            }
        }

        #endregion
    }
}