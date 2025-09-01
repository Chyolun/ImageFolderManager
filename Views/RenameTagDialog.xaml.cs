using System;
using System.Windows;
using MahApps.Metro.Controls;

namespace ImageFolderManager.Views
{
    public partial class RenameTagDialog : MetroWindow
    {
        public string CurrentTag { get; private set; }
        public string NewTag { get; private set; }

        public RenameTagDialog(string currentTag)
        {
            InitializeComponent();
            CurrentTag = currentTag;
            CurrentTagText.Text = currentTag;
            NewTagTextBox.Text = currentTag;

            // Set focus to the text box and select all text
            NewTagTextBox.Loaded += (s, e) => {
                NewTagTextBox.Focus();
                NewTagTextBox.SelectAll();
            };
        }

        private void Rename_Click(object sender, RoutedEventArgs e)
        {
            string newTag = NewTagTextBox.Text?.Trim();

            if (string.IsNullOrWhiteSpace(newTag))
            {
                MessageBox.Show("Tag name cannot be empty.", "Invalid Tag Name", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            NewTag = newTag;
            this.DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            // Use the built-in DialogResult property
            this.DialogResult = false;
        }
    }
}