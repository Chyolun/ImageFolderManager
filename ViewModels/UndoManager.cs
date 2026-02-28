using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ImageFolderManager.Services;

namespace ImageFolderManager.ViewModels
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Operation type enumeration
    // ─────────────────────────────────────────────────────────────────────────

    public enum UndoOperationType
    {
        Move,
        MultiMove,
        Copy,
        Create,
        Delete,
        Rename
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Immutable record for a single undoable operation
    // ─────────────────────────────────────────────────────────────────────────

    public sealed class UndoResult
    {
        public bool Success { get; }
        public string Message { get; }
        public string RestoredPath { get; }   
        public string PreviousPath { get; }  
        public UndoOperationType OperationType { get; }

        public IReadOnlyList<(string PreviousPath, string RestoredPath)> MultiPaths { get; }
        public UndoResult(bool success, string message, UndoOperationType type,
                      string restoredPath = null, string previousPath = null,
                      IReadOnlyList<(string, string)> multiPaths = null)
        {
            Success = success;
            Message = message;
            OperationType = type;
            RestoredPath = restoredPath;
            PreviousPath = previousPath;
            MultiPaths = multiPaths ?? Array.Empty<(string, string)>();
        }
    }

    public sealed class UndoRecord
    {
        public UndoOperationType OperationType { get; }
        public DateTime Timestamp { get; }

        // Move / Rename: single source → destination
        public string SourcePath { get; }
        public string DestinationPath { get; }

        // MultiMove: multiple sources → same destination directory
        public IReadOnlyList<string> SourcePaths { get; }

        // Copy: the path that was *created* (to delete on undo)
        public string CreatedPath { get; }

        // Delete: snapshot of tags/rating for restore after undo is not feasible
        // (files are in Recycle Bin; we only restore the metadata file path reference)
        // Create: path that was created (to delete on undo)

        // Description shown in UI / status bar
        public string Description { get; }

        // ── Factory methods ────────────────────────────────────────────────

        public static UndoRecord ForMove(string sourcePath, string destinationPath)
        {
            string name = Path.GetFileName(sourcePath);
            return new UndoRecord(
                UndoOperationType.Move,
                sourcePath,
                destinationPath,
                null,
                null,
                $"Move '{name}' → '{Path.GetFileName(destinationPath)}'");
        }

        public static UndoRecord ForMultiMove(IEnumerable<string> sourcePaths, string destinationDirectory)
        {
            var list = sourcePaths.ToList().AsReadOnly();
            return new UndoRecord(
                UndoOperationType.MultiMove,
                null,
                destinationDirectory,
                list,
                null,
                $"Move {list.Count} folders → '{Path.GetFileName(destinationDirectory)}'");
        }

        public static UndoRecord ForCopy(string copiedPath)
        {
            string name = Path.GetFileName(copiedPath);
            return new UndoRecord(
                UndoOperationType.Copy,
                null,
                null,
                null,
                copiedPath,
                $"Copy → '{name}' (undo will delete copy)");
        }

        public static UndoRecord ForCreate(string createdPath)
        {
            string name = Path.GetFileName(createdPath);
            return new UndoRecord(
                UndoOperationType.Create,
                null,
                null,
                null,
                createdPath,
                $"Create '{name}'");
        }

        public static UndoRecord ForRename(string oldPath, string newPath)
        {
            string oldName = Path.GetFileName(oldPath);
            string newName = Path.GetFileName(newPath);
            return new UndoRecord(
                UndoOperationType.Rename,
                oldPath,
                newPath,
                null,
                null,
                $"Rename '{oldName}' → '{newName}'");
        }

        // ── Private constructor ────────────────────────────────────────────

        private UndoRecord(
            UndoOperationType type,
            string source,
            string destination,
            IReadOnlyList<string> sources,
            string created,
            string description)
        {
            OperationType   = type;
            SourcePath      = source;
            DestinationPath = destination;
            SourcePaths     = sources;
            CreatedPath     = created;
            Description     = description;
            Timestamp       = DateTime.Now;
        }

        public override string ToString() => $"[{Timestamp:HH:mm:ss}] {Description}";
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  UndoManager  — the single source of truth for undoable operations
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Manages a stack of undoable folder operations.
    /// Supports Move, MultiMove, Copy, Create, and Rename.
    /// Raises <see cref="StateChanged"/> whenever the stack is mutated so that
    /// bound UI commands can refresh their CanExecute state.
    /// </summary>
    public sealed class UndoManager
    {
        private const int MaxHistory = 50;

        private readonly Stack<UndoRecord> _stack = new Stack<UndoRecord>();
        private readonly UnifiedFolderService _folderService;

        // ── Events ────────────────────────────────────────────────────────

        /// <summary>Fired whenever the undo stack changes (push, pop, or clear).</summary>
        public event EventHandler StateChanged;

        /// <summary>Fired with a status message after each undo operation.</summary>
        public event EventHandler<string> StatusChanged;

        // ── Properties ────────────────────────────────────────────────────

        /// <summary>True when there is at least one undoable operation.</summary>
        public bool CanUndo => _stack.Count > 0;

        /// <summary>Number of operations currently in the stack.</summary>
        public int Count => _stack.Count;

        /// <summary>Description of the next operation to undo, or null.</summary>
        public string NextUndoDescription =>
            _stack.Count > 0 ? _stack.Peek().Description : null;

        // ── Constructor ───────────────────────────────────────────────────

        public UndoManager(UnifiedFolderService folderService)
        {
            _folderService = folderService
                ?? throw new ArgumentNullException(nameof(folderService));
        }

        // ── Push ──────────────────────────────────────────────────────────

        /// <summary>Records a new undoable operation.</summary>
        public void Push(UndoRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));

            _stack.Push(record);

            // Trim history to prevent unbounded growth
            while (_stack.Count > MaxHistory)
            {
                // Stack doesn't support remove-from-bottom directly;
                // rebuild without the oldest entry.
                var items = _stack.ToArray();          // newest first
                _stack.Clear();
                foreach (var item in items.Take(MaxHistory).Reverse())
                    _stack.Push(item);
            }

            Debug.WriteLine($"[UndoManager] Pushed: {record}");
            RaiseStateChanged();
        }

        // ── Undo ─────────────────────────────────────────────────────────

        /// <summary>
        /// Reverses the most recent operation.
        /// Returns a user-facing message describing what happened (success or failure).
        /// </summary>
        public async Task<UndoResult> UndoLastAsync()
        {
            if (_stack.Count == 0)
                return new UndoResult(false, "Nothing to undo.", UndoOperationType.Move);

            var record = _stack.Pop();
            RaiseStateChanged();

            try
            {
                UndoResult result = await ExecuteUndoAsync(record);
                RaiseStatus(result.Message);
                return result;
            }
            catch (Exception ex)
            {
                _stack.Push(record);
                RaiseStateChanged();
                string errorMsg = $"Undo failed: {ex.Message}";
                RaiseStatus(errorMsg);
                return new UndoResult(false, errorMsg, record.OperationType);
            }
        }

        // ── Clear ─────────────────────────────────────────────────────────

        /// <summary>Clears the entire undo history.</summary>
        public void Clear()
        {
            _stack.Clear();
            RaiseStateChanged();
        }

        // ── Private helpers ───────────────────────────────────────────────

        private async Task<UndoResult> ExecuteUndoAsync(UndoRecord record)
        {
            switch (record.OperationType)
            {
                case UndoOperationType.Move: return await UndoMoveAsync(record);
                case UndoOperationType.MultiMove: return await UndoMultiMoveAsync(record);
                case UndoOperationType.Copy: return await UndoCopyAsync(record);
                case UndoOperationType.Create: return await UndoCreateAsync(record);
                case UndoOperationType.Rename: return await UndoRenameAsync(record);
                default:
                    return new UndoResult(false,
                        $"Undo not implemented for '{record.OperationType}'.",
                        record.OperationType);
            }
        }


        // Move: move the folder back to its original location
        private async Task<UndoResult> UndoMoveAsync(UndoRecord record)
        {
            if (!Directory.Exists(record.DestinationPath))
                return new UndoResult(false,
                    $"Cannot undo move: '{record.DestinationPath}' no longer exists.",
                    UndoOperationType.Move);

            if (Directory.Exists(record.SourcePath))
                return new UndoResult(false,
                    $"Cannot undo move: original path '{record.SourcePath}' already occupied.",
                    UndoOperationType.Move);

            bool ok = await _folderService.MoveFolderAsync(record.DestinationPath, record.SourcePath);
            return ok
                ? new UndoResult(true,
                    $"Undo move: restored '{Path.GetFileName(record.SourcePath)}'.",
                    UndoOperationType.Move,
                    restoredPath: record.SourcePath,      //
                    previousPath: record.DestinationPath) // 
                : new UndoResult(false,
                    $"Undo move failed for '{record.DestinationPath}'.",
                    UndoOperationType.Move);
        }

        // MultiMove: move each folder back to its original source path
        private async Task<UndoResult> UndoMultiMoveAsync(UndoRecord record)
        {
            int success = 0, failed = 0;
            var restoredPairs = new List<(string PreviousPath, string RestoredPath)>();

            foreach (string originalSource in record.SourcePaths)
            {
                string name = Path.GetFileName(originalSource);
                string currentPath = Path.Combine(record.DestinationPath, name);

                if (!Directory.Exists(currentPath) || Directory.Exists(originalSource))
                {
                    failed++;
                    Debug.WriteLine($"[UndoManager] MultiMove undo skip: currentPath={currentPath}");
                    continue;
                }

                bool ok = await _folderService.MoveFolderAsync(currentPath, originalSource);
                if (ok)
                {
                    success++;
                    restoredPairs.Add((currentPath, originalSource));
                }
                else
                {
                    failed++;
                }
            }

            string msg = failed == 0
                ? $"Undo move: restored {success} folder(s)."
                : $"Undo move: restored {success}, failed {failed}.";

            return new UndoResult(success > 0, msg, UndoOperationType.MultiMove,
                multiPaths: restoredPairs.AsReadOnly());
        }

        // Copy: delete the created copy
        private async Task<UndoResult> UndoCopyAsync(UndoRecord record)
        {
            if (!Directory.Exists(record.CreatedPath))
                return new UndoResult(false,
                    $"Cannot undo copy: '{record.CreatedPath}' no longer exists.",
                    UndoOperationType.Copy);

            bool ok = await _folderService.DeleteFolderAsync(record.CreatedPath, useRecycleBin: true);
            return ok
                ? new UndoResult(true,
                    $"Undo copy: deleted '{Path.GetFileName(record.CreatedPath)}'.",
                    UndoOperationType.Copy,
                    previousPath: record.CreatedPath) 
                : new UndoResult(false,
                    $"Undo copy failed for '{record.CreatedPath}'.",
                    UndoOperationType.Copy);
        }


        // Create: delete the newly created folder
        private async Task<UndoResult> UndoCreateAsync(UndoRecord record)
        {
            if (!Directory.Exists(record.CreatedPath))
                return new UndoResult(false,
                    $"Cannot undo create: '{record.CreatedPath}' no longer exists.",
                    UndoOperationType.Create);

            bool ok = await _folderService.DeleteFolderAsync(record.CreatedPath, useRecycleBin: true);
            return ok
                ? new UndoResult(true,
                    $"Undo create: deleted '{Path.GetFileName(record.CreatedPath)}'.",
                    UndoOperationType.Create,
                    previousPath: record.CreatedPath)
                : new UndoResult(false,
                    $"Undo create failed for '{record.CreatedPath}'.",
                    UndoOperationType.Create);
        }

        // Rename: rename back to the old name
        private async Task<UndoResult> UndoRenameAsync(UndoRecord record)
        {
            if (!Directory.Exists(record.DestinationPath))
                return new UndoResult(false,
                    $"Cannot undo rename: '{record.DestinationPath}' no longer exists.",
                    UndoOperationType.Rename);

            if (Directory.Exists(record.SourcePath))
                return new UndoResult(false,
                    $"Cannot undo rename: original name already exists.",
                    UndoOperationType.Rename);

            bool ok = await _folderService.RenameFolderAsync(
                record.DestinationPath,
                Path.GetFileName(record.SourcePath));

            return ok
                ? new UndoResult(true,
                    $"Undo rename: restored to '{Path.GetFileName(record.SourcePath)}'.",
                    UndoOperationType.Rename,
                    restoredPath: record.SourcePath,
                    previousPath: record.DestinationPath)
                : new UndoResult(false,
                    $"Undo rename failed.",
                    UndoOperationType.Rename);
        }

        private void RaiseStateChanged() =>
            StateChanged?.Invoke(this, EventArgs.Empty);

        private void RaiseStatus(string message) =>
            StatusChanged?.Invoke(this, message);
    }
}
