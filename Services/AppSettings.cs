using System;
using System.IO;
using System.Windows;
using Newtonsoft.Json;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using ImageFolderManager.Models;

namespace ImageFolderManager.Services
{
    public class AppSettings : INotifyPropertyChanged
    {
        private static readonly string SettingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ImageFolderManager",
            "settings.json");

        // Default settings
        private string _defaultRootDirectory = string.Empty;

        private int _maxCacheSize = 512;
        public int TrimThreshold => (int)(MaxCacheSize * 1.2);
        public int TrimTarget => (int)(MaxCacheSize * 0.7);

        private int _previewHeight = 500;

        private int _previewWidth = 500;

        // In AppSettings constructor
        private AppSettings()
        {   
        }

        public string DefaultRootDirectory
        {
            get => _defaultRootDirectory;
            set
            {
                if (_defaultRootDirectory != value)
                {
                    _defaultRootDirectory = value;
                    OnPropertyChanged();
                    Save();
                }
            }
        }
    
        public int PreviewWidth
        {
            get => _previewWidth;
            set
            {
                int newValue = ValidatePreviewDimension(value, 100, 1000);
                if (_previewWidth != newValue)
                {
                    _previewWidth = newValue;
                    OnPropertyChanged();          
                    Save();
                }
            }
        }
      
        public int PreviewHeight
        {
            get => _previewHeight;
            set
            {
                int newValue = ValidatePreviewDimension(value, 100, 1000);
                if (_previewHeight != newValue)
                {
                    _previewHeight = newValue;
                    OnPropertyChanged();                  
                    Save();
                }
            }
        }

        private int ValidatePreviewDimension(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        // Additional settings
        private bool _autoExpandFolders = false;
        public bool AutoExpandFolders
        {
            get => _autoExpandFolders;
            set
            {
                if (_autoExpandFolders != value)
                {
                    _autoExpandFolders = value;
                    OnPropertyChanged();
                    Save();
                }
            }
        }

        private int _maxRecentFolders = 10;
        public int MaxRecentFolders
        {
            get => _maxRecentFolders;
            set
            {
                if (_maxRecentFolders != value)
                {
                    _maxRecentFolders = Math.Max(1, Math.Min(20, value));
                    OnPropertyChanged();
                    Save();
                }
            }
        }

        private List<string> _recentFolders = new List<string>();
        public List<string> RecentFolders
        {
            get => _recentFolders;
            set
            {
                _recentFolders = value ?? new List<string>();
                OnPropertyChanged();
                Save();
            }
        }

        // Singleton instance
        private static AppSettings _instance;
        private static readonly object _instanceLock = new object();

        public static AppSettings Instance
        {
            get
            {
                lock (_instanceLock)
                {
                    if (_instance == null)
                    {
                        _instance = Load();
                    }
                    return _instance;
                }
            }
        }
    
        // Load settings from file
        private static AppSettings Load()
        {
            var settings = new AppSettings();

            try
            {
                // Ensure directory exists
                string directory = Path.GetDirectoryName(SettingsFilePath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // If settings file exists, load it
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath);
                    var loadedSettings = JsonConvert.DeserializeObject<AppSettings>(json);

                    if (loadedSettings != null)
                    {
                        // Copy properties to ensure we use the property setters with validation
                        settings.DefaultRootDirectory = loadedSettings.DefaultRootDirectory;
                        settings.PreviewWidth = loadedSettings.PreviewWidth;
                        settings.PreviewHeight = loadedSettings.PreviewHeight;
                        settings.AutoExpandFolders = loadedSettings.AutoExpandFolders;
                        settings.MaxRecentFolders = loadedSettings.MaxRecentFolders;
                        settings.MaxCacheSize = loadedSettings.MaxCacheSize;
                        settings.ParallelThreadCount = loadedSettings.ParallelThreadCount;
                        settings.RecentFolders = loadedSettings.RecentFolders ?? new List<string>();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading settings: {ex.Message}");
                MessageBox.Show($"Error loading settings: {ex.Message}. Default settings will be used.",
                    "Settings Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return settings;
        }

        public int MaxCacheSize
        {
            get => _maxCacheSize;
            set
            {
                int newValue = Math.Max(100, Math.Min(2000, value));
                if (_maxCacheSize != newValue)
                {
                    _maxCacheSize = newValue;
                    OnPropertyChanged();
                    Save();
                }
            }
        }

        private int _parallelThreadCount = 3;
        public int ParallelThreadCount
        {
            get => _parallelThreadCount;
            set
            {
                int newValue = Math.Max(1, Math.Min(16, value));
                if (_parallelThreadCount != newValue)
                {
                    _parallelThreadCount = newValue;
                    OnPropertyChanged();
                    Save();
                }
            }
        }

        // Save settings to file
        public void Save()
        {
            try
            {
                string directory = Path.GetDirectoryName(SettingsFilePath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(SettingsFilePath, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error saving settings: {ex.Message}");

                // Only show message box if running in UI context
                if (Application.Current?.Dispatcher != null &&
                    Application.Current.Dispatcher.CheckAccess())
                {
                    MessageBox.Show($"Error saving settings: {ex.Message}",
                        "Settings Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        // Add a folder to recent folders list
        public void AddRecentFolder(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
                return;

            // Create a new list to trigger property change
            var updatedList = new List<string>(_recentFolders);

            // Remove if already exists
            updatedList.Remove(folderPath);

            // Add to the beginning
            updatedList.Insert(0, folderPath);

            // Trim to max size
            while (updatedList.Count > MaxRecentFolders)
            {
                updatedList.RemoveAt(updatedList.Count - 1);
            }

            // Update property
            RecentFolders = updatedList;
        }

        // Clear thumbnail cache
        public void ClearThumbnailCache()
        {
            try
            {
                ImageCache.ClearCache();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error clearing thumbnail cache: {ex.Message}");
                MessageBox.Show($"Error clearing thumbnail cache: {ex.Message}",
                    "Cache Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}