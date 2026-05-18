using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using ImageFolderManager.Services;
using MahApps.Metro.Controls;

namespace ImageFolderManager.Views
{
    public partial class AutoAssortmentDialog : MetroWindow
    {
        public ObservableCollection<AutoAssortmentReviewItem> ReviewItems { get; }
            = new ObservableCollection<AutoAssortmentReviewItem>();

        public IReadOnlyList<AutoAssortmentExecutionItem> SelectedMoves { get; private set; }
            = Array.Empty<AutoAssortmentExecutionItem>();

        public AutoAssortmentDialog(AutoAssortmentPlan plan)
        {
            InitializeComponent();
            DataContext = this;

            if (plan == null)
                throw new ArgumentNullException(nameof(plan));

            SummaryTextBlock.Text =
                $"Source: {plan.SourceDirectory}{Environment.NewLine}" +
                $"Folders to review: {plan.Items.Count}{Environment.NewLine}" +
                $"Author folders found under root: {plan.AuthorTargets.Count}";

            var allTargetOptions = BuildTargetOptions(plan.AuthorTargets);
            foreach (var item in plan.Items)
            {
                var reviewItem = new AutoAssortmentReviewItem(item, allTargetOptions);
                reviewItem.PropertyChanged += ReviewItem_PropertyChanged;
                ReviewItems.Add(reviewItem);
            }

            RefreshFooterStatus();
        }

