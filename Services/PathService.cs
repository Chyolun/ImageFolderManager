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
            if (string.IsNullOrEmpty(path1) || string.IsNullOrEmpty(path2))
                return false;

            try
            {
                // Normalize paths for comparison
                string normalizedPath1 = Path.GetFullPath(path1).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string normalizedPath2 = Path.GetFullPath(path2).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                return string.Equals(normalizedPath1, normalizedPath2, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // If path normalization fails, fall back to simple string comparison
                return string.Equals(path1, path2, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Checks if a child path is within a parent path
        /// </summary>
        public static bool IsPathWithin(string parentPath, string childPath)
        {
            if (string.IsNullOrEmpty(parentPath) || string.IsNullOrEmpty(childPath))
                return false;

            try
            {
                // Normalize paths
                string normalizedParent = Path.GetFullPath(parentPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string normalizedChild = Path.GetFullPath(childPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                // Add trailing separator to parent for proper comparison
                normalizedParent += Path.DirectorySeparatorChar;

                return normalizedChild.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase) ||
                       PathsEqual(normalizedParent.TrimEnd(Path.DirectorySeparatorChar), normalizedChild);
            }
            catch
            {
                return false;
            }
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
        public static string GetUniqueDirectoryPath(string parentPath, string directoryName)
        {
            if (string.IsNullOrEmpty(parentPath) || string.IsNullOrEmpty(directoryName))
                throw new ArgumentException("Parent path and directory name cannot be null or empty");

            string basePath = Path.Combine(parentPath, directoryName);

            if (!Directory.Exists(basePath))
                return basePath;

            int counter = 1;
            string uniquePath;

            do
            {
                uniquePath = Path.Combine(parentPath, $"{directoryName} ({counter})");
                counter++;
            }
            while (Directory.Exists(uniquePath));

            return uniquePath;
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