using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using ImageFolderManager.Models;

namespace ImageFolderManager.Controls
{
    public partial class NativeDirectoryTreeControl : UserControl
    {
        // Events to communicate with WPF
        public event EventHandler<string> DirectorySelected;
        public event EventHandler<List<string>> DirectoriesSelected;

        // Shell32 API for system folder icons
        [DllImport("Shell32.dll")]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

        [StructLayout(LayoutKind.Sequential)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        private const uint SHGFI_ICON = 0x100;
        private const uint SHGFI_SMALLICON = 0x1;
        private const uint SHGFI_LARGEICON = 0x0;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

        private ImageList _imageList;
        private TreeView _treeView;
        private Dictionary<string, int> _iconCache = new Dictionary<string, int>();
        private string _rootDirectory;
        private bool _multiSelect = false;
        private List<TreeNode> _selectedNodes = new List<TreeNode>();

        public event EventHandler<string> LoadImagesRequested;
        public event EventHandler<string> NewFolderRequested;
        public event EventHandler<string> CutRequested;
        public event EventHandler<string> CopyRequested;
        public event EventHandler<string> PasteRequested;
        public event EventHandler<string> ShowInExplorerRequested;
        public event EventHandler<string> DeleteRequested;
        public event EventHandler<string> RenameRequested;

        private bool _isDragging = false;
        private Point _dragStartPoint;
        private TreeNode _draggedNode;
        private DateTime _mouseDownTime;
        private const int DRAG_DELAY_MS = 300; // 300ms delay before starting drag
        private const double DRAG_DISTANCE_MULTIPLIER = 1.5; // Increase drag distance threshold
        private TreeNode _lastSelectedNode;

        public NativeDirectoryTreeControl()
        {
            InitializeComponent();
           
        }

        private void InitializeComponent()
        {
            this.SuspendLayout();
            // 
            // NativeDirectoryTreeControl
            // 
            this.Name = "NativeDirectoryTreeControl";
            this.Size = new System.Drawing.Size(250, 400);
            this.ResumeLayout(false);

            _imageList = new ImageList();
            _imageList.ColorDepth = ColorDepth.Depth32Bit;
            _imageList.ImageSize = new Size(16, 16);

            _treeView = new TreeView();
            _treeView.Dock = DockStyle.Fill;
            _treeView.HideSelection = false;
            _treeView.ImageList = _imageList;
            _treeView.AfterSelect += TreeView_AfterSelect;
            _treeView.BeforeExpand += TreeView_BeforeExpand;
            _treeView.NodeMouseClick += TreeView_NodeMouseClick;

            // Add the tree view to the control
            Controls.Add(_treeView);

            // Set up context menu
            SetupContextMenu();

            // Set up drag & drop
            SetupDragDrop();

            // Set up multi-selection
            SetupMultiSelection();
        }

        /// <summary>
        /// Set the root directory for the tree view
        /// </summary>
        public void SetRootDirectory(string rootDirectory)
        {
            _rootDirectory = rootDirectory;
            ReloadTree();
        }

        /// <summary>
        /// Enable or disable multi-selection
        /// </summary>
        public void SetMultiSelect(bool multiSelect)
        {
            _multiSelect = multiSelect;
            _treeView.HideSelection = false; // Always show selected node
        }

        /// <summary>
        /// Reload the entire tree
        /// </summary>
        public void ReloadTree()
        {
            try
            {
                _treeView.BeginUpdate();
                _treeView.Nodes.Clear();

                if (!string.IsNullOrEmpty(_rootDirectory) && Directory.Exists(_rootDirectory))
                {
                    // Add root directory
                    var rootNode = CreateDirectoryNode(_rootDirectory);
                    _treeView.Nodes.Add(rootNode);
                    rootNode.Expand();
                }
                else
                {
                    // Add drives as root nodes
                    foreach (var drive in DriveInfo.GetDrives())
                    {
                        if (drive.IsReady)
                        {
                            var driveNode = CreateDirectoryNode(drive.RootDirectory.FullName);
                            _treeView.Nodes.Add(driveNode);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading directory tree: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _treeView.EndUpdate();
            }
        }

        /// <summary>
        /// Create a tree node for a directory
        /// </summary>
        private TreeNode CreateDirectoryNode(string path)
        {
            var dirInfo = new DirectoryInfo(path);
            var node = new TreeNode(dirInfo.Name);
            node.Tag = path;
            node.ImageIndex = GetIconIndex(path);
            node.SelectedImageIndex = node.ImageIndex;

            // Add dummy node to show expand button
            try
            {
                if (Directory.GetDirectories(path).Length > 0)
                {
                    node.Nodes.Add(new TreeNode("Loading...") { Tag = "dummy" });
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Add dummy node to show expand button even if we can't access the directory
                node.Nodes.Add(new TreeNode("Loading...") { Tag = "dummy" });
            }
            catch (Exception)
            {
                // Ignore other exceptions
            }

            return node;
        }

        /// <summary>
        /// Get system icon for a path
        /// </summary>
        private int GetIconIndex(string path)
        {
            if (_iconCache.ContainsKey(path))
            {
                return _iconCache[path];
            }

            try
            {
                SHFILEINFO shfi = new SHFILEINFO();
                SHGetFileInfo(path, FILE_ATTRIBUTE_DIRECTORY, ref shfi, (uint)Marshal.SizeOf(shfi), SHGFI_ICON | SHGFI_SMALLICON);

                if (shfi.hIcon != IntPtr.Zero)
                {
                    Icon icon = Icon.FromHandle(shfi.hIcon);
                    _imageList.Images.Add(icon);
                    int index = _imageList.Images.Count - 1;
                    _iconCache[path] = index;
                    return index;
                }
            }
            catch
            {
                // Use default folder icon if we can't get the system icon
            }

            // Return default icon index if we can't get the system icon
            return 0;
        }

        /// <summary>
        /// Handle tree node expansion
        /// </summary>
        private void TreeView_BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            var node = e.Node;

            // Clear dummy nodes
            if (node.Nodes.Count == 1 && node.Nodes[0].Tag?.ToString() == "dummy")
            {
                node.Nodes.Clear();

                try
                {
                    string path = node.Tag?.ToString();
                    if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                    {
                        _treeView.BeginUpdate();

                        // Add subdirectories
                        foreach (var dir in Directory.GetDirectories(path))
                        {
                            try
                            {
                                var dirNode = CreateDirectoryNode(dir);
                                node.Nodes.Add(dirNode);
                            }
                            catch
                            {
                                // Skip directories we can't access
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error expanding directory: {ex.Message}", "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    _treeView.EndUpdate();
                }
            }
        }

        /// <summary>
        /// Handle tree node selection
        /// </summary>
        private void TreeView_AfterSelect(object sender, TreeViewEventArgs e)
        {

            if (Control.ModifierKeys == Keys.None)
            {
                ClearSelectedNodes();
            }

            var selectedPath = e.Node.Tag?.ToString();
            if (!string.IsNullOrEmpty(selectedPath) && Directory.Exists(selectedPath))
            {
                DirectorySelected?.Invoke(this, selectedPath);
            }
        }

     
        private void ClearSelectedNodes()
        {
            foreach (var node in _selectedNodes)
            {
                node.BackColor = _treeView.BackColor;
                node.ForeColor = _treeView.ForeColor;
            }
            _selectedNodes.Clear();
        }

        public List<string> GetSelectedPaths()
        {
            return _selectedNodes
                .Select(n => n.Tag?.ToString())
                .Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p))
                .ToList();
        }

        /// <summary>
        /// Shows the context menu for the selected node
        /// </summary>
        private void ShowContextMenu(TreeNode node, System.Drawing.Point location)
        {
            if (node == null) return;

            // Create Windows Forms context menu
            var contextMenu = new ContextMenuStrip();

            // Get the path from the node
            string folderPath = node.Tag as string;
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath)) return;

            // Create a FolderInfo object to use with the context menu
            string folderName = Path.GetFileName(folderPath);

            // Add "Load Images" option
            var loadImagesItem = new ToolStripMenuItem("Load Images", null, (s, e) => {
                // Notify WPF host about request to load images
                OnLoadImagesRequested(folderPath);
            })
            {
                ToolTipText = "Double-click"
            };
            contextMenu.Items.Add(loadImagesItem);
            contextMenu.Items.Add(new ToolStripSeparator());

            // Add "New Folder" option
            var newFolderItem = new ToolStripMenuItem("New Folder", null, (s, e) => {
                OnNewFolderRequested(folderPath);
            })
            {
                ToolTipText = "Ctrl+N"
            };
            contextMenu.Items.Add(newFolderItem);

            // Add Cut, Copy, Paste options
            var cutItem = new ToolStripMenuItem("Cut", null, (s, e) => {
                OnCutRequested(folderPath);
            })
            {
                ToolTipText = "Ctrl+X"
            };
            contextMenu.Items.Add(cutItem);

            var copyItem = new ToolStripMenuItem("Copy", null, (s, e) => {
                OnCopyRequested(folderPath);
            })
            {
                ToolTipText = "Ctrl+C"
            };
            contextMenu.Items.Add(copyItem);

            var pasteItem = new ToolStripMenuItem("Paste", null, (s, e) => {
                OnPasteRequested(folderPath);
            })
            {
                ToolTipText = "Ctrl+V",
                Enabled = CanPaste() // This will be implemented
            };
            contextMenu.Items.Add(pasteItem);

            contextMenu.Items.Add(new ToolStripSeparator());

            // Add Show in Explorer
            var showInExplorerItem = new ToolStripMenuItem("Show in Explorer", null, (s, e) => {
                OnShowInExplorerRequested(folderPath);
            });
            contextMenu.Items.Add(showInExplorerItem);

            // Add Delete option
            var deleteItem = new ToolStripMenuItem("Delete", null, (s, e) => {
                OnDeleteRequested(folderPath);
            })
            {
                ToolTipText = "Delete"
            };
            contextMenu.Items.Add(deleteItem);

            contextMenu.Items.Add(new ToolStripSeparator());

            // Add Rename option
            var renameItem = new ToolStripMenuItem("Rename", null, (s, e) => {
                OnRenameRequested(folderPath);
            })
            {
                ToolTipText = "F2"
            };
            contextMenu.Items.Add(renameItem);

            // Show the context menu
            contextMenu.Show(_treeView, location);
        }
        private void SetupContextMenu()
        {
            // Add the MouseUp event to detect right-clicks
            _treeView.MouseUp += (s, e) => {
                if (e.Button == MouseButtons.Right)
                {
                    // Get the node at the position
                    var node = _treeView.GetNodeAt(e.Location);
                    if (node != null)
                    {
                        // Select the node if not already selected
                        if (_treeView.SelectedNode != node)
                        {
                            _treeView.SelectedNode = node;
                        }

                        // Show context menu
                        ShowContextMenu(node, e.Location);
                    }
                }
            };
        }
        private void OnLoadImagesRequested(string path)
        {
            LoadImagesRequested?.Invoke(this, path);
        }

        private void OnNewFolderRequested(string path)
        {
            NewFolderRequested?.Invoke(this, path);
        }

        private void OnCutRequested(string path)
        {
            CutRequested?.Invoke(this, path);
        }

        private void OnCopyRequested(string path)
        {
            CopyRequested?.Invoke(this, path);
        }

        private void OnPasteRequested(string path)
        {
            PasteRequested?.Invoke(this, path);
        }

        private void OnShowInExplorerRequested(string path)
        {
            ShowInExplorerRequested?.Invoke(this, path);
        }

        private void OnDeleteRequested(string path)
        {
            DeleteRequested?.Invoke(this, path);
        }

        private void OnRenameRequested(string path)
        {
            RenameRequested?.Invoke(this, path);
        }

        /// <summary>
        /// Set up drag & drop functionality
        /// </summary>
        private void SetupDragDrop()
        {
            // Enable drag & drop for the tree view
            _treeView.AllowDrop = true;

            // Mouse events for drag detection
            _treeView.MouseDown += TreeView_MouseDown;
            _treeView.MouseMove += TreeView_MouseMove;
            _treeView.MouseUp += TreeView_MouseUp;

            // Drag & drop events
            _treeView.DragOver += TreeView_DragOver;
            _treeView.DragDrop += TreeView_DragDrop;
        }

        /// <summary>
        /// Handles the MouseDown event to start drag operation
        /// </summary>
        private void TreeView_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                // Record start point and time
                _dragStartPoint = e.Location;
                _mouseDownTime = DateTime.Now;

                // Get the node under the mouse
                var node = _treeView.GetNodeAt(e.Location);
                if (node != null)
                {
                    _draggedNode = node;
                }
            }
        }

        /// <summary>
        /// Handles the MouseMove event to initiate drag operation
        /// </summary>
        private void TreeView_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && !_isDragging && _draggedNode != null)
            {
                // Check if enough time has passed to start drag
                TimeSpan timeSinceMouseDown = DateTime.Now - _mouseDownTime;
                if (timeSinceMouseDown.TotalMilliseconds >= DRAG_DELAY_MS)
                {
                    // Calculate distance moved
                    int dx = Math.Abs(e.X - _dragStartPoint.X);
                    int dy = Math.Abs(e.Y - _dragStartPoint.Y);

                    // Increase drag distance threshold
                    double dragThreshold = SystemInformation.DragSize.Width * DRAG_DISTANCE_MULTIPLIER;

                    // Check if moved far enough to start drag
                    if (dx > dragThreshold || dy > dragThreshold)
                    {
                        StartDrag(_draggedNode);
                    }
                }
            }
        }

