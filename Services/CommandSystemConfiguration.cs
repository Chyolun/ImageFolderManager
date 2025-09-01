using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace ImageFolderManager.Services
{
    /// <summary>
    /// Configuration settings for the command system and state machine
    /// </summary>
    public class CommandSystemConfiguration
    {
        // Path locking configuration
        public TimeSpan LockTimeout { get; set; } = TimeSpan.FromMinutes(5);
        public TimeSpan DeadlockCheckInterval { get; set; } = TimeSpan.FromSeconds(30);

        // State machine configuration
        public TimeSpan StateCleanupInterval { get; set; } = TimeSpan.FromMinutes(5);
        public TimeSpan StaleStateTimeout { get; set; } = TimeSpan.FromHours(1);

        // Command execution configuration
        public int MaxConcurrentCommands { get; set; } = Environment.ProcessorCount;
        public int CommandHistoryLimit { get; set; } = 100;
        public bool EnableCommandLogging { get; set; } = true;

        // Exception handling configuration
        public string LogDirectory { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ImageFolderManager", "Logs");
        public int MaxLogEntries { get; set; } = 10000;
        public TimeSpan LogFlushInterval { get; set; } = TimeSpan.FromSeconds(5);

        // Performance configuration
        public bool EnablePerformanceMetrics { get; set; } = false;
        public TimeSpan PerformanceMetricsInterval { get; set; } = TimeSpan.FromMinutes(1);

        // Default instance
        private static CommandSystemConfiguration _instance;
        private static readonly object _lock = new object();

        public static CommandSystemConfiguration Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = LoadConfiguration();
                        }
                    }
                }
                return _instance;
            }
        }

        private static CommandSystemConfiguration LoadConfiguration()
        {
            try
            {
                var configPath = GetConfigurationFilePath();

                if (File.Exists(configPath))
                {
                    var json = File.ReadAllText(configPath);
                    return JsonConvert.DeserializeObject<CommandSystemConfiguration>(json)
                           ?? new CommandSystemConfiguration();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load command system configuration: {ex.Message}");
            }

            return new CommandSystemConfiguration();
        }

        public void SaveConfiguration()
        {
            try
            {
                var configPath = GetConfigurationFilePath();
                var configDir = Path.GetDirectoryName(configPath);

                if (!Directory.Exists(configDir))
                    Directory.CreateDirectory(configDir);

                var json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(configPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save command system configuration: {ex.Message}");
            }
        }

        private static string GetConfigurationFilePath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ImageFolderManager",
                "command_system_config.json");
        }

        /// <summary>
        /// Reset to default values
        /// </summary>
        public void ResetToDefaults()
        {
            LockTimeout = TimeSpan.FromMinutes(5);
            DeadlockCheckInterval = TimeSpan.FromSeconds(30);
            StateCleanupInterval = TimeSpan.FromMinutes(5);
            StaleStateTimeout = TimeSpan.FromHours(1);
            MaxConcurrentCommands = Environment.ProcessorCount;
            CommandHistoryLimit = 100;
            EnableCommandLogging = true;
            LogDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ImageFolderManager", "Logs");
            MaxLogEntries = 10000;
            LogFlushInterval = TimeSpan.FromSeconds(5);
            EnablePerformanceMetrics = false;
            PerformanceMetricsInterval = TimeSpan.FromMinutes(1);
        }

        /// <summary>
        /// Validate configuration values
        /// </summary>
        public bool ValidateConfiguration(out string[] errors)
        {
            var errorList = new List<string>();

            if (LockTimeout <= TimeSpan.Zero)
                errorList.Add("Lock timeout must be greater than zero");

            if (DeadlockCheckInterval <= TimeSpan.Zero)
                errorList.Add("Deadlock check interval must be greater than zero");

            if (MaxConcurrentCommands <= 0)
                errorList.Add("Max concurrent commands must be greater than zero");

            if (CommandHistoryLimit <= 0)
                errorList.Add("Command history limit must be greater than zero");

            if (MaxLogEntries <= 0)
                errorList.Add("Max log entries must be greater than zero");

            if (string.IsNullOrWhiteSpace(LogDirectory))
                errorList.Add("Log directory cannot be empty");

            errors = errorList.ToArray();
            return errorList.Count == 0;
        }
    }
}