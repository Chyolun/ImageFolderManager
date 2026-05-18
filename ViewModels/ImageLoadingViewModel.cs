using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ImageFolderManager.Models;
using ImageFolderManager.Services;
using ImageFolderManager.Views;

namespace ImageFolderManager.ViewModels
{
    /// <summary>
    /// Handles image loading, caching, and display operations
    /// </summary>
    public class ImageLoadingViewModel : ViewModelBase
    {
        private readonly UnifiedFolderService _folderService;
        private CancellationTokenSource _imageLoadingCts;
        private readonly object _loadingLock = new object();

        #region Properties

        private ObservableCollection<ImageInfo> _images = new ObservableCollection<ImageInfo>();
        public ObservableCollection<ImageInfo> Images
        {
            get => _images;
            private set => SetProperty(ref _images, value);
        }

        private bool _isLoadingImages;
        public bool IsLoadingImages
        {
            get => _isLoadingImages;
            private set => SetProperty(ref _isLoadingImages, value);
        }

        private FolderInfo _currentFolder;
        public FolderInfo CurrentFolder
        {
            get => _currentFolder;
            private set => SetProperty(ref _currentFolder, value);
        }

        #endregion

        #region Events

        public event EventHandler<string> StatusMessageChanged;
        public event EventHandler<ImageLoadingEventArgs> ImagesLoaded;

        #endregion

        public ImageLoadingViewModel(UnifiedFolderService folderService)
        {
            _folderService = folderService ?? throw new ArgumentNullException(nameof(folderService));
        }

        #region Public Methods

        public async Task LoadImagesAsync(FolderInfo folder)
        {
            if (folder == null) return;

            lock (_loadingLock)
            {
                if (_isLoadingImages) return;
                IsLoadingImages = true;
            }

            // Cancel any existing loading operation
            CancelCurrentLoading();

            _imageLoadingCts = new CancellationTokenSource();
            CurrentFolder = folder;

            try
            {
                Images.Clear();
                UpdateStatus($"Loading images from '{folder.Name}'...");

                var imageFiles = GetImageFiles(folder.FolderPath);
                if (imageFiles.Count == 0)
                {
                    UpdateStatus($"No images found in '{folder.Name}'");
                    OnImagesLoaded(new ImageLoadingEventArgs { Folder = folder, ImageCount = 0 });
                    return;
                }

                await LoadImagesWithProgress(imageFiles, folder);
            }
            catch (OperationCanceledException)
            {
                UpdateStatus("Image loading cancelled.");
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error loading images: {ex.Message}");
            }
            finally
            {
                lock (_loadingLock)
                {
                    IsLoadingImages = false;
                }

                _imageLoadingCts?.Dispose();
                _imageLoadingCts = null;
            }
        }

        public void CancelCurrentLoading()
        {
            if (_imageLoadingCts != null && !_imageLoadingCts.IsCancellationRequested)
            {
                _imageLoadingCts.Cancel();

                // Also cancel individual image loading in ImageCache
                foreach (var image in Images)
                {
                    image.CancelLoading();
                }
            }
        }

        public void ClearImages()
        {
            CancelCurrentLoading();
            Images.Clear();
            CurrentFolder = null;
            UpdateStatus("Images cleared");
        }

        #endregion

        #region Private Methods

        private List<string> GetImageFiles(string path)
        {
            var supportedExtensions = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" };

            if (!Directory.Exists(path))
                return new List<string>();

            return Directory.GetFiles(path)
                .Where(file => supportedExtensions.Contains(
                    Path.GetExtension(file).ToLowerInvariant()))
                .OrderBy(file => Path.GetFileName(file), WindowsNaturalStringComparer.Instance)
                .ToList();
        }

