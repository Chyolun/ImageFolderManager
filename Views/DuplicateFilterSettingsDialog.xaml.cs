using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MahApps.Metro.Controls;
using ImageFolderManager.Services;

namespace ImageFolderManager.Views
{
    /// <summary>
    /// Settings dialog for duplicate folder detection filters
    /// </summary>
    public partial class DuplicateFilterSettingsDialog : MetroWindow
    {
        private readonly AppSettings _settings;
        private ObservableCollection<string> _excludedFolderNames;

        public DuplicateFilterSettingsDialog()
        {
            InitializeComponent();
            _settings = AppSettings.Instance;
            InitializeSettings();
        }

        /// <summary>
        /// Initializes the dialog with current settings
        /// </summary>
        private void InitializeSettings()
        {
            // Initialize checkbox
            EnableFiltersCheckBox.IsChecked = _settings.EnableDuplicateFilters;

            // Initialize minimum length slider and textbox
            MinLengthSlider.Value = _settings.MinFolderNameLength;
            MinLengthTextBox.Text = _settings.MinFolderNameLength.ToString();

            // Initialize excluded folder names list
            _excludedFolderNames = new ObservableCollection<string>(_settings.ExcludedFolderNames);
            ExcludedFoldersListBox.ItemsSource = _excludedFolderNames;

            // Update UI state based on filter enabled status
            UpdateUIState();
        }

        /// <summary>
        /// Updates UI state based on whether filters are enabled
        /// </summary>
        private void UpdateUIState()
        {
            bool filtersEnabled = EnableFiltersCheckBox.IsChecked ?? false;

            MinLengthPanel.IsEnabled = filtersEnabled;
            ExclusionPanel.IsEnabled = filtersEnabled;
        }

        /// <summary>
        /// Handles enable filters checkbox change
        /// </summary>
        private void EnableFiltersCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            UpdateUIState();
        }

        /// <summary>
        /// Handles enable filters checkbox change
        /// </summary>
        private void EnableFiltersCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            UpdateUIState();
        }

        /// <summary>
        /// Handles minimum length slider value change
        /// </summary>
        private void MinLengthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (MinLengthTextBox != null)
            {
                MinLengthTextBox.Text = ((int)e.NewValue).ToString();
            }
        }

        /// <summary>
        /// Handles minimum length textbox text change
        /// </summary>
        private void MinLengthTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (int.TryParse(MinLengthTextBox.Text, out int value))
            {
                if (value >= 1 && value <= 50 && MinLengthSlider != null)
                {
                    MinLengthSlider.Value = value;
                }
            }
        }

        /// <summary>
        /// Handles add folder name button click
        /// </summary>
        private void AddFolderName_Click(object sender, RoutedEventArgs e)
        {
            string folderName = NewFolderNameTextBox.Text?.Trim();

            if (string.IsNullOrWhiteSpace(folderName))
            {
                MessageBox.Show("Please enter a folder name to exclude.", "Invalid Input",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Check if already exists (case-insensitive)
            if (_excludedFolderNames.Any(name =>
                string.Equals(name, folderName, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show($"Folder name '{folderName}' is already in the exclusion list.",
                    "Duplicate Entry", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _excludedFolderNames.Add(folderName);
            NewFolderNameTextBox.Clear();
        }

        /// <summary>
        /// Handles remove folder name button click
        /// </summary>
        private void RemoveFolderName_Click(object sender, RoutedEventArgs e)
        {
            if (ExcludedFoldersListBox.SelectedItem is string selectedName)
            {
                _excludedFolderNames.Remove(selectedName);
            }
            else
            {
                MessageBox.Show("Please select a folder name to remove.", "No Selection",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        /// <summary>
        /// Handles OK button click
        /// </summary>
        private void OK_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Validate minimum length
                if (!int.TryParse(MinLengthTextBox.Text, out int minLength) ||
                    minLength < 1 || minLength > 50)
                {
                    MessageBox.Show("Minimum folder name length must be between 1 and 50 characters.",
                        "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Apply settings
                _settings.EnableDuplicateFilters = EnableFiltersCheckBox.IsChecked ?? false;
                _settings.MinFolderNameLength = minLength;
                _settings.ExcludedFolderNames = _excludedFolderNames.ToList();

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving settings: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Handles Cancel button click
        /// </summary>
        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        /// <summary>
        /// Handles Enter key press in the new folder name textbox
        /// </summary>
        private void NewFolderNameTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                AddFolderName_Click(sender, e);
            }
        }

        /// <summary>
        /// Handles reset to defaults button click
        /// </summary>
        private void ResetDefaults_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "This will reset all duplicate filter settings to their default values. Continue?",
                "Reset Settings",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                EnableFiltersCheckBox.IsChecked = false;
                MinLengthSlider.Value = 3;
                MinLengthTextBox.Text = "3";
                _excludedFolderNames.Clear();
                UpdateUIState();
            }
        }
    }
}