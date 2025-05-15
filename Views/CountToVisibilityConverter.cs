using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ImageFolderManager.Views
{
    /// <summary>
    /// Converter that returns Visibility.Collapsed when count > 0, otherwise Visibility.Visible
    /// Used to show placeholder message when tag cloud is empty
    /// </summary>
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
}