using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ImageFolderManager.Services
{
    /// <summary>
    /// Processes file system events with deduplication and batching to prevent event storms
    /// </summary>
    public class FileSystemEventProcessor : IDisposable
    {
        // Event deduplication - maps path to latest event
        private readonly ConcurrentDictionary<string, PendingFileSystemEvent> _pendingEvents =
            new ConcurrentDictionary<string, PendingFileSystemEvent>();

        // Batch processing timer
        private readonly Timer _batchProcessor;
        private readonly SemaphoreSlim _processingLock = new SemaphoreSlim(1, 1);

        // Configuration
        private const int BATCH_INTERVAL_MS = 500; // Process batches every 500ms
        private const int MAX_EVENTS_PER_BATCH = 100; // Limit batch size for performance

        // Event handler for processed events
        public event Func<List<ProcessedFileSystemEvent>, Task> EventsProcessed;

        private bool _disposed = false;

        public FileSystemEventProcessor()
        {
            _batchProcessor = new Timer(ProcessBatch, null, BATCH_INTERVAL_MS, BATCH_INTERVAL_MS);
        }

        /// <summary>
        /// Queue a file system event for processing
        /// </summary>
        public void QueueEvent(string path, WatcherChangeTypes changeType)
        {
            if (_disposed || string.IsNullOrWhiteSpace(path))
                return;

            var normalizedPath = PathNormalizationService.GetCanonicalPath(path);
            var now = DateTime.Now;

            _pendingEvents.AddOrUpdate(normalizedPath,
                // Add new event
                new PendingFileSystemEvent
                {
                    Path = normalizedPath,
                    ChangeType = changeType,
                    FirstSeen = now,
                    LastUpdated = now,
                    EventCount = 1
                },
                // Update existing event (deduplication)
                (key, existing) =>
                {
                    // For same path, latest event type wins
                    // But track how many events we've seen for debugging
                    return new PendingFileSystemEvent
                    {
                        Path = normalizedPath,
                        ChangeType = changeType, // Latest change type wins
                        FirstSeen = existing.FirstSeen,
                        LastUpdated = now,
                        EventCount = existing.EventCount + 1
                    };
                });
        }

        /// <summary>
        /// Queue a rename operation as atomic delete + create
        /// </summary>
        public void QueueRenameEvent(string oldPath, string newPath)
        {
            if (_disposed)
                return;

            // Handle rename as atomic operation to prevent ordering issues
            var normalizedOldPath = PathNormalizationService.GetCanonicalPath(oldPath);
            var normalizedNewPath = PathNormalizationService.GetCanonicalPath(newPath);
            var now = DateTime.Now;

            // Remove old path
            _pendingEvents.AddOrUpdate(normalizedOldPath,
                new PendingFileSystemEvent
                {
                    Path = normalizedOldPath,
                    ChangeType = WatcherChangeTypes.Deleted,
                    FirstSeen = now,
                    LastUpdated = now,
                    EventCount = 1,
                    IsPartOfRename = true,
                    RenamePartner = normalizedNewPath
                },
                (key, existing) => new PendingFileSystemEvent
                {
                    Path = normalizedOldPath,
                    ChangeType = WatcherChangeTypes.Deleted,
                    FirstSeen = existing.FirstSeen,
                    LastUpdated = now,
                    EventCount = existing.EventCount + 1,
                    IsPartOfRename = true,
                    RenamePartner = normalizedNewPath
                });

            // Add new path
            _pendingEvents.AddOrUpdate(normalizedNewPath,
                new PendingFileSystemEvent
                {
                    Path = normalizedNewPath,
                    ChangeType = WatcherChangeTypes.Created,
                    FirstSeen = now,
                    LastUpdated = now,
                    EventCount = 1,
                    IsPartOfRename = true,
                    RenamePartner = normalizedOldPath
                },
                (key, existing) => new PendingFileSystemEvent
                {
                    Path = normalizedNewPath,
                    ChangeType = WatcherChangeTypes.Created,
                    FirstSeen = existing.FirstSeen,
                    LastUpdated = now,
                    EventCount = existing.EventCount + 1,
                    IsPartOfRename = true,
                    RenamePartner = normalizedOldPath
                });
        }

        /// <summary>
        /// Process pending events in batches
        /// </summary>
        private async void ProcessBatch(object state)
        {
            if (_disposed || !await _processingLock.WaitAsync(100))
                return;

            try
            {
                var eventsToProcess = ExtractPendingEvents();

                if (eventsToProcess.Count == 0)
                    return;

                // Convert to processed events with proper ordering
                var processedEvents = OrderEventsForProcessing(eventsToProcess);

                // Notify subscribers
                if (EventsProcessed != null)
                {
                    try
                    {
                        await EventsProcessed(processedEvents);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error processing file system events: {ex.Message}");
                    }
                }
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Extract pending events atomically
        /// </summary>
        private List<PendingFileSystemEvent> ExtractPendingEvents()
        {
            var events = new List<PendingFileSystemEvent>();
            var keysToRemove = new List<string>();

            // Extract up to MAX_EVENTS_PER_BATCH events
            foreach (var kvp in _pendingEvents.Take(MAX_EVENTS_PER_BATCH))
            {
                if (_pendingEvents.TryRemove(kvp.Key, out var eventData))
                {
                    events.Add(eventData);
                }
            }

            return events;
        }

        /// <summary>
        /// Order events for safe processing (deletions before creations, parents before children)
        /// </summary>
        private List<ProcessedFileSystemEvent> OrderEventsForProcessing(List<PendingFileSystemEvent> events)
        {
            var processedEvents = new List<ProcessedFileSystemEvent>();

            // Group rename pairs
            var renamePairs = new Dictionary<string, PendingFileSystemEvent>();
            var standaloneEvents = new List<PendingFileSystemEvent>();

            foreach (var evt in events)
            {
                if (evt.IsPartOfRename && !string.IsNullOrEmpty(evt.RenamePartner))
                {
                    renamePairs[evt.Path] = evt;
                }
                else
                {
                    standaloneEvents.Add(evt);
                }
            }

            // Process rename pairs first (as single operations)
            foreach (var deleteEvent in renamePairs.Values.Where(e => e.ChangeType == WatcherChangeTypes.Deleted))
            {
                if (renamePairs.TryGetValue(deleteEvent.RenamePartner, out var createEvent))
                {
                    processedEvents.Add(new ProcessedFileSystemEvent
                    {
                        EventType = ProcessedEventType.Rename,
                        OldPath = deleteEvent.Path,
                        NewPath = createEvent.Path,
                        Timestamp = Math.Max(deleteEvent.LastUpdated.Ticks, createEvent.LastUpdated.Ticks)
                    });
                }
            }

            // Process standalone events in order: Delete -> Create -> Change
            var orderedStandalone = standaloneEvents
                .OrderBy(e => e.ChangeType == WatcherChangeTypes.Deleted ? 0 :
                            e.ChangeType == WatcherChangeTypes.Created ? 1 : 2)
                .ThenBy(e => e.Path.Length) // Parents before children
                .ThenBy(e => e.LastUpdated);

            foreach (var evt in orderedStandalone)
            {
                var processedType = evt.ChangeType switch
                {
                    WatcherChangeTypes.Created => ProcessedEventType.Create,
                    WatcherChangeTypes.Deleted => ProcessedEventType.Delete,
                    WatcherChangeTypes.Changed => ProcessedEventType.Change,
                    _ => ProcessedEventType.Change
                };

                processedEvents.Add(new ProcessedFileSystemEvent
                {
                    EventType = processedType,
                    NewPath = evt.Path,
                    Timestamp = evt.LastUpdated.Ticks,
                    EventCount = evt.EventCount
                });
            }

            return processedEvents;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _batchProcessor?.Dispose();
            _processingLock?.Dispose();
            _pendingEvents.Clear();
        }
    }

    /// <summary>
    /// Pending file system event with deduplication info
    /// </summary>
    public class PendingFileSystemEvent
    {
        public string Path { get; set; }
        public WatcherChangeTypes ChangeType { get; set; }
        public DateTime FirstSeen { get; set; }
        public DateTime LastUpdated { get; set; }
        public int EventCount { get; set; }
        public bool IsPartOfRename { get; set; }
        public string RenamePartner { get; set; }
    }

    /// <summary>
    /// Processed file system event ready for handling
    /// </summary>
    public class ProcessedFileSystemEvent
    {
        public ProcessedEventType EventType { get; set; }
        public string OldPath { get; set; } // For renames
        public string NewPath { get; set; }
        public long Timestamp { get; set; }
        public int EventCount { get; set; } = 1;
    }

    public enum ProcessedEventType
    {
        Create,
        Delete,
        Change,
        Rename
    }
}