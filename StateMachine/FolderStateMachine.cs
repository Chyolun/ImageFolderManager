using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ImageFolderManager.Services;

namespace ImageFolderManager.StateMachine
{
    /// <summary>
    /// State machine for managing folder states and transitions
    /// </summary>
    public class FolderStateMachine : IDisposable
    {
        private readonly ConcurrentDictionary<string, FolderStateInfo> _folderStates;
        private readonly Timer _cleanupTimer;
        private readonly SemaphoreSlim _transitionSemaphore;
        private bool _disposed = false;

        // Events for state changes
        public event EventHandler<FolderStateChangedEventArgs> StateChanged;

        // Configuration
        private readonly TimeSpan _cleanupInterval = TimeSpan.FromMinutes(5);
        private readonly TimeSpan _staleStateTimeout = TimeSpan.FromHours(1);

        public FolderStateMachine()
        {
            _folderStates = new ConcurrentDictionary<string, FolderStateInfo>(StringComparer.OrdinalIgnoreCase);
            _transitionSemaphore = new SemaphoreSlim(1, 1);

            // Setup cleanup timer for stale states
            _cleanupTimer = new Timer(CleanupStaleStates, null, _cleanupInterval, _cleanupInterval);
        }

        /// <summary>
        /// Transition a folder to a new state
        /// </summary>
        public async Task<bool> TransitionStateAsync(string path, FolderState newState, string operationId = null)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(FolderStateMachine));

            if (string.IsNullOrWhiteSpace(path))
                return false;

            var normalizedPath = PathService.NormalizePath(path);

