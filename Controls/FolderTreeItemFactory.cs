using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ImageFolderManager.Controls
{
    /// <summary>
    /// Builds and manages TreeViewItem nodes backed by <see cref="FolderNode"/> data.
    /// All I/O happens on background threads; the UI thread only assembles pre-built objects.
    ///
    /// Design reference: XnViewMP — folders are loaded one level at a time using plain
    /// filesystem APIs, with a static icon geometry so no COM / thumbnail calls are made
    /// during tree construction.
    /// </summary>
    internal static class FolderTreeItemFactory
    {
        // ── Tunables ──────────────────────────────────────────────────────────

        /// <summary>Number of child items added to the UI in one Dispatcher batch.</summary>
        private const int UI_BATCH_SIZE = 50;

        /// <summary>
        /// DispatcherPriority used for batched UI updates.
        /// Background priority keeps the UI responsive during large expansions.
        /// </summary>
        private const DispatcherPriority BATCH_PRIORITY =
            DispatcherPriority.Background;

        // ── Static icon geometry (drawn once, frozen, shared by all nodes) ────
        // A simple folder shape — no COM, no I/O, renders at any DPI.
        private static readonly Geometry _folderGeometry = BuildFolderGeometry();
        private static readonly SolidColorBrush _iconBrush =
            Freeze(new SolidColorBrush(Color.FromRgb(255, 198, 79)));   // Windows-yellow
        private static readonly SolidColorBrush _textBrush =
            Freeze(new SolidColorBrush(Colors.White));
        private static readonly SolidColorBrush _transparentBrush =
            Freeze(new SolidColorBrush(Colors.Transparent));

        // ─────────────────────────────────────────────────────────────────────
        // Public API
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a root-level TreeViewItem for <paramref name="node"/> synchronously.
        /// Must be called on the UI thread.
        /// </summary>
        public static TreeViewItem CreateItem(FolderNode node)
        {
            var item = MakeItem(node);
            // Probe on the calling thread (root node only — usually fast)
            EnsurePlaceholder(item, node);
            return item;
        }

        internal static UIElement CreateHeader(string name)
        {
            return BuildHeader(name);
        }

        /// <summary>
        /// Expands <paramref name="parentItem"/> by loading its children from
        /// <paramref name="parentNode"/> asynchronously, then inserting them into
        /// the UI in batches so the window stays responsive.
        ///
        /// Cancels any previous in-flight expansion of this node if called again.
        /// </summary>
        public static async Task ExpandAsync(
            TreeViewItem parentItem,
            FolderNode parentNode,
            Dictionary<string, TreeViewItem> pathMap,
            CancellationToken ct)
        {
            // Remove the placeholder "Loading…" item
            parentItem.Items.Clear();

            void RestorePlaceholderIfNeeded()
            {
                if (parentItem.Items.Count > 0)
                    return;

                bool shouldShowPlaceholder = false;

                if (parentNode.ChildrenLoaded)
                {
                    shouldShowPlaceholder = parentNode.Children.Count > 0;
                }
                else
                {
                    shouldShowPlaceholder = parentNode.HasSubDirectories;
                }

                if (shouldShowPlaceholder)
                {
                    parentItem.Items.Add(MakePlaceholder());
                }
            }

            // Load children on a background thread (pure filesystem I/O)
            IReadOnlyList<FolderNode> children;
            try
            {
                children = await parentNode.LoadChildrenAsync(ct);
            }
            catch (OperationCanceledException)
            {
                RestorePlaceholderIfNeeded();
                return;
            }
            catch
            {
                // Access denied etc. — leave the node empty
                return;
            }

            if (ct.IsCancellationRequested || children == null)
            {
                RestorePlaceholderIfNeeded();
                return;
            }

            // Insert children into the UI in batches at Background priority.
            // Each batch yields control back to the message pump so the window
            // stays interactive throughout a 500-item expansion.
            int total = children.Count;
            int inserted = 0;

            while (inserted < total)
            {
                if (ct.IsCancellationRequested)
                {
                    RestorePlaceholderIfNeeded();
                    return;
                }

                int end = Math.Min(inserted + UI_BATCH_SIZE, total);

                // Probe "has sub-dirs" for the next batch on the background thread
                // BEFORE we touch the UI thread, so the UI batch itself is fast.
                var batch = new (FolderNode node, bool hasSub)[end - inserted];
                int batchIdx = 0;
                for (int i = inserted; i < end; i++)
                {
                    var child = children[i];
                    // HasSubDirectories is cached after first call — safe to probe here
                    bool hasSub = await Task.Run(() => child.HasSubDirectories, ct);
                    batch[batchIdx++] = (child, hasSub);
                }

                if (ct.IsCancellationRequested)
                {
                    RestorePlaceholderIfNeeded();
                    return;
                }

                // Now hand off to the UI thread for the fast insert-only pass
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    foreach (var (node, hasSub) in batch)
                    {
                        var item = MakeItem(node);
                        if (hasSub)
                            item.Items.Add(MakePlaceholder()); // show expand arrow

                        parentItem.Items.Add(item);

                        // Register in path map for quick lookup
                        pathMap[node.FullPath] = item;
                    }
                }, BATCH_PRIORITY);

                inserted = end;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Internal helpers
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Creates a minimal TreeViewItem for a folder node.</summary>
        private static TreeViewItem MakeItem(FolderNode node)
        {
            var item = new TreeViewItem
            {
                Tag = node,
                Header = BuildHeader(node.Name),
                IsExpanded = false,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            return item;
        }

        /// <summary>
        /// Probes whether <paramref name="node"/> has sub-dirs and, if so, adds
        /// a placeholder child so the expand arrow is shown.
        /// Call only after I/O is acceptable on the current thread.
        /// </summary>
        private static void EnsurePlaceholder(TreeViewItem item, FolderNode node)
        {
            if (node.HasSubDirectories)
                item.Items.Add(MakePlaceholder());
        }

        /// <summary>
        /// Returns a disabled placeholder TreeViewItem that triggers lazy loading.
        /// Using a real (disabled) TreeViewItem instead of a bare string keeps
        /// VirtualizingStackPanel happy and avoids type-check surprises.
        /// </summary>
        internal static TreeViewItem MakePlaceholder()
        {
            return new TreeViewItem
            {
                Header = "Loading…",
                IsEnabled = false,
                Tag = "__PLACEHOLDER__"
            };
        }

        /// <summary>Returns true when an item contains only the lazy-load placeholder.</summary>
        internal static bool HasOnlyPlaceholder(TreeViewItem item)
        {
            return item.Items.Count == 1
                && item.Items[0] is TreeViewItem ti
                && ti.Tag as string == "__PLACEHOLDER__";
        }

        // ── Header (icon + label) ─────────────────────────────────────────────

        private static UIElement BuildHeader(string name)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                // Avoid creating a Margin object per-item; use padding instead
            };

            // Static folder icon — shared geometry, no allocation, no I/O
            var icon = new System.Windows.Shapes.Path
            {
                Data = _folderGeometry,
                Fill = _iconBrush,
                Width = 16,
                Height = 14,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                SnapsToDevicePixels = true,
            };

            var label = new TextBlock
            {
                Text = name,
                Foreground = _textBrush,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                // Prevent TextBlock from measuring every character during layout
                TextTrimming = TextTrimming.None,
            };

            panel.Children.Add(icon);
            panel.Children.Add(label);
            return panel;
        }

        // ── Icon geometry (folder shape, drawn in code so no XAML file needed) ─

        private static Geometry BuildFolderGeometry()
        {
            // Simple flat folder: tab on top-left + body
            // Coordinates match a 16×14 viewport
            const string data =
                "M0,3 L5,3 L6,1.5 L14,1.5 L14,3 L16,3 L16,14 L0,14 Z " +
                "M1,4 L1,13 L15,13 L15,4 Z";
            var g = Geometry.Parse(data);
            g.Freeze();
            return g;
        }

        private static T Freeze<T>(T freezable) where T : Freezable
        {
            freezable.Freeze();
            return freezable;
        }
    }
}
