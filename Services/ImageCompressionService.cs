using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;

namespace ImageFolderManager.Services
{
    /// <summary>
    /// Provides WebP image compression for an entire folder.
    /// Original image pixel dimensions are always preserved.
    /// </summary>
    public class ImageCompressionService
    {
        private static readonly HashSet<string> SupportedExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" };

        // ─────────────────────────────────────────────────────────────────────────
        // Size helpers  (called by ImageCompressionDialog for the live preview)
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the combined byte size of all supported image files in the folder.
        /// </summary>
        public static long GetFolderImageSize(string folderPath)
        {
            if (!Directory.Exists(folderPath)) return 0;
            return Directory.GetFiles(folderPath)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
                .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
        }

        /// <summary>
        /// Estimates the compressed folder size WITHOUT writing any files.
        /// Performs an in-memory trial compression on the first image to derive a
        /// realistic ratio, then scales to the whole folder.
        /// </summary>
        public static async Task<long> EstimateCompressedSizeAsync(
            string folderPath,
            int quality,
            CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(folderPath)) return 0;

            var files = Directory.GetFiles(folderPath)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
                .ToList();
            if (files.Count == 0) return 0;

            long originalTotal = files.Sum(f =>
            { try { return new FileInfo(f).Length; } catch { return 0L; } });

            string trialFile     = files.First();
            long   trialOriginal = new FileInfo(trialFile).Length;
            if (trialOriginal == 0)
                return (long)(originalTotal * HeuristicRatio(quality));

            try
            {
                long trialCompressed = await Task.Run(
                    () => TrialCompress(trialFile, quality, cancellationToken),
                    cancellationToken);

                if (trialCompressed <= 0)
                    return (long)(originalTotal * HeuristicRatio(quality));

                double ratio = (double)trialCompressed / trialOriginal;
                return (long)(originalTotal * ratio);
            }
            catch
            {
                return (long)(originalTotal * HeuristicRatio(quality));
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Compression
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Compresses all supported images in <paramref name="folderPath"/> to WebP.
        /// The original pixel dimensions (width x height) are strictly preserved —
        /// MagickImage loads at full resolution and we never call Resize/Scale/Sample.
        /// </summary>
        public async Task<CompressionResult> CompressImagesAsync(
            string folderPath,
            int quality,
            bool deleteSourceFiles,
            IProgress<double> progress = null,
            CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(folderPath))
                throw new DirectoryNotFoundException($"Folder not found: {folderPath}");

            quality = Math.Max(1, Math.Min(100, quality));

            var files = Directory.GetFiles(folderPath)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
                .OrderBy(f => Path.GetFileName(f))
                .ToList();

            var result = new CompressionResult { TotalFiles = files.Count };
            if (files.Count == 0) return result;

            int processed = 0;
            foreach (string sourcePath in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ProcessSingleFile(sourcePath, quality, deleteSourceFiles, result);
                }, cancellationToken);
                processed++;
                progress?.Report((double)processed / files.Count);
            }
            return result;
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Private helpers
        // ─────────────────────────────────────────────────────────────────────────

        private static void ProcessSingleFile(
            string sourcePath, int quality, bool deleteSourceFiles, CompressionResult result)
        {
            try
            {
                string directory      = Path.GetDirectoryName(sourcePath) ?? string.Empty;
                string nameWithoutExt = Path.GetFileNameWithoutExtension(sourcePath);
                string sourceExt      = Path.GetExtension(sourcePath).ToLowerInvariant();
                bool   sourceIsWebp   = sourceExt == ".webp";

                string outputPath = Path.Combine(directory, nameWithoutExt + ".webp");
                // Temp path avoids reading and writing the same file simultaneously
                string writePath  = sourceIsWebp
                    ? Path.Combine(directory, nameWithoutExt + "_tmp_compress.webp")
                    : outputPath;

                long originalSize = new FileInfo(sourcePath).Length;

                using (var image = new MagickImage(sourcePath))
                {
                    // Pixel dimensions are unchanged: we only strip metadata and re-encode.
                    // No Resize / Scale / Sample call is made.
                    image.Strip();                     // remove EXIF / ICC / embedded thumbnail
                    image.Format  = MagickFormat.WebP;
                    image.Quality = (uint)quality;
                    image.Write(writePath);
                }

                long compressedSize = new FileInfo(writePath).Length;

                if (sourceIsWebp)
                {
                    File.Delete(sourcePath);
                    File.Move(writePath, sourcePath);  // atomic replace
                }
                else if (deleteSourceFiles)
                {
                    File.Delete(sourcePath);
                }

                result.SucceededFiles++;
                result.OriginalTotalBytes   += originalSize;
                result.CompressedTotalBytes += compressedSize;
            }
            catch (Exception ex)
            {
                result.FailedFiles++;
                result.Errors.Add($"{Path.GetFileName(sourcePath)}: {ex.Message}");
                System.Diagnostics.Debug.WriteLine(
                    $"[ImageCompressionService] Error on '{sourcePath}': {ex.Message}");
            }
        }

        /// <summary>
        /// In-memory trial compression — no files are written to disk.
        /// </summary>
        private static long TrialCompress(string filePath, int quality, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            using (var image = new MagickImage(filePath))
            {
                image.Strip();
                image.Format  = MagickFormat.WebP;
                image.Quality = (uint)quality;
                using (var ms = new MemoryStream())
                {
                    image.Write(ms);
                    return ms.Length;
                }
            }
        }

        /// <summary>
        /// Quality-to-ratio heuristic: quality 100 ≈ 90 %, quality 1 ≈ 5 % of original.
        /// </summary>
        private static double HeuristicRatio(int quality)
        {
            double q = Math.Max(1, Math.Min(100, quality));
            return 0.05 + (q - 1) / 99.0 * 0.85;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Result DTO
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Summary of a completed compression run.</summary>
    public class CompressionResult
    {
        public int          TotalFiles           { get; set; }
        public int          SucceededFiles       { get; set; }
        public int          FailedFiles          { get; set; }
        public long         OriginalTotalBytes   { get; set; }
        public long         CompressedTotalBytes { get; set; }
        public List<string> Errors               { get; } = new List<string>();

        public double SpaceSavedPercent =>
            OriginalTotalBytes > 0
                ? (1.0 - (double)CompressedTotalBytes / OriginalTotalBytes) * 100.0
                : 0.0;

        public string Summary =>
            $"Converted {SucceededFiles}/{TotalFiles} images.  " +
            $"Space saved: {FormatBytes(OriginalTotalBytes - CompressedTotalBytes)} " +
            $"({SpaceSavedPercent:F1}%).";

        public static string FormatBytes(long bytes)
        {
            if (bytes < 0)                   return $"-{FormatBytes(-bytes)}";
            if (bytes < 1024)                return $"{bytes} B";
            if (bytes < 1024 * 1024)         return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }
    }
}
