using System;
using System.Windows;
using MahApps.Metro.Controls;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace ImageFolderManager.Views
{
    public partial class ProgressDialog : MetroWindow
    {
        private CancellationTokenSource _cancellationTokenSource;
        private TaskCompletionSource<bool> _dialogCompletionSource;

        public event EventHandler CancelRequested;
        public bool IsCancelled { get; private set; }

        public ProgressDialog(string title, string operationText)
        {
            InitializeComponent();

            Title = title;
            OperationText.Text = operationText;
            IsCancelled = false;
            _cancellationTokenSource = new CancellationTokenSource();
            _dialogCompletionSource = new TaskCompletionSource<bool>();

            // Configure window as a modal dialog
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;

            // Complete the dialog task when the window is closed
            Closed += (s, e) =>
            {
                _dialogCompletionSource.TrySetResult(true);
            };
        }

        public void UpdateProgress(double progress, string statusText)
        {
            // Ensure we're on the UI thread
            Dispatcher.Invoke(() =>
            {
                ProgressBar.Value = progress;
                StatusText.Text = statusText;

                // If progress reaches 100%, close the dialog automatically
                if (progress >= 1.0)
                {
                    // Small delay to show completion before closing
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                    timer.Tick += (s, e) =>
                    {
                        timer.Stop();
                        Close();
                    };
                    timer.Start();
                }
            });
        }

        public CancellationToken GetCancellationToken()
        {
            return _cancellationTokenSource.Token;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            IsCancelled = true;
            _cancellationTokenSource.Cancel();
            CancelRequested?.Invoke(this, EventArgs.Empty);

            // Update UI to show cancellation status
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = "Cancelling operation...";
                CancelButton.IsEnabled = false;

                // Close the dialog after a brief delay to show cancellation message
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                timer.Tick += (s, args) =>
                {
                    timer.Stop();
                    Close();
                };
                timer.Start();
            });
        }

      
    }
}