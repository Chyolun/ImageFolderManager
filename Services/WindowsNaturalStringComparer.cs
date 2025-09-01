using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ImageFolderManager.Services
{
    /// <summary>
    /// Windows natural string comparer that matches Windows Explorer sorting behavior.
    /// Uses the Windows Shell's StrCmpLogicalW API for consistent file system ordering.
    /// </summary>
    public class WindowsNaturalStringComparer : IComparer<string>
    {
        #region Windows API Imports

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int StrCmpLogicalW(string x, string y);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int CompareStringEx(
            string lpLocaleName,
            uint dwCmpFlags,
            string lpString1,
            int cchCount1,
            string lpString2,
            int cchCount2,
            IntPtr lpVersionInformation,
            IntPtr lpReserved,
            IntPtr lParam);

        #endregion

        #region Constants

        private const uint NORM_IGNORECASE = 0x00000001;
        private const uint SORT_STRINGSORT = 0x00001000;

        #endregion

        #region Singleton Pattern

        private static readonly Lazy<WindowsNaturalStringComparer> _instance =
            new Lazy<WindowsNaturalStringComparer>(() => new WindowsNaturalStringComparer());

        /// <summary>
        /// Gets the singleton instance of the natural string comparer
        /// </summary>
        public static WindowsNaturalStringComparer Instance => _instance.Value;

        #endregion

        #region Constructor

        private WindowsNaturalStringComparer()
        {
            // Private constructor for singleton pattern
        }

        #endregion

        #region IComparer<string> Implementation

        /// <summary>
        /// Compares two strings using Windows natural sorting logic.
        /// This matches the behavior seen in Windows Explorer.
        /// </summary>
        /// <param name="x">First string to compare</param>
        /// <param name="y">Second string to compare</param>
        /// <returns>
        /// Less than zero: x is less than y
        /// Zero: x equals y  
        /// Greater than zero: x is greater than y
        /// </returns>
        public int Compare(string x, string y)
        {
            // Handle null cases
            if (x == null && y == null) return 0;
            if (x == null) return -1;
            if (y == null) return 1;

            try
            {
                // Use Windows Shell's logical string comparison
                // This handles numeric sequences naturally (1, 2, 10 instead of 1, 10, 2)
                return StrCmpLogicalW(x, y);
            }
            catch (Exception)
            {
                // Fallback to case-insensitive string comparison if API fails
                return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
            }
        }

        #endregion

        #region Alternative Comparison Methods

        /// <summary>
        /// Alternative comparison using CompareStringEx API with locale support.
        /// This can be used as a fallback if StrCmpLogicalW is not available.
        /// </summary>
        /// <param name="x">First string to compare</param>
        /// <param name="y">Second string to compare</param>
        /// <param name="locale">Locale name (null for system default)</param>
        /// <returns>Comparison result</returns>
        public int CompareWithLocale(string x, string y, string locale = null)
        {
            // Handle null cases
            if (x == null && y == null) return 0;
            if (x == null) return -1;
            if (y == null) return 1;

            try
            {
                // Use locale-aware comparison with case-insensitive and string sort flags
                int result = CompareStringEx(
                    locale, // null for system default
                    NORM_IGNORECASE | SORT_STRINGSORT,
                    x, x.Length,
                    y, y.Length,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

                // CompareStringEx returns 1, 2, or 3 for less than, equal, or greater than
                // Convert to standard comparer return values
                return result - 2;
            }
            catch (Exception)
            {
                // Fallback to basic string comparison
                return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
            }
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Checks if two strings are equal using natural comparison
        /// </summary>
        /// <param name="x">First string</param>
        /// <param name="y">Second string</param>
        /// <returns>True if strings are equal under natural comparison</returns>
        public bool Equals(string x, string y)
        {
            return Compare(x, y) == 0;
        }

        /// <summary>
        /// Determines if the first string comes before the second in natural sort order
        /// </summary>
        /// <param name="x">First string</param>
        /// <param name="y">Second string</param>
        /// <returns>True if x comes before y in natural sort order</returns>
        public bool IsLessThan(string x, string y)
        {
            return Compare(x, y) < 0;
        }

        /// <summary>
        /// Determines if the first string comes after the second in natural sort order
        /// </summary>
        /// <param name="x">First string</param>
        /// <param name="y">Second string</param>
        /// <returns>True if x comes after y in natural sort order</returns>
        public bool IsGreaterThan(string x, string y)
        {
            return Compare(x, y) > 0;
        }

        #endregion

        #region Static Helper Methods

        /// <summary>
        /// Static helper method for quick natural string comparison
        /// </summary>
        /// <param name="x">First string to compare</param>
        /// <param name="y">Second string to compare</param>
        /// <returns>Comparison result</returns>
        public static int CompareNatural(string x, string y)
        {
            return Instance.Compare(x, y);
        }

        /// <summary>
        /// Sorts a collection of strings using natural ordering
        /// </summary>
        /// <param name="strings">Collection of strings to sort</param>
        /// <returns>Sorted list using natural ordering</returns>
        public static List<string> SortNatural(IEnumerable<string> strings)
        {
            var list = new List<string>(strings);
            list.Sort(Instance);
            return list;
        }

        #endregion
    }
}