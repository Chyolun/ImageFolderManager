using ImageFolderManager.Commands;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ImageFolderManager.Services
{
    /// <summary>
    /// Centralized exception handling and logging service for the command system
    /// </summary>
    public class ExceptionHandlingService : IDisposable
    {
        private readonly ConcurrentQueue<LogEntry> _logQueue;
        private readonly Timer _flushTimer;
        private readonly string _logFilePath;
        private readonly SemaphoreSlim _flushSemaphore;
        private bool _disposed = false;

        // Configuration
        private readonly int _maxLogEntries = 10000;
        private readonly TimeSpan _flushInterval = TimeSpan.FromSeconds(5);

        public ExceptionHandlingService(string logDirectory = null)
        {
            _logQueue = new ConcurrentQueue<LogEntry>();
            _flushSemaphore = new SemaphoreSlim(1, 1);

            // Setup log file path
            var logDir = logDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ImageFolderManager", "Logs");

            Directory.CreateDirectory(logDir);
            _logFilePath = Path.Combine(logDir, $"commands_{DateTime.Now:yyyy-MM-dd}.log");

            // Setup flush timer
            _flushTimer = new Timer(FlushLogs, null, _flushInterval, _flushInterval);

            // Log service startup
            LogInfo("ExceptionHandlingService", "Exception handling service started");
        }

        /// <summary>
        /// Log an exception with context information
        /// </summary>
        public void LogException(string context, Exception exception, string additionalInfo = null)
        {
            var entry = new LogEntry
            {
                Timestamp = DateTime.Now,
                Level = LogLevel.Error,
                Context = context ?? "Unknown",
                Message = exception?.Message ?? "No message",
                Exception = exception,
                AdditionalInfo = additionalInfo,
                ThreadId = Thread.CurrentThread.ManagedThreadId
            };

            EnqueueLogEntry(entry);

            // Also write to Debug output for immediate visibility during development
            Debug.WriteLine($"[ERROR] [{entry.Timestamp:HH:mm:ss.fff}] {context}: {exception?.Message}");
            if (exception != null)
            {
                Debug.WriteLine($"Exception Details: {exception}");
            }
        }

        /// <summary>
        /// Log a warning message
        /// </summary>
        public void LogWarning(string context, string message, string additionalInfo = null)
        {
            var entry = new LogEntry
            {
                Timestamp = DateTime.Now,
                Level = LogLevel.Warning,
                Context = context ?? "Unknown",
                Message = message ?? "No message",
                AdditionalInfo = additionalInfo,
                ThreadId = Thread.CurrentThread.ManagedThreadId
            };

            EnqueueLogEntry(entry);
            Debug.WriteLine($"[WARNING] [{entry.Timestamp:HH:mm:ss.fff}] {context}: {message}");
        }

        /// <summary>
        /// Log an informational message
        /// </summary>
        public void LogInfo(string context, string message, string additionalInfo = null)
        {
            var entry = new LogEntry
            {
                Timestamp = DateTime.Now,
                Level = LogLevel.Info,
                Context = context ?? "Unknown",
                Message = message ?? "No message",
                AdditionalInfo = additionalInfo,
                ThreadId = Thread.CurrentThread.ManagedThreadId
            };

            EnqueueLogEntry(entry);
            Debug.WriteLine($"[INFO] [{entry.Timestamp:HH:mm:ss.fff}] {context}: {message}");
        }

        /// <summary>
        /// Log command execution details
        /// </summary>
        public void LogCommand(string commandId, string commandType, string action, string details = null)
        {
            var message = $"Command {commandId} ({commandType}): {action}";
            LogInfo("CommandExecution", message, details);
        }

        /// <summary>
        /// Log command failure with detailed information
        /// </summary>
        public void LogCommandFailure(string commandId, string commandType, Exception exception, string additionalInfo = null)
        {
            var context = $"CommandFailure_{commandType}";
            var message = $"Command {commandId} failed: {exception?.Message ?? "Unknown error"}";

            var entry = new LogEntry
            {
                Timestamp = DateTime.Now,
                Level = LogLevel.Error,
                Context = context,
                Message = message,
                Exception = exception,
                AdditionalInfo = additionalInfo,
                ThreadId = Thread.CurrentThread.ManagedThreadId
            };

            EnqueueLogEntry(entry);
            Debug.WriteLine($"[ERROR] [{entry.Timestamp:HH:mm:ss.fff}] {context}: {message}");
        }

        /// <summary>
        /// Handle and log exceptions from folder operations
        /// </summary>
        public CommandResult HandleFolderOperationException(string operation, Exception exception, string folderPath = null)
        {
            var context = $"FolderOperation_{operation}";
            var additionalInfo = folderPath != null ? $"Path: {folderPath}" : null;

            LogException(context, exception, additionalInfo);

            // Categorize exception and provide user-friendly message
            string userMessage = CategorizeException(exception);

            return new CommandResult
            {
                Success = false,
                Message = userMessage,
                Exception = exception
            };
        }

        /// <summary>
        /// Categorize exceptions to provide user-friendly messages
        /// </summary>
        private string CategorizeException(Exception exception)
        {
            return exception switch
            {
                UnauthorizedAccessException => "Access denied. You may not have permission to perform this operation.",
                DirectoryNotFoundException => "The folder could not be found. It may have been moved or deleted.",
                IOException ioEx when ioEx.Message.Contains("being used") => "The folder is currently being used by another process.",
                IOException => "An input/output error occurred. The folder may be locked or the disk may be full.",
                OperationCanceledException => "The operation was cancelled.",
                TimeoutException => "The operation timed out. The system may be busy.",
                ArgumentException => "Invalid folder name or path.",
                _ => $"An unexpected error occurred: {exception.Message}"
            };
        }

        /// <summary>
        /// Get recent log entries for debugging
        /// </summary>
        public LogEntry[] GetRecentLogs(int maxCount = 100)
        {
            var logs = new LogEntry[Math.Min(maxCount, _logQueue.Count)];
            var tempQueue = new LogEntry[_logQueue.Count];

            // Copy current queue to temporary array
            int index = 0;
            while (_logQueue.TryDequeue(out var entry) && index < tempQueue.Length)
            {
                tempQueue[index++] = entry;
                // Re-enqueue to preserve the log queue
                _logQueue.Enqueue(entry);
            }

            // Get the most recent entries
            var startIndex = Math.Max(0, index - maxCount);
            Array.Copy(tempQueue, startIndex, logs, 0, Math.Min(maxCount, index));

            return logs;
        }

        /// <summary>
        /// Force immediate flush of all log entries
        /// </summary>
        public async Task FlushAllLogsAsync()
        {
            await _flushSemaphore.WaitAsync();
            try
            {
                await FlushLogsToFile();
            }
            finally
            {
                _flushSemaphore.Release();
            }
        }

        private void EnqueueLogEntry(LogEntry entry)
        {
            _logQueue.Enqueue(entry);

            // Prevent memory issues by limiting queue size
            while (_logQueue.Count > _maxLogEntries)
            {
                _logQueue.TryDequeue(out _);
            }
        }

        private async void FlushLogs(object state)
        {
            if (_disposed) return;

            if (await _flushSemaphore.WaitAsync(100)) // Don't block if busy
            {
                try
                {
                    await FlushLogsToFile();
                }
                finally
                {
                    _flushSemaphore.Release();
                }
            }
        }

        private async Task FlushLogsToFile()
        {
            if (_logQueue.IsEmpty) return;

            try
            {
                using var writer = new StreamWriter(_logFilePath, append: true);

                while (_logQueue.TryDequeue(out var entry))
                {
                    var logLine = FormatLogEntry(entry);
                    await writer.WriteLineAsync(logLine);
                }

                await writer.FlushAsync();
            }
            catch (Exception ex)
            {
                // Avoid recursive logging - just output to Debug
                Debug.WriteLine($"Failed to write logs to file: {ex.Message}");
            }
        }

        private string FormatLogEntry(LogEntry entry)
        {
            var level = entry.Level.ToString().ToUpper().PadRight(7);
            var timestamp = entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var threadId = entry.ThreadId.ToString().PadLeft(3);

            var line = $"[{timestamp}] [{level}] [T{threadId}] [{entry.Context}] {entry.Message}";

            if (!string.IsNullOrEmpty(entry.AdditionalInfo))
            {
                line += $" | {entry.AdditionalInfo}";
            }

            if (entry.Exception != null)
            {
                line += $"\n    Exception: {entry.Exception.GetType().Name}: {entry.Exception.Message}";
                if (!string.IsNullOrEmpty(entry.Exception.StackTrace))
                {
                    // Include first few lines of stack trace
                    var stackLines = entry.Exception.StackTrace.Split('\n');
                    for (int i = 0; i < Math.Min(3, stackLines.Length); i++)
                    {
                        line += $"\n    {stackLines[i].Trim()}";
                    }
                }
            }

            return line;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _flushTimer?.Dispose();

                // Final flush
                Task.Run(async () => await FlushAllLogsAsync()).Wait(TimeSpan.FromSeconds(2));

                _flushSemaphore?.Dispose();
                _disposed = true;
            }
        }
    }

    /// <summary>
    /// Represents a single log entry
    /// </summary>
    public class LogEntry
    {
        public DateTime Timestamp { get; set; }
        public LogLevel Level { get; set; }
        public string Context { get; set; }
        public string Message { get; set; }
        public Exception Exception { get; set; }
        public string AdditionalInfo { get; set; }
        public int ThreadId { get; set; }
    }

    public enum LogLevel
    {
        Info,
        Warning,
        Error
    }
}