using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImageFolderManager.Services;
using System.Threading;
using System.Threading.Tasks;

namespace ImageFolderManager.Controls
{
    /// <summary>
    /// Represents a single folder node in the tree.
    /// Uses only filesystem APIs — no ShellObject / COM calls — for maximum speed.
    /// </summary>
    public class FolderNode
    {
        // ── Identity ─────────────────────────────────────────────────────────
        public string FullPath { get; }
        public string Name { get; }

        // ── Lazy-load state ───────────────────────────────────────────────────
        private volatile bool _childrenLoaded = false;
        private List<FolderNode> _children = null;
        private readonly SemaphoreSlim _loadLock = new SemaphoreSlim(1, 1);

        // ── Cached "has sub-dirs" flag ────────────────────────────────────────
        // null = not yet probed; true/false = known
        private bool? _hasSubDirectories;

        public FolderNode(string fullPath)
        {
            FullPath = fullPath;
            Name = Path.GetFileName(fullPath);
            if (string.IsNullOrEmpty(Name))
                Name = fullPath; // drive root: "C:\"
        }

        /// <summary>
        /// Returns true when this folder has at least one sub-directory.
        /// Uses EnumerateDirectories and stops after the first hit — O(1) I/O cost.
        /// Result is cached after the first call.
        /// </summary>
        public bool HasSubDirectories
        {
            get
            {
                if (_hasSubDirectories.HasValue)
                    return _hasSubDirectories.Value;

                _hasSubDirectories = ProbeHasSubDirectories(FullPath);
                return _hasSubDirectories.Value;
            }
        }

        /// <summary>
        /// Whether children have been loaded into memory.
        /// </summary>
        public bool ChildrenLoaded => _childrenLoaded;

        /// <summary>
        /// Synchronously returns already-loaded children (or empty list if not yet loaded).
        /// Call <see cref="LoadChildrenAsync"/> first.
        /// </summary>
        public IReadOnlyList<FolderNode> Children =>
            _children ?? (IReadOnlyList<FolderNode>)Array.Empty<FolderNode>();

        // ── Loading ───────────────────────────────────────────────────────────

        /// <summary>
        /// Loads the immediate children of this folder on a background thread.
        /// Safe to call multiple times — only the first call does real work.
        /// </summary>
        public async Task<IReadOnlyList<FolderNode>> LoadChildrenAsync(
            CancellationToken cancellationToken = default)
        {
            if (_childrenLoaded)
                return Children;

            // Serialize concurrent expansion of the same node
            await _loadLock.WaitAsync(cancellationToken);
            try
            {
                if (_childrenLoaded)
                    return Children;

                var nodes = await Task.Run(() =>
                    EnumerateChildren(FullPath, cancellationToken), cancellationToken);

                _children = nodes;
                _childrenLoaded = true;
                // Update the flag from what we actually found
                _hasSubDirectories = nodes.Count > 0;

                return nodes;
            }
            finally
            {
                _loadLock.Release();
            }
        }

        /// <summary>
        /// Forces a reload of children (used after folder create/delete/rename).
        /// </summary>
        public void InvalidateChildren()
        {
            _childrenLoaded = false;
            _children = null;
            _hasSubDirectories = null;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static List<FolderNode> EnumerateChildren(
            string path, CancellationToken ct)
        {
            var list = new List<FolderNode>();
            try
            {
                // EnumerateDirectories is lazy — much faster than GetDirectories for large trees
                foreach (var dir in Directory.EnumerateDirectories(
                    path, "*", SearchOption.TopDirectoryOnly))
                {
                    ct.ThrowIfCancellationRequested();

                    // Skip hidden / system folders (optional — remove if all folders are wanted)
                    var attrs = File.GetAttributes(dir);
                    if ((attrs & FileAttributes.Hidden) != 0 ||
                        (attrs & FileAttributes.System) != 0)
                        continue;

                    list.Add(new FolderNode(dir));
                }

                // Natural sort (Windows Explorer order)
                list.Sort((a, b) =>
                    WindowsNaturalStringComparer.Instance.Compare(a.Name, b.Name));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Access denied, broken junction, etc. — return whatever we have
            }
            return list;
        }

        private static bool ProbeHasSubDirectories(string path)
        {
            try
            {
                using (var e = Directory.EnumerateDirectories(
                    path, "*", SearchOption.TopDirectoryOnly).GetEnumerator())
                {
                    while (e.MoveNext())
                    {
                        // Skip hidden/system so the arrow indicator matches what will actually load
                        var attrs = File.GetAttributes(e.Current);
                        if ((attrs & FileAttributes.Hidden) != 0 ||
                            (attrs & FileAttributes.System) != 0)
                            continue;
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Fast natural-order string comparer (same ordering as Windows Explorer)
    // ─────────────────────────────────────────────────────────────────────────
    internal static class NaturalStringComparer
    {
        public static int Compare(string x, string y)
        {
            if (x == null && y == null) return 0;
            if (x == null) return -1;
            if (y == null) return 1;

            int ix = 0, iy = 0;
            while (ix < x.Length && iy < y.Length)
            {
                if (char.IsDigit(x[ix]) && char.IsDigit(y[iy]))
                {
                    // Compare numeric segments by value
                    long nx = 0, ny = 0;
                    while (ix < x.Length && char.IsDigit(x[ix]))
                        nx = nx * 10 + (x[ix++] - '0');
                    while (iy < y.Length && char.IsDigit(y[iy]))
                        ny = ny * 10 + (y[iy++] - '0');
                    if (nx != ny) return nx.CompareTo(ny);
                }
                else
                {
                    int cmp = char.ToUpperInvariant(x[ix])
                              .CompareTo(char.ToUpperInvariant(y[iy]));
                    if (cmp != 0) return cmp;
                    ix++; iy++;
                }
            }
            return x.Length.CompareTo(y.Length);
        }
    }
}