using System;
using System.Linq;
using System.Windows;

namespace DesktopIniManager.Services;

internal static class ThemeService
{
    private static readonly string[] ThemePaths =
    {
            "Themes/DarkTheme.xaml", "Themes/LightTheme.xaml",
            "Themes/DIM1_BlueTheme.xaml", "Themes/DIM2_AmberTheme.xaml",
            "Themes/DIM3_VioletTheme.xaml", "Themes/DIM4_RedTheme.xaml",
            "Themes/DIM5_GreenTheme.xaml"
        };

    public static void Apply(bool dark) => Apply(dark ? "Dark" : "Light");

    public static void Apply(string theme)
    {
        string name = Normalize(theme);
        int index = name == "Light" ? 1 : name == "Dark" ? 0 : name[3] - '0' + 1;
        var replacement = new ResourceDictionary
        {
            Source = new Uri(ThemePaths[index], UriKind.Relative)
        };
        // Supply only missing resources; the selected file's own colors take precedence.
        if (index >= 2)
            replacement.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(ThemePaths[0], UriKind.Relative)
            });

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var current = dictionaries.FirstOrDefault(item => item.Source != null &&
            ThemePaths.Any(path => item.Source.OriginalString.EndsWith(path, StringComparison.OrdinalIgnoreCase)));
        if (current == null) dictionaries.Insert(0, replacement);
        else dictionaries[dictionaries.IndexOf(current)] = replacement;
    }

    public static string Normalize(string theme)
    {
        switch ((theme ?? string.Empty).Trim().ToUpperInvariant())
        {
            case "LIGHT": return "Light";
            case "DIM1": return "DIM1";
            case "DIM2": return "DIM2";
            case "DIM3": return "DIM3";
            case "DIM4": return "DIM4";
            case "DIM5": return "DIM5";
            default: return "Dark";
        }
    }
}
