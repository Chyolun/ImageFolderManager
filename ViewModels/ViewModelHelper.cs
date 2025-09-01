using System;
using System.Collections.Generic;
using System.Windows.Input;
using ImageFolderManager.Models;

namespace ImageFolderManager.ViewModels
{
    /// <summary>
    /// Helper classes and models used across multiple ViewModels
    /// </summary>

    #region Models

    /// <summary>
    /// Represents a folder move operation for undo functionality
    /// </summary>
    public class FolderMoveOperation
    {
        public List<string> SourcePaths { get; set; } = new List<string>();
        public string DestinationPath { get; set; }
        public bool IsMultipleMove { get; set; }
        public DateTime Timestamp { get; set; }

        // Store parent paths for refreshing after undo
        public List<string> SourceParentPaths { get; set; } = new List<string>();
    }

    /// <summary>
    /// Represents a star for rating display
    /// </summary>
    public class StarModel
    {
        public int Value { get; set; }
        public string Symbol { get; set; }
    }

    #endregion

    #region Event Args

    
    /// <summary>
    /// Event arguments for tag updates
    /// </summary>
    public class TagsUpdatedEventArgs : EventArgs
    {
        /// <summary>
        /// The folder whose tags were updated
        /// </summary>
        public FolderInfo Folder { get; set; }

        /// <summary>
        /// The updated collection of tags (never null)
        /// </summary>
        public List<string> Tags { get; set; } = new List<string>();

        /// <summary>
        /// The folder's rating
        /// </summary>
        public int Rating { get; set; }

        /// <summary>
        /// Default constructor
        /// </summary>
        public TagsUpdatedEventArgs()
        {
            // Initialize with default values
            Tags = new List<string>();
            Rating = 0;
        }

        /// <summary>
        /// Constructor with parameters
        /// </summary>
        /// <param name="folder">The folder whose tags were updated</param>
        /// <param name="tags">The updated tags</param>
        /// <param name="rating">The folder's rating</param>
        public TagsUpdatedEventArgs(FolderInfo folder, IEnumerable<string> tags = null, int rating = 0)
        {
            Folder = folder;
            Tags = tags != null ? new List<string>(tags) : new List<string>();
            Rating = rating;
        }
    }

    /// <summary>
    /// Event arguments for image loading completion
    /// </summary>
    public class ImageLoadingEventArgs : EventArgs
    {
        public FolderInfo Folder { get; set; }
        public int ImageCount { get; set; }
    }

    #endregion


    /// <summary>
    /// Event arguments for folder operation events with enhanced undo support
    /// </summary>
    public class FolderOperationEventArgs : EventArgs
    {
        /// <summary>
        /// The type of folder operation that was performed
        /// </summary>
        public FolderOperation Operation { get; set; }

        /// <summary>
        /// The source path of the operation
        /// </summary>
        public string SourcePath { get; set; }

        /// <summary>
        /// The destination path of the operation (null for delete operations)
        /// </summary>
        public string DestinationPath { get; set; }

        /// <summary>
        /// Whether the operation was successful
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Indicates whether this event represents an undo operation
        /// This property is used by the refresh system to determine 
        /// when comprehensive interface refresh is needed
        /// </summary>
        public bool IsUndoOperation { get; set; }

        /// <summary>
        /// Additional context information about the operation
        /// </summary>
        public string Context { get; set; }

        /// <summary>
        /// The timestamp when the operation was completed
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.Now;

        /// <summary>
        /// Number of items affected by the operation (for batch operations)
        /// </summary>
        public int AffectedItemCount { get; set; } = 1;

        /// <summary>
        /// Error message if the operation failed
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Creates a new instance for a successful operation
        /// </summary>
        /// <param name="operation">Type of operation</param>
        /// <param name="sourcePath">Source path</param>
        /// <param name="destinationPath">Destination path</param>
        /// <param name="isUndoOperation">Whether this is an undo operation</param>
        /// <returns>Configured event args</returns>
        public static FolderOperationEventArgs CreateSuccess(
            FolderOperation operation,
            string sourcePath,
            string destinationPath = null,
            bool isUndoOperation = false)
        {
            return new FolderOperationEventArgs
            {
                Operation = operation,
                SourcePath = sourcePath,
                DestinationPath = destinationPath,
                Success = true,
                IsUndoOperation = isUndoOperation,
                Timestamp = DateTime.Now
            };
        }