        private void ReviewItem_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AutoAssortmentReviewItem.ShouldMove) ||
                e.PropertyName == nameof(AutoAssortmentReviewItem.SelectedTarget))
            {
                RefreshFooterStatus();
            }
        }

        private static List<AutoAssortmentTargetOption> BuildTargetOptions(IReadOnlyList<AutoAssortmentAuthorTarget> authorTargets)
        {
            var options = new List<AutoAssortmentTargetOption>
            {
                AutoAssortmentTargetOption.Skip
            };

            options.AddRange((authorTargets ?? Array.Empty<AutoAssortmentAuthorTarget>())
                .Select(target => new AutoAssortmentTargetOption(
                    target.DirectoryPath,
                    $"{target.AuthorName}  ({target.RelativePath})")));

            return options;
        }

        private void SelectAllMatched_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in ReviewItems)
            {
                item.ShouldMove = item.HasRecommendedTarget;
            }

            RefreshFooterStatus();
        }

        private void SkipUnmatched_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in ReviewItems.Where(item => !item.HasRecommendedTarget))
            {
                item.ShouldMove = false;
                item.SelectedTarget = AutoAssortmentTargetOption.Skip;
            }

            RefreshFooterStatus();
        }

        private void MoveAllVisible_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in ReviewItems.Where(item => item.SelectedTarget != null && !item.SelectedTarget.IsSkip))
            {
                item.ShouldMove = true;
            }

            RefreshFooterStatus();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            var selectedMoves = ReviewItems
                .Where(item => item.ShouldMove &&
                               item.SelectedTarget != null &&
                               !item.SelectedTarget.IsSkip)
                .Select(item => new AutoAssortmentExecutionItem(
                    item.SourcePath,
                    item.SelectedTarget.TargetDirectoryPath))
                .ToList();

            if (selectedMoves.Count == 0)
            {
                MessageBox.Show(
                    this,
                    "No folders are selected to move.",
                    "Auto Assortment",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            SelectedMoves = selectedMoves;
            DialogResult = true;
            Close();
        }

        private void RefreshFooterStatus()
        {
            int moveCount = ReviewItems.Count(item =>
                item.ShouldMove &&
                item.SelectedTarget != null &&
                !item.SelectedTarget.IsSkip);
            int unmatchedCount = ReviewItems.Count(item => !item.HasRecommendedTarget);
            FooterStatusTextBlock.Text =
                $"Ready to move {moveCount} folder(s). Unmatched: {unmatchedCount}.";
        }

        public sealed class AutoAssortmentReviewItem : INotifyPropertyChanged
        {
            private bool _shouldMove;
            private AutoAssortmentTargetOption _selectedTarget;
            private readonly bool _hasRecommendedTarget;

            public AutoAssortmentReviewItem(AutoAssortmentPlanItem planItem, IReadOnlyList<AutoAssortmentTargetOption> allTargetOptions)
            {
                if (planItem == null)
                    throw new ArgumentNullException(nameof(planItem));

                SourcePath = planItem.SourcePath;
                SourceFolderName = planItem.SourceFolderName;
                RelativeSourcePath = planItem.RelativeSourcePath;
                MatchSummary = $"{planItem.MatchSummary} • {planItem.ScannedFileCount} file(s) scanned";

                var orderedOptions = BuildOrderedTargetOptions(planItem, allTargetOptions);
                TargetOptions = new ObservableCollection<AutoAssortmentTargetOption>(orderedOptions);

                string recommendedTargetPath = planItem.CandidateTargets.FirstOrDefault()?.Target?.DirectoryPath;
                _hasRecommendedTarget = !string.IsNullOrWhiteSpace(recommendedTargetPath);
                SelectedTarget = TargetOptions.FirstOrDefault(option =>
                    string.Equals(option.TargetDirectoryPath, recommendedTargetPath, StringComparison.OrdinalIgnoreCase))
                    ?? AutoAssortmentTargetOption.Skip;

                ShouldMove = SelectedTarget != null && !SelectedTarget.IsSkip;
            }

            private static IReadOnlyList<AutoAssortmentTargetOption> BuildOrderedTargetOptions(
                AutoAssortmentPlanItem planItem,
                IReadOnlyList<AutoAssortmentTargetOption> allTargetOptions)
            {
                var ordered = new List<AutoAssortmentTargetOption> { AutoAssortmentTargetOption.Skip };
                var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var candidate in planItem.CandidateTargets)
                {
                    if (candidate?.Target == null || string.IsNullOrWhiteSpace(candidate.Target.DirectoryPath))
                        continue;

                    if (!usedPaths.Add(candidate.Target.DirectoryPath))
                        continue;

                    ordered.Add(new AutoAssortmentTargetOption(
                        candidate.Target.DirectoryPath,
                        $"{candidate.Target.AuthorName}  ({candidate.Target.RelativePath})"));
                }

                foreach (var option in allTargetOptions ?? Array.Empty<AutoAssortmentTargetOption>())
                {
                    if (option == null || option.IsSkip)
                        continue;

                    if (!usedPaths.Add(option.TargetDirectoryPath))
                        continue;

                    ordered.Add(option);
                }

                return ordered;
            }

            public string SourcePath { get; }
            public string SourceFolderName { get; }
            public string RelativeSourcePath { get; }
            public string MatchSummary { get; }
            public ObservableCollection<AutoAssortmentTargetOption> TargetOptions { get; }

            public bool HasRecommendedTarget => _hasRecommendedTarget;

            public bool ShouldMove
            {
                get => _shouldMove;
                set => SetProperty(ref _shouldMove, value);
            }

            public AutoAssortmentTargetOption SelectedTarget
            {
                get => _selectedTarget;
                set
                {
                    if (SetProperty(ref _selectedTarget, value))
                    {
                        bool shouldMove = value != null && !value.IsSkip;
                        if (_shouldMove != shouldMove)
                        {
                            _shouldMove = shouldMove;
                            OnPropertyChanged(nameof(ShouldMove));
                        }
                        OnPropertyChanged(nameof(DestinationPreview));
                    }
                }
            }

            public string DestinationPreview
            {
                get
                {
                    if (SelectedTarget == null || SelectedTarget.IsSkip)
                        return "(Skip)";

                    return Path.Combine(SelectedTarget.TargetDirectoryPath, SourceFolderName);
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;

            private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string propertyName = null)
            {
                if (EqualityComparer<T>.Default.Equals(storage, value))
                    return false;

                storage = value;
                OnPropertyChanged(propertyName);
                return true;
            }

            private void OnPropertyChanged([CallerMemberName] string propertyName = null)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        public sealed class AutoAssortmentTargetOption
        {
            private AutoAssortmentTargetOption(string targetDirectoryPath, string displayText, bool isSkip)
            {
                TargetDirectoryPath = targetDirectoryPath;
                DisplayText = displayText;
                IsSkip = isSkip;
            }

            public static AutoAssortmentTargetOption Skip { get; } =
                new AutoAssortmentTargetOption(string.Empty, "(Skip)", isSkip: true);

            public AutoAssortmentTargetOption(string targetDirectoryPath, string displayText)
                : this(targetDirectoryPath, displayText, isSkip: false)
            {
            }

            public string TargetDirectoryPath { get; }
            public string DisplayText { get; }
            public bool IsSkip { get; }
        }
    }
}
