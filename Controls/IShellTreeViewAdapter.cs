using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ImageFolderManager.Models;

namespace ImageFolderManager.Controls
{
    /// <summary>
    /// MainViewModel-facing contract for tree operations.
    /// Keeps ViewModel independent from concrete ShellTreeView internals.
    /// </summary>
    public interface IShellTreeViewAdapter
    {
        bool HasSelectedItems();
        List<FolderInfo> GetSelectedFolderInfos();

        Task SetRootDirectory(string rootPath, bool showLoadingIndicator = true);
        void ClearTreeView();

        Task RefreshTreeFull(string pathToSelect = null, bool preserveExpanded = true);
        Task RefreshTreeIncremental(
            ShellTreeView.FolderOperationType operationType,
            string sourcePath,
            string destinationPath = null);
        Task RefreshTreeIncrementalBatchMove(List<string> sourcePaths, List<string> destinationPaths);

        Task<bool> NavigateToPathAsync(
            string path,
            CancellationToken cancellationToken = default,
            bool promptToChangeRoot = false,
            bool centerInView = false);
        void SelectPath(string path);

        bool HasPathMappings { get; }
        bool IsPathMapped(string path);
    }
}