        /// <summary>
        /// Creates a new instance for a failed operation
        /// </summary>
        /// <param name="operation">Type of operation</param>
        /// <param name="sourcePath">Source path</param>
        /// <param name="errorMessage">Error description</param>
        /// <param name="isUndoOperation">Whether this is an undo operation</param>
        /// <returns>Configured event args</returns>
        public static FolderOperationEventArgs CreateFailure(
            FolderOperation operation,
            string sourcePath,
            string errorMessage,
            bool isUndoOperation = false)
        {
            return new FolderOperationEventArgs
            {
                Operation = operation,
                SourcePath = sourcePath,
                Success = false,
                IsUndoOperation = isUndoOperation,
                ErrorMessage = errorMessage,
                Timestamp = DateTime.Now
            };
        }

        /// <summary>
        /// Creates a new instance for batch operations
        /// </summary>
        /// <param name="operation">Type of operation</param>
        /// <param name="sourcePath">Primary source path</param>
        /// <param name="destinationPath">Primary destination path</param>
        /// <param name="itemCount">Number of items in the batch</param>
        /// <param name="isUndoOperation">Whether this is an undo operation</param>
        /// <returns>Configured event args</returns>
        public static FolderOperationEventArgs CreateBatchSuccess(
            FolderOperation operation,
            string sourcePath,
            string destinationPath,
            int itemCount,
            bool isUndoOperation = false)
        {
            return new FolderOperationEventArgs
            {
                Operation = operation,
                SourcePath = sourcePath,
                DestinationPath = destinationPath,
                Success = true,
                IsUndoOperation = isUndoOperation,
                AffectedItemCount = itemCount,
                Context = $"Batch operation affecting {itemCount} items",
                Timestamp = DateTime.Now
            };
        }

        /// <summary>
        /// Returns a string representation of the operation for logging
        /// </summary>
        /// <returns>Formatted string describing the operation</returns>
        public override string ToString()
        {
            var operationType = IsUndoOperation ? $"Undo {Operation}" : Operation.ToString();
            var status = Success ? "Success" : "Failed";
            var itemInfo = AffectedItemCount > 1 ? $" ({AffectedItemCount} items)" : "";

            return $"[{Timestamp:HH:mm:ss}] {operationType}: {SourcePath} → {DestinationPath} - {status}{itemInfo}";
        }
    }

    /// <summary>
    /// Enumeration of folder operations
    /// </summary>
    public enum FolderOperation
    {
        /// <summary>
        /// Folder creation operation
        /// </summary>
        Create,

        /// <summary>
        /// Folder deletion operation
        /// </summary>
        Delete,

        /// <summary>
        /// Folder move operation
        /// </summary>
        Move,

        /// <summary>
        /// Folder copy operation
        /// </summary>
        Copy,

        /// <summary>
        /// Folder rename operation
        /// </summary>
        Rename,

        /// <summary>
        /// Folder refresh operation
        /// </summary>
        Refresh
    }

    /// <summary>
    /// Event arguments for refresh requests
    /// </summary>
    public class RefreshRequestEventArgs : EventArgs
    {
        /// <summary>
        /// Type of refresh being requested
        /// </summary>
        public RefreshType RefreshType { get; set; }

        /// <summary>
        /// Primary target path for the refresh operation
        /// </summary>
        public string TargetPath { get; set; }

        /// <summary>
        /// Additional paths that were affected and may need refresh
        /// </summary>
        public List<string> AffectedPaths { get; set; } = new List<string>();

        /// <summary>
        /// Path to select after refresh (optional)
        /// </summary>
        public string SelectPath { get; set; }
    }

    /// <summary>
    /// Types of refresh operations
    /// </summary>
    public enum RefreshType
    {
        /// <summary>
        /// Standard refresh after normal operations
        /// </summary>
        Standard,

        /// <summary>
        /// Comprehensive refresh after undo operations
        /// </summary>
        PostUndo,

        /// <summary>
        /// Full system refresh
        /// </summary>
        Complete
    }

    /// <summary>
    /// Represents the result of a folder import operation
    /// </summary>
    public class FolderImportResult
    {
        public string SourcePath { get; set; }
        public string DestinationPath { get; set; }
        public string FolderName { get; set; }
        public bool Success { get; set; }
        public string Message { get; set; }
    }
}