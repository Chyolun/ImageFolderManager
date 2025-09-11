using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ImageFolderManager.Models;
using ImageFolderManager.ViewModels;
using ImageFolderManager.Services;
using ImageFolderManager.Controls;
using static ImageFolderManager.Controls.ShellTreeView;

namespace ImageFolderManager.Diagnostics
{
    /// <summary>
    /// Debug monitor for TreeView refresh issues
    /// Tracks the complete event chain from folder operations to UI updates
    /// </summary>
    public static class TreeViewRefreshDebugger
    {
        private static readonly List<DebugLogEntry> _debugLog = new List<DebugLogEntry>();
        private static readonly object _logLock = new object();
        private static bool _isEnabled = false;
        private static MainViewModel _mainViewModel;
        private static FolderOperationsViewModel _folderOpsViewModel;
        private static ShellTreeView _shellTreeView;
        private static UnifiedFolderService _folderService;

        #region Debug Log Entry Model

        public class DebugLogEntry
        {
            public DateTime Timestamp { get; set; }
            public string Component { get; set; }
            public string Event { get; set; }
            public string Details { get; set; }
            public string ThreadId { get; set; }
            public bool IsUIThread { get; set; }
            public Exception Exception { get; set; }

            public override string ToString()
            {
                var threadInfo = IsUIThread ? "UI" : "BG";
                var exceptionInfo = Exception != null ? $" [ERROR: {Exception.Message}]" : "";
                return $"[{Timestamp:HH:mm:ss.fff}] [{threadInfo}:{ThreadId}] {Component}.{Event}: {Details}{exceptionInfo}";
            }
        }

        #endregion

        #region Public Interface

        /// <summary>
        /// Initialize debug monitoring for TreeView refresh issues
        /// </summary>
        /// <param name="mainViewModel">Main view model instance</param>
        /// <param name="folderOpsViewModel">Folder operations view model</param>
        /// <param name="shellTreeView">Shell tree view control</param>
        /// <param name="folderService">Unified folder service</param>
        public static void Initialize(
            MainViewModel mainViewModel,
            FolderOperationsViewModel folderOpsViewModel,
            ShellTreeView shellTreeView,
            UnifiedFolderService folderService)
        {
            if (_isEnabled) return;

            _mainViewModel = mainViewModel;
            _folderOpsViewModel = folderOpsViewModel;
            _shellTreeView = shellTreeView;
            _folderService = folderService;

            AttachEventHandlers();
            LogEvent("TreeViewRefreshDebugger", "Initialize", "Debug monitoring started");
            _isEnabled = true;
        }

        /// <summary>
        /// Stop debug monitoring and save logs
        /// </summary>
        public static void Shutdown()
        {
            if (!_isEnabled) return;

            DetachEventHandlers();
            SaveDebugLog();
            LogEvent("TreeViewRefreshDebugger", "Shutdown", "Debug monitoring stopped");
            _isEnabled = false;
        }