            await _transitionSemaphore.WaitAsync();
            try
            {
                var stateInfo = _folderStates.GetOrAdd(normalizedPath, p => new FolderStateInfo(p));

                // Check if transition is valid
                if (!IsValidTransition(stateInfo.CurrentState, newState))
                {
                    Debug.WriteLine($"Invalid state transition for {normalizedPath}: {stateInfo.CurrentState} → {newState}");
                    return false;
                }

                var oldState = stateInfo.CurrentState;

                // Update state information
                stateInfo.PreviousState = stateInfo.CurrentState;
                stateInfo.CurrentState = newState;
                stateInfo.LastStateChange = DateTime.Now;
                stateInfo.TransitionCount++;

                if (!string.IsNullOrEmpty(operationId))
                {
                    stateInfo.OperationId = operationId;
                }

                // Clear error message on successful transition away from error state
                if (oldState == FolderState.Error && newState != FolderState.Error)
                {
                    stateInfo.ErrorMessage = null;
                }

                Debug.WriteLine($"State transition: {normalizedPath} {oldState} → {newState}");

                // Fire state changed event
                StateChanged?.Invoke(this, new FolderStateChangedEventArgs(normalizedPath, oldState, newState, operationId));

                return true;
            }
            finally
            {
                _transitionSemaphore.Release();
            }
        }

        /// <summary>
        /// Get the current state of a folder
        /// </summary>
        public FolderState GetFolderState(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return FolderState.Available;

            var normalizedPath = PathService.NormalizePath(path);
            return _folderStates.TryGetValue(normalizedPath, out var stateInfo)
                ? stateInfo.CurrentState
                : FolderState.Available;
        }

        /// <summary>
        /// Get detailed state information for a folder
        /// </summary>
        public FolderStateInfo GetFolderStateInfo(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            var normalizedPath = PathService.NormalizePath(path);
            return _folderStates.TryGetValue(normalizedPath, out var stateInfo)
                ? stateInfo
                : null;
        }

        /// <summary>
        /// Set error state for a folder with error message
        /// </summary>
        public async Task<bool> SetErrorStateAsync(string path, string errorMessage, string operationId = null)
        {
            var result = await TransitionStateAsync(path, FolderState.Error, operationId);

            if (result)
            {
                var normalizedPath = PathService.NormalizePath(path);
                if (_folderStates.TryGetValue(normalizedPath, out var stateInfo))
                {
                    stateInfo.ErrorMessage = errorMessage;
                }
            }

            return result;
        }

        /// <summary>
        /// Remove a folder from the state machine
        /// </summary>
        public async Task<bool> RemoveFolderAsync(string path)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(FolderStateMachine));

            if (string.IsNullOrWhiteSpace(path))
                return false;

            var normalizedPath = PathService.NormalizePath(path);

            await _transitionSemaphore.WaitAsync();
            try
            {
                var removed = _folderStates.TryRemove(normalizedPath, out var stateInfo);

                if (removed)
                {
                    Debug.WriteLine($"Removed folder from state machine: {normalizedPath}");

                    // Fire state changed event indicating deletion
                    StateChanged?.Invoke(this, new FolderStateChangedEventArgs(
                        normalizedPath, stateInfo.CurrentState, FolderState.Deleted, null));
                }

                return removed;
            }
            finally
            {
                _transitionSemaphore.Release();
            }
        }

        /// <summary>
        /// Check if all folders in the given paths are available (not processing)
        /// </summary>
        public bool AreAllFoldersAvailable(params string[] paths)
        {
            if (paths == null || paths.Length == 0)
                return true;

            return paths.All(path =>
            {
                var state = GetFolderState(path);
                return state == FolderState.Available || state == FolderState.Monitoring;
            });
        }

        /// <summary>
        /// Get all folders in a specific state
        /// </summary>
        public string[] GetFoldersInState(FolderState state)
        {
            return _folderStates.Values
                .Where(info => info.CurrentState == state)
                .Select(info => info.Path)
                .ToArray();
        }

        /// <summary>
        /// Get count of folders in each state
        /// </summary>
        public StateStatistics GetStateStatistics()
        {
            var stats = new StateStatistics();

            foreach (var stateInfo in _folderStates.Values)
            {
                switch (stateInfo.CurrentState)
                {
                    case FolderState.Available:
                        stats.AvailableCount++;
                        break;
                    case FolderState.Processing:
                        stats.ProcessingCount++;
                        break;
                    case FolderState.Error:
                        stats.ErrorCount++;
                        break;
                    case FolderState.Monitoring:
                        stats.MonitoringCount++;
                        break;
                    case FolderState.Deleted:
                        stats.DeletedCount++;
                        break;
                }
            }

            stats.TotalCount = _folderStates.Count;
            return stats;
        }

        /// <summary>
        /// Clear all folder states
        /// </summary>
        public async Task ClearAllStatesAsync()
        {
            await _transitionSemaphore.WaitAsync();
            try
            {
                _folderStates.Clear();
                Debug.WriteLine("Cleared all folder states");
            }
            finally
            {
                _transitionSemaphore.Release();
            }
        }

        /// <summary>
        /// Check if a state transition is valid
        /// </summary>
        private bool IsValidTransition(FolderState currentState, FolderState newState)
        {
            // Define valid state transitions
            return (currentState, newState) switch
            {
                // From Available
                (FolderState.Available, FolderState.Processing) => true,
                (FolderState.Available, FolderState.Monitoring) => true,
                (FolderState.Available, FolderState.Error) => true,
                (FolderState.Available, FolderState.Deleted) => true,

                // From Processing
                (FolderState.Processing, FolderState.Available) => true,
                (FolderState.Processing, FolderState.Error) => true,
                (FolderState.Processing, FolderState.Deleted) => true,

                // From Error
                (FolderState.Error, FolderState.Available) => true,
                (FolderState.Error, FolderState.Processing) => true,
                (FolderState.Error, FolderState.Deleted) => true,

                // From Monitoring
                (FolderState.Monitoring, FolderState.Available) => true,
                (FolderState.Monitoring, FolderState.Processing) => true,
                (FolderState.Monitoring, FolderState.Error) => true,
                (FolderState.Monitoring, FolderState.Deleted) => true,

                // From Deleted (limited transitions)
                (FolderState.Deleted, FolderState.Available) => true, // If folder is recreated

                // Same state (no-op)
                var (current, target) when current == target => true,

                // All other transitions are invalid
                _ => false
            };
        }

        /// <summary>
        /// Clean up stale states periodically
        /// </summary>
        private void CleanupStaleStates(object state)
        {
            try
            {
                var cutoffTime = DateTime.Now - _staleStateTimeout;
                var staleKeys = _folderStates
                    .Where(kvp => kvp.Value.LastStateChange < cutoffTime &&
                                 kvp.Value.CurrentState != FolderState.Processing)
                    .Select(kvp => kvp.Key)
                    .ToArray();

                foreach (var key in staleKeys)
                {
                    _folderStates.TryRemove(key, out _);
                }

                if (staleKeys.Length > 0)
                {
                    Debug.WriteLine($"Cleaned up {staleKeys.Length} stale folder states");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during state cleanup: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _cleanupTimer?.Dispose();
                _transitionSemaphore?.Dispose();
                _disposed = true;
            }
        }
    }

    /// <summary>
    /// Event arguments for state change notifications
    /// </summary>
    public class FolderStateChangedEventArgs : EventArgs
    {
        public string Path { get; }
        public FolderState OldState { get; }
        public FolderState NewState { get; }
        public string OperationId { get; }
        public DateTime Timestamp { get; }

        public FolderStateChangedEventArgs(string path, FolderState oldState, FolderState newState, string operationId)
        {
            Path = path;
            OldState = oldState;
            NewState = newState;
            OperationId = operationId;
            Timestamp = DateTime.Now;
        }
    }

    /// <summary>
    /// Statistics about folder states
    /// </summary>
    public class StateStatistics
    {
        public int TotalCount { get; set; }
        public int AvailableCount { get; set; }
        public int ProcessingCount { get; set; }
        public int ErrorCount { get; set; }
        public int MonitoringCount { get; set; }
        public int DeletedCount { get; set; }
    }
}