        /// <summary>
        /// Handles the MouseUp event to cancel drag operation
        /// </summary>
        private void TreeView_MouseUp(object sender, MouseEventArgs e)
        {
            _isDragging = false;
            _draggedNode = null;
        }

        /// <summary>
        /// Starts a drag operation for the specified node
        /// </summary>
        private void StartDrag(TreeNode node)
        {
            if (node == null) return;

            string path = node.Tag as string;
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;

            // Start the drag operation
            _isDragging = true;

            // Get the set of paths to drag
            List<string> dragPaths;
            if (_selectedNodes.Contains(node) && _selectedNodes.Count > 1)
            {
                // If node is part of multi-selection, drag all selected nodes
                dragPaths = _selectedNodes
                    .Select(n => n.Tag as string)
                    .Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p))
                    .ToList();
            }
            else
            {
                // Otherwise, just drag this node
                dragPaths = new List<string> { path };
            }

            // Create drag data
            var dragData = new DataObject(DataFormats.FileDrop, dragPaths.ToArray());

            // Add effect data
            bool isCut = ModifierKeys.HasFlag(Keys.Shift); // Shift key for cut (move) operation
            dragData.SetData("IsCutOperation", isCut);

            // Start the drag drop operation
            _treeView.DoDragDrop(dragData, DragDropEffects.Copy | DragDropEffects.Move);
        }

        /// <summary>
        /// Handles the DragOver event to determine drop effect
        /// </summary>
        private void TreeView_DragOver(object sender, DragEventArgs e)
        {
            // Get the node under the mouse
            var pt = _treeView.PointToClient(new Point(e.X, e.Y));
            var targetNode = _treeView.GetNodeAt(pt);

            // Default to none
            e.Effect = DragDropEffects.None;

            // Check if drop is possible
            if (targetNode != null && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                // Get target path
                string targetPath = targetNode.Tag as string;
                if (!string.IsNullOrEmpty(targetPath) && Directory.Exists(targetPath))
                {
                    // Get source paths
                    string[] sourcePaths = (string[])e.Data.GetData(DataFormats.FileDrop);
                    if (sourcePaths != null && sourcePaths.Length > 0)
                    {
                        // Verify target isn't child of any source
                        bool canDrop = true;
                        foreach (string sourcePath in sourcePaths)
                        {
                            if (string.IsNullOrEmpty(sourcePath) || !Directory.Exists(sourcePath))
                            {
                                canDrop = false;
                                break;
                            }

                            if (sourcePath == targetPath || IsSubdirectory(sourcePath, targetPath))
                            {
                                canDrop = false;
                                break;
                            }
                        }

                        if (canDrop)
                        {
                            // Set effect based on modifiers
                            if ((e.KeyState & 8) == 8) // Ctrl key
                            {
                                e.Effect = DragDropEffects.Copy;
                            }
                            else
                            {
                                e.Effect = DragDropEffects.Move;
                            }

                            // Highlight the drop target
                            HighlightDropTarget(targetNode);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Checks if child is a subdirectory of parent
        /// </summary>
        private bool IsSubdirectory(string parent, string child)
        {
            try
            {
                DirectoryInfo parentInfo = new DirectoryInfo(parent);
                DirectoryInfo childInfo = new DirectoryInfo(child);

                while (childInfo.Parent != null)
                {
                    if (childInfo.Parent.FullName.Equals(parentInfo.FullName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    childInfo = childInfo.Parent;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Highlights a node as a drop target
        /// </summary>
        private void HighlightDropTarget(TreeNode node)
        {
            // Clear previous highlights
            foreach (TreeNode n in _treeView.Nodes)
            {
                if (!_selectedNodes.Contains(n))
                {
                    n.BackColor = _treeView.BackColor;
                }
            }

            // Highlight the drop target
            if (node != null && !_selectedNodes.Contains(node))
            {
                node.BackColor = System.Drawing.Color.FromArgb(80, 0, 120, 215);
            }
        }

        /// <summary>
        /// Handles the DragDrop event to complete the drag operation
        /// </summary>
        private void TreeView_DragDrop(object sender, DragEventArgs e)
        {
            // Clear highlights
            foreach (TreeNode node in _treeView.Nodes)
            {
                if (!_selectedNodes.Contains(node))
                {
                    node.BackColor = _treeView.BackColor;
                }
            }

            // Get the node under the mouse
            var pt = _treeView.PointToClient(new Point(e.X, e.Y));
            var targetNode = _treeView.GetNodeAt(pt);

            if (targetNode != null && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                // Get target path
                string targetPath = targetNode.Tag as string;
                if (!string.IsNullOrEmpty(targetPath) && Directory.Exists(targetPath))
                {
                    // Get source paths
                    string[] sourcePaths = (string[])e.Data.GetData(DataFormats.FileDrop);
                    if (sourcePaths != null && sourcePaths.Length > 0)
                    {
                        // Determine operation type
                        bool isCopy = (e.Effect == DragDropEffects.Copy);

                        // Trigger drop event - let the WPF host handle it
                        OnFolderDropped(sourcePaths, targetPath, isCopy);
                    }
                }
            }
        }

        // Event and method for folder drop
        public event EventHandler<FolderDropEventArgs> FolderDropped;

        private void OnFolderDropped(string[] sourcePaths, string targetPath, bool isCopy)
        {
            FolderDropped?.Invoke(this, new FolderDropEventArgs(sourcePaths, targetPath, isCopy));
        }

        // Ensure to call SetupDragDrop from the constructor or InitializeComponent
        // Add this line to the constructor or InitializeComponent method:
        // SetupDragDrop();

        // Helper method for the CanPaste property requested earlier
        private bool _canPaste = false;

        public void SetCanPaste(bool canPaste)
        {
            _canPaste = canPaste;
        }

        public bool CanPaste()
        {
            return _canPaste;
        }

       
        /// <summary>
        /// Select a path in the tree view
        /// </summary>
        public void SelectPath(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                return;

            try
            {
                // Build the path segments
                var segments = new List<string>();
                var current = new DirectoryInfo(path);

                while (current != null)
                {
                    segments.Insert(0, current.FullName);
                    current = current.Parent;
                }

                // Find and expand each segment
                TreeNode node = null;

                foreach (var segment in segments)
                {
                    if (node == null)
                    {
                        // Find in root nodes
                        foreach (TreeNode rootNode in _treeView.Nodes)
                        {
                            string nodePath = rootNode.Tag?.ToString();
                            if (nodePath != null && segment.StartsWith(nodePath, StringComparison.OrdinalIgnoreCase))
                            {
                                node = rootNode;
                                node.Expand();
                                break;
                            }
                        }
                    }
                    else
                    {
                        // Find in child nodes
                        bool found = false;
                        foreach (TreeNode childNode in node.Nodes)
                        {
                            string nodePath = childNode.Tag?.ToString();
                            if (nodePath != null && nodePath.Equals(segment, StringComparison.OrdinalIgnoreCase))
                            {
                                node = childNode;
                                node.Expand();
                                found = true;
                                break;
                            }
                        }

                        if (!found)
                        {
                            // If not found, we need to expand the node to load its children
                            node.Expand();

                            // Try again after expansion
                            foreach (TreeNode childNode in node.Nodes)
                            {
                                string nodePath = childNode.Tag?.ToString();
                                if (nodePath != null && nodePath.Equals(segment, StringComparison.OrdinalIgnoreCase))
                                {
                                    node = childNode;
                                    node.Expand();
                                    break;
                                }
                            }
                        }
                    }
                }

                // Select the final node
                if (node != null && node.Tag?.ToString() == path)
                {
                    _treeView.SelectedNode = node;
                    node.EnsureVisible();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error selecting path: {ex.Message}");
            }
        }

        /// <summary>
        /// Sets up enhanced multi-selection functionality
        /// </summary>
        private void SetupMultiSelection()
        {
            // We'll use a KeyDown event to handle keyboard shortcuts
            _treeView.KeyDown += TreeView_KeyDown;
        }

        /// <summary>
        /// Handles key down events for multi-selection keyboard shortcuts
        /// </summary>
        private void TreeView_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.A && e.Control)
            {
                // Ctrl+A to select all visible nodes
                SelectAllVisibleNodes();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                // Escape to clear selection
                ClearSelectedNodes();
                e.Handled = true;
            }
        }

        /// <summary>
        /// Selects all visible nodes in the tree
        /// </summary>
        private void SelectAllVisibleNodes()
        {
            // Clear current selection
            ClearSelectedNodes();

            // Select all visible nodes
            var visibleNodes = GetAllVisibleNodes();
            foreach (var node in visibleNodes)
            {
                _selectedNodes.Add(node);
                node.BackColor = SystemColors.Highlight;
                node.ForeColor = SystemColors.HighlightText;
            }

            // Notify about multi-selection
            if (_selectedNodes.Count > 0)
            {
                var selectedPaths = _selectedNodes
                    .Select(n => n.Tag?.ToString())
                    .Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p))
                    .ToList();

                DirectoriesSelected?.Invoke(this, selectedPaths);
            }
        }

        /// <summary>
        /// Gets all visible nodes in the tree
        /// </summary>
        private List<TreeNode> GetAllVisibleNodes()
        {
            var nodes = new List<TreeNode>();
            CollectVisibleNodes(_treeView.Nodes, nodes);
            return nodes;
        }

        /// <summary>
        /// Recursively collects all visible nodes
        /// </summary>
        private void CollectVisibleNodes(TreeNodeCollection nodeCollection, List<TreeNode> nodes)
        {
            foreach (TreeNode node in nodeCollection)
            {
                nodes.Add(node);

                if (node.IsExpanded && node.Nodes.Count > 0)
                {
                    CollectVisibleNodes(node.Nodes, nodes);
                }
            }
        }

     
        /// <summary>
        /// Handles mouse click for multi-selection
        /// </summary>
        private void TreeView_NodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
                return;

            if (_multiSelect)
            {
                if (Control.ModifierKeys == Keys.Control)
                {
                    // Ctrl+Click toggles selection of the clicked node
                    if (_selectedNodes.Contains(e.Node))
                    {
                        // Deselect the node
                        _selectedNodes.Remove(e.Node);
                        e.Node.BackColor = _treeView.BackColor;
                        e.Node.ForeColor = _treeView.ForeColor;
                    }
                    else
                    {
                        // Select the node
                        _selectedNodes.Add(e.Node);
                        e.Node.BackColor = SystemColors.Highlight;
                        e.Node.ForeColor = SystemColors.HighlightText;

                        // Update last selected node
                        _lastSelectedNode = e.Node;
                    }
                }
                else if (Control.ModifierKeys == Keys.Shift && _lastSelectedNode != null)
                {
                    // Shift+Click selects a range of nodes
                    SelectNodeRange(_lastSelectedNode, e.Node);
                }
                else
                {
                    // Regular click, clear selection and select just this node
                    ClearSelectedNodes();

                    _selectedNodes.Add(e.Node);
                    e.Node.BackColor = SystemColors.Highlight;
                    e.Node.ForeColor = SystemColors.HighlightText;

                    // Update last selected node
                    _lastSelectedNode = e.Node;
                }

                // Notify about selection
                var selectedPaths = _selectedNodes
                    .Select(n => n.Tag?.ToString())
                    .Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p))
                    .ToList();

                if (selectedPaths.Count > 0)
                {
                    DirectoriesSelected?.Invoke(this, selectedPaths);
                }

                // Also update the TreeView's selected node to the clicked node
                _treeView.SelectedNode = e.Node;

                // Force normal selection logic to be skipped
                _treeView.SelectedNode = null;
            }
            else
            {
                // In single selection mode, just select the node normally
                _treeView.SelectedNode = e.Node;
            }
        }

        /// <summary>
        /// Selects a range of nodes between start and end
        /// </summary>
        private void SelectNodeRange(TreeNode start, TreeNode end)
        {
            // First, get all visible nodes
            var allNodes = GetAllVisibleNodes();

            // Find indices of start and end nodes
            int startIndex = allNodes.IndexOf(start);
            int endIndex = allNodes.IndexOf(end);

            if (startIndex == -1 || endIndex == -1)
                return;

            // Ensure startIndex <= endIndex
            if (startIndex > endIndex)
            {
                int temp = startIndex;
                startIndex = endIndex;
                endIndex = temp;
            }

            // Clear current selection
            ClearSelectedNodes();

            // Select all nodes in the range
            for (int i = startIndex; i <= endIndex; i++)
            {
                var node = allNodes[i];
                _selectedNodes.Add(node);
                node.BackColor = SystemColors.Highlight;
                node.ForeColor = SystemColors.HighlightText;
            }
        }

        /// <summary>
        /// Gets the selected folder infos for operations
        /// </summary>
        public List<FolderInfo> GetSelectedFolderInfos()
        {
            var folderInfos = new List<FolderInfo>();

            foreach (var node in _selectedNodes)
            {
                string path = node.Tag as string;
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                {
                    folderInfos.Add(new FolderInfo(path));
                }
            }

            return folderInfos;
        }
    }

    public class FolderDropEventArgs : EventArgs
    {
        public string[] SourcePaths { get; }
        public string TargetPath { get; }
        public bool IsCopy { get; }

        public FolderDropEventArgs(string[] sourcePaths, string targetPath, bool isCopy)
        {
            SourcePaths = sourcePaths;
            TargetPath = targetPath;
            IsCopy = isCopy;
        }
    }

    // Extension for TreeView to support multi-selection
    public static class TreeViewExtensions
    {
        public static List<TreeNode> SelectedNodes(this TreeView treeView)
        {
            return GetOrCreateSelectedNodes(treeView);
        }

        private static List<TreeNode> GetOrCreateSelectedNodes(TreeView treeView)
        {
            // Use the Tag property to store our selected nodes collection
            if (treeView.Tag == null || !(treeView.Tag is List<TreeNode>))
            {
                treeView.Tag = new List<TreeNode>();
            }

            return (List<TreeNode>)treeView.Tag;
        }
    }
}