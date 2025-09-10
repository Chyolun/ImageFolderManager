using System;

namespace ImageFolderManager.StateMachine
{
    /// <summary>
    /// Represents the current state of a folder
    /// </summary>
    public enum FolderState
    {
        /// <summary>
        /// Folder is available for operations
        /// </summary>
        Available,

        /// <summary>
        /// Folder is currently being processed (locked)
        /// </summary>
        Processing,

        /// <summary>
        /// Folder operation failed and is in error state
        /// </summary>
        Error,

        /// <summary>
        /// Folder has been deleted or no longer exists
        /// </summary>
        Deleted,

        /// <summary>
        /// Folder is being monitored for changes
        /// </summary>
        Monitoring
    }

    /// <summary>
    /// Information about a folder's state
    /// </summary>
    public class FolderStateInfo
    {
        public string Path { get; set; }
        public FolderState CurrentState { get; set; }
        public FolderState PreviousState { get; set; }
        public DateTime LastStateChange { get; set; }
        public string OperationId { get; set; }
        public string ErrorMessage { get; set; }
        public int TransitionCount { get; set; }

        public FolderStateInfo(string path)
        {
            Path = path;
            CurrentState = FolderState.Available;
            PreviousState = FolderState.Available;
            LastStateChange = DateTime.Now;
            TransitionCount = 0;
        }
    }


}