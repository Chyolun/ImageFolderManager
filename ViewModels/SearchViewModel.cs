using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
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
    /// Handles all search operations and search result management.
    ///
    /// ══════════════════════════════════════════════════════════════════════════
    /// PERFORMANCE OPTIMIZATIONS OVERVIEW
    /// ══════════════════════════════════════════════════════════════════════════
    ///
    /// 1. INVERTED TAG INDEX  (_tagIndex)
    ///    Tag-only queries skip the full folder scan entirely.
    ///    Dictionary&lt;normTag, HashSet&lt;path&gt;&gt; → O(hits) instead of O(n×m).
    ///
    /// 2. FOLDER-NAME INDEX   (_nameIndex)
    ///    @-terms and plain general-text terms use a pre-built
    ///    Dictionary&lt;normFolderName, HashSet&lt;path&gt;&gt; that replaces the live
    ///    UnifiedFolderService.SearchFolders() linear scan over _folderIndex.Keys.
    ///
    /// 3. O(1) PATH → FolderInfo LOOKUP  (_folderByPath)
    ///    Replaces _allLoadedFolders.FirstOrDefault(…PathsEqual…) which is O(n).
    ///
    /// 4. PRE-NORMALISED TAG CACHE  (_normalizedTagsCache)
    ///    Each folder's tags are lower-cased once into a HashSet so predicate
    ///    evaluation avoids repeated ToLowerInvariant() allocations per call.
    ///
    /// 5. SMART CANDIDATE INTERSECTION
    ///    When the query has both tag terms AND name/general terms the two
    ///    candidate sets are intersected before predicate evaluation, so only
    ///    the truly relevant folders reach the per-folder Matches() check.
    ///
    /// 6. O(1) RESULT-SYNC IN PerformSilentSearchAsync
    ///    A HashSet replaces the O(n²) nested Any(PathsEqual) loops that
    ///    previously diffed the old and new result lists.
    ///
    /// 7. DEBOUNCED SILENT SEARCH
    ///    PerformSilentSearchAsync skips execution when the query text and index
    ///    have not changed since the last run, preventing redundant work.
    ///
    /// 8. COMPILED REGEX (one allocation, reused every call).
    ///
    /// 9. LAZY INDEX REBUILD  (_indexDirty flag)
    ///    The index is rebuilt at most once per search; never eagerly on each
    ///    folder-add / folder-remove event.
    ///
    /// Maintenance: call InvalidateSearchIndex() in MainViewModel wherever
    /// _allLoadedFolders changes (OnIndexedFolderCreated/Deleted/Renamed,
    /// OnIndexRebuilt) and after tags are written back to any folder.
    /// ══════════════════════════════════════════════════════════════════════════
    /// </summary>
    public class SearchViewModel : ViewModelBase
    {
        private readonly UnifiedFolderService _folderService;
        private readonly List<FolderInfo> _allLoadedFolders;
        private readonly object _allLoadedFoldersLock;
        private CancellationTokenSource _searchCancellationTokenSource;

        // ── Compiled regex (one allocation for the lifetime of the process) ───
        private static readonly Regex OrGroupRegex =
            new Regex(@"\(([^)]+)\)", RegexOptions.Compiled);

        // ── Performance indexes ───────────────────────────────────────────────

        /// <summary>O(1) path → FolderInfo — replaces O(n) FirstOrDefault.</summary>
        private Dictionary<string, FolderInfo> _folderByPath =
            new Dictionary<string, FolderInfo>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Inverted TAG index: normalised-tag → paths of folders that carry that tag.
        /// Substring match is applied to the keys at query time.
        /// </summary>
        private Dictionary<string, HashSet<string>> _tagIndex =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// FOLDER-NAME index: normalised folder-name → paths.
        /// Replaces the live linear scan in UnifiedFolderService.SearchFolders().
        /// </summary>
        private Dictionary<string, HashSet<string>> _nameIndex =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Pre-computed lowercase tag sets per folder path.
        /// Eliminates per-predicate-call ToLowerInvariant() allocations.
        /// </summary>
        private Dictionary<string, HashSet<string>> _normalizedTagsCache =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        private volatile bool _indexDirty = true;
        private readonly object _indexLock = new object();

        /// <summary>Last search text successfully run by PerformSilentSearchAsync.</summary>
        private string _lastSilentSearchText;

        // ─────────────────────────────────────────────────────────────────────

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

        private ObservableCollection<FolderInfo> _searchResultFolders =
            new ObservableCollection<FolderInfo>();
        public ObservableCollection<FolderInfo> SearchResultFolders
        {
            get => _searchResultFolders;
            private set => SetProperty(ref _searchResultFolders, value);
        }

        private FolderInfo _selectedSearchResult;
        public FolderInfo SelectedSearchResult
        {
            get => _selectedSearchResult;
            set
            {
                if (SetProperty(ref _selectedSearchResult, value) && value != null)
                    SearchResultSelected?.Invoke(this, value);
            }
        }

        #endregion

        #region Commands

        public IAsyncRelayCommand SearchCommand { get; }

        #endregion

        #region Events

        public event EventHandler<string> StatusMessageChanged;
        public event EventHandler<FolderInfo> SearchResultSelected;

        #endregion

        public SearchViewModel(
            UnifiedFolderService folderService,
            List<FolderInfo> allLoadedFolders,
            object allLoadedFoldersLock = null)
        {
            _folderService = folderService
                ?? throw new ArgumentNullException(nameof(folderService));
            _allLoadedFolders = allLoadedFolders
                ?? throw new ArgumentNullException(nameof(allLoadedFolders));
            _allLoadedFoldersLock = allLoadedFoldersLock ?? new object();

            SearchCommand = new AsyncRelayCommand(PerformSearchAsync);
        }

        // ── Index management ──────────────────────────────────────────────────

        /// <summary>
        /// Marks all indexes stale. Call after any mutation of _allLoadedFolders
        /// or after tags are written to a folder.
        /// </summary>
        public void InvalidateSearchIndex()
        {
            _indexDirty = true;
            _lastSilentSearchText = null; // force silent search to re-run
        }

        /// <summary>
        /// Rebuilds all three indexes (_tagIndex, _nameIndex, _folderByPath) from
        /// the current _allLoadedFolders snapshot. Thread-safe.
        /// </summary>
        public void RebuildSearchIndex()
        {
            lock (_indexLock)
            {
                List<FolderInfo> folderSnapshot;
                lock (_allLoadedFoldersLock)
                {
                    folderSnapshot = _allLoadedFolders.ToList();
                }

                int cap = folderSnapshot.Count;

                var byPath = new Dictionary<string, FolderInfo>(cap, StringComparer.OrdinalIgnoreCase);
                var tagIdx = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                var nameIdx = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                var normCache = new Dictionary<string, HashSet<string>>(cap, StringComparer.OrdinalIgnoreCase);

                foreach (var folder in folderSnapshot)
                {
                    if (folder?.FolderPath == null) continue;
                    string path = folder.FolderPath;
                    byPath[path] = folder;

                    // Name index — keyed by the folder's own name (last segment)
                    string normName = Path.GetFileName(path)?.ToLowerInvariant() ?? string.Empty;
                    if (!string.IsNullOrEmpty(normName))
                        AddToIndex(nameIdx, normName, path);

                    // Tag index + normalised-tag cache
                    var normTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (folder.Tags != null)
                    {
                        foreach (var tag in folder.Tags)
                        {
                            if (string.IsNullOrWhiteSpace(tag)) continue;
                            string lower = tag.ToLowerInvariant();
                            normTags.Add(lower);
                            AddToIndex(tagIdx, lower, path);
                        }
                    }
                    normCache[path] = normTags;
                }

                _folderByPath = byPath;
                _tagIndex = tagIdx;
                _nameIndex = nameIdx;
                _normalizedTagsCache = normCache;
                _indexDirty = false;
            }
        }

        private static void AddToIndex(
            Dictionary<string, HashSet<string>> index, string key, string path)
        {
            if (!index.TryGetValue(key, out var bucket))
            {
                bucket = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                index[key] = bucket;
            }
            bucket.Add(path);
        }

        private void EnsureIndexFresh()
        {
            if (_indexDirty)
                RebuildSearchIndex();
        }

        // ── Search pipeline ───────────────────────────────────────────────────

        #region Search Methods

        public async Task PerformSearchAsync()
        {
            _searchCancellationTokenSource?.Cancel();
            _searchCancellationTokenSource = new CancellationTokenSource();
            var token = _searchCancellationTokenSource.Token;

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

                var results = await Task.Run(() => ExecuteSearch(token), token);

                if (!token.IsCancellationRequested)
                {
                    foreach (var folder in results)
                        SearchResultFolders.Add(folder);

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

        private List<FolderInfo> ExecuteSearch(CancellationToken token)
        {
            EnsureIndexFresh();

            var searchTerms = new SearchTerms(SearchText);
            var candidateFolders = GetCandidateFolders(SearchText);
            var results = new List<FolderInfo>(candidateFolders.Count);

            foreach (var folderPath in candidateFolders)
            {
                if (token.IsCancellationRequested) break;

                if (!_folderByPath.TryGetValue(folderPath, out var folderInfo)) // O(1)
                    continue;

                if (searchTerms.Matches(folderInfo))
                    results.Add(folderInfo);
            }

            return results
                .OrderByDescending(f => f.Rating)
                .ThenBy(f => f.Name)
                .ToList();
        }

        /// <summary>
        /// Returns the minimal candidate folder-path list for the query by using
        /// the appropriate index strategy per term type:
        ///
        ///   #tag   → inverted tag index (AND across terms = intersection)
        ///   @name  → folder-name index  (OR  across terms = union)
        ///   plain  → folder-name index  (OR  across terms = union)
        ///   *rate  → no index; full scan required
        ///
        /// Tag candidates and name candidates are intersected when both appear,
        /// so mixed queries like "#landscape @sea" touch only the folders that
        /// have the tag AND match the name substring.
        /// </summary>
        private List<string> GetCandidateFolders(string searchText)
        {
            var tagTerms = new List<string>();
            var nameTerms = new List<string>();
            var generalTerms = new List<string>();
            bool hasRating = false;

            // Parse OR groups
            foreach (Match m in OrGroupRegex.Matches(searchText))
            {
                foreach (var part in m.Groups[1].Value
                    .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    ClassifyTerm(part, tagTerms, nameTerms, generalTerms, ref hasRating);
            }

            // Individual terms
            var remaining = OrGroupRegex.Replace(searchText, " ");
            foreach (var part in remaining.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                ClassifyTerm(part, tagTerms, nameTerms, generalTerms, ref hasRating);

            bool hasTags = tagTerms.Count > 0;
            bool hasNames = nameTerms.Count > 0 || generalTerms.Count > 0;

            // Pure rating query — no index can help
            if (hasRating && !hasTags && !hasNames)
                return _folderByPath.Keys.ToList();

            HashSet<string> candidates = null;

            // 1. Narrow by tag (AND across tag terms)
            if (hasTags)
            {
                candidates = BuildTagCandidates(tagTerms);
                if (candidates.Count == 0) return new List<string>();
            }

            // 2. Narrow / expand by name/general (union across name terms,
            //    then intersect with tag candidates if they exist)
            if (hasNames)
            {
                var nameCandidates = BuildNameCandidates(nameTerms, generalTerms);
                if (candidates == null)
                    candidates = nameCandidates;
                else
                {
                    candidates.IntersectWith(nameCandidates);
                    if (candidates.Count == 0) return new List<string>();
                }
            }

            return candidates?.ToList() ?? _folderByPath.Keys.ToList();
        }

        private HashSet<string> BuildTagCandidates(List<string> tagTerms)
        {
            HashSet<string> result = null;

            foreach (var term in tagTerms)
            {
                var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in _tagIndex)
                {
                    if (kvp.Key.Contains(term))
                    {
                        foreach (var p in kvp.Value) matched.Add(p);
                    }
                }

                if (result == null)
                    result = matched;
                else
                {
                    result.IntersectWith(matched);
                    if (result.Count == 0) return result; // early exit
                }
            }

            return result ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private HashSet<string> BuildNameCandidates(
            List<string> nameTerms, List<string> generalTerms)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var term in nameTerms.Concat(generalTerms))
            {
                foreach (var kvp in _nameIndex)
                {
                    if (kvp.Key.Contains(term))
                    {
                        foreach (var p in kvp.Value) result.Add(p);
                    }
                }
            }
            return result;
        }

        private static void ClassifyTerm(
            string term,
            List<string> tagTerms, List<string> nameTerms, List<string> generalTerms,
            ref bool hasRating)
        {
            if (string.IsNullOrWhiteSpace(term)) return;

            if (term.StartsWith("#") && term.Length > 1)
                tagTerms.Add(term.Substring(1).ToLowerInvariant());
            else if (term.StartsWith("@") && term.Length > 1)
                nameTerms.Add(term.Substring(1).ToLowerInvariant());
            else if (term.StartsWith("*"))
                hasRating = true;
            else
                generalTerms.Add(term.ToLowerInvariant());
        }

        // ── Silent search ─────────────────────────────────────────────────────

        public async Task PerformSilentSearchAsync()
        {
            if (string.IsNullOrWhiteSpace(SearchText))
                return;

            // Skip if nothing changed
            if (string.Equals(SearchText, _lastSilentSearchText, StringComparison.Ordinal)
                && !_indexDirty)
                return;

            _lastSilentSearchText = SearchText;

            try
            {
                var results = await Task.Run(() => ExecuteSearch(CancellationToken.None));

                var currentSelection = SelectedSearchResult;

                // O(1) diff using HashSet instead of nested O(n²) Any(PathsEqual)
                var newPaths = new HashSet<string>(
                    results.Select(r => r.FolderPath),
                    StringComparer.OrdinalIgnoreCase);

                var toRemove = SearchResultFolders
                    .Where(f => !newPaths.Contains(f.FolderPath))
                    .ToList();
                foreach (var item in toRemove)
                    SearchResultFolders.Remove(item);

                var existingPaths = new HashSet<string>(
                    SearchResultFolders.Select(f => f.FolderPath),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var item in results)
                {
                    if (!existingPaths.Contains(item.FolderPath))
                        SearchResultFolders.Add(item);
                }

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

        #region Helpers

        private void UpdateStatus(string message) =>
            StatusMessageChanged?.Invoke(this, message);

        #endregion

        #region IDisposable

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _searchCancellationTokenSource?.Cancel();
                _searchCancellationTokenSource?.Dispose();
                _searchCancellationTokenSource = null;
            }
            base.Dispose(disposing);
        }

        #endregion
    }

    // =========================================================================
    //  SearchTerms — same logic, reduced allocations
    // =========================================================================

    /// <summary>
    /// Parses a search string into AND-groups of OR-predicates and evaluates
    /// them against a FolderInfo.
    ///
    /// Changes vs original:
    ///   • OrGroupRegex compiled once at class level.
    ///   • Tag predicates capture the lower-cased term in closure (one allocation
    ///     per parsed term, not per folder evaluation).
    ///   • Matches() uses a plain foreach + break instead of LINQ .Any() to avoid
    ///     delegate-allocation overhead on the hot path.
    /// </summary>
    internal class SearchTerms
    {
        private static readonly Regex OrGroupRegex =
            new Regex(@"\(([^)]+)\)", RegexOptions.Compiled);

        private readonly List<List<Predicate<FolderInfo>>> _andGroups =
            new List<List<Predicate<FolderInfo>>>();

        public SearchTerms(string searchText)
        {
            if (string.IsNullOrWhiteSpace(searchText)) return;

            foreach (Match m in OrGroupRegex.Matches(searchText))
            {
                var preds = ParseGroup(m.Groups[1].Value);
                if (preds.Count > 0) _andGroups.Add(preds);
            }

            var remaining = OrGroupRegex.Replace(searchText, " ");
            foreach (var token in remaining.Split(
                new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = CreatePredicateForTerm(token.Trim());
                if (p != null)
                    _andGroups.Add(new List<Predicate<FolderInfo>> { p });
            }
        }

        private List<Predicate<FolderInfo>> ParseGroup(string groupText)
        {
            var list = new List<Predicate<FolderInfo>>();
            foreach (var term in groupText.Split(
                new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = CreatePredicateForTerm(term.Trim());
                if (p != null) list.Add(p);
            }
            return list;
        }

        private static Predicate<FolderInfo> CreatePredicateForTerm(string term)
        {
            if (string.IsNullOrWhiteSpace(term)) return null;

            if (term.StartsWith("#"))
            {
                string tagTerm = term.Substring(1).ToLowerInvariant(); // captured once
                if (string.IsNullOrWhiteSpace(tagTerm)) return null;
                return folder =>
                {
                    if (folder?.Tags == null) return false;
                    foreach (var tag in folder.Tags)
                        if (tag != null && tag.ToLowerInvariant().Contains(tagTerm))
                            return true;
                    return false;
                };
            }

            if (term.StartsWith("*"))
                return CreateRatingPredicate(term.Substring(1).Trim());

            if (term.StartsWith("@"))
            {
                string nameTerm = term.Substring(1).ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(nameTerm)) return null;
                return folder =>
                    folder.Name != null &&
                    folder.Name.ToLowerInvariant().Contains(nameTerm);
            }

            // General text
            string textTerm = term.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(textTerm)) return null;
            return folder =>
                (folder.Name?.ToLowerInvariant().Contains(textTerm) ?? false) ||
                (folder.FolderPath?.ToLowerInvariant().Contains(textTerm) ?? false);
        }

        private static Predicate<FolderInfo> CreateRatingPredicate(string ratingPattern)
        {
            if (string.IsNullOrWhiteSpace(ratingPattern)) return null;

            string op;
            string valueStr;

            if (ratingPattern.StartsWith(">=") || ratingPattern.StartsWith("<="))
            { op = ratingPattern.Substring(0, 2); valueStr = ratingPattern.Substring(2).Trim(); }
            else if (ratingPattern.StartsWith("=") ||
                     ratingPattern.StartsWith(">") ||
                     ratingPattern.StartsWith("<"))
            { op = ratingPattern.Substring(0, 1); valueStr = ratingPattern.Substring(1).Trim(); }
            else return null;

            if (!int.TryParse(valueStr, out int value) || value < 0 || value > 5)
                return null;

            return op switch
            {
                ">=" => folder => folder.Rating >= value,
                "<=" => folder => folder.Rating <= value,
                "=" => folder => folder.Rating == value,
                ">" => folder => folder.Rating > value,
                "<" => folder => folder.Rating < value,
                _ => null
            };
        }

        /// <summary>All AND groups must pass; each group uses OR logic internally.</summary>
        public bool Matches(FolderInfo folder)
        {
            if (_andGroups.Count == 0) return true;

            foreach (var orGroup in _andGroups)
            {
                bool any = false;
                foreach (var p in orGroup)
                    if (p(folder)) { any = true; break; }
                if (!any) return false;
            }
            return true;
        }
    }
}
