using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace ImageFolderManager.Services
{
    public enum NodeLoadingState
    {
        NotLoaded,
        Loading,
        Loaded,
        Error,
        Refreshing
    }

    /// <summary>
    /// Manages hierarchical node loading states to prevent race conditions
    /// </summary>
    public class HierarchicalNodeManager : IDisposable
    {
        private readonly ConcurrentDictionary<string, NodeState> _nodeStates = new();
        private bool _disposed = false;

        /// <summary>
        /// Try to transition node to loading state
        /// </summary>
        public async Task<bool> TryTransitionToLoading(string path)
        {
            if (_disposed) return false;

            var normalizedPath = PathNormalizationService.GetCanonicalPath(path);
            var state = _nodeStates.GetOrAdd(normalizedPath, p => new NodeState(p));

            return await state.TryTransitionAsync(NodeLoadingState.NotLoaded, NodeLoadingState.Loading) ||
                   await state.TryTransitionAsync(NodeLoadingState.Error, NodeLoadingState.Loading);
        }

        /// <summary>
        /// Try to transition node to refreshing state
        /// </summary>
        public async Task<bool> TryTransitionToRefreshing(string path)
        {
            if (_disposed) return false;

            var normalizedPath = PathNormalizationService.GetCanonicalPath(path);
            var state = _nodeStates.GetOrAdd(normalizedPath, p => new NodeState(p));

            return await state.TryTransitionAsync(NodeLoadingState.Loaded, NodeLoadingState.Refreshing);
        }

        /// <summary>
        /// Complete loading operation
        /// </summary>
        public async Task CompleteLoading(string path, bool success)
        {
            if (_disposed) return;

            var normalizedPath = PathNormalizationService.GetCanonicalPath(path);
            if (_nodeStates.TryGetValue(normalizedPath, out var state))
            {
                var targetState = success ? NodeLoadingState.Loaded : NodeLoadingState.Error;
                await state.TryTransitionAsync(NodeLoadingState.Loading, targetState);
            }
        }

        /// <summary>
        /// Complete refresh operation
        /// </summary>
        public async Task CompleteRefresh(string path, bool success)
        {
            if (_disposed) return;

            var normalizedPath = PathNormalizationService.GetCanonicalPath(path);
            if (_nodeStates.TryGetValue(normalizedPath, out var state))
            {
                var targetState = success ? NodeLoadingState.Loaded : NodeLoadingState.Error;
                await state.TryTransitionAsync(NodeLoadingState.Refreshing, targetState);
            }
        }

        /// <summary>
        /// Get current state of a node
        /// </summary>
        public NodeLoadingState GetNodeState(string path)
        {
            var normalizedPath = PathNormalizationService.GetCanonicalPath(path);
            return _nodeStates.TryGetValue(normalizedPath, out var state)
                ? state.CurrentState
                : NodeLoadingState.NotLoaded;
        }

        /// <summary>
        /// Reset node state (for error recovery)
        /// </summary>
        public async Task ResetNodeState(string path)
        {
            var normalizedPath = PathNormalizationService.GetCanonicalPath(path);
            if (_nodeStates.TryGetValue(normalizedPath, out var state))
            {
                await state.ForceTransitionAsync(NodeLoadingState.NotLoaded);
            }
        }

        /// <summary>
        /// Remove node state (when folder deleted)
        /// </summary>
        public void RemoveNodeState(string path)
        {
            var normalizedPath = PathNormalizationService.GetCanonicalPath(path);
            _nodeStates.TryRemove(normalizedPath, out _);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var state in _nodeStates.Values)
            {
                state.Dispose();
            }
            _nodeStates.Clear();
        }
    }

    /// <summary>
    /// Individual node state with thread-safe transitions
    /// </summary>
    public class NodeState : IDisposable
    {
        private readonly SemaphoreSlim _transitionLock = new(1, 1);
        private NodeLoadingState _currentState = NodeLoadingState.NotLoaded;
        private readonly string _path;

        public NodeLoadingState CurrentState
        {
            get
            {
                _transitionLock.Wait();
                try
                {
                    return _currentState;
                }
                finally
                {
                    _transitionLock.Release();
                }
            }
        }

        public NodeState(string path)
        {
            _path = path;
        }

        /// <summary>
        /// Try to transition from one state to another
        /// </summary>
        public async Task<bool> TryTransitionAsync(NodeLoadingState from, NodeLoadingState to)
        {
            await _transitionLock.WaitAsync();
            try
            {
                if (_currentState == from)
                {
                    Debug.WriteLine($"Node {_path}: {from} → {to}");
                    _currentState = to;
                    return true;
                }
                Debug.WriteLine($"Node {_path}: Transition {from} → {to} rejected (current: {_currentState})");
                return false;
            }
            finally
            {
                _transitionLock.Release();
            }
        }

        /// <summary>
        /// Force transition (for error recovery)
        /// </summary>
        public async Task ForceTransitionAsync(NodeLoadingState to)
        {
            await _transitionLock.WaitAsync();
            try
            {
                Debug.WriteLine($"Node {_path}: Force transition to {to}");
                _currentState = to;
            }
            finally
            {
                _transitionLock.Release();
            }
        }

        public void Dispose()
        {
            _transitionLock?.Dispose();
        }
    }
}