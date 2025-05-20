using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using ImageFolderManager.Models;
using Newtonsoft.Json;

namespace ImageFolderManager.Services
{
    /// <summary>
    /// Application settings service
    /// </summary>
    public class AppSettings : INotifyPropertyChanged
    {
        private static readonly string SettingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ImageFolderManager",
            "settings.json");

        #region Property Definitions

        // Default settings
        private string _defaultRootDirectory = string.Empty;

        private int _maxCacheSize = 512;
        public int TrimThreshold => (int)(MaxCacheSize * 1.2);
        public int TrimTarget => (int)(MaxCacheSize * 0.7);

        private int _previewHeight = 500;
        private int _previewWidth = 500;
        private bool _autoExpandFolders = false;
        private int _maxRecentFolders = 10;
        private List<string> _recentFolders = new List<string>();
        private int _parallelThreadCount = 3;

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
                int newValue = ValidateRange(value, 100, 1000);
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
                int newValue = ValidateRange(value, 100, 1000);
                if (_previewHeight != newValue)
                {
                    _previewHeight = newValue;
                    OnPropertyChanged();
                    Save();
                }
            }
        }

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

        public int MaxRecentFolders
        {
            get => _maxRecentFolders;
            set
            {
                if (_maxRecentFolders != value)
                {
                    _maxRecentFolders = ValidateRange(value, 1, 20);
                    OnPropertyChanged();
                    Save();
                }
            }
        }

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

        public int MaxCacheSize
        {
            get => _maxCacheSize;
            set
            {
                int newValue = ValidateRange(value, 100, 2000);
                if (_maxCacheSize != newValue)
                {
                    _maxCacheSize = newValue;
                    OnPropertyChanged();
                    Save();
                }
            }
        }

        public int ParallelThreadCount
        {
            get => _parallelThreadCount;
            set
            {
                int newValue = ValidateRange(value, 1, 16);
                if (_parallelThreadCount != newValue)
                {
                    _parallelThreadCount = newValue;
                    OnPropertyChanged();
                    Save();
                }
            }
        }

        #endregion

        #region Singleton Implementation

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

        // Private constructor - singleton pattern
        private AppSettings() { }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Validates value is within specified range
        /// </summary>
        private int ValidateRange(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        /// <summary>
        /// Loads settings from file
        /// </summary>
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
                        // Copy properties to new instance to ensure validators are applied
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

        /// <summary>
        /// Saves settings to file
        /// </summary>
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

                // Only show message box in UI context
                if (Application.Current?.Dispatcher != null &&
                    Application.Current.Dispatcher.CheckAccess())
                {
                    MessageBox.Show($"Error saving settings: {ex.Message}",
                        "Settings Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        /// <summary>
        /// Adds a folder to recent folders list
        /// </summary>
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

        /// <summary>
        /// Clears thumbnail cache
        /// </summary>
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

        #endregion

        #region INotifyPropertyChanged Implementation

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion
    }
}