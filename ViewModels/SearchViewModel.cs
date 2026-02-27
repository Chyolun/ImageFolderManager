using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using ImageFolderManager.Models;
using ImageFolderManager.Services;

namespace ImageFolderManager.ViewModels
{
    /// <summary>
    /// Handles all search operations and search result management
    /// </summary>
    public class SearchViewModel : ViewModelBase
    {
        private readonly UnifiedFolderService _folderService;
        private readonly List<FolderInfo> _allLoadedFolders;
        private CancellationTokenSource _searchCancellationTokenSource;

        #region Properties

        private string _searchText;
        public string SearchText
        {
            get => _searchText;
            set => SetProperty(ref _searchText, value);
        }

        private bool _isSearching;
        public bool IsSearching
        {
            get => _isSearching;
            private set => SetProperty(ref _isSearching, value);
        }

        private ObservableCollection<FolderInfo> _searchResultFolders = new ObservableCollection<FolderInfo>();
        public ObservableCollection<FolderInfo> SearchResultFolders
        {
            get => _searchResultFolders;
            private set => SetProperty(ref _searchResultFolders, value);
        }

        private FolderInfo _selectedSearchResult;
        public FolderInfo SelectedSearchResult
        {
            get => _selectedSearchResult;
            set => SetProperty(ref _selectedSearchResult, value);
        }

        #endregion

        #region Commands

        public IAsyncRelayCommand SearchCommand { get; }

        #endregion

        #region Events

        public event EventHandler<string> StatusMessageChanged;
        public event EventHandler<FolderInfo> SearchResultSelected;

        #endregion

        public SearchViewModel(UnifiedFolderService folderService, List<FolderInfo> allLoadedFolders)
        {
            _folderService = folderService ?? throw new ArgumentNullException(nameof(folderService));
            _allLoadedFolders = allLoadedFolders ?? throw new ArgumentNullException(nameof(allLoadedFolders));

            // Initialize commands
            SearchCommand = new AsyncRelayCommand(PerformSearchAsync);
        }

        #region Search Methods

        public async Task PerformSearchAsync()
        {
            // Cancel any existing search
            _searchCancellationTokenSource?.Cancel();
            _searchCancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _searchCancellationTokenSource.Token;

            try
            {
                SearchResultFolders.Clear();
                UpdateStatus("Searching...");
                IsSearching = true;

                if (string.IsNullOrWhiteSpace(SearchText))
                {
                    UpdateStatus("Ready");
                    return;
                }

                var results = await Task.Run(() => ExecuteSearch(cancellationToken), cancellationToken);

                if (!cancellationToken.IsCancellationRequested)
                {
                    foreach (var folder in results)
                    {
                        SearchResultFolders.Add(folder);
                    }

                    UpdateStatus($"Found {results.Count} matching folders");
                }
            }
            catch (OperationCanceledException)
            {
                UpdateStatus("Search cancelled");
            }
            catch (Exception ex)
            {
                UpdateStatus($"Search error: {ex.Message}");
            }
            finally
            {
                IsSearching = false;
            }
        }

        private List<FolderInfo> ExecuteSearch(CancellationToken cancellationToken)
        {
            var searchTerms = new SearchTerms(SearchText);
            var results = new List<FolderInfo>();

            // Use new candidate folder selection that properly handles parentheses syntax
            var candidateFolders = GetCandidateFolders(SearchText);

            foreach (var folderPath in candidateFolders)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                var folderInfo = _allLoadedFolders.FirstOrDefault(f =>
                    PathService.PathsEqual(f.FolderPath, folderPath));

                if (folderInfo != null && searchTerms.Matches(folderInfo))
                {
                    results.Add(folderInfo);
                }
            }

            return results.OrderByDescending(f => f.Rating)
              .ThenBy(f => f.Name)
              .ToList();
        }


