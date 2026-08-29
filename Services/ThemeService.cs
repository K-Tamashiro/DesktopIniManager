using System;
using System.Linq;
using System.Windows;

namespace DesktopIniManager.Services
{
    internal static class ThemeService
    {
        public static void Apply(bool dark)
        {
            var dictionaries = Application.Current.Resources.MergedDictionaries;
            var current = dictionaries.FirstOrDefault(item => item.Source != null && item.Source.OriginalString.StartsWith("Themes/", StringComparison.OrdinalIgnoreCase));
            var replacement = new ResourceDictionary { Source = new Uri(dark ? "Themes/DarkTheme.xaml" : "Themes/LightTheme.xaml", UriKind.Relative) };
            if (current == null) dictionaries.Insert(0, replacement); else dictionaries[dictionaries.IndexOf(current)] = replacement;
        }
    }
}
