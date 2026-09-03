using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopIniManager.Views
{
    internal sealed class SplashWindow : Window
    {
        private readonly TextBlock stage;
        private readonly TextBlock previous;
        private readonly ProgressBar progress;

        internal SplashWindow()
        {
            Title = "DesktopIniManager — Starting";
            Width = Math.Min(940, SystemParameters.WorkArea.Width * 0.9);
            Height = Width / 2;
            if (Height > SystemParameters.WorkArea.Height * 0.9)
            { Height = SystemParameters.WorkArea.Height * 0.9; Width = Height * 2; }
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Background = new SolidColorBrush(Color.FromRgb(3, 15, 28));
            var canvas = new Canvas { Width = 1774, Height = 887 };
            Content = new Viewbox { Stretch = Stretch.Uniform, Child = canvas };
            canvas.Children.Add(new Image
            {
                Width = 1774, Height = 887, Stretch = Stretch.Fill,
                Source = new BitmapImage(new Uri("pack://application:,,,/DesktopIniManager;component/Assets/splash-background.png"))
            });
            AddText(canvas, "Initializing…", 377, 484, 36, "#339AFF", 820);
            stage = AddText(canvas, "Loading startup settings…", 377, 548, 28, "#ECF2FA", 850);
            previous = AddText(canvas, "", 377, 603, 22, "#9CAFC5", 850);
            progress = new ProgressBar
            {
                Width = 700, Height = 6, Minimum = 0, Maximum = 4,
                Foreground = new SolidColorBrush(Color.FromRgb(37, 148, 255)),
                Background = new SolidColorBrush(Color.FromRgb(25, 44, 65))
            };
            Canvas.SetLeft(progress, 377); Canvas.SetTop(progress, 676); canvas.Children.Add(progress);
            AddText(canvas, "© " + DateTime.Now.Year + " ZEBRASOFT Co.,Ltd.", 1090, 786, 25, "#BCCADA", 630).TextAlignment = TextAlignment.Right;
            AddText(canvas, "By Tamayan", 1090, 824, 22, "#8EA7C2", 630).TextAlignment = TextAlignment.Right;
        }

        private static TextBlock AddText(Canvas canvas, string text, double left, double top, double size, string color, double width)
        {
            var label = new TextBlock { Text = text, FontFamily = new FontFamily("Segoe UI"), FontSize = size,
                Foreground = (Brush)new BrushConverter().ConvertFromString(color), Width = width, TextTrimming = TextTrimming.CharacterEllipsis };
            Canvas.SetLeft(label, left); Canvas.SetTop(label, top); canvas.Children.Add(label);
            return label;
        }

        internal void Report(string message, int completed)
        {
            if (stage.Text != message) previous.Text = stage.Text;
            stage.Text = message;
            progress.Value = completed;
        }
    }
}
