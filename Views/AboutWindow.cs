using DesktopIniManager.Properties;
using ExcelDataReader;
using NAudio.Wave;
using System;
using System.IO;
using System.IO.Hashing;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UglyToad.PdfPig;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace DesktopIniManager.Views;

internal sealed class AboutWindow : Window
{
    internal AboutWindow()
    {
        Title = "DesktopIniManager — About";
        Width = 660;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "WindowBackground");
        SetResourceReference(ForegroundProperty, "Ink");
        var layout = new StackPanel();
        var image = new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/DesktopIniManager;component/Assets/Version.png")),
            Stretch = Stretch.Uniform
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        layout.Children.Add(image);
        var footer = new Grid { Margin = new Thickness(20, 16, 20, 18) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var assembly = typeof(MainWindow).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString() ?? "—";
        details.Children.Add(new TextBlock
        { Text = "DesktopIniManager", FontSize = 20, FontWeight = FontWeights.SemiBold });
        details.Children.Add(new TextBlock
        {
            Text = $"Version {version}  ·  {(Environment.Is64BitProcess ? "64" : "32")}-bit",
            Margin = new Thickness(0, 6, 0, 0)
        });
        // Keep the original version area's height; the longer contents scroll inside it.
        details.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var versionHeight = details.DesiredSize.Height;
        details.Children.Add(new TextBlock
        {
            Text = "Libraries / APIs",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 14, 0, 6)
        });
        AddComponent(details, ".NET / WPF", Environment.Version.ToString(),
            "UI rendering, image decoding, GIF / video playback");
        AddComponent(details, "Windows API", null, "Shell / User32 / Kernel32 — file, icon, and window operations");
        AddComponent(details, "ExcelDataReader", VersionOf(typeof(ExcelReaderFactory).Assembly),
            "Excel file reading · MIT", "ExcelDataReader-LICENSE.txt");
        AddComponent(details, "PdfPig", VersionOf(typeof(PdfDocument).Assembly),
            "PDF text extraction · Apache-2.0", "PdfPig-LICENSE.txt");
        AddComponent(details, "NAudio", VersionOf(typeof(AudioFileReader).Assembly),
            "Audio decoding and playback · MIT", "NAudio-LICENSE.txt");
        AddComponent(details, "System.IO.Hashing", VersionOf(typeof(XxHash64).Assembly),
            "Hashing for file comparison", "System.IO.Hashing-NOTICES.txt", "Bundled notices TXT");
        AddComponent(details, "Mathertel Diff", null,
            "Text diff algorithm (DIM-modified) · BSD-3-Clause", "Mathertel-Diff-LICENSE.txt");
        AddComponent(details, "FastVolumeIndex.Core", null, "DIM file indexing and search component");
        footer.Children.Add(new ScrollViewer
        {
            Height = versionHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = details,
            ToolTip = "Scroll to view libraries, APIs, and licenses"
        });
        var close = new Button
        {
            Content = StringOverlay.Get("Common_Close"),
            MinWidth = 96,
            Margin = new Thickness(20, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Bottom,
            IsDefault = true,
            IsCancel = true
        };
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1);
        footer.Children.Add(close);
        layout.Children.Add(footer);
        Content = layout;
    }

    private static string VersionOf(Assembly assembly)
    {
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
               ?? assembly.GetName().Version?.ToString();
    }

    private void AddComponent(Panel panel, string name, string version, string description,
        string license = null, string linkLabel = "License TXT")
    {
        var item = new StackPanel { Margin = new Thickness(0, 0, 10, 12) };
        item.Children.Add(new TextBlock
        {
            Text = name + (string.IsNullOrEmpty(version) ? "" : "  " + version),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        item.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap });
        if (license != null)
        {
            var text = new TextBlock { Margin = new Thickness(0, 3, 0, 0) };
            var link = new Hyperlink(new Run(linkLabel));
            link.SetResourceReference(Hyperlink.ForegroundProperty, "Accent");
            link.Click += (_, _) => ShowLicense(name, license);
            text.Inlines.Add(link);
            item.Children.Add(text);
        }

        panel.Children.Add(item);
    }

    private void ShowLicense(string component, string file)
    {
        // Embed the distributed originals so the dialog also works when launched elsewhere.
        using var stream = typeof(AboutWindow).Assembly.GetManifestResourceStream("DIM.Licenses." + file);
        if (stream == null)
        {
            MessageBox.Show(this, "License document not found: " + file, Title, MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        using var reader = new StreamReader(stream);
        var dialog = new Window
        {
            Owner = this,
            Title = component + " — " + file,
            Width = 720,
            Height = 520,
            MinWidth = 420,
            MinHeight = 260,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        dialog.SetResourceReference(BackgroundProperty, "WindowBackground");
        dialog.SetResourceReference(ForegroundProperty, "Ink");
        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var content = new TextBox
        {
            Text = reader.ReadToEnd(),
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            Padding = new Thickness(10)
        };
        content.SetResourceReference(BackgroundProperty, "CardBackground");
        content.SetResourceReference(ForegroundProperty, "Ink");
        grid.Children.Add(content);
        var close = new Button
        {
            Content = StringOverlay.Get("Common_Close"),
            IsCancel = true,
            IsDefault = true,
            MinWidth = 96,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        close.Click += (_, _) => dialog.Close();
        Grid.SetRow(close, 1);
        grid.Children.Add(close);
        dialog.Content = grid;
        dialog.ShowDialog();
    }
}