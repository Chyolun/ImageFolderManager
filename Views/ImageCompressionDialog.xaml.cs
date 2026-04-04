// ImageCompressionDialog.xaml.cs
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using ImageFolderManager.Services;
using MahApps.Metro.Controls;

namespace ImageFolderManager.Views
{
    public partial class ImageCompressionDialog : MetroWindow
    {
        public int Quality { get; private set; } = 80;
        public bool DeleteSourceFiles { get; private set; } = false;

        private readonly string _folderPath;
        private long _originalSize;
        private CancellationTokenSource _estimateCts;
        private const int EstimateDebounceMs = 400;

        public ImageCompressionDialog(string folderPath, string folderName)
        {
            InitializeComponent();
            _folderPath = folderPath ?? string.Empty;
            FolderNameText.Text = string.IsNullOrWhiteSpace(folderName) ? "Selected Folder" : folderName;
            _originalSize = ImageCompressionService.GetFolderImageSize(_folderPath);
            BeforeSizeText.Text = CompressionResult.FormatBytes(_originalSize);
            
            this.Loaded += OnDialogLoaded;
        }

        /// <summary>
        /// Fires after InitializeComponent + layout pass — all controls are ready.
        /// </summary>
        private void OnDialogLoaded(object sender, RoutedEventArgs e)
        {
            this.Loaded -= OnDialogLoaded; // only once
            ScheduleEstimate();
        }

        private void QualitySlider_ValueChanged(
            object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (QualityValueText != null)
                QualityValueText.Text = ((int)e.NewValue).ToString();

            // Guard: controls may not be ready during InitializeComponent
            if (EstimateProgress != null)
                ScheduleEstimate();
        }

        private void DeleteSourceCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (DeleteWarningText != null)
                DeleteWarningText.Visibility =
                    DeleteSourceCheckBox.IsChecked == true
                        ? Visibility.Visible
                        : Visibility.Collapsed;
        }


        private void CompressButton_Click(object sender, RoutedEventArgs e)
        {
            Quality = (int)QualitySlider.Value;
            DeleteSourceFiles = DeleteSourceCheckBox.IsChecked == true;
            _estimateCts?.Cancel();
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _estimateCts?.Cancel();
            DialogResult = false;
        }

        /// <summary>
        /// Debounced size estimation: cancels any running estimate, waits for the
        /// slider to settle, then runs a trial in-memory compression and updates the UI.
        /// </summary>
        private void ScheduleEstimate()
        {
            _estimateCts?.Cancel();
            _estimateCts = new CancellationTokenSource();

            var token = _estimateCts.Token;
            int quality = (int)QualitySlider.Value;

            // These controls are guaranteed non-null here (called from Loaded or later)
            EstimateProgress.Visibility = Visibility.Visible;
            SavedText.Visibility = Visibility.Collapsed;
            AfterSizeText.Text = "—";
            EstimateNote.Text = "Calculating estimate...";

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(EstimateDebounceMs, token);

                    long estimatedSize = await ImageCompressionService
                        .EstimateCompressedSizeAsync(_folderPath, quality, token);

                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (token.IsCancellationRequested) return;

                        EstimateProgress.Visibility = Visibility.Collapsed;
                        AfterSizeText.Text = CompressionResult.FormatBytes(estimatedSize);

                        long saved = _originalSize - estimatedSize;
                        double savedPct = _originalSize > 0
                            ? (double)saved / _originalSize * 100.0 : 0.0;

                        if (saved >= 0)
                        {
                            SavedText.Text = $"Save approx. {CompressionResult.FormatBytes(saved)}" +
                                             $"  ({savedPct:F1}% smaller)";
                            SavedText.Foreground = TryFindResource("StatusSuccessBrush") as Brush
                                ?? new SolidColorBrush(Color.FromRgb(0x81, 0xC7, 0x84));
                        }
                        else
                        {
                            SavedText.Text = $"Approx. {CompressionResult.FormatBytes(-saved)} larger";
                            SavedText.Foreground = TryFindResource("StatusWarningBrush") as Brush
                                ?? new SolidColorBrush(Color.FromRgb(0xFF, 0xB7, 0x4D));
                        }

                        SavedText.Visibility = Visibility.Visible;
                        EstimateNote.Text = "* Estimate based on trial compression of first image";
                    });
                }
                catch (OperationCanceledException)
                {
                    // Normal — slider moved again, discard this result
                }
                catch (Exception ex)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (token.IsCancellationRequested) return;
                        EstimateProgress.Visibility = Visibility.Collapsed;
                        AfterSizeText.Text = "—";
                        EstimateNote.Text = $"Estimate unavailable: {ex.Message}";
                    });
                }
            }, token);
        }
    }
}
