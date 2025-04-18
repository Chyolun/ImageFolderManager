using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using ImageFolderManager.Models;

namespace ImageFolderManager.Services
{
    public class FileSystemWatcherService
    {
        private Dictionary<string, FileSystemWatcher> _watchers = new Dictionary<string, FileSystemWatcher>();
        private FolderService _folderService;
        private Action<FolderInfo, FileSystemEventArgs, WatcherChangeTypes> _callbackAction;

        public FileSystemWatcherService(FolderService folderService, Action<FolderInfo, FileSystemEventArgs, WatcherChangeTypes> callbackAction)
        {
            _folderService = folderService;
            _callbackAction = callbackAction;
        }

        public void WatchFolder(FolderInfo folder)
        {
            if (folder == null || !Directory.Exists(folder.FolderPath))
                return;

            if (_watchers.ContainsKey(folder.FolderPath))
                return;

            var watcher = new FileSystemWatcher
            {
                Path = folder.FolderPath,
                NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName |
                               NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                Filter = "*.*",
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };

            watcher.Created += (s, e) => OnFileSystemChanged(folder, e, WatcherChangeTypes.Created);
            watcher.Deleted += (s, e) => OnFileSystemChanged(folder, e, WatcherChangeTypes.Deleted);
            watcher.Renamed += (s, e) => OnFileSystemChanged(folder, e, WatcherChangeTypes.Renamed);
            watcher.Changed += (s, e) => OnFileSystemChanged(folder, e, WatcherChangeTypes.Changed);

            _watchers[folder.FolderPath] = watcher;
        }

        public void UnwatchFolder(string folderPath)
        {
            if (_watchers.TryGetValue(folderPath, out var watcher))
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
                _watchers.Remove(folderPath);

                // Also unwatch any subfolders being watched
                var subfoldersToUnwatch = _watchers.Keys
                    .Where(path => path.StartsWith(folderPath + Path.DirectorySeparatorChar))
                    .ToList();

                foreach (var subPath in subfoldersToUnwatch)
                {
                    UnwatchFolder(subPath);
                }
            }
        }

        public void UnwatchAllFolders()
        {
            foreach (var watcher in _watchers.Values)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }

            _watchers.Clear();
        }

        private void OnFileSystemChanged(FolderInfo folder, FileSystemEventArgs e, WatcherChangeTypes changeType)
        {
            // Execute on UI thread
            Application.Current.Dispatcher.Invoke(() =>
            {
                _callbackAction?.Invoke(folder, e, changeType);
            });
        }
    }
}