using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace ImageFolderManager.Models
{
    public class ImageInfo : INotifyPropertyChanged, IDisposable
    {
        private string _filePath;
        public string FilePath
        {
            get => _filePath;
            set
            {
                if (_filePath != value)
                {
                    _filePath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(FileName));
                }
            }
        }

        // Automatically get filename from path
        public string FileName => string.IsNullOrEmpty(FilePath) ? string.Empty : Path.GetFileName(FilePath);

        private BitmapImage _thumbnail;
        public BitmapImage Thumbnail
        {
            get => _thumbnail;
            private set
            {
                if (_thumbnail != value)
                {
                    _thumbnail = value;
                    OnPropertyChanged();
                }
            }
        }

        // Track thumbnail loading state
        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            private set
            {
                if (_isLoading != value)
                {
                    _isLoading = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _isLoaded;
        public bool IsLoaded
        {
            get => _isLoaded;
            private set
            {
                if (_isLoaded != value)
                {
                    _isLoaded = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _isDisposed;
        private CancellationTokenSource _loadingCts;

        // Async thumbnail loading with caching, progress reporting and cancellation
        public async Task<bool> LoadThumbnailAsync(
            CancellationToken externalCancellationToken = default,
            IProgress<double> progress = null)
        {
            if (_isDisposed || string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath))
                return false;

            // Cancel any existing loading operation
            CancelLoading();

            try
            {
                IsLoading = true;

                // Create a new CTS for this loading operation, linked to the external token
                _loadingCts = CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken);
                var token = _loadingCts.Token;

                // Clear old thumbnail to allow GC to collect it
                if (_thumbnail != null && _isLoaded)
                {
                    Thumbnail = null;
                }

                var progressWrapper = new Progress<double>(p =>
                {
                    // Forward progress reporting
                    progress?.Report(p);

                    // Update on UI thread if needed
                    // System.Windows.Application.Current.Dispatcher.Invoke(() => { });
                });

                // Load thumbnail with progress reporting
                var thumbnail = await ImageCache.LoadThumbnailAsync(FilePath, token, progressWrapper);

                // Check if operation was cancelled or object was disposed
                if (token.IsCancellationRequested || _isDisposed)
                {
                    return false;
                }

                // Update the thumbnail
                Thumbnail = thumbnail;
                IsLoaded = thumbnail != null;

                return IsLoaded;
            }
            catch (OperationCanceledException)
            {
                // Operation was cancelled, this is expected
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading thumbnail: {ex.Message}");
                return false;
            }
            finally
            {
                IsLoading = false;
                _loadingCts?.Dispose();
                _loadingCts = null;
            }
        }

        // Cancel the current loading operation
        public void CancelLoading()
        {
            try
            {
                if (_isLoading && _loadingCts != null && !_loadingCts.IsCancellationRequested)
                {
                    _loadingCts.Cancel();
                }

                // Also notify the ImageCache to cancel this path
                if (!string.IsNullOrEmpty(FilePath))
                {
                    ImageCache.CancelLoading(FilePath);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error cancelling loading: {ex.Message}");
            }
        }

        // Handle cleanup of resources
        public void Dispose()
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
                CancelLoading();
                Thumbnail = null;
                _loadingCts?.Dispose();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }
    }
}