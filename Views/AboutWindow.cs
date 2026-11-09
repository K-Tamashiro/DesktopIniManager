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
    /// <summary>
    ///     バージョン情報ウィンドウを作成し、アプリケーションのバージョンと使用しているライブラリ・API の一覧を表示します。
    /// </summary>
    internal AboutWindow()
    {
        Title = StringOverlay.Get("About_WindowTitle");
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
            Text = string.Format(StringOverlay.Get("About_Version"), version, Environment.Is64BitProcess ? "64" : "32"),
            Margin = new Thickness(0, 6, 0, 0)
        });
        // Keep the original version area's height; the longer contents scroll inside it.
        details.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var versionHeight = details.DesiredSize.Height;
        details.Children.Add(new TextBlock
        {
            Text = StringOverlay.Get("About_Libraries"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 14, 0, 6)
        });
        AddComponent(details, ".NET / WPF", Environment.Version.ToString(),
            StringOverlay.Get("About_WpfDescription"));
        AddComponent(details, "Windows API", null, StringOverlay.Get("About_WindowsApiDescription"));
        AddComponent(details, "ExcelDataReader", VersionOf(typeof(ExcelReaderFactory).Assembly),
            StringOverlay.Get("About_ExcelDescription"), "ExcelDataReader-LICENSE.txt");
        AddComponent(details, "PdfPig", VersionOf(typeof(PdfDocument).Assembly),
            StringOverlay.Get("About_PdfDescription"), "PdfPig-LICENSE.txt");
        AddComponent(details, "NAudio", VersionOf(typeof(AudioFileReader).Assembly),
            StringOverlay.Get("About_NAudioDescription"), "NAudio-LICENSE.txt");
        AddComponent(details, "System.IO.Hashing", VersionOf(typeof(XxHash64).Assembly),
            StringOverlay.Get("About_HashDescription"), "System.IO.Hashing-NOTICES.txt", StringOverlay.Get("About_NoticesLink"));
        AddComponent(details, "Mathertel Diff", null,
            StringOverlay.Get("About_DiffDescription"), "Mathertel-Diff-LICENSE.txt");
        AddComponent(details, "FastVolumeIndex.Core", null, StringOverlay.Get("About_IndexDescription"));
        footer.Children.Add(new ScrollViewer
        {
            Height = versionHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = details,
            ToolTip = StringOverlay.Get("About_ScrollTip")
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

    /// <summary>
    ///     アセンブリの表示用バージョンを取得します。ビルド メタデータ (+ 以降) は除きます。
    /// </summary>
    /// <param name="assembly">対象のアセンブリ。</param>
    /// <returns>バージョン文字列。</returns>
    private static string VersionOf(Assembly assembly)
    {
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
               ?? assembly.GetName().Version?.ToString();
    }

    /// <summary>
    ///     使用しているコンポーネントの名前、バージョン、説明、ライセンスへのリンクをパネルに追加します。
    /// </summary>
    /// <param name="panel">追加先のパネル。</param>
    /// <param name="name">コンポーネント名。</param>
    /// <param name="version">バージョン。</param>
    /// <param name="description">説明。</param>
    /// <param name="license">埋め込みのライセンス ファイル名。null の場合はリンクを表示しません。</param>
    /// <param name="linkLabel">ライセンス リンクの表示文字列。</param>
    private void AddComponent(Panel panel, string name, string version, string description,
        string license = null, string linkLabel = null)
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
            var link = new Hyperlink(new Run(linkLabel ?? StringOverlay.Get("About_LicenseLink")));
            link.SetResourceReference(TextElement.ForegroundProperty, "Accent");
            link.Click += (_, _) => ShowLicense(name, license);
            text.Inlines.Add(link);
            item.Children.Add(text);
        }

        panel.Children.Add(item);
    }

    /// <summary>
    ///     埋め込みリソースのライセンス文書を別ウィンドウで表示します。
    /// </summary>
    /// <param name="component">コンポーネント名。</param>
    /// <param name="file">ライセンス ファイル名。</param>
    private void ShowLicense(string component, string file)
    {
        // Embed the distributed originals so the dialog also works when launched elsewhere.
        using var stream = typeof(AboutWindow).Assembly.GetManifestResourceStream("DIM.Licenses." + file);
        if (stream == null)
        {
            MessageBox.Show(this, string.Format(StringOverlay.Get("About_LicenseMissing"), file), Title, MessageBoxButton.OK,
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