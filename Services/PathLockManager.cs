using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ImageFolderManager.Services
{
    /// <summary>
    /// Path-level lock manager for preventing concurrent operations on the same paths
    /// </summary>
    public class PathLockManager : IDisposable
    {
        private readonly ConcurrentDictionary<string, PathLockEntry> _pathLocks;
        private readonly SemaphoreSlim _lockManagerSemaphore;
        private readonly Timer _deadlockDetectionTimer;
        private bool _disposed = false;

        // Configuration
        private readonly TimeSpan _lockTimeout = TimeSpan.FromMinutes(5);
        private readonly TimeSpan _deadlockCheckInterval = TimeSpan.FromSeconds(30);

        public PathLockManager()
        {
            _pathLocks = new ConcurrentDictionary<string, PathLockEntry>(StringComparer.OrdinalIgnoreCase);
            _lockManagerSemaphore = new SemaphoreSlim(1, 1);

            // Setup deadlock detection timer
            _deadlockDetectionTimer = new Timer(DetectAndResolveDeadlocks, null,
                _deadlockCheckInterval, _deadlockCheckInterval);
        }

        /// <summary>
        /// Acquire locks for multiple paths with deadlock prevention
        /// </summary>
        public async Task<PathLockToken> AcquireLocksAsync(string[] paths, CancellationToken cancellationToken = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PathLockManager));

            if (paths == null || paths.Length == 0)
                return new PathLockToken(new string[0], this);

            // Normalize and deduplicate paths, sort to prevent deadlocks
            var normalizedPaths = paths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(PathService.NormalizePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (normalizedPaths.Length == 0)
                return new PathLockToken(new string[0], this);

            var lockId = Guid.NewGuid().ToString();
            var lockedPaths = new List<string>();

            try
            {
                Debug.WriteLine($"[{lockId}] Acquiring locks for {normalizedPaths.Length} paths");

                // Acquire locks in sorted order to prevent deadlocks
                foreach (var path in normalizedPaths)
                {
                    await AcquireSinglePathLockAsync(path, lockId, cancellationToken);
                    lockedPaths.Add(path);
                }

                Debug.WriteLine($"[{lockId}] Successfully acquired all locks");
                return new PathLockToken(lockedPaths.ToArray(), this, lockId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[{lockId}] Failed to acquire locks: {ex.Message}");

                // Release any locks we managed to acquire
                await ReleaseLocksByIdAsync(lockId);
                throw;
            }
        }

        /// <summary>
        /// Acquire lock for a single path
        /// </summary>
        private async Task AcquireSinglePathLockAsync(string path, string lockId, CancellationToken cancellationToken)
        {
            var timeout = DateTime.Now.Add(_lockTimeout);

            while (DateTime.Now < timeout && !cancellationToken.IsCancellationRequested)
            {
                await _lockManagerSemaphore.WaitAsync(cancellationToken);

                try
                {
                    var lockEntry = _pathLocks.GetOrAdd(path, p => new PathLockEntry(p));

                    if (lockEntry.TryAcquire(lockId))
                    {
                        Debug.WriteLine($"[{lockId}] Acquired lock for path: {path}");
                        return;
                    }

                    // Check if the current lock holder is still active
                    if (DateTime.Now - lockEntry.AcquiredAt > _lockTimeout)
                    {
                        Debug.WriteLine($"[{lockId}] Forcibly releasing expired lock for path: {path}");
                        lockEntry.Release();

                        if (lockEntry.TryAcquire(lockId))
                        {
                            Debug.WriteLine($"[{lockId}] Acquired lock after timeout for path: {path}");
                            return;
                        }
                    }
                }
                finally
                {
                    _lockManagerSemaphore.Release();
                }

                // Wait a bit before retrying
                await Task.Delay(50, cancellationToken);
            }

            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException();

            throw new TimeoutException($"Failed to acquire lock for path '{path}' within timeout period");
        }

        /// <summary>
        /// Release locks by lock ID
        /// </summary>
        internal async Task ReleaseLocksByIdAsync(string lockId)
        {
            if (_disposed || string.IsNullOrEmpty(lockId))
                return;

            await _lockManagerSemaphore.WaitAsync();

            try
            {
                var releasedPaths = new List<string>();

                foreach (var kvp in _pathLocks)
                {
                    if (kvp.Value.LockId == lockId)
                    {
                        kvp.Value.Release();
                        releasedPaths.Add(kvp.Key);

                        // Remove unused lock entries
                        if (!kvp.Value.IsLocked)
                        {
                            _pathLocks.TryRemove(kvp.Key, out _);
                        }
                    }
                }

                if (releasedPaths.Count > 0)
                {
                    Debug.WriteLine($"[{lockId}] Released locks for {releasedPaths.Count} paths");
                }
            }
            finally
            {
                _lockManagerSemaphore.Release();
            }
        }

        /// <summary>
        /// Release locks for specific paths
        /// </summary>
        internal async Task ReleasePathsAsync(string[] paths, string lockId)
        {
            if (_disposed || paths == null || paths.Length == 0)
                return;

            await _lockManagerSemaphore.WaitAsync();

            try
            {
                foreach (var path in paths)
                {
                    var normalizedPath = PathService.NormalizePath(path);

                    if (_pathLocks.TryGetValue(normalizedPath, out var lockEntry) &&
                        lockEntry.LockId == lockId)
                    {
                        lockEntry.Release();

                        if (!lockEntry.IsLocked)
                        {
                            _pathLocks.TryRemove(normalizedPath, out _);
                        }
                    }
                }
            }
            finally
            {
                _lockManagerSemaphore.Release();
            }
        }

        /// <summary>
        /// Check if a path is currently locked
        /// </summary>
        public bool IsPathLocked(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            var normalizedPath = PathService.NormalizePath(path);
            return _pathLocks.TryGetValue(normalizedPath, out var lockEntry) && lockEntry.IsLocked;
        }

        /// <summary>
        /// Get information about all current locks
        /// </summary>
        public LockManagerStatistics GetStatistics()
        {
            var stats = new LockManagerStatistics
            {
                TotalPaths = _pathLocks.Count,
                LockedPaths = _pathLocks.Values.Count(entry => entry.IsLocked),
                ActiveLockIds = _pathLocks.Values.Where(entry => entry.IsLocked).Select(entry => entry.LockId).Distinct().Count()
            };

            return stats;
        }

        /// <summary>
        /// Detect and resolve potential deadlocks
        /// </summary>
        private void DetectAndResolveDeadlocks(object state)
        {
            try
            {
                var expiredLocks = _pathLocks.Values
                    .Where(entry => entry.IsLocked && DateTime.Now - entry.AcquiredAt > _lockTimeout)
                    .ToArray();

                if (expiredLocks.Length > 0)
                {
                    Debug.WriteLine($"Detected {expiredLocks.Length} expired locks, forcing release");

                    Task.Run(async () =>
                    {
                        await _lockManagerSemaphore.WaitAsync();
                        try
                        {
                            foreach (var lockEntry in expiredLocks)
                            {
                                lockEntry.Release();
                            }

                            // Clean up unused entries
                            var keysToRemove = _pathLocks.Where(kvp => !kvp.Value.IsLocked).Select(kvp => kvp.Key).ToArray();
                            foreach (var key in keysToRemove)
                            {
                                _pathLocks.TryRemove(key, out _);
                            }
                        }
                        finally
                        {
                            _lockManagerSemaphore.Release();
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during deadlock detection: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _deadlockDetectionTimer?.Dispose();
                _lockManagerSemaphore?.Dispose();
                _disposed = true;
            }
        }
    }

    /// <summary>
    /// Represents a lock entry for a specific path
    /// </summary>
    internal class PathLockEntry
    {
        private readonly object _lockObject = new object();

        public string Path { get; }
        public bool IsLocked { get; private set; }
        public string LockId { get; private set; }
        public DateTime AcquiredAt { get; private set; }

        public PathLockEntry(string path)
        {
            Path = path;
        }

        public bool TryAcquire(string lockId)
        {
            lock (_lockObject)
            {
                if (IsLocked)
                    return false;

                IsLocked = true;
                LockId = lockId;
                AcquiredAt = DateTime.Now;
                return true;
            }
        }

        public void Release()
        {
            lock (_lockObject)
            {
                IsLocked = false;
                LockId = null;
                AcquiredAt = default;
            }
        }
    }

    /// <summary>
    /// Token representing acquired locks that automatically releases on disposal
    /// </summary>
    public class PathLockToken : IDisposable
    {
        private readonly PathLockManager _lockManager;
        private readonly string[] _lockedPaths;
        private readonly string _lockId;
        private bool _disposed = false;

        internal PathLockToken(string[] lockedPaths, PathLockManager lockManager, string lockId = null)
        {
            _lockedPaths = lockedPaths ?? new string[0];
            _lockManager = lockManager;
            _lockId = lockId ?? Guid.NewGuid().ToString();
        }

        public string[] LockedPaths => _lockedPaths.ToArray();
        public string LockId => _lockId;
        public int PathCount => _lockedPaths.Length;

        public void Dispose()
        {
            if (!_disposed && _lockManager != null)
            {
                Task.Run(async () => await _lockManager.ReleaseLocksByIdAsync(_lockId));
                _disposed = true;
            }
        }
    }

    /// <summary>
    /// Statistics about the lock manager state
    /// </summary>
    public class LockManagerStatistics
    {
        public int TotalPaths { get; set; }
        public int LockedPaths { get; set; }
        public int ActiveLockIds { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }
}