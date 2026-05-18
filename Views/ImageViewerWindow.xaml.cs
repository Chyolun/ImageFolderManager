using ImageMagick;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Controls;
using ImageFolderManager.Services;

namespace ImageFolderManager.Views
{
    public partial class ImageViewerWindow
    {
        private readonly List<string> _imagePaths;
        private int _currentIndex;
        private readonly DispatcherTimer _animationTimer;
        private readonly Stopwatch _animationClock = new Stopwatch();
        private long _nextFrameDueMs;
        private readonly object _animationFramesLock = new object();
        private List<BitmapSource> _animationFrames;
        private List<int> _animationFrameDelays;
        private int _animationFrameIndex;
        private bool _isAnimated;
        private CancellationTokenSource _animationLoadCts;
        private int _animationLoadRequestId;
        private double _zoom = 1.0;
        private const double MinZoom = 0.05;
        private const double MaxZoom = 20.0;
        private const long LargeAnimatedWebpThresholdBytes = 10L * 1024L * 1024L;
        private const int MaxLargeAnimationFrames = 1200;
        private const int MaxLargeAnimationDimension = 1920;
        private const long DefaultAnimationMemoryBudgetBytes = 192L * 1024L * 1024L;
        private const long WebpAnimationBudgetMinBytes = 500L * 1024L * 1024L;
        private const long WebpAnimationBudgetMaxBytes = 700L * 1024L * 1024L;
        private const int WebpBudgetScaleFactor = 70; // 10MB -> ~700MB
        private const int AnimationTickIntervalMs = 15;
        private const int MaxPlaybackFps = 24;
        private const int MinFrameDelayMs = (1000 + MaxPlaybackFps - 1) / MaxPlaybackFps;
        private bool _isDragging;
        private Point _dragStartPoint;
        private double _dragStartHorizontalOffset;
        private double _dragStartVerticalOffset;
        private bool _isFitMode = true;
        private const double EdgeTriggerWidth = 120.0;

        public ImageViewerWindow(IEnumerable<string> imagePaths, int initialIndex)
        {
            InitializeComponent();

            var sourcePaths = imagePaths?
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
                ?? new List<string>();

            string initialPath = initialIndex >= 0 && initialIndex < sourcePaths.Count
                ? sourcePaths[initialIndex]
                : null;

            _imagePaths = sourcePaths
                .OrderBy(path => Path.GetFileName(path), WindowsNaturalStringComparer.Instance)
                .ToList();

            if (_imagePaths.Count == 0)
            {
                throw new ArgumentException("No image paths provided.", nameof(imagePaths));
            }

            _currentIndex = !string.IsNullOrWhiteSpace(initialPath)
                ? _imagePaths.FindIndex(path => string.Equals(path, initialPath, StringComparison.OrdinalIgnoreCase))
                : -1;

            if (_currentIndex < 0)
            {
                _currentIndex = Math.Max(0, Math.Min(initialIndex, _imagePaths.Count - 1));
            }

            _animationTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(AnimationTickIntervalMs)
            };
            _animationTimer.Tick += AnimationTimer_Tick;

            Loaded += (_, __) => LoadCurrentImage(fitToWindow: true);
            Closed += (_, __) =>
            {
                EndDrag();
                StopAnimation();
                CancelAnimationLoad();
            };
            SizeChanged += (_, __) =>
            {
                if (!_isAnimated)
                {
                    return;
                }

                // Keep animated images fit on resize for better default browsing experience.
                FitToWindow();
            };
        }

        private void LoadCurrentImage(bool fitToWindow)
        {
            if (_currentIndex < 0 || _currentIndex >= _imagePaths.Count)
            {
                return;
            }

            var filePath = _imagePaths[_currentIndex];
            if (!File.Exists(filePath))
            {
                MessageBox.Show(this, $"File not found:\n{filePath}", "Image Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            StopAnimation();
            CancelAnimationLoad();
            RenderOptions.SetBitmapScalingMode(ViewerImage, BitmapScalingMode.HighQuality);
            ViewerImage.Source = null;
            lock (_animationFramesLock)
            {
                _animationFrames = null;
                _animationFrameDelays = null;
                _animationFrameIndex = 0;
            }
            _isAnimated = false;

            var extension = Path.GetExtension(filePath)?.ToLowerInvariant() ?? string.Empty;
            if (extension == ".gif")
            {
                _ = TryLoadAnimatedAsync(filePath);
            }
            else if (extension == ".webp")
            {
                _ = TryLoadWebpAsync(filePath);
            }
            else
            {
                LoadStatic(filePath);
            }

            if (fitToWindow)
            {
                _isFitMode = true;
                Dispatcher.BeginInvoke(new Action(FitToWindow), DispatcherPriority.Loaded);
            }
            else
            {
                _isFitMode = false;
                ApplyZoom();
            }

            UpdateStatus(filePath);
        }

        private void LoadStatic(string filePath)
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.UriSource = new Uri(filePath);
            bitmap.EndInit();
            bitmap.Freeze();
            ViewerImage.Source = bitmap;
        }

        private void CancelAnimationLoad()
        {
            var cts = _animationLoadCts;
            if (cts != null)
            {
                _animationLoadCts = null;
                cts.Cancel();
            }
        }

        private async Task TryLoadAnimatedAsync(string filePath)
        {
            CancelAnimationLoad();

            var cts = new CancellationTokenSource();
            _animationLoadCts = cts;
            int requestId = unchecked(++_animationLoadRequestId);
            var localFrames = new List<BitmapSource>(64);
            var localDelays = new List<int>(64);
            bool playbackStarted = false;
            bool firstFrameShown = false;

            bool IsRequestStillValid()
            {
                return !cts.IsCancellationRequested &&
                       requestId == _animationLoadRequestId &&
                       _currentIndex >= 0 &&
                       _currentIndex < _imagePaths.Count &&
                       string.Equals(_imagePaths[_currentIndex], filePath, StringComparison.OrdinalIgnoreCase);
            }

            void ShowFirstFrameOnUiThreadIfReady()
            {
                if (firstFrameShown)
                {
                    return;
                }

                BitmapSource first = null;
                lock (_animationFramesLock)
                {
                    if (localFrames.Count == 0)
                    {
                        return;
                    }

                    first = localFrames[0];
                    firstFrameShown = true;
                }

                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!IsRequestStillValid())
                    {
                        return;
                    }

                    if (!_isAnimated)
                    {
                        ViewerImage.Source = first;
                        UpdateStatus(filePath);
                    }
                }), DispatcherPriority.Render);
            }

            void StartPlaybackOnUiThreadIfReady()
            {
                if (playbackStarted)
                {
                    return;
                }

                lock (_animationFramesLock)
                {
                    if (localFrames.Count <= 1)
                    {
                        return;
                    }
                    playbackStarted = true;
                }

                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!IsRequestStillValid())
                    {
                        return;
                    }

                    lock (_animationFramesLock)
                    {
                        if (localFrames.Count <= 1)
                        {
                            return;
                        }

                        _animationFrames = localFrames;
                        _animationFrameDelays = localDelays;
                        _animationFrameIndex = 0;
                        _isAnimated = true;

                        ViewerImage.Source = _animationFrames[_animationFrameIndex];
                        RenderOptions.SetBitmapScalingMode(ViewerImage, BitmapScalingMode.LowQuality);

                        _animationClock.Restart();
                        _nextFrameDueMs = _animationFrameDelays[_animationFrameIndex];
                    }

                    _animationTimer.Start();
                    UpdateStatus(filePath);
                }), DispatcherPriority.Render);
            }

            try
            {
                await Task.Run(() =>
                {
                    DecodeAnimatedFrames(filePath, cts.Token, frame =>
                    {
                        if (cts.IsCancellationRequested || frame.Pixels.Length == 0)
                        {
                            return;
                        }

                        var bitmap = CreateBitmapSourceFromDecoded(frame);

                        lock (_animationFramesLock)
                        {
                            localFrames.Add(bitmap);
                            localDelays.Add(frame.DelayMs);
                        }

                        frame.Pixels = Array.Empty<byte>();

                        if (!firstFrameShown)
                        {
                            ShowFirstFrameOnUiThreadIfReady();
                        }

                        if (!playbackStarted)
                        {
                            StartPlaybackOnUiThreadIfReady();
                        }
                    });
                });

                if (IsRequestStillValid() && !firstFrameShown)
                {
                    ShowFirstFrameOnUiThreadIfReady();
                }

                if (IsRequestStillValid() && !playbackStarted)
                {
                    StartPlaybackOnUiThreadIfReady();
                }
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            catch
            {
                Debug.WriteLine($"TryLoadAnimatedAsync failed: {filePath}");
                _isAnimated = false;
                lock (_animationFramesLock)
                {
                    _animationFrames = null;
                    _animationFrameDelays = null;
                }
            }
            finally
            {
                if (ReferenceEquals(_animationLoadCts, cts))
                {
                    _animationLoadCts = null;
                }
                cts.Dispose();
            }
        }

        private async Task TryLoadWebpAsync(string filePath)
        {
            CancelAnimationLoad();

            var cts = new CancellationTokenSource();
            _animationLoadCts = cts;
            int requestId = unchecked(++_animationLoadRequestId);

            bool IsRequestStillValid()
            {
                return !cts.IsCancellationRequested &&
                       requestId == _animationLoadRequestId &&
                       _currentIndex >= 0 &&
                       _currentIndex < _imagePaths.Count &&
                       string.Equals(_imagePaths[_currentIndex], filePath, StringComparison.OrdinalIgnoreCase);
            }

            try
            {
                var previewFrame = await Task.Run(
                    () => TryDecodeWebpPreviewFrame(filePath, cts.Token),
                    cts.Token);

                if (previewFrame != null && IsRequestStillValid())
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!IsRequestStillValid())
                        {
                            return;
                        }

                        ViewerImage.Source = previewFrame;
                        RenderOptions.SetBitmapScalingMode(ViewerImage, BitmapScalingMode.HighQuality);

                        if (_isFitMode)
                        {
                            Dispatcher.BeginInvoke(new Action(FitToWindow), DispatcherPriority.Loaded);
                        }
                    }, DispatcherPriority.Render, cts.Token);
                }

                if (IsRequestStillValid())
                {
                    await TryLoadAnimatedAsync(filePath);
                }
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            catch
            {
                Debug.WriteLine($"TryLoadWebpAsync failed: {filePath}");
            }
            finally
            {
                if (ReferenceEquals(_animationLoadCts, cts))
                {
                    _animationLoadCts = null;
                }

                cts.Dispose();
            }
        }

        private sealed class DecodedAnimationFrame
        {
            public int Width { get; }
            public int Height { get; }
            public byte[] Pixels { get; set; }
            public int DelayMs { get; }

            public DecodedAnimationFrame(int width, int height, byte[] pixels, int delayMs)
            {
                Width = width;
                Height = height;
                Pixels = pixels;
                DelayMs = delayMs;
            }
        }

        private static void DecodeAnimatedFrames(
            string filePath,
            CancellationToken ct,
            Action<DecodedAnimationFrame> onFrame)
        {
            using (var collection = new MagickImageCollection(filePath))
            {
                if (collection.Count <= 1)
                {
                    return;
                }

                bool isWebp = Path.GetExtension(filePath)
                    .Equals(".webp", StringComparison.OrdinalIgnoreCase);
                long fileSizeBytes = 0;
                try
                {
                    fileSizeBytes = new FileInfo(filePath).Length;
                }
                catch
                {
                    // keep 0; fallback budget below
                }

                if (RequiresCoalesce(collection))
                {
                    collection.Coalesce();
                }

                bool isLargeWebp = isWebp && fileSizeBytes >= LargeAnimatedWebpThresholdBytes;
                int webpDelayScale = DetectWebpDelayScale(collection, isWebp);

                int frameStep = 1;
                int targetMaxFrames = Math.Min(collection.Count, MaxLargeAnimationFrames);

                int estimatedWidth = (int)collection[0].Width;
                int estimatedHeight = (int)collection[0].Height;
                if (isLargeWebp && (estimatedWidth > MaxLargeAnimationDimension || estimatedHeight > MaxLargeAnimationDimension))
                {
                    double scale = Math.Min((double)MaxLargeAnimationDimension / estimatedWidth, (double)MaxLargeAnimationDimension / estimatedHeight);
                    estimatedWidth = Math.Max(1, (int)Math.Round(estimatedWidth * scale));
                    estimatedHeight = Math.Max(1, (int)Math.Round(estimatedHeight * scale));
                }

                long bytesPerFrame = Math.Max(1L, (long)estimatedWidth * estimatedHeight * 4L);
                long memoryBudget = ComputeAnimationMemoryBudgetBytes(fileSizeBytes, isWebp);
                int maxFramesByMemory = (int)Math.Max(2L, memoryBudget / bytesPerFrame);
                targetMaxFrames = Math.Min(targetMaxFrames, maxFramesByMemory);

                if (collection.Count > targetMaxFrames)
                {
                    frameStep = (int)Math.Ceiling((double)collection.Count / targetMaxFrames);
                }

                for (int i = 0; i < collection.Count; i += frameStep)
                {
                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }

                    int accumulatedDelayCs = 0;
                    int maxIndex = Math.Min(collection.Count, i + frameStep);
                    for (int j = i; j < maxIndex; j++)
                    {
                        accumulatedDelayCs += Math.Max(1, (int)collection[j].AnimationDelay);
                    }

                    var frame = collection[i];
                    int maxDimension = isLargeWebp ? MaxLargeAnimationDimension : 0;
                    var decoded = DecodeFrame(frame, maxDimension);
                    onFrame(new DecodedAnimationFrame(
                        decoded.Width,
                        decoded.Height,
                        decoded.Pixels,
                        ConvertDelayToMilliseconds(accumulatedDelayCs, isWebp, webpDelayScale)));
                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }
                }
            }
        }

        private static long ComputeAnimationMemoryBudgetBytes(long fileSizeBytes, bool isWebp)
        {
            if (!isWebp)
            {
                return DefaultAnimationMemoryBudgetBytes;
            }

            long scaled = Math.Max(1L, fileSizeBytes) * WebpBudgetScaleFactor;
            if (scaled < WebpAnimationBudgetMinBytes)
            {
                return WebpAnimationBudgetMinBytes;
            }

            if (scaled > WebpAnimationBudgetMaxBytes)
            {
                return WebpAnimationBudgetMaxBytes;
            }

            return scaled;
        }

        private static int DetectWebpDelayScale(MagickImageCollection collection, bool isWebp)
        {
            if (!isWebp || collection.Count == 0)
            {
                return 1;
            }

            int probe = Math.Min(16, collection.Count);
            int tinyDelayCount = 0;
            for (int i = 0; i < probe; i++)
            {
                int d = Math.Max(1, (int)collection[i].AnimationDelay);
                if (d <= 10)
                {
                    tinyDelayCount++;
                }
            }

            // Many WebP encoders store centiseconds-like values (5/10).
            // Promote to milliseconds scale for stable real-world speed.
            return tinyDelayCount >= (probe * 3 / 4) ? 10 : 1;
        }

        private static bool RequiresCoalesce(MagickImageCollection collection)
        {
            if (collection.Count <= 1)
            {
                return false;
            }

            int canvasWidth = collection[0].Page.Width > 0
                ? (int)collection[0].Page.Width
                : (int)collection[0].Width;
            int canvasHeight = collection[0].Page.Height > 0
                ? (int)collection[0].Page.Height
                : (int)collection[0].Height;

            for (int i = 0; i < collection.Count; i++)
            {
                var frame = collection[i];
                bool fullCanvas = frame.Width == canvasWidth &&
                                  frame.Height == canvasHeight &&
                                  frame.Page.X == 0 &&
                                  frame.Page.Y == 0;
                if (!fullCanvas)
                {
                    return true;
                }
            }

            return false;
        }

        private static (int Width, int Height, byte[] Pixels) DecodeFrame(IMagickImage<byte> frame, int maxDimension)
        {
            using (var cloned = frame.Clone())
            {
                if (maxDimension > 0 &&
                    (cloned.Width > maxDimension || cloned.Height > maxDimension))
                {
                    cloned.Resize(new MagickGeometry((uint)maxDimension, (uint)maxDimension));
                }

                var width = (int)cloned.Width;
                var height = (int)cloned.Height;
                var pixels = cloned.GetPixels().ToByteArray(PixelMapping.BGRA);
                return (width, height, pixels);
            }
        }

        private static int ConvertDelayToMilliseconds(int rawDelay, bool isWebp, int webpDelayScale)
        {
            // ImageMagick delay values are generally centiseconds for GIF.
            // For WebP, delay unit can vary by source/decoder, so we probe and scale.
            int ms = isWebp ? rawDelay * webpDelayScale : rawDelay * 10;
            if (ms < MinFrameDelayMs) ms = MinFrameDelayMs;
            if (ms > 500) ms = 500;
            return ms;
        }

        private static BitmapSource CreateBitmapSourceFromDecoded(DecodedAnimationFrame frame)
        {
            var stride = frame.Width * 4;
            var bitmap = BitmapSource.Create(
                frame.Width,
                frame.Height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                frame.Pixels,
                stride);
            bitmap.Freeze();
            return bitmap;
        }

        private static BitmapSource TryDecodeWebpPreviewFrame(string filePath, CancellationToken ct)
        {
            using (var collection = new MagickImageCollection(filePath))
            {
                ct.ThrowIfCancellationRequested();
                if (collection.Count == 0)
                {
                    return null;
                }

                var firstFrame = collection[0];
                var decoded = DecodeFrame(firstFrame, maxDimension: 0);
                return CreateBitmapSourceFromDecoded(new DecodedAnimationFrame(
                    decoded.Width,
                    decoded.Height,
                    decoded.Pixels,
                    MinFrameDelayMs));
            }
        }

        private void StopAnimation()
        {
            _animationTimer.Stop();
            _animationClock.Reset();
            _nextFrameDueMs = 0;
            lock (_animationFramesLock)
            {
                _animationFrameIndex = 0;
            }
            _isAnimated = false;
            RenderOptions.SetBitmapScalingMode(ViewerImage, BitmapScalingMode.HighQuality);
        }

        private void AnimationTimer_Tick(object sender, EventArgs e)
        {
            long elapsedMs = _animationClock.ElapsedMilliseconds;
            if (elapsedMs < _nextFrameDueMs)
            {
                return;
            }

            BitmapSource nextSource = null;
            lock (_animationFramesLock)
            {
                if (!_isAnimated ||
                    _animationFrames == null ||
                    _animationFrameDelays == null ||
                    _animationFrames.Count == 0)
                {
                    return;
                }

                int previousIndex = _animationFrameIndex;
                int safety = 0;
                int maxAdvance = Math.Max(4, _animationFrames.Count);

                // Time-based advancement with frame skipping when UI lags.
                while (elapsedMs >= _nextFrameDueMs && safety < maxAdvance)
                {
                    _animationFrameIndex = (_animationFrameIndex + 1) % _animationFrames.Count;
                    _nextFrameDueMs += _animationFrameDelays[_animationFrameIndex];
                    safety++;
                }

                if (_animationFrameIndex != previousIndex)
                {
                    nextSource = _animationFrames[_animationFrameIndex];
                }
            }

            if (nextSource != null)
            {
                ViewerImage.Source = nextSource;
            }
        }

        private void PrevButton_Click(object sender, RoutedEventArgs e)
        {
            ShowPrevious();
        }

        private void NextButton_Click(object sender, RoutedEventArgs e)
        {
            ShowNext();
        }

        private void ShowPrevious()
        {
            if (_imagePaths.Count == 0)
            {
                return;
            }

            _currentIndex = (_currentIndex - 1 + _imagePaths.Count) % _imagePaths.Count;
            LoadCurrentImage(fitToWindow: true);
        }

        private void ShowNext()
        {
            if (_imagePaths.Count == 0)
            {
                return;
            }

            _currentIndex = (_currentIndex + 1) % _imagePaths.Count;
            LoadCurrentImage(fitToWindow: true);
        }

        private void FitButton_Click(object sender, RoutedEventArgs e)
        {
            FitToWindow();
        }

        private void ActualSizeButton_Click(object sender, RoutedEventArgs e)
        {
            _zoom = 1.0;
            _isFitMode = false;
            ApplyZoom();
        }

        private void FitToWindow()
        {
            if (ViewerImage.Source == null)
            {
                return;
            }

            var viewportWidth = Math.Max(1, ImageScrollViewer.ViewportWidth);
            var viewportHeight = Math.Max(1, ImageScrollViewer.ViewportHeight);

            var imageWidth = Math.Max(1, ViewerImage.Source.Width);
            var imageHeight = Math.Max(1, ViewerImage.Source.Height);

            var scaleX = viewportWidth / imageWidth;
            var scaleY = viewportHeight / imageHeight;

            _zoom = Math.Max(MinZoom, Math.Min(MaxZoom, Math.Min(scaleX, scaleY)));
            _isFitMode = true;
            ApplyZoom();
        }

        private void SetZoom(double newZoom)
        {
            _zoom = Math.Max(MinZoom, Math.Min(MaxZoom, newZoom));
            _isFitMode = false;
            ApplyZoom();
        }

        private void ToggleFitAndActualSize()
        {
            if (_isFitMode)
            {
                _zoom = 1.0;
                _isFitMode = false;
                ApplyZoom();
            }
            else
            {
                FitToWindow();
            }
        }

        private void ApplyZoom()
        {
            ImageScaleTransform.ScaleX = _zoom;
            ImageScaleTransform.ScaleY = _zoom;
            ZoomText.Text = $"Zoom: {_zoom * 100:0}%";
        }

        private void ImageScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
            SetZoom(_zoom * factor);
            e.Handled = true;
        }

        private void ImageScrollViewer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (ViewerImage.Source == null)
            {
                return;
            }

            if (e.ClickCount == 2)
            {
                EndDrag();
                ToggleFitAndActualSize();
                e.Handled = true;
                return;
            }

            _isDragging = true;
            _dragStartPoint = e.GetPosition(ImageScrollViewer);
            _dragStartHorizontalOffset = ImageScrollViewer.HorizontalOffset;
            _dragStartVerticalOffset = ImageScrollViewer.VerticalOffset;
            ImageScrollViewer.CaptureMouse();
            Mouse.OverrideCursor = Cursors.SizeAll;
            e.Handled = true;
        }

        private void ImageScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging)
            {
                return;
            }

            var currentPoint = e.GetPosition(ImageScrollViewer);
            var deltaX = currentPoint.X - _dragStartPoint.X;
            var deltaY = currentPoint.Y - _dragStartPoint.Y;

            ImageScrollViewer.ScrollToHorizontalOffset(_dragStartHorizontalOffset - deltaX);
            ImageScrollViewer.ScrollToVerticalOffset(_dragStartVerticalOffset - deltaY);
        }

        private void ImageScrollViewer_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            EndDrag();
        }

        private void ImageScrollViewer_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_isDragging && e.LeftButton != MouseButtonState.Pressed)
            {
                EndDrag();
            }
        }

        private void EndDrag()
        {
            if (!_isDragging)
            {
                return;
            }

            _isDragging = false;

            if (ImageScrollViewer.IsMouseCaptured)
            {
                ImageScrollViewer.ReleaseMouseCapture();
            }

            if (Mouse.OverrideCursor == Cursors.SizeAll)
            {
                Mouse.OverrideCursor = null;
            }
        }

        private void ImageAreaHost_MouseMove(object sender, MouseEventArgs e)
        {
            var position = e.GetPosition(ImageAreaHost);
            var width = ImageAreaHost.ActualWidth;
            if (width <= 0)
            {
                return;
            }

            var showLeft = position.X <= EdgeTriggerWidth;
            var showRight = position.X >= (width - EdgeTriggerWidth);

            if (showLeft && showRight)
            {
                // Extremely narrow viewport: only show the nearer side.
                showLeft = position.X <= width / 2;
                showRight = !showLeft;
            }

            LeftNavButton.Visibility = showLeft ? Visibility.Visible : Visibility.Collapsed;
            RightNavButton.Visibility = showRight ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ImageAreaHost_MouseLeave(object sender, MouseEventArgs e)
        {
            LeftNavButton.Visibility = Visibility.Collapsed;
            RightNavButton.Visibility = Visibility.Collapsed;
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Left:
                    ShowPrevious();
                    e.Handled = true;
                    break;
                case Key.Right:
                    ShowNext();
                    e.Handled = true;
                    break;
                case Key.Add:
                case Key.OemPlus:
                    SetZoom(_zoom * 1.15);
                    e.Handled = true;
                    break;
                case Key.Subtract:
                case Key.OemMinus:
                    SetZoom(_zoom / 1.15);
                    e.Handled = true;
                    break;
                case Key.D0:
                case Key.NumPad0:
                    if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                    {
                        _zoom = 1.0;
                        _isFitMode = false;
                        ApplyZoom();
                        e.Handled = true;
                    }
                    break;
                case Key.Escape:
                    EndDrag();
                    Close();
                    e.Handled = true;
                    break;
            }
        }

        private void UpdateStatus(string filePath)
        {
            FileNameText.Text = Path.GetFileName(filePath);
            IndexText.Text = $"{_currentIndex + 1} / {_imagePaths.Count}";

            var info = new FileInfo(filePath);
            var animatedText = _isAnimated ? " | Animated" : string.Empty;
            var bitmap = ViewerImage.Source as BitmapSource;
            InfoText.Text = $"{bitmap?.PixelWidth ?? 0}x{bitmap?.PixelHeight ?? 0} | {FormatFileSize(info.Length)}{animatedText}";
        }

        private static string FormatFileSize(long bytes)
        {
            const double kb = 1024.0;
            const double mb = kb * 1024.0;

            if (bytes >= mb)
            {
                return $"{bytes / mb:0.##} MB";
            }

            if (bytes >= kb)
            {
                return $"{bytes / kb:0.##} KB";
            }

            return $"{bytes} B";
        }
    }
}
