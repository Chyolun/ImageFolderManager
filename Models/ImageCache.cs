using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageFolderManager.Services;
using ImageMagick;
using System.Windows;

namespace ImageFolderManager.Models
{
    /// <summary>
    /// Provides caching for image thumbnails with memory and disk caching
    /// </summary>
    public static class ImageCache
    {
        // Configuration from AppSettings
        private static int MAX_CACHE_SIZE => AppSettings.Instance.MaxCacheSize;
        private static int TRIM_THRESHOLD => AppSettings.Instance.TrimThreshold;
        private static int TRIM_TARGET => AppSettings.Instance.TrimTarget;

        // Cache storage using weak references
        private class CacheItem
        {
            public WeakReference<BitmapImage> Image { get; }
            public DateTime LastAccessed { get; set; }

            public CacheItem(BitmapImage image)
            {
                Image = new WeakReference<BitmapImage>(image);
                LastAccessed = DateTime.UtcNow;
            }
        }

        // Memory cache
        private static readonly ConcurrentDictionary<string, CacheItem> _thumbnailCache = new();

        // Thread synchronization
        private static readonly SemaphoreSlim _cacheLock = new(1, 1);
        private static SemaphoreSlim _diskOperationLock = new SemaphoreSlim(
                                                AppSettings.Instance.ParallelThreadCount,
                                                AppSettings.Instance.ParallelThreadCount);
        private static bool _isTrimming = false;

        // Disk cache path 
        private static readonly string _cacheFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ImageFolderManager", "Cache");

        // WebP quality setting
        private const int WEBP_QUALITY = 85;

        // Cancellation tracking
        private static ConcurrentDictionary<string, CancellationTokenSource> _loadingOperations =
            new ConcurrentDictionary<string, CancellationTokenSource>();