        /// <summary>
        /// Save current debug log to file
        /// </summary>
        public static void SaveDebugLog()
        {
            lock (_logLock)
            {
                try
                {
                    var logPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                        $"TreeViewDebug_{DateTime.Now:yyyyMMdd_HHmmss}.log");

                    using (var writer = new StreamWriter(logPath))
                    {
                        writer.WriteLine("=== TreeView Refresh Debug Log ===");
                        writer.WriteLine($"Generated: {DateTime.Now}");
                        writer.WriteLine($"Total Entries: {_debugLog.Count}");
                        writer.WriteLine();

                        foreach (var entry in _debugLog)
                        {
                            writer.WriteLine(entry.ToString());
                            if (entry.Exception != null)
                            {
                                writer.WriteLine($"    Stack Trace: {entry.Exception.StackTrace}");
                            }
                        }
                    }

                    LogEvent("TreeViewRefreshDebugger", "SaveDebugLog", $"Log saved to: {logPath}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to save debug log: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Clear debug log entries
        /// </summary>
        public static void ClearLog()
        {
            lock (_logLock)
            {
                _debugLog.Clear();
                LogEvent("TreeViewRefreshDebugger", "ClearLog", "Debug log cleared");
            }
        }

        /// <summary>
        /// Get current debug log entries (thread-safe copy)
        /// </summary>
        public static List<DebugLogEntry> GetCurrentLog()
        {
            lock (_logLock)
            {
                return new List<DebugLogEntry>(_debugLog);
            }
        }

        #endregion

        #region Event Handler Attachment

        private static void AttachEventHandlers()
        {
            LogEvent("TreeViewRefreshDebugger", "AttachEventHandlers", "Attaching event handlers");

            // Monitor FolderOperationsViewModel events
            if (_folderOpsViewModel != null)
            {
                _folderOpsViewModel.FolderOperationCompleted += OnFolderOperationCompleted;
                _folderOpsViewModel.StatusMessageChanged += OnFolderOpsStatusChanged;
                LogEvent("FolderOperationsViewModel", "EventSubscription", "Subscribed to FolderOperationCompleted and StatusMessageChanged");
            }

            // Monitor MainViewModel TreeView refresh commands
            if (_mainViewModel != null)
            {
                // Note: We would need to modify MainViewModel to expose events for complete monitoring
                LogEvent("MainViewModel", "EventSubscription", "MainViewModel monitoring enabled (manual hooks required)");
            }

            // Monitor UnifiedFolderService events (if available)
            if (_folderService != null)
            {
                // Note: Need to check if UnifiedFolderService exposes relevant events
                LogEvent("UnifiedFolderService", "EventSubscription", "Service monitoring enabled (manual hooks required)");
            }
        }

        private static void DetachEventHandlers()
        {
            LogEvent("TreeViewRefreshDebugger", "DetachEventHandlers", "Detaching event handlers");

            if (_folderOpsViewModel != null)
            {
                _folderOpsViewModel.FolderOperationCompleted -= OnFolderOperationCompleted;
                _folderOpsViewModel.StatusMessageChanged -= OnFolderOpsStatusChanged;
            }
        }

        #endregion

        #region Event Handlers

        private static void OnFolderOperationCompleted(object sender, FolderOperationEventArgs e)
        {
            LogEvent("FolderOperationsViewModel", "FolderOperationCompleted",
                $"Operation: {e.Operation}, Source: {e.SourcePath}, Dest: {e.DestinationPath}, Success: {e.Success}, IsUndo: {e.IsUndoOperation}");

            // Check if MainViewModel should receive this event
            if (_mainViewModel != null)
            {
                LogEvent("DebuggerCheck", "MainViewModelEventCheck",
                    "Checking if MainViewModel OnFolderOperationCompleted will be called...");

                // This should trigger MainViewModel.OnFolderOperationCompleted
                // We can't directly monitor this without modifying MainViewModel
                LogEvent("DebuggerCheck", "ExpectedFlow",
                    "Expected: MainViewModel.OnFolderOperationCompleted -> HandleFolderOperationCompletedAsync -> ShellTreeView.RefreshTreeIncremental");
            }
        }

        private static void OnFolderOpsStatusChanged(object sender, string status)
        {
            LogEvent("FolderOperationsViewModel", "StatusMessageChanged", $"Status: {status}");
        }

        #endregion

        #region Manual Monitoring Methods (to be called from modified ViewModels)

        /// <summary>
        /// Call this from MainViewModel.OnFolderOperationCompleted
        /// </summary>
        public static void TrackMainViewModelEventReceived(FolderOperationEventArgs e)
        {
            LogEvent("MainViewModel", "OnFolderOperationCompleted",
                $"Event received - Operation: {e.Operation}, Source: {e.SourcePath}, Success: {e.Success}");
        }

        /// <summary>
        /// Call this from MainViewModel.HandleFolderOperationCompletedAsync (start)
        /// </summary>
        public static void TrackHandleFolderOperationStart(FolderOperationEventArgs e)
        {
            LogEvent("MainViewModel", "HandleFolderOperationStart",
                $"Starting async handler for {e.Operation}");
        }

        /// <summary>
        /// Call this from MainViewModel.HandleFolderOperationCompletedAsync (end)
        /// </summary>
        public static void TrackHandleFolderOperationEnd(FolderOperationEventArgs e, bool success, string error = null)
        {
            LogEvent("MainViewModel", "HandleFolderOperationEnd",
                $"Completed async handler for {e.Operation}, Success: {success}, Error: {error}");
        }

        /// <summary>
        /// Call this from MainViewModel.ExecuteFolderOperationOnUIThread (start)
        /// </summary>
        public static void TrackUIThreadExecutionStart(FolderOperationEventArgs e)
        {
            LogEvent("MainViewModel", "ExecuteFolderOperationOnUIThread_Start",
                $"Starting UI thread execution for {e.Operation}");
        }

        /// <summary>
        /// Call this from MainViewModel.ExecuteFolderOperationOnUIThread (before TreeView call)
        /// </summary>
        public static void TrackBeforeTreeViewRefresh(FolderOperationEventArgs e, FolderOperationType mappedType)
        {
            LogEvent("MainViewModel", "BeforeTreeViewRefresh",
                $"About to call ShellTreeView.RefreshTreeIncremental - Operation: {mappedType}, Source: {e.SourcePath}, Dest: {e.DestinationPath}");
        }

        /// <summary>
        /// Call this from MainViewModel.ExecuteFolderOperationOnUIThread (after TreeView call)
        /// </summary>
        public static void TrackAfterTreeViewRefresh(FolderOperationEventArgs e, bool success, string error = null)
        {
            LogEvent("MainViewModel", "AfterTreeViewRefresh",
                $"Completed ShellTreeView.RefreshTreeIncremental - Success: {success}, Error: {error}");
        }

        /// <summary>
        /// Call this from ShellTreeView.RefreshTreeIncremental (start)
        /// </summary>
        public static void TrackTreeViewRefreshStart(FolderOperationType operationType, string sourcePath, string destinationPath)
        {
            LogEvent("ShellTreeView", "RefreshTreeIncremental_Start",
                $"Operation: {operationType}, Source: {sourcePath}, Dest: {destinationPath}");
        }

        /// <summary>
        /// Call this from ShellTreeView.RefreshTreeIncremental (end)
        /// </summary>
        public static void TrackTreeViewRefreshEnd(FolderOperationType operationType, bool success, string error = null)
        {
            LogEvent("ShellTreeView", "RefreshTreeIncremental_End",
                $"Operation: {operationType}, Success: {success}, Error: {error}");
        }

        /// <summary>
        /// Call this from ShellTreeView refresh operation handlers
        /// </summary>
        public static void TrackTreeViewOperation(string operationName, string details, bool success = true, Exception exception = null)
        {
            LogEvent("ShellTreeView", operationName, details, exception);
        }

        /// <summary>
        /// Call this from UnifiedFolderService when processing folder changes
        /// </summary>
        public static void TrackFolderServiceOperation(string operationName, string details, Exception exception = null)
        {
            LogEvent("UnifiedFolderService", operationName, details, exception);
        }

        /// <summary>
        /// Track TreeView state validation
        /// </summary>
        public static void TrackTreeViewStateValidation(string context, bool isValid, string reason = null)
        {
            LogEvent("ShellTreeView", "StateValidation",
                $"Context: {context}, Valid: {isValid}, Reason: {reason}");
        }

        /// <summary>
        /// Track path-to-TreeViewItem mapping issues
        /// </summary>
        public static void TrackPathMappingIssue(string path, string issue)
        {
            LogEvent("ShellTreeView", "PathMappingIssue",
                $"Path: {path}, Issue: {issue}");
        }

        #endregion

        #region Utility Methods

        private static void LogEvent(string component, string eventName, string details, Exception exception = null)
        {
            if (!_isEnabled) return;

            var entry = new DebugLogEntry
            {
                Timestamp = DateTime.Now,
                Component = component,
                Event = eventName,
                Details = details,
                ThreadId = Thread.CurrentThread.ManagedThreadId.ToString(),
                IsUIThread = Application.Current?.Dispatcher?.CheckAccess() ?? false,
                Exception = exception
            };

            lock (_logLock)
            {
                _debugLog.Add(entry);

                // Prevent memory issues - keep only last 10000 entries
                if (_debugLog.Count > 10000)
                {
                    _debugLog.RemoveRange(0, 1000);
                }
            }

            // Also write to Debug output for immediate visibility
            Debug.WriteLine(entry.ToString());
        }

        /// <summary>
        /// Create a debug checkpoint - useful for marking specific test scenarios
        /// </summary>
        public static void CreateCheckpoint(string checkpointName, string description = "")
        {
            LogEvent("DebugCheckpoint", checkpointName,
                $"=== CHECKPOINT: {description} ===");
        }

        /// <summary>
        /// Monitor specific folder operation - call before performing an operation
        /// </summary>
        public static void BeginOperationMonitoring(string operationType, string sourcePath, string destPath = null)
        {
            CreateCheckpoint($"BeginOperation_{operationType}",
                $"Starting {operationType} - Source: {sourcePath}, Dest: {destPath}");
        }

        /// <summary>
        /// End operation monitoring - call after operation should be complete
        /// </summary>
        public static void EndOperationMonitoring(string operationType, string expectedResult)
        {
            CreateCheckpoint($"EndOperation_{operationType}",
                $"Expected result: {expectedResult}");

            // Delay slightly to capture any delayed events
            Task.Delay(1000).ContinueWith(_ => {
                CreateCheckpoint($"DelayedCheck_{operationType}",
                    "Checking for delayed events/updates");
            });
        }

        #endregion
    }

    #region Usage Instructions

   

    #endregion
}