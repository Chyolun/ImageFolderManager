using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;

namespace ImageFolderManager.Views
{
    public class CountToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int count)
            {
                return count > 0 ? Visibility.Collapsed : Visibility.Visible;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
    /// <summary>
    /// Optimized converter that transforms a rating value directly into a string of stars
    /// </summary>
    public class RatingToStarsDisplayConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
                return "☆☆☆☆☆";  // Default: all empty stars

            // Get the rating value (should be between 0-5)
            if (!int.TryParse(value.ToString(), out int rating))
                return "☆☆☆☆☆";

            // Ensure rating is within range
            rating = Math.Max(0, Math.Min(5, rating));

            // Build the star string efficiently
            StringBuilder stars = new StringBuilder(5);

            // Add filled stars
            for (int i = 0; i < rating; i++)
            {
                stars.Append('★');
            }

            // Add empty stars
            for (int i = rating; i < 5; i++)
            {
                stars.Append('☆');
            }

            return stars.ToString();
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converter that transforms a collection of tags into a formatted string
    /// with an optional fallback message when no tags are present
    /// </summary>
    public class TagsToStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
                return "No tags";

            var tags = value as System.Collections.ObjectModel.ObservableCollection<string>;
            if (tags == null || tags.Count == 0)
                return "No tags";

            // Format tags with # prefix
            return string.Join(" ", tags.Select(tag => $"#{tag}"));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converter that returns visibility based on whether tags exist
    /// </summary>
    public class HasTagsToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
                return System.Windows.Visibility.Collapsed;

            var tags = value as System.Collections.ObjectModel.ObservableCollection<string>;
            return (tags != null && tags.Count > 0)
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