        private async Task LoadImagesWithProgress(List<string> imageFiles, FolderInfo folder)
        {
            var progressDialog = new ProgressDialog(
                "Loading Images",
                $"Loading image previews from '{folder.Name}'...");

            progressDialog.Owner = Application.Current.MainWindow;

            progressDialog.CancelRequested += (s, e) =>
            {
                _imageLoadingCts?.Cancel();
                UpdateStatus("Image loading cancelled.");
            };

            var loadingTask = LoadImagesInBatches(imageFiles, progressDialog, _imageLoadingCts.Token);

            progressDialog.ShowDialog();

            if (progressDialog.IsCancelled && !_imageLoadingCts.IsCancellationRequested)
            {
                _imageLoadingCts.Cancel();
            }

            await loadingTask;

            if (!_imageLoadingCts.IsCancellationRequested)
            {
                UpdateStatus($"Loaded {Images.Count} images from '{folder.Name}'");
                OnImagesLoaded(new ImageLoadingEventArgs
                {
                    Folder = folder,
                    ImageCount = Images.Count
                });
            }
        }

        private async Task LoadImagesInBatches(
            List<string> imageFiles,
            ProgressDialog progressDialog,
            CancellationToken cancellationToken)
        {
            int totalImages = imageFiles.Count;
            int processedImages = 0;
            int batchSize = Math.Max(1, Math.Min(10, totalImages / 10));
            int parallelism = Math.Min(Environment.ProcessorCount, AppSettings.Instance.ParallelThreadCount);

            for (int startIndex = 0; startIndex < imageFiles.Count; startIndex += batchSize)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                var batch = imageFiles.Skip(startIndex).Take(batchSize).ToList();
                var batchResults = new List<ImageInfo>();

                using var throttler = new SemaphoreSlim(parallelism, parallelism);
                var loadTasks = batch.Select(async file =>
                {
                    await throttler.WaitAsync(cancellationToken);
                    try
                    {
                        var imageInfo = new ImageInfo { FilePath = file };
                        bool success = await imageInfo.LoadThumbnailAsync(cancellationToken);

                        if (success && !cancellationToken.IsCancellationRequested)
                        {
                            lock (batchResults)
                            {
                                batchResults.Add(imageInfo);
                            }
                        }
                        else if (!success)
                        {
                            imageInfo.Dispose();
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when cancelling
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error loading image: {ex.Message}");
                    }
                    finally
                    {
                        throttler.Release();
                    }
                }).ToList();

                await Task.WhenAll(loadTasks);

                processedImages += batch.Count;
                double progress = (double)processedImages / totalImages;

                if (!cancellationToken.IsCancellationRequested)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        var sortedResults = batchResults
                            .OrderBy(img => Path.GetFileName(img.FilePath), WindowsNaturalStringComparer.Instance)
                            .ToList();
                        foreach (var img in sortedResults)
                        {
                            Images.Add(img);
                        }
                    });

                    progressDialog.UpdateProgress(progress, $"Loaded {processedImages} of {totalImages} images...");
                    UpdateStatus($"Loaded {Images.Count} of {totalImages} images...");
                }
            }

            progressDialog.UpdateProgress(1.0, "Loading complete!");
        }

        #endregion

        #region Helper Methods

        private void UpdateStatus(string message)
        {
            StatusMessageChanged?.Invoke(this, message);
        }

        private void OnImagesLoaded(ImageLoadingEventArgs e)
        {
            ImagesLoaded?.Invoke(this, e);
        }

        #endregion

        #region IDisposable Implementation

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Cancel current loading operations
                CancelCurrentLoading();

                // Dispose the cancellation token source
                _imageLoadingCts?.Dispose();
                _imageLoadingCts = null;

                // Dispose all ImageInfo objects which implement IDisposable
                if (Images != null)
                {
                    foreach (var image in Images)
                    {
                        image?.Dispose();
                    }
                    Images.Clear();
                }

                // Clear current folder reference
                CurrentFolder = null;
            }

            base.Dispose(disposing);
        }

        #endregion
    }
}
