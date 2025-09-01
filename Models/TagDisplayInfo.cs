using System;

namespace ImageFolderManager.Models
{
    /// <summary>
    /// Represents a tag with its category information for display
    /// </summary>
    public class TagDisplayInfo
    {
        public string TagName { get; set; }
        public string Category { get; set; } = "Uncategorized";

        // For tooltip display
        public string TooltipText => $"{Category}: {TagName}";

        // For display in UI
        public string DisplayText => $"#{TagName}";

        public TagDisplayInfo(string tagName, string category = "Uncategorized")
        {
            TagName = tagName?.Trim() ?? string.Empty;
            Category = category?.Trim() ?? "Uncategorized";
        }
    }
}