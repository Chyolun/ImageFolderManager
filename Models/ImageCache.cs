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
    /// Provides an optimized caching mechanism for image thumbnails with LRU eviction policy and content-based caching
    /// </summary>
    public static class ImageCache
    {
        // Configuration
        private static int MAX_CACHE_SIZE => AppSettings.Instance.MaxCacheSize; // Maximum number of items in memory cache
        private static int TRIM_THRESHOLD => AppSettings.Instance.TrimThreshold; // When to start cache trimming
        private static int TRIM_TARGET => AppSettings.Instance.TrimTarget;    // How many items to keep after trimming

        // Cache storage
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

        private static readonly ConcurrentDictionary<string, CacheItem> _thumbnailCache = new();

        // Thread synchronization
        private static readonly SemaphoreSlim _cacheLock = new(1, 1);
        private static SemaphoreSlim _diskOperationLock = new SemaphoreSlim(
                                                AppSettings.Instance.ParallelThreadCount,
                                                AppSettings.Instance.ParallelThreadCount);
        private static bool _isTrimming = false;

        // Disk cache path AppData\Roaming\ImageFolderManager\Cache
        private static readonly string _cacheFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ImageFolderManager", "Cache");


        // Statistics
        private static long _cacheHits = 0;
        private static long _cacheMisses = 0;

        // WebP quality setting (0-100)
        private const int WEBP_QUALITY = 85;

        // Cancellation tracking
        private static ConcurrentDictionary<string, CancellationTokenSource> _loadingOperations =
            new ConcurrentDictionary<string, CancellationTokenSource>();

        static ImageCache()
        {
            try
            {
                // Use PathService to check directory existence
                if (!PathService.DirectoryExists(_cacheFolder))
                {
                    Directory.CreateDirectory(_cacheFolder);
                }
                Task.Run(CleanupOrphanedCacheFilesAsync);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error initializing cache folder: {ex.Message}");

                try
                {
                    if (!Directory.Exists(_cacheFolder))
                    {
                        Directory.CreateDirectory(_cacheFolder);
                    }
                }
                catch (Exception createEx)
                {
                    Debug.WriteLine($"Fatal error: Cannot create cache directory: {createEx.Message}");
                }
            }
        }

        /// <summary>
        /// Loads a thumbnail for the specified image path, using cache when available
        /// </summary>
        /// <param name="path">Path to the source image</param>
        /// <param name="cancellationToken">Optional cancellation token</param>
        /// <param name="progressCallback">Optional callback to report loading progress</param>
        /// <returns>A BitmapImage thumbnail or null if loading failed or was cancelled</returns>
        public static async Task<BitmapImage> LoadThumbnailAsync(
            string path,
            CancellationToken cancellationToken = default,
            IProgress<double> progressCallback = null)
        {
            // Normalize path to ensure consistency in cache keys
            string normalizedPath = PathService.NormalizePath(path);

            // Check if path is valid
            if (string.IsNullOrEmpty(normalizedPath) || !File.Exists(normalizedPath))
                return null;

            // Register this loading operation for possible cancellation
            var operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _loadingOperations[normalizedPath] = operationCts;

            try
            {
                // Report initial progress
                progressCallback?.Report(0.1);

                // Check memory cache first (fastest)
                if (TryGetFromMemoryCache(normalizedPath, out var cachedImage))
                {
                    Interlocked.Increment(ref _cacheHits);
                    progressCallback?.Report(1.0); // Complete
                    return cachedImage;
                }

                // Check if the operation was cancelled
                operationCts.Token.ThrowIfCancellationRequested();

                Interlocked.Increment(ref _cacheMisses);
                progressCallback?.Report(0.2);

                // Calculate content-based cache key
                string contentHash = await CalculateFileContentHashAsync(normalizedPath, operationCts.Token);

                // Check if operation was cancelled
                operationCts.Token.ThrowIfCancellationRequested();
                progressCallback?.Report(0.3);

                // Get thumbnail cache path based on content hash
                string thumbPath = GetThumbnailCachePath(normalizedPath, contentHash);

                // Check disk cache next
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
                                progressCallback?.Report(1.0); // Complete
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

                // Check if operation was cancelled
                operationCts.Token.ThrowIfCancellationRequested();
                progressCallback?.Report(0.6);

                // Generate new thumbnail
                try
                {
                    return await Task.Run(async () =>
                    {
                        try
                        {
                            int decodeWidth = Services.AppSettings.Instance.PreviewWidth;
                            int decodeHeight = Services.AppSettings.Instance.PreviewHeight;

                            // Check if operation was cancelled
                            operationCts.Token.ThrowIfCancellationRequested();
                            progressCallback?.Report(0.7);

                            // Use asynchronous decoding with optimized parameters
                            BitmapImage bitmap = await DecodeImageOptimizedAsync(normalizedPath, decodeWidth, decodeHeight, operationCts.Token);

                            if (bitmap != null)
                            {
                                StoreInMemoryCache(normalizedPath, bitmap);
                                progressCallback?.Report(0.9);

                                // Save to disk cache asynchronously without waiting for completion
                                _ = SaveThumbnailToDiskAsync(bitmap, thumbPath, contentHash);

                                progressCallback?.Report(1.0); // Complete
                            }
                            return bitmap;
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error generating thumbnail for {normalizedPath}: {ex.Message}");
                            return null;
                        }
                    }, operationCts.Token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error in thumbnail generation task: {ex.Message}");
                    return null;
                }
            }
            finally
            {
                // Clean up the cancellation token source
                _loadingOperations.TryRemove(normalizedPath, out _);
                operationCts.Dispose();
            }
        }

        /// <summary>
        /// Optimized image decoding with better performance parameters
        /// </summary>
        private static async Task<BitmapImage> DecodeImageOptimizedAsync(
                string filePath,
                int targetWidth,
                int targetHeight,
                CancellationToken cancellationToken)
        {
            try
            {
                // Read the file data on a background thread
                byte[] imageData;
                using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true))
                {
                    // Check for cancellation
                    cancellationToken.ThrowIfCancellationRequested();

                    // Read the file into memory
                    var memoryStream = new MemoryStream();
                    await fileStream.CopyToAsync(memoryStream, 81920, cancellationToken);
                    imageData = memoryStream.ToArray();
                }

                cancellationToken.ThrowIfCancellationRequested();

                // Create and initialize BitmapImage on the UI thread
                return await Application.Current.Dispatcher.InvokeAsync(() => {
                    try
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.StreamSource = new MemoryStream(imageData);
                        bitmap.CreateOptions = BitmapCreateOptions.DelayCreation;
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.DecodePixelWidth = targetWidth;

                        // Enable better downsampling to reduce aliasing
                        RenderOptions.SetBitmapScalingMode(bitmap, BitmapScalingMode.HighQuality);

                        // Use lower bit depth if possible to save memory
                        if (IsJpegOrPng(filePath))
                        {
                            // For some formats, we can reduce memory usage with 8-bit format
                            bitmap.DownloadCompleted += (s, e) => {
                                // Additional optimization after load if needed
                            };
                        }

                        bitmap.EndInit();

                        if (bitmap.CanFreeze && !bitmap.IsFrozen)
                        {
                            bitmap.Freeze(); // Important for cross-thread usage and performance
                        }

                        return bitmap;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error creating bitmap on UI thread: {ex.Message}");
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
                Debug.WriteLine($"Error in optimized decoding: {ex.Message}");

                // Fallback to simple decoding if optimization fails
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
                catch (Exception innerEx)
                {
                    Debug.WriteLine($"Error in fallback decoding: {innerEx.Message}");
                    return null;
                }
            }
        }

        /// <summary>
        /// Determines if a file is a JPEG or PNG based on extension
        /// </summary>
        private static bool IsJpegOrPng(string filePath)
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            return ext == ".jpg" || ext == ".jpeg" || ext == ".png";
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
                    Debug.WriteLine($"Error cancelling loading operation: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Cancels all ongoing loading operations
        /// </summary>
        public static void CancelAllLoading()
        {
            foreach (var cts in _loadingOperations.Values)
            {
                try
                {
                    cts.Cancel();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error cancelling loading operation: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Calculates a stable hash based on the file's content properties
        /// This allows the same image to be identified even if it's moved to a new location
        /// </summary>
        private static async Task<string> CalculateFileContentHashAsync(string filePath, CancellationToken cancellationToken)
        {
            try
            {
                // Use the PathService method if available
                if (PathService.CreateFileContentHash(filePath) is string serviceHash && !string.IsNullOrEmpty(serviceHash))
                {
                    return serviceHash;
                }

                // Get key file properties that should be stable even if the file is moved
                var fileInfo = new FileInfo(filePath);
                if (!fileInfo.Exists)
                    return string.Empty;

                // Only calculate MD5 for the first few KB of large files for performance
                int bytesToRead = (int)Math.Min(fileInfo.Length, 32 * 1024); // 32KB max
                byte[] fileBytes = new byte[bytesToRead];

                // Check cancellation before file read
                cancellationToken.ThrowIfCancellationRequested();

                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true))
                {
                    await fs.ReadAsync(fileBytes, 0, bytesToRead, cancellationToken);
                }

                // Check cancellation after file read
                cancellationToken.ThrowIfCancellationRequested();

                // Combine file size, creation time, last write time, and the first few KB of content
                using (var md5 = MD5.Create())
                {
                    var data = Encoding.UTF8.GetBytes(
                        $"{fileInfo.Length}|{fileInfo.CreationTimeUtc.Ticks}|{fileInfo.LastWriteTimeUtc.Ticks}");

                    // First hash the metadata
                    md5.TransformBlock(data, 0, data.Length, data, 0);

                    // Then include some of the actual file content
                    md5.TransformFinalBlock(fileBytes, 0, fileBytes.Length);

                    // Return as base64 string (URL-safe version)
                    return Convert.ToBase64String(md5.Hash)
                        .Replace('+', '-').Replace('/', '_').Replace("=", "");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error calculating content hash: {ex.Message}");
                // Fallback to a simpler hash in case of error
                return Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(filePath + DateTime.UtcNow.Ticks.ToString()))
                    .Replace('+', '-').Replace('/', '_').Replace("=", "");
            }
        }

        /// <summary>
        /// Tries to retrieve an image from the memory cache
        /// </summary>
        private static bool TryGetFromMemoryCache(string path, out BitmapImage bitmap)
        {
            bitmap = null;
            path = PathService.NormalizePath(path);

            if (_thumbnailCache.TryGetValue(path, out var cacheItem))
            {
                if (cacheItem.Image.TryGetTarget(out bitmap))
                {
                    // Update last accessed time for LRU tracking
                    cacheItem.LastAccessed = DateTime.UtcNow;
                    return true;
                }

                // Reference was collected by GC, remove from cache
                _thumbnailCache.TryRemove(path, out _);
            }

            return false;
        }

        /// <summary>
        /// Stores an image in the memory cache and triggers trimming if needed
        /// </summary>
        private static void StoreInMemoryCache(string path, BitmapImage bitmap)
        {
            path = PathService.NormalizePath(path);
            _thumbnailCache[path] = new CacheItem(bitmap);

            // Trim cache if it grows too large
            if (_thumbnailCache.Count > TRIM_THRESHOLD && !_isTrimming)
            {
                Task.Run(TrimCacheAsync);
            }
        }

        /// <summary>
        /// Trims the cache using LRU policy to stay within memory limits
        /// </summary>
        private static async Task TrimCacheAsync()
        {
            // Use lock to ensure only one trim operation runs at a time
            if (!await _cacheLock.WaitAsync(0))
                return;

            try
            {
                _isTrimming = true;

                if (_thumbnailCache.Count <= MAX_CACHE_SIZE)
                    return;

                Debug.WriteLine($"Trimming cache from {_thumbnailCache.Count} items to {TRIM_TARGET} items");

                // First, remove items where WeakReference is no longer valid
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

                // Request garbage collection to free up memory
                GC.Collect(1, GCCollectionMode.Optimized, false);

                Debug.WriteLine($"Cache trimmed to {_thumbnailCache.Count} items");
            }
            finally
            {
                _isTrimming = false;
                _cacheLock.Release();
            }
        }

        public static void UpdateParallelThreadCount(int threadCount)
        {
            try
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

                Debug.WriteLine($"Updated parallel thread count to {threadCount}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error updating parallel thread count: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the file path for caching a thumbnail
        /// </summary>
        private static string GetThumbnailCachePath(string originalPath, string contentHash)
        {
            if (string.IsNullOrEmpty(originalPath))
                return null;

            // Normalize path
            originalPath = PathService.NormalizePath(originalPath);

            // Include thumbnail size in the filename to ensure different sizes get different cache files
            int width = Services.AppSettings.Instance.PreviewWidth;
            int height = Services.AppSettings.Instance.PreviewHeight;

            // Create a filename using the content hash plus dimensions
            // Use WebP extension for the cached file
            string filename = $"{contentHash}_{width}x{height}.webp";

            return Path.Combine(_cacheFolder, filename);
        }

        /// <summary>
        /// Loads an image from a file asynchronously
        /// </summary>
        private static async Task<BitmapImage> LoadImageFromFileAsync(string filePath, CancellationToken cancellationToken = default)
        {
            filePath = PathService.NormalizePath(filePath);

            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return null;

            try
            {
                // For WebP files, use Magick.NET for decoding
                string extension = Path.GetExtension(filePath).ToLowerInvariant();
                if (extension == ".webp")
                {
                    return await Task.Run(() =>
                    {
                        try
                        {
                            // Check cancellation
                            cancellationToken.ThrowIfCancellationRequested();

                            // Use Magick.NET to load WebP file
                            using (var image = new MagickImage(filePath))
                            {
                                // Convert to memory stream in a format WPF can display
                                using (var memoryStream = new MemoryStream())
                                {
                                    // Convert to PNG in memory for WPF compatibility
                                    image.Format = MagickFormat.Png;
                                    image.Write(memoryStream);
                                    memoryStream.Position = 0;

                                    // Create BitmapImage from the stream
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
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error loading WebP image: {ex.Message}");
                            return null;
                        }
                    }, cancellationToken);
                }
                else
                {
                    // Standard loading for other image formats
                    return await Task.Run(() =>
                    {
                        try
                        {
                            // Check cancellation
                            cancellationToken.ThrowIfCancellationRequested();

                            // Use FileStream with async pattern for .NET Framework 4.8
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
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error loading image from file {filePath}: {ex.Message}");
                            return null;
                        }
                    }, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in image loading task: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Saves a thumbnail to the disk cache in WebP format
        /// </summary>
        /// <summary>
        /// Optimized WebP encoding for disk cache storage
        /// </summary>
        private static async Task SaveThumbnailToDiskAsync(BitmapImage image, string filePath, string contentHash)
        {
            if (image == null || string.IsNullOrEmpty(filePath))
                return;

            try
            {
                // Try to acquire the disk lock but don't wait too long
                if (!await _diskOperationLock.WaitAsync(100))
                {
                    // If we can't quickly acquire the lock, skip this save operation
                    // The thumbnail is already in memory cache, so it's not critical
                    return;
                }

                try
                {
                    // Ensure directory exists
                    string dirPath = Path.GetDirectoryName(filePath);
                    if (!PathService.DirectoryExists(dirPath))
                    {
                        Directory.CreateDirectory(dirPath);
                    }

                    // Check if the file already exists (avoid redundant writes)
                    if (File.Exists(filePath))
                    {
                        // Skip if the file already exists
                        return;
                    }

                    // Convert BitmapImage to WebP using Magick.NET
                    using (var magickImage = ConvertBitmapImageToMagickImage(image))
                    {
                        if (magickImage != null)
                        {
                            // Calculate optimal quality based on image content
                            // Lower quality for larger images to save space
                            int quality = CalculateOptimalQuality(image.PixelWidth, image.PixelHeight);

                            // Configure WebP settings
                            magickImage.Format = MagickFormat.WebP;
                            magickImage.Quality = (uint)quality;

                            // Set WebP specific options
                            magickImage.Settings.SetDefine(MagickFormat.WebP, "lossless", "false");
                            magickImage.Settings.SetDefine(MagickFormat.WebP, "method", "4"); // Balance between speed and quality (0-6)
                            magickImage.Settings.SetDefine(MagickFormat.WebP, "thread-level", "1"); // Multithreaded compression

                            // Write the WebP file
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
                Debug.WriteLine($"Error saving thumbnail to disk: {ex.Message}");
            }
        }

        /// <summary>
        /// Calculates optimal WebP quality based on image dimensions
        /// </summary>
        private static int CalculateOptimalQuality(int width, int height)
        {
            int pixelCount = width * height;

            // For very small thumbnails, use higher quality
            if (pixelCount < 10000) // e.g. 100x100
                return 92;

            // For medium-sized thumbnails, use medium quality
            if (pixelCount < 40000) // e.g. 200x200
                return 85;

            // For larger thumbnails, use lower quality
            return 80;
        }

        /// <summary>
        /// Converts a BitmapImage to MagickImage for WebP encoding
        /// </summary>
        private static MagickImage ConvertBitmapImageToMagickImage(BitmapImage bitmapImage)
        {
            try
            {
                // Convert BitmapImage to PNG in memory first
                PngBitmapEncoder encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmapImage));

                using (MemoryStream memoryStream = new MemoryStream())
                {
                    encoder.Save(memoryStream);
                    memoryStream.Position = 0;

                    // Create MagickImage from the PNG stream
                    return new MagickImage(memoryStream);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error converting BitmapImage to MagickImage: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Clears both memory and disk caches
        /// </summary>
        public static void ClearCache()
        {
            // Cancel all ongoing loading operations first
            CancelAllLoading();

            // Clear memory cache
            _thumbnailCache.Clear();

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

            // Force garbage collection to clean up
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        /// <summary>
        /// Returns statistics about the cache performance
        /// </summary>
        public static (int MemoryCacheSize, long Hits, long Misses, double HitRatio) GetStatistics()
        {
            long totalRequests = _cacheHits + _cacheMisses;
            double hitRatio = totalRequests > 0 ? (double)_cacheHits / totalRequests : 0;

            return (_thumbnailCache.Count, _cacheHits, _cacheMisses, hitRatio);
        }

        /// <summary>
        /// Cleans up orphaned cache files that don't match any current images
        /// </summary>
        private static async Task CleanupOrphanedCacheFilesAsync()
        {
            try
            {
                // Only run if cache folder exists
                if (!PathService.DirectoryExists(_cacheFolder))
                    return;

                // Get cache files older than 7 days
                var oldFiles = Directory.GetFiles(_cacheFolder)
                    .Select(f => new FileInfo(f))
                    .Where(f => f.LastAccessTime < DateTime.Now.AddDays(-7))
                    .ToList();

                if (oldFiles.Count == 0)
                    return;

                Debug.WriteLine($"Cleaning up {oldFiles.Count} orphaned cache files");

                foreach (var file in oldFiles)
                {
                    try
                    {
                        await _diskOperationLock.WaitAsync();
                        try
                        {
                            file.Delete();
                        }
                        finally
                        {
                            _diskOperationLock.Release();
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error deleting orphaned cache file: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in orphaned cache cleanup: {ex.Message}");
            }
        }
    }
}