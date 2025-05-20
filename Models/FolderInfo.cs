using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using ImageFolderManager.Services;

namespace ImageFolderManager.Models
{
    public class FolderInfo : INotifyPropertyChanged
    {
        private string _folderPath;
        public string FolderPath
        {
            get => _folderPath;
            set
            {
                if (_folderPath != value)
                {
                    _folderPath = value;
                    OnPropertyChanged();
                    // Also notify that Name property has changed since it depends on FolderPath
                    OnPropertyChanged(nameof(Name));
                }
            }
        }

        public FolderInfo Parent { get; set; }

        private ObservableCollection<FolderInfo> _children;
        public ObservableCollection<FolderInfo> Children
        {
            get => _children;
            set
            {
                _children = value;
                OnPropertyChanged();
            }
        }

        private ObservableCollection<ImageInfo> _images;
        public ObservableCollection<ImageInfo> Images
        {
            get => _images;
            set
            {
                _images = value;
                OnPropertyChanged();
            }
        }

        private ObservableCollection<string> _tags = new();
        public ObservableCollection<string> Tags
        {
            get => _tags;
            set
            {
                _tags = value;
                OnPropertyChanged();
            }
        }

        private int _rating;
        public int Rating
        {
            get => _rating;
            set
            {
                _rating = value;
                OnPropertyChanged();
            }
        }

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                _isExpanded = value;
                OnPropertyChanged(); // update binding
            }
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        public string Name => Path.GetFileName(FolderPath);
        public FolderInfo() { }

        public FolderInfo(string path, FolderInfo parent = null)
        {
            // Normalize path when saving to ensure consistency
            FolderPath = PathService.NormalizePath(path);
            Parent = parent;
            Tags = new ObservableCollection<string>();
            Children = new ObservableCollection<FolderInfo>();
            Images = new ObservableCollection<ImageInfo>();

            if (HasSubfolders(path))
            {
                Children.Add(null);
            }
        }

        public void LoadChildren()
        {
            if (Children.Count == 1 && Children[0] == null)
            {
                Children.Clear();

                try
                {
                    // Use PathService to verify directory exists
                    if (!PathService.DirectoryExists(FolderPath))
                        return;

                    var subDirs = Directory.GetDirectories(FolderPath);
                    foreach (var dir in subDirs)
                    {
                        var child = new FolderInfo(PathService.NormalizePath(dir), this);
                        Children.Add(child);
                    }
                }
                catch (Exception ex)
                {
                    // Log error but don't crash
                    System.Diagnostics.Debug.WriteLine($"Error loading children for {FolderPath}: {ex.Message}");
                }
            }
            else if (Children.Count == 0)
            {
                // If Children is empty, try loading anyway
                try
                {
                    // Use PathService to verify directory exists
                    if (!PathService.DirectoryExists(FolderPath))
                        return;

                    var subDirs = Directory.GetDirectories(FolderPath);
                    foreach (var dir in subDirs)
                    {
                        var child = new FolderInfo(PathService.NormalizePath(dir), this);
                        Children.Add(child);
                    }
                }
                catch (Exception ex)
                {
                    // Log error but don't crash
                    System.Diagnostics.Debug.WriteLine($"Error loading children for {FolderPath}: {ex.Message}");
                }
            }
        }
        private bool HasSubfolders(string path)
        {
            try
            {
                // Always check the file system directly to ensure we have the latest state
                // Don't rely on cached information
                if (!Directory.Exists(path))
                    return false;

                try
                {
                    // Use GetDirectories with TopDirectoryOnly for better performance
                    var dirs = Directory.GetDirectories(path, "*", SearchOption.TopDirectoryOnly);
                    return dirs.Length > 0;
                }
                catch (UnauthorizedAccessException)
                {
                    // For unauthorized directories, assume they might have subdirectories
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error checking for subdirectories in {path}: {ex.Message}");
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }


        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }
    }
}