using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using ImageFolderManager.Controls;
using ImageMagick;

namespace ImageFolderManager.Services
{
    public static class AnimatedImageLoader
    {
        /// <summary>
        /// Load all frames of the GIF. If it is not a GIF (frame count <= 1), null is returned.
        /// </summary>
        public static async Task<List<AnimatedFrame>> LoadFramesAsync(
            string filePath,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return null;

            string ext = Path.GetExtension(filePath).ToLowerInvariant();

            try
            {
                if (ext == ".gif")
                    return await LoadGifFramesAsync(filePath, cancellationToken);
                if (ext == ".webp")
                    return await LoadWebPFramesAsync(filePath, cancellationToken);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AnimatedImageLoader] Error loading frames: {ex.Message}");
            }
            return null;
        }

        // ── GIF ──────────────────────────────────────────────────────────────

        private static Task<List<AnimatedFrame>> LoadGifFramesAsync(
            string filePath, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                byte[] data = File.ReadAllBytes(filePath);
                using var ms = new MemoryStream(data);

                var decoder = new GifBitmapDecoder(
                    ms,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);

                if (decoder.Frames.Count <= 1)
                    return null; // Static GIF, go normal thumbnail

                var frames = new List<AnimatedFrame>();
                var metadata = decoder.Metadata as BitmapMetadata;

                for (int i = 0; i < decoder.Frames.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var bitmapFrame = decoder.Frames[i];
                    // Read frame delay (unit: 1/100 second)
                    int delayCs = 10; // delay 10/100s = 100ms
                    try
                    {
                        var frameMeta = bitmapFrame.Metadata as BitmapMetadata;
                        if (frameMeta != null)
                        {
                            // "/grctlext/Delay" return ushort，单位 1/100s
                            var delayObj = frameMeta.GetQuery("/grctlext/Delay");
                            if (delayObj is ushort delayU)
                                delayCs = delayU;
                        }
                    }
                    catch { /* The metadata read failed and the default value is used，使用默认值 */ }

                    // Convert to WriteableBitmap (make sure UI thread is available)
                    var wb = new WriteableBitmap(bitmapFrame);
                    wb.Freeze();

                    frames.Add(new AnimatedFrame
                    {
                        Image = wb,
                        DelayMs = delayCs * 10 // 1/100s → ms
                    });
                }

                return frames.Count > 1 ? frames : null;
            }, cancellationToken);
        }

        // ── WebP ─────────────────────────────────────────────────────────────

        private static Task<List<AnimatedFrame>> LoadWebPFramesAsync(
            string filePath, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var collection = new MagickImageCollection(filePath);

                if (collection.Count <= 1)
                    return null; // Static WebP

                var frames = new List<AnimatedFrame>();

                foreach (var magickImage in collection)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Read frame delay (Magick.NET units: milliseconds, AnimationDelay units: 1/100s)
                    int delayMs = (int)(magickImage.AnimationDelay * 10); // cs → ms
                    if (delayMs < 20) delayMs = 100;

                    // Convert to BitmapSource
                    using var pngStream = new MemoryStream();
                    magickImage.Format = MagickFormat.Png;
                    magickImage.Write(pngStream);
                    pngStream.Position = 0;

                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.StreamSource = pngStream;
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();

                    frames.Add(new AnimatedFrame { Image = bitmap, DelayMs = delayMs });
                }

                return frames.Count > 1 ? frames : null;
            }, cancellationToken);
        }
    }
}