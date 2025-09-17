using System;
using System.Collections.Concurrent;
using System.IO;

namespace ImageFolderManager.Services
{
    /// <summary>
    /// Centralized path normalization service to ensure consistency across all components
    /// </summary>
    public static class PathNormalizationService
    {
        // Cache normalized paths to improve performance
        private static readonly ConcurrentDictionary<string, string> _normalizationCache =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Maximum cache size to prevent memory issues
        private const int MAX_CACHE_SIZE = 10000;

        /// <summary>
        /// Get canonical path - single source of truth for all path operations
        /// </summary>
        public static string GetCanonicalPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            // Use cache for performance, but limit size
            if (_normalizationCache.Count > MAX_CACHE_SIZE)
            {
                // Clear half the cache when limit is reached
                var keysToRemove = new string[MAX_CACHE_SIZE / 2];
                var count = 0;
                foreach (var key in _normalizationCache.Keys)
                {
                    keysToRemove[count++] = key;
                    if (count >= keysToRemove.Length) break;
                }

                foreach (var key in keysToRemove)
                {
                    _normalizationCache.TryRemove(key, out _);
                }
            }

            return _normalizationCache.GetOrAdd(path, p =>
            {
                try
                {
                    // Ensure consistent normalization across all operations
                    return Path.GetFullPath(p)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .ToUpperInvariant(); // Use uppercase for case-insensitive consistency
                }
                catch
                {
                    // If normalization fails, return cleaned input
                    return p.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
            });
        }

        /// <summary>
        /// Clear the normalization cache (for testing or memory management)
        /// </summary>
        public static void ClearCache()
        {
            _normalizationCache.Clear();
        }

        /// <summary>
        /// Check if two paths are equivalent using canonical comparison
        /// </summary>
        public static bool ArePathsEqual(string path1, string path2)
        {
            if (string.IsNullOrWhiteSpace(path1) && string.IsNullOrWhiteSpace(path2))
                return true;

            if (string.IsNullOrWhiteSpace(path1) || string.IsNullOrWhiteSpace(path2))
                return false;

            return GetCanonicalPath(path1) == GetCanonicalPath(path2);
        }
    }
}