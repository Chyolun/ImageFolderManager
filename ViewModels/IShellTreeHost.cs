using System.Collections.Generic;
using System.Threading.Tasks;
using ImageFolderManager.Models;

namespace ImageFolderManager.ViewModels
{
    /// <summary>
    /// Minimal host contract that ShellTreeView depends on.
    /// Keeps the control decoupled from a concrete MainViewModel type.
    /// </summary>
    public interface IShellTreeHost
    {
        void NotifyFolderSelected(FolderInfo folder, bool loadImages);
        void NotifyMultiSelectionChanged(int selectedCount, string lastFolderName);
        void NotifySelectionCleared();

        Task CreateNewFolder(FolderInfo parentFolder);
        Task RenameFolder(FolderInfo folder);
        Task<bool> DeleteFolders(IEnumerable<FolderInfo> folders);
        Task<bool> MoveFolders(IEnumerable<FolderInfo> sources, FolderInfo target);

        void CutFolders(IEnumerable<FolderInfo> folders);
        void CopyFolders(IEnumerable<FolderInfo> folders);
        bool HasClipboardContent();
        Task<bool> PasteFolders(FolderInfo targetFolder);

        Task BatchUpdateTags(List<FolderInfo> folders);
        void ShowInExplorer(FolderInfo folder);

        void RefreshEditCommands();
    }
}
