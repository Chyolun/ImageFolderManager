using ImageFolderManager.Models;
using ImageFolderManager.Services;
using ImageFolderManager.Controls;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Media.Imaging;
using System;
using System.IO;
using System.Collections.Generic;

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
                // Clean up old thumbnail
                _thumbnail = null;
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

    private bool _isAnimated;
    public bool IsAnimated
    {
        get => _isAnimated;
        private set { if (_isAnimated != value) { _isAnimated = value; OnPropertyChanged(); } }
    }

    private List<AnimatedFrame> _animatedFrames;
    public List<AnimatedFrame> AnimatedFrames
    {
        get => _animatedFrames;
        private set { if (_animatedFrames != value) { _animatedFrames = value; OnPropertyChanged(); } }
    }

    /// <summary>
    /// Loads the thumbnail for this image
    /// </summary>
    public async Task<bool> LoadThumbnailAsync(
        CancellationToken externalCancellationToken = default,
        IProgress<double> progress = null)
    {
        // Validate path
        string normalizedPath = PathService.NormalizePath(FilePath);
        if (string.IsNullOrEmpty(normalizedPath) || !File.Exists(normalizedPath))
            return false;

        // Short-circuit if already loaded
        if (_isLoaded && _thumbnail != null && !_thumbnail.IsFrozen)
            return true;

        // Cancel any existing loading operation
        CancelLoading();

        try
        {
            IsLoading = true;

            // Create new cancellation token source linked to external token
            _loadingCts = CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken);
            var token = _loadingCts.Token;

            // Clear old thumbnail to help GC
            if (_thumbnail != null && _isLoaded)
            {
                Thumbnail = null;
            }

           // Check if it's a GIF
            string ext = Path.GetExtension(normalizedPath).ToLowerInvariant();
            if (ext == ".gif" || ext == ".webp")
            {
                var frames = await AnimatedImageLoader.LoadFramesAsync(normalizedPath, token);
                if (frames != null && frames.Count > 1)
                {
                    AnimatedFrames = frames;
                    IsAnimated = true;
                    IsLoaded = true;
                    IsLoading = false;
                    return true;
                }
            }
        
            // Load thumbnail through ImageCache
            var thumbnail = await ImageCache.LoadThumbnailAsync(FilePath, token, progress);

            // Check if operation was cancelled or object was disposed
            if (token.IsCancellationRequested || _isDisposed)
                return false;

            // Update thumbnail
            Thumbnail = thumbnail;
            IsLoaded = thumbnail != null;

            return IsLoaded;
        }
        catch (OperationCanceledException)
        {
            // Operation was cancelled, which is expected
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

    /// <summary>
    /// Cancels the current loading operation
    /// </summary>
    public void CancelLoading()
    {
        try
        {
            // Cancel our local operation
            if (_isLoading && _loadingCts != null && !_loadingCts.IsCancellationRequested)
            {
                _loadingCts.Cancel();
            }

            // Also notify the ImageCache to cancel
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

    /// <summary>
    /// Handles cleanup of resources
    /// </summary>
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