using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using ImageFolderManager.Services;
using MahApps.Metro.Controls;

namespace ImageFolderManager.Views
{
    public partial class BatchTagsDialog : MetroWindow
    {
        public HashSet<string> TagsToAdd { get; private set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> TagsToRemove { get; private set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public int FolderCount { get; private set; }
        public List<string> CommonTags { get; private set; }

        public BatchTagsDialog(int folderCount, List<string> commonTags)
        {
            InitializeComponent();

            FolderCount = folderCount;
            CommonTags = commonTags ?? new List<string>();

            // Update UI
            SelectedFoldersText.Text = $"Selected folders: {FolderCount}";

            // Display common tags if any
            if (CommonTags.Count > 0)
            {
                CommonTagsTextBlock.Text = string.Join(" ", CommonTags.Select(tag => $"#{tag}"));
            }
            else
            {
                CommonTagsTextBlock.Text = "No common tags found";
            }

            // Set focus to the add tags textbox
            AddTagsTextBox.Loaded += (s, e) =>
            {
                AddTagsTextBox.Focus();
            };
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            // Parse tags to add
            ParseTags(AddTagsTextBox.Text, TagsToAdd);

            // Parse tags to remove
            ParseTags(RemoveTagsTextBox.Text, TagsToRemove);

            // If both are empty, ask for confirmation
            if (TagsToAdd.Count == 0 && TagsToRemove.Count == 0)
            {
                var result = MessageBox.Show(
                    "No tags specified for adding or removing. Do you want to continue?",
                    "No Tags Specified",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.No)
                {
                    return;
                }
            }

            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void ParseTags(string input, HashSet<string> tagSet)
        {
            // Use TagHelper for parsing
            var parsedTags = TagHelper.ParseTags(input);

            tagSet.Clear();
            foreach (var tag in parsedTags)
            {
                tagSet.Add(tag);
            }
        }
    }
}