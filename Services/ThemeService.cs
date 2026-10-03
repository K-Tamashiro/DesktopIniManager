using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace DesktopIniManager.Services
{
    internal static class ThemeService
    {
        public static void Apply(bool dark) => Apply(dark ? "Dark" : "Light");

        public static void Apply(string theme)
        {
            string name = Normalize(theme);

            // DIM1-DIM5 are colour variations of the normal dark theme.
            // Only DarkTheme.xaml / LightTheme.xaml are loaded from resources,
            // so no additional XAML resource files are required by the project.
            bool light = string.Equals(name, "Light", StringComparison.OrdinalIgnoreCase);
            var replacement = new ResourceDictionary
            {
                Source = new Uri(light ? "Themes/LightTheme.xaml" : "Themes/DarkTheme.xaml", UriKind.Relative)
            };

            if (!light && name.StartsWith("DIM", StringComparison.OrdinalIgnoreCase))
                ApplyDimPalette(replacement, name);

            var dictionaries = Application.Current.Resources.MergedDictionaries;
            var current = dictionaries.FirstOrDefault(item => item.Source != null &&
                (item.Source.OriginalString.EndsWith("Themes/DarkTheme.xaml", StringComparison.OrdinalIgnoreCase) ||
                 item.Source.OriginalString.EndsWith("Themes/LightTheme.xaml", StringComparison.OrdinalIgnoreCase)));

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

        private static void ApplyDimPalette(ResourceDictionary resources, string theme)
        {
            string[] colors;
            switch (theme)
            {
                case "DIM1": colors = new[] { "#101923", "#192838", "#2F80ED", "#2467BE", "#172B42", "#EAF2FC", "#A9BCD1", "#304A64", "#46647F", "#536A80", "#182535", "#204B78", "#69A9FF" }; break;
                case "DIM2": colors = new[] { "#1B1710", "#2A2418", "#F0A51A", "#C88412", "#332817", "#F7F0E3", "#C9B99B", "#5A4930", "#756144", "#806F55", "#292217", "#664817", "#FFC247" }; break;
                case "DIM3": colors = new[] { "#17131F", "#261F33", "#A86AE8", "#8652BB", "#2E203C", "#F2ECFA", "#BCAECF", "#4C3B62", "#65517D", "#716383", "#241D30", "#563A73", "#C18BFF" }; break;
                case "DIM4": colors = new[] { "#1D1214", "#2D1D20", "#E45151", "#B83D3D", "#352022", "#F8ECEC", "#CBB0B0", "#604044", "#79545A", "#826268", "#2B1B1E", "#693235", "#FF7777" }; break;
                default:     colors = new[] { "#101C18", "#192C25", "#35B879", "#298F5E", "#17352A", "#EAF7F1", "#A8C9BA", "#315748", "#477363", "#557C6D", "#182A23", "#235C43", "#5DDB9B" }; break;
            }

            Set(resources, "WindowBackground", colors[0]);
            Set(resources, "HeaderBackground", colors[0]);
            Set(resources, "CardBackground", colors[1]);
            Set(resources, "Accent", colors[2]);
            Set(resources, "ButtonHover", colors[3]);
            Set(resources, "AccentSoft", colors[4]);
            Set(resources, "Ink", colors[5]);
            Set(resources, "Muted", colors[6]);
            Set(resources, "Line", colors[7]);
            Set(resources, "InputLine", colors[8]);
            Set(resources, "Secondary", colors[9]);
            Set(resources, "ThemeToggle", colors[10]);
            Set(resources, "ThemeSelected", colors[11]);
            Set(resources, "HistoryTabBackground", colors[1]);
            Set(resources, "HistoryTabSelectedBackground", colors[0]);
            Set(resources, "HistoryTabSelectedForeground", colors[12]);
        }

        private static void Set(ResourceDictionary resources, string key, string color)
        {
            resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        }
    }
}