        static ImageCache()
        {
            try
            {
                // Ensure cache folder exists
                if (!PathService.DirectoryExists(_cacheFolder))
                {
                    Directory.CreateDirectory(_cacheFolder);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error initializing cache folder: {ex.Message}");
            }
        }

        /// <summary>
        /// Loads a thumbnail for the specified image path, using cache when available
        /// </summary>
        public static async Task<BitmapImage> LoadThumbnailAsync(
            string path,
            CancellationToken cancellationToken = default,
            IProgress<double> progressCallback = null)
        {
            // Normalize path
            string normalizedPath = PathService.NormalizePath(path);

            // Check if path is valid
            if (string.IsNullOrEmpty(normalizedPath) || !File.Exists(normalizedPath))
                return null;

            // Register for cancellation
            var operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _loadingOperations[normalizedPath] = operationCts;

            try
            {
                // Report initial progress
                progressCallback?.Report(0.1);

                // Check memory cache
                if (TryGetFromMemoryCache(normalizedPath, out var cachedImage))
                {
                    progressCallback?.Report(1.0);
                    return cachedImage;
                }

                // Check cancellation
                operationCts.Token.ThrowIfCancellationRequested();
                progressCallback?.Report(0.2);

                // Get cache key based on file content
                string contentHash = await CalculateFileContentHashAsync(normalizedPath, operationCts.Token);

                // Check cancellation
                operationCts.Token.ThrowIfCancellationRequested();
                progressCallback?.Report(0.3);

                // Get disk cache path
                string thumbPath = GetThumbnailCachePath(normalizedPath, contentHash);

                // Check disk cache
                if (File.Exists(thumbPath))
                {
                    try
                    {
                        await _diskOperationLock.WaitAsync(operationCts.Token);
                        try
                        {
                            progressCallback?.Report(0.5);
                            var bitmap = await LoadImageFromFileAsync(thumbPath, operationCts.Token);
                            if (bitmap != null)
                            {
                                StoreInMemoryCache(normalizedPath, bitmap);
                                progressCallback?.Report(1.0);
                                return bitmap;
                            }
                        }
                        finally
                        {
                            _diskOperationLock.Release();
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error loading cached thumbnail: {ex.Message}");
                        // Fall through to regenerate
                    }
                }

                // Check cancellation
                operationCts.Token.ThrowIfCancellationRequested();
                progressCallback?.Report(0.6);

                // Generate new thumbnail
                return await Task.Run(async () =>
                {
                    try
                    {
                        int decodeWidth = AppSettings.Instance.PreviewWidth;
                        int decodeHeight = AppSettings.Instance.PreviewHeight;

                        operationCts.Token.ThrowIfCancellationRequested();
                        progressCallback?.Report(0.7);

                        // Generate the thumbnail
                        BitmapImage bitmap = await GenerateThumbnailAsync(normalizedPath, decodeWidth, decodeHeight, operationCts.Token);

                        if (bitmap != null)
                        {
                            StoreInMemoryCache(normalizedPath, bitmap);
                            progressCallback?.Report(0.9);

                            // Save to disk cache asynchronously
                            _ = SaveThumbnailToDiskAsync(bitmap, thumbPath);

                            progressCallback?.Report(1.0);
                        }
                        return bitmap;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error generating thumbnail: {ex.Message}");
                        return null;
                    }
                }, operationCts.Token);
            }
            finally
            {
                // Clean up
                _loadingOperations.TryRemove(normalizedPath, out _);
                operationCts.Dispose();
            }
        }

        /// <summary>
        /// Generates a thumbnail with specified dimensions
        /// </summary>
        private static async Task<BitmapImage> GenerateThumbnailAsync(
            string filePath,
            int targetWidth,
            int targetHeight,
            CancellationToken cancellationToken)
        {
            try
            {
                // Load image data
                byte[] imageData;
                using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var memoryStream = new MemoryStream();
                    await fileStream.CopyToAsync(memoryStream, 81920, cancellationToken);
                    imageData = memoryStream.ToArray();
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Create bitmap on UI thread
                return await Application.Current.Dispatcher.InvokeAsync(() => {
                    try
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.StreamSource = new MemoryStream(imageData);
                        bitmap.CreateOptions = BitmapCreateOptions.DelayCreation;
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.DecodePixelWidth = targetWidth;

                        // Use high quality scaling
                        RenderOptions.SetBitmapScalingMode(bitmap, BitmapScalingMode.HighQuality);

                        bitmap.EndInit();

                        if (bitmap.CanFreeze && !bitmap.IsFrozen)
                        {
                            bitmap.Freeze(); // Important for cross-thread usage
                        }

                        return bitmap;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error creating bitmap: {ex.Message}");
                        return null;
                    }
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in thumbnail generation: {ex.Message}");

                // Fallback to simpler method if needed
                try
                {
                    return await Application.Current.Dispatcher.InvokeAsync(() => {
                        try
                        {
                            var bitmap = new BitmapImage();
                            bitmap.BeginInit();
                            bitmap.UriSource = new Uri(filePath);
                            bitmap.DecodePixelWidth = targetWidth;
                            bitmap.CacheOption = BitmapCacheOption.OnLoad;
                            bitmap.EndInit();
                            if (bitmap.CanFreeze)
                                bitmap.Freeze();
                            return bitmap;
                        }
                        catch
                        {
                            return null;
                        }
                    });
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// Cancels loading operation for the specified path
        /// </summary>
        public static void CancelLoading(string path)
        {
            string normalizedPath = PathService.NormalizePath(path);
            if (_loadingOperations.TryGetValue(normalizedPath, out var cts))
            {
                try
                {
                    cts.Cancel();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error cancelling loading: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Calculates a hash based on file content for cache key
        /// </summary>
        private static async Task<string> CalculateFileContentHashAsync(string filePath, CancellationToken cancellationToken)
        {
            try
            {
                // Try to get hash from PathService first
                if (PathService.CreateFileContentHash(filePath) is string serviceHash && !string.IsNullOrEmpty(serviceHash))
                {
                    return serviceHash;
                }

                // Fallback to our own hash calculation
                var fileInfo = new FileInfo(filePath);
                if (!fileInfo.Exists)
                    return string.Empty;

                // Only read first 32KB for large files
                int bytesToRead = (int)Math.Min(fileInfo.Length, 32 * 1024);
                byte[] fileBytes = new byte[bytesToRead];

                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true))
                {
                    await fs.ReadAsync(fileBytes, 0, bytesToRead, cancellationToken);
                }

                // Create hash from file metadata and sample content
                using (var md5 = MD5.Create())
                {
                    var data = Encoding.UTF8.GetBytes(
                        $"{fileInfo.Length}|{fileInfo.CreationTimeUtc.Ticks}|{fileInfo.LastWriteTimeUtc.Ticks}");

                    md5.TransformBlock(data, 0, data.Length, data, 0);
                    md5.TransformFinalBlock(fileBytes, 0, fileBytes.Length);

                    return Convert.ToBase64String(md5.Hash)
                        .Replace('+', '-').Replace('/', '_').Replace("=", "");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error calculating content hash: {ex.Message}");

                // Simple fallback hash
                return Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(filePath + DateTime.UtcNow.Ticks.ToString()))
                    .Replace('+', '-').Replace('/', '_').Replace("=", "");
            }
        }

        /// <summary>
        /// Tries to retrieve an image from memory cache
        /// </summary>
        private static bool TryGetFromMemoryCache(string path, out BitmapImage bitmap)
        {
            bitmap = null;

            if (_thumbnailCache.TryGetValue(path, out var cacheItem))
            {
                if (cacheItem.Image.TryGetTarget(out bitmap))
                {
                    // Update last accessed time
                    cacheItem.LastAccessed = DateTime.UtcNow;
                    return true;
                }

                // Reference was collected, remove from cache
                _thumbnailCache.TryRemove(path, out _);
            }

            return false;
        }

        /// <summary>
        /// Stores an image in the memory cache
        /// </summary>
        private static void StoreInMemoryCache(string path, BitmapImage bitmap)
        {
            _thumbnailCache[path] = new CacheItem(bitmap);

            // Trim cache if needed
            if (_thumbnailCache.Count > TRIM_THRESHOLD && !_isTrimming)
            {
                Task.Run(TrimCacheAsync);
            }
        }

        /// <summary>
        /// Trims the cache using LRU policy
        /// </summary>
        private static async Task TrimCacheAsync()
        {
            if (!await _cacheLock.WaitAsync(0))
                return;

            try
            {
                _isTrimming = true;

                if (_thumbnailCache.Count <= MAX_CACHE_SIZE)
                    return;

                // Remove items where WeakReference is no longer valid
                foreach (var key in _thumbnailCache.Keys.ToList())
                {
                    if (_thumbnailCache.TryGetValue(key, out var cacheItem) &&
                        !cacheItem.Image.TryGetTarget(out _))
                    {
                        _thumbnailCache.TryRemove(key, out _);
                    }
                }

                // If still above target, remove oldest accessed items
                if (_thumbnailCache.Count > TRIM_TARGET)
                {
                    var itemsToRemove = _thumbnailCache
                        .OrderBy(kvp => kvp.Value.LastAccessed)
                        .Take(_thumbnailCache.Count - TRIM_TARGET)
                        .Select(kvp => kvp.Key)
                        .ToList();

                    foreach (var key in itemsToRemove)
                    {
                        _thumbnailCache.TryRemove(key, out _);
                    }
                }

                // Request garbage collection
                GC.Collect(1, GCCollectionMode.Optimized, false);
            }
            finally
            {
                _isTrimming = false;
                _cacheLock.Release();
            }
        }

        /// <summary>
        /// Updates thread count for parallel operations
        /// </summary>
        public static void UpdateParallelThreadCount(int threadCount)
        {
            var oldLock = _diskOperationLock;
            oldLock.Wait();

            try
            {
                _diskOperationLock = new SemaphoreSlim(threadCount, threadCount);
            }
            finally
            {
                oldLock.Release();
                oldLock.Dispose();
            }
        }

        /// <summary>
        /// Gets the file path for caching a thumbnail
        /// </summary>
        private static string GetThumbnailCachePath(string originalPath, string contentHash)
        {
            if (string.IsNullOrEmpty(originalPath))
                return null;

            // Include thumbnail dimensions in filename
            int width = AppSettings.Instance.PreviewWidth;
            int height = AppSettings.Instance.PreviewHeight;
            string filename = $"{contentHash}_{width}x{height}.webp";

            return Path.Combine(_cacheFolder, filename);
        }

        /// <summary>
        /// Loads an image from a file
        /// </summary>
        private static async Task<BitmapImage> LoadImageFromFileAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return null;

            try
            {
                // Special handling for WebP files
                string extension = Path.GetExtension(filePath).ToLowerInvariant();
                if (extension == ".webp")
                {
                    return await Task.Run(() =>
                    {
                        try
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            using (var image = new MagickImage(filePath))
                            {
                                using (var memoryStream = new MemoryStream())
                                {
                                    image.Format = MagickFormat.Png;
                                    image.Write(memoryStream);
                                    memoryStream.Position = 0;

                                    var bitmap = new BitmapImage();
                                    bitmap.BeginInit();
                                    bitmap.StreamSource = memoryStream;
                                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                                    bitmap.CreateOptions = BitmapCreateOptions.None;
                                    bitmap.EndInit();
                                    if (bitmap.CanFreeze)
                                        bitmap.Freeze();
                                    return bitmap;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error loading WebP: {ex.Message}");
                            return null;
                        }
                    }, cancellationToken);
                }
                else
                {
                    // Standard loading for other formats
                    return await Task.Run(() =>
                    {
                        try
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                            {
                                var bitmap = new BitmapImage();
                                bitmap.BeginInit();
                                bitmap.StreamSource = stream;
                                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                                bitmap.CreateOptions = BitmapCreateOptions.None;
                                bitmap.EndInit();
                                if (bitmap.CanFreeze)
                                    bitmap.Freeze();
                                return bitmap;
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error loading image: {ex.Message}");
                            return null;
                        }
                    }, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in image loading: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Saves a thumbnail to disk cache
        /// </summary>
        private static async Task SaveThumbnailToDiskAsync(BitmapImage image, string filePath)
        {
            if (image == null || string.IsNullOrEmpty(filePath))
                return;

            try
            {
                // Don't wait long for disk lock
                if (!await _diskOperationLock.WaitAsync(100))
                    return;

                try
                {
                    // Ensure directory exists
                    string dirPath = Path.GetDirectoryName(filePath);
                    if (!PathService.DirectoryExists(dirPath))
                    {
                        Directory.CreateDirectory(dirPath);
                    }

                    // Skip if file already exists
                    if (File.Exists(filePath))
                        return;

                    // Convert to WebP using Magick.NET
                    using (var magickImage = ConvertBitmapImageToMagickImage(image))
                    {
                        if (magickImage != null)
                        {
                            // Set WebP format and quality
                            magickImage.Format = MagickFormat.WebP;
                            magickImage.Quality = WEBP_QUALITY;

                            // Write the file
                            magickImage.Write(filePath);
                        }
                    }
                }
                finally
                {
                    _diskOperationLock.Release();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error saving thumbnail: {ex.Message}");
            }
        }

        /// <summary>
        /// Converts BitmapImage to MagickImage for saving
        /// </summary>
        private static MagickImage ConvertBitmapImageToMagickImage(BitmapImage bitmapImage)
        {
            try
            {
                // Convert to PNG in memory
                PngBitmapEncoder encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmapImage));

                using (MemoryStream memoryStream = new MemoryStream())
                {
                    encoder.Save(memoryStream);
                    memoryStream.Position = 0;

                    // Create MagickImage from stream
                    return new MagickImage(memoryStream);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error converting to MagickImage: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Clears all caches
        /// </summary>
        public static void ClearCache()
        {
            // Clear memory cache
            _thumbnailCache.Clear();

            // Clear disk cache
            try
            {
                if (PathService.DirectoryExists(_cacheFolder))
                {
                    foreach (var file in Directory.GetFiles(_cacheFolder))
                    {
                        try { File.Delete(file); }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error deleting cache file: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error clearing cache: {ex.Message}");
            }

            // Force garbage collection
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }
}