        private List<string> GetCandidateFolders(string searchText)
        {
            // Extract general text terms from both OR groups and individual terms
            var generalTerms = new List<string>();

            // Handle (...) groups
            var orGroupPattern = @"\(([^)]+)\)";
            var regex = new Regex(orGroupPattern);
            var regexMatches = regex.Matches(searchText);

            foreach (Match match in regexMatches)
            {
                var groupContent = match.Groups[1].Value;
                var groupTerms = groupContent.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(term => !term.StartsWith("#") && !term.StartsWith("*") && !term.StartsWith("@"))
                    .Where(term => !string.IsNullOrWhiteSpace(term));
                generalTerms.AddRange(groupTerms);
            }

            // Remove (...) groups and process remaining individual terms
            var remainingText = regex.Replace(searchText, " ");
            var individualTerms = remainingText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(term => !term.StartsWith("#") && !term.StartsWith("*") && !term.StartsWith("@"))
                .Where(term => !string.IsNullOrWhiteSpace(term));
            generalTerms.AddRange(individualTerms);

            // If no general terms found, this is a pure special syntax search
            if (!generalTerms.Any())
            {
                return _folderService.IndexedFolders.ToList();
            }

            // Use general terms to pre-filter candidate folders
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var term in generalTerms)
            {
                var folderMatches = _folderService.SearchFolders(term);
                foreach (var match in folderMatches)
                {
                    candidates.Add(match);
                }
            }

            return candidates.ToList();
        }

        public async Task PerformSilentSearchAsync()
        {
            if (string.IsNullOrWhiteSpace(SearchText))
                return;

            try
            {
                var results = await Task.Run(() => ExecuteSearch(CancellationToken.None));

                // Update search results without clearing selection
                var currentSelection = SelectedSearchResult;

                // Sync results efficiently
                var toRemove = SearchResultFolders
                    .Where(existing => !results.Any(newItem =>
                        PathService.PathsEqual(existing.FolderPath, newItem.FolderPath)))
                    .ToList();

                foreach (var item in toRemove)
                {
                    SearchResultFolders.Remove(item);
                }

                var toAdd = results
                    .Where(newItem => !SearchResultFolders.Any(existing =>
                        PathService.PathsEqual(existing.FolderPath, newItem.FolderPath)))
                    .ToList();

                foreach (var item in toAdd)
                {
                    SearchResultFolders.Add(item);
                }

                // Restore selection if it still exists
                if (currentSelection != null)
                {
                    SelectedSearchResult = SearchResultFolders.FirstOrDefault(f =>
                        PathService.PathsEqual(f.FolderPath, currentSelection.FolderPath));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in silent search: {ex.Message}");
            }
        }

        public void ClearResults()
        {
            SearchResultFolders.Clear();
            SelectedSearchResult = null;
            SearchText = string.Empty;
            UpdateStatus("Search cleared");
        }

        #endregion


        #region Helper Methods

        private void UpdateStatus(string message)
        {
            StatusMessageChanged?.Invoke(this, message);
        }

        #endregion

        #region IDisposable Implementation

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Cancel any ongoing search operations
                _searchCancellationTokenSource?.Cancel();
                _searchCancellationTokenSource?.Dispose();
                _searchCancellationTokenSource = null;

                // Clear collections
                SearchResultFolders?.Clear();
            }

            base.Dispose(disposing);
        }

        #endregion
    }

    /// <summary>
    /// Encapsulates search term parsing and matching logic
    /// </summary>
    public class SearchTerms
    {
        private readonly List<List<Predicate<FolderInfo>>> _andGroups = new List<List<Predicate<FolderInfo>>>();

        public SearchTerms(string searchText)
        {
            if (string.IsNullOrWhiteSpace(searchText))
                return;

            // Parse the search text to extract OR groups and individual terms
            var groups = ParseSearchGroups(searchText);

            foreach (var group in groups)
            {
                var orPredicates = ParseGroup(group);
                if (orPredicates.Count > 0)
                {
                    _andGroups.Add(orPredicates);
                }
            }
        }

        /// <summary>
        /// Parses search text into groups, handling () OR syntax
        /// Example: "(@man @people) #photon *>3" -> ["@man @people", "#photon", "*>3"]
        /// </summary>
        private List<string> ParseSearchGroups(string searchText)
        {
            var groups = new List<string>();
            var remainingText = searchText;

            // Extract all (...) groups first
            var orGroupPattern = @"\(([^)]+)\)";
            var regex = new Regex(orGroupPattern);
            var regexMatches = regex.Matches(searchText);

            foreach (Match match in regexMatches)
            {
                // Add the content inside () as an OR group
                groups.Add(match.Groups[1].Value.Trim());
                // Remove this match from remaining text
                remainingText = remainingText.Replace(match.Value, " ");
            }

            // Split remaining text by spaces to get individual AND terms
            var individualTerms = remainingText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(term => !string.IsNullOrWhiteSpace(term))
                .ToList();

            // Each individual term becomes its own group (single item OR group)
            groups.AddRange(individualTerms);

            return groups;
        }

        /// <summary>
        /// Parses a single group (either OR group content or individual term)
        /// Returns list of predicates that will be combined with OR logic
        /// </summary>
        private List<Predicate<FolderInfo>> ParseGroup(string groupText)
        {
            var orPredicates = new List<Predicate<FolderInfo>>();

            // Split by spaces to get individual terms within the group
            var terms = groupText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var term in terms)
            {
                var predicate = CreatePredicateForTerm(term.Trim());
                if (predicate != null)
                {
                    orPredicates.Add(predicate);
                }
            }

            return orPredicates;
        }

        /// <summary>
        /// Creates a predicate for a single search term
        /// </summary>
        private Predicate<FolderInfo> CreatePredicateForTerm(string term)
        {
            if (string.IsNullOrWhiteSpace(term))
                return null;

            if (term.StartsWith("#")) // Tag search
            {
                string tagTerm = term.Substring(1).ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(tagTerm))
                {
                    return folder =>
                        folder.Tags != null &&
                        folder.Tags.Any(tag => tag.ToLowerInvariant().Contains(tagTerm));
                }
            }
            else if (term.StartsWith("*")) // Rating search
            {
                string ratingPattern = term.Substring(1).Trim();
                return CreateRatingPredicate(ratingPattern);
            }
            else if (term.StartsWith("@")) // Folder name search
            {
                string folderNameTerm = term.Substring(1).ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(folderNameTerm))
                {
                    return folder =>
                        folder.Name != null &&
                        folder.Name.ToLowerInvariant().Contains(folderNameTerm);
                }
            }
            else // General text search (name or path)
            {
                string textTerm = term.ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(textTerm))
                {
                    return folder =>
                        (folder.Name?.ToLowerInvariant().Contains(textTerm) ?? false) ||
                        (folder.FolderPath?.ToLowerInvariant().Contains(textTerm) ?? false);
                }
            }

            return null;
        }

        /// <summary>
        /// Creates rating comparison predicate
        /// </summary>
        private Predicate<FolderInfo> CreateRatingPredicate(string ratingPattern)
        {
            if (string.IsNullOrWhiteSpace(ratingPattern))
                return null;

            string comparisonOperator;
            string valueStr;

            if (ratingPattern.StartsWith(">=") || ratingPattern.StartsWith("<="))
            {
                comparisonOperator = ratingPattern.Substring(0, 2);
                valueStr = ratingPattern.Substring(2).Trim();
            }
            else if (ratingPattern.StartsWith("=") || ratingPattern.StartsWith(">") || ratingPattern.StartsWith("<"))
            {
                comparisonOperator = ratingPattern.Substring(0, 1);
                valueStr = ratingPattern.Substring(1).Trim();
            }
            else
            {
                return null;
            }

            if (!int.TryParse(valueStr, out int value) || value < 0 || value > 5)
                return null;

            return comparisonOperator switch
            {
                ">=" => folder => folder.Rating >= value,
                "<=" => folder => folder.Rating <= value,
                "=" => folder => folder.Rating == value,
                ">" => folder => folder.Rating > value,
                "<" => folder => folder.Rating < value,
                _ => null
            };
        }


        /// <summary>
        /// Tests if a folder matches all AND groups (each group must have at least one OR match)
        /// </summary>
        public bool Matches(FolderInfo folder)
        {
            if (_andGroups.Count == 0)
                return true;

            // All AND groups must match (each group needs at least one OR predicate to match)
            foreach (var orPredicates in _andGroups)
            {
                bool anyMatch = orPredicates.Any(predicate => predicate(folder));
                if (!anyMatch)
                    return false; // This AND group failed, so overall match fails
            }

            return true; // All AND groups passed
        }
    }
}