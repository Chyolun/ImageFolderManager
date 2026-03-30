using ImageMagick;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Controls;

namespace ImageFolderManager.Views
{
    public partial class ImageViewerWindow
    {
        private readonly List<string> _imagePaths;
        private int _currentIndex;
        private readonly DispatcherTimer _animationTimer;
        private List<BitmapSource> _animationFrames;
        private List<int> _animationFrameDelays;
        private int _animationFrameIndex;
        private bool _isAnimated;
        private double _zoom = 1.0;
        private const double MinZoom = 0.05;
        private const double MaxZoom = 20.0;
        private bool _isDragging;
        private Point _dragStartPoint;
        private double _dragStartHorizontalOffset;
        private double _dragStartVerticalOffset;
        private bool _isFitMode = true;
        private const double EdgeTriggerWidth = 120.0;

        public ImageViewerWindow(IEnumerable<string> imagePaths, int initialIndex)
        {
            InitializeComponent();

            _imagePaths = imagePaths?.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                ?? new List<string>();

            if (_imagePaths.Count == 0)
            {
                throw new ArgumentException("No image paths provided.", nameof(imagePaths));
            }

            _currentIndex = Math.Max(0, Math.Min(initialIndex, _imagePaths.Count - 1));

            _animationTimer = new DispatcherTimer(DispatcherPriority.Render);
            _animationTimer.Tick += AnimationTimer_Tick;

            Loaded += (_, __) => LoadCurrentImage(fitToWindow: true);
            Closed += (_, __) =>
            {
                EndDrag();
                StopAnimation();
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
            ViewerImage.Source = null;
            _animationFrames = null;
            _animationFrameDelays = null;
            _animationFrameIndex = 0;
            _isAnimated = false;

            var extension = Path.GetExtension(filePath)?.ToLowerInvariant() ?? string.Empty;
            if (extension == ".gif" || extension == ".webp")
            {
                TryLoadAnimated(filePath);
            }

            if (!_isAnimated)
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

        private void TryLoadAnimated(string filePath)
        {
            try
            {
                using (var collection = new MagickImageCollection(filePath))
                {
                    if (collection.Count <= 1)
                    {
                        return;
                    }

                    collection.Coalesce();

                    _animationFrames = new List<BitmapSource>(collection.Count);
                    _animationFrameDelays = new List<int>(collection.Count);

                    foreach (var frame in collection)
                    {
                        var delayCs = Math.Max(1, (int)frame.AnimationDelay);
                        _animationFrameDelays.Add(delayCs * 10);
                        _animationFrames.Add(CreateBitmapSource(frame));
                    }
                }

                if (_animationFrames.Count == 0)
                {
                    return;
                }

                _isAnimated = true;
                _animationFrameIndex = 0;
                ViewerImage.Source = _animationFrames[_animationFrameIndex];
                _animationTimer.Interval = TimeSpan.FromMilliseconds(_animationFrameDelays[_animationFrameIndex]);
                _animationTimer.Start();
            }
            catch
            {
                _isAnimated = false;
                _animationFrames = null;
                _animationFrameDelays = null;
            }
        }

        private static BitmapSource CreateBitmapSource(IMagickImage<byte> frame)
        {
            using (var cloned = frame.Clone())
            {
                using (var stream = new MemoryStream())
                {
                    cloned.Format = MagickFormat.Png;
                    cloned.Write(stream);
                    stream.Position = 0;

                    var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    var bitmap = decoder.Frames[0];
                    bitmap.Freeze();
                    return bitmap;
                }
            }
        }

        private void StopAnimation()
        {
            _animationTimer.Stop();
            _animationFrameIndex = 0;
        }

        private void AnimationTimer_Tick(object sender, EventArgs e)
        {
            if (!_isAnimated || _animationFrames == null || _animationFrames.Count == 0)
            {
                return;
            }

            _animationFrameIndex = (_animationFrameIndex + 1) % _animationFrames.Count;
            ViewerImage.Source = _animationFrames[_animationFrameIndex];
            _animationTimer.Interval = TimeSpan.FromMilliseconds(_animationFrameDelays[_animationFrameIndex]);
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
