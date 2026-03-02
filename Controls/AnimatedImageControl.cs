using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ImageFolderManager.Controls
{
    /// <summary>
    /// Custom Image control with multi-frame animation playback (for GIF/WebP preview)
    /// </summary>
    public class AnimatedImageControl : Control
    {
        private Image _imageElement;
        private DispatcherTimer _timer;
        private int _currentFrameIndex;

        static AnimatedImageControl()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(AnimatedImageControl),
                new FrameworkPropertyMetadata(typeof(AnimatedImageControl)));
        }

        public AnimatedImageControl()
        {
            Unloaded += (s, e) => StopAnimation();
        }


        #region Dependency Properties

        public static readonly DependencyProperty FramesProperty =
            DependencyProperty.Register(
                nameof(Frames),
                typeof(List<AnimatedFrame>),
                typeof(AnimatedImageControl),
                new PropertyMetadata(null, OnFramesChanged));

        public List<AnimatedFrame> Frames
        {
            get => (List<AnimatedFrame>)GetValue(FramesProperty);
            set => SetValue(FramesProperty, value);
        }

        public static readonly DependencyProperty StretchProperty =
            DependencyProperty.Register(
                nameof(Stretch),
                typeof(System.Windows.Media.Stretch),
                typeof(AnimatedImageControl),
                new PropertyMetadata(System.Windows.Media.Stretch.Uniform));

        public System.Windows.Media.Stretch Stretch
        {
            get => (System.Windows.Media.Stretch)GetValue(StretchProperty);
            set => SetValue(StretchProperty, value);
        }

        #endregion

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            _imageElement = GetTemplateChild("PART_Image") as Image;
            StartAnimation();
        }

        private static void OnFramesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AnimatedImageControl ctrl)
            {
                ctrl.StopAnimation();
                ctrl._currentFrameIndex = 0;
                if (ctrl._imageElement != null)
                    ctrl.StartAnimation();
            }
        }

        private void StartAnimation()
        {
            if (_imageElement == null || Frames == null || Frames.Count == 0)
                return;

            // show the first frame
            ShowFrame(0);

            if (Frames.Count == 1)
                return; // if there is only one frame, no timer is needed

            _timer = new DispatcherTimer(DispatcherPriority.Render);
            _timer.Tick += OnTimerTick;
            SetTimerInterval();
            _timer.Start();
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            _currentFrameIndex = (_currentFrameIndex + 1) % Frames.Count;
            ShowFrame(_currentFrameIndex);
            SetTimerInterval();
        }

        private void ShowFrame(int index)
        {
            if (_imageElement != null && Frames != null && index < Frames.Count)
                _imageElement.Source = Frames[index].Image;
        }

        private void SetTimerInterval()
        {
            if (_timer == null || Frames == null || _currentFrameIndex >= Frames.Count)
                return;

            int delayMs = Frames[_currentFrameIndex].DelayMs;
            // GIF has a standard minimum frame delay of 10ms, and browsers typically use 100ms when it's too small
            if (delayMs < 20) delayMs = 100;
            _timer.Interval = TimeSpan.FromMilliseconds(delayMs);
        }

        private void StopAnimation()
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Tick -= OnTimerTick;
                _timer = null;
            }
        }
    }

    /// <summary>
    /// Animation frame data
    /// </summary>
    public class AnimatedFrame
    {
        public BitmapSource Image { get; set; }
        public int DelayMs { get; set; } //Duration of the frame (ms)
    }
}