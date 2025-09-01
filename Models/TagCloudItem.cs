using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace ImageFolderManager.Models
{
    public class TagCloudItem : INotifyPropertyChanged
    {
        private string _tag;
        public string Tag
        {
            get => _tag;
            set
            {
                if (_tag != value)
                {
                    _tag = value;
                    OnPropertyChanged();
                }
            }
        }

        private string _category = "Uncategorized";
        public string Category
        {
            get => _category;
            set
            {
                if (_category != value)
                {
                    _category = value ?? "Uncategorized";
                    OnPropertyChanged();
                }
            }
        }

        private int _count;
        public int Count
        {
            get => _count;
            set
            {
                if (_count != value)
                {
                    _count = value;
                    OnPropertyChanged();
                }
            }
        }

        private double _fontSize;
        public double FontSize
        {
            get => _fontSize;
            set
            {
                if (_fontSize != value)
                {
                    _fontSize = value;
                    OnPropertyChanged();
                }
            }
        }

        private SolidColorBrush _color;
        public SolidColorBrush Color
        {
            get => _color;
            set
            {
                if (_color != value)
                {
                    _color = value;
                    OnPropertyChanged();
                }
            }
        }

        // For tooltip display
        public string CountDisplay => $"Used in {Count} folder{(Count == 1 ? "" : "s")} (Category: {Category})";

        // Full tag identifier including category (for storage and reference)
        public string FullTagIdentifier => $"{Category}::{Tag}";

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        // Create a copy of this tag item
        public TagCloudItem Clone()
        {
            return new TagCloudItem
            {
                Tag = this.Tag,
                Category = this.Category,
                Count = this.Count,
                FontSize = this.FontSize,
                Color = this.Color
            };
        }
    }

    /// <summary>
    /// Represents a category in the tag cloud
    /// </summary>
    public class TagCategory : INotifyPropertyChanged
    {
        private string _name;
        public string Name
        {
            get => _name;
            set
            {
                if (_name != value)
                {
                    _name = value;
                    OnPropertyChanged();
                    // Also notify that DisplayName property has changed
                    OnPropertyChanged(nameof(DisplayName));
                }
            }
        }

        private int _tagCount;
        public int TagCount
        {
            get => _tagCount;
            set
            {
                if (_tagCount != value)
                {
                    _tagCount = value;
                    OnPropertyChanged();
                    // Also notify that DisplayName property has changed
                    OnPropertyChanged(nameof(DisplayName));
                }
            }
        }

        // Display name for the tab
        public string DisplayName => TagCount > 0 ? $"{Name} ({TagCount})" : Name;

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}