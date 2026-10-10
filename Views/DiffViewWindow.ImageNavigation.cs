using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopIniManager.Views;

internal sealed partial class DiffViewWindow
{
    private const double NavigatorWidth = 100, NavigatorHeight = 60;
    private Canvas imageNavigator;
    private Thumb imageViewport;
    private double navigationScale, navigationLeft, navigationTop, imageWidth, imageHeight;
    private Point navigationDrag;
    private TextBlock sourcePixelDetails, targetPixelDetails;
    private BitmapSource sourcePixels, targetPixels;

    private void ResetImageNavigation()
    {
        StopImagePan();
        if (imageViewport?.IsMouseCaptured == true) imageViewport.ReleaseMouseCapture();
        imageControlRow.Visibility = Visibility.Collapsed;
        imageDetails.Children.Clear();
        imageNavigator = null;
        imageViewport = null;
        sourcePixels = targetPixels = null;
        sourcePixelDetails = targetPixelDetails = null;
    }

    private void BuildImageNavigation(double width, double height)
    {
        imageWidth = width; imageHeight = height;
        navigationScale = Math.Min(NavigatorWidth / Math.Max(1, width), NavigatorHeight / Math.Max(1, height));
        navigationLeft = (NavigatorWidth - width * navigationScale) / 2;
        navigationTop = (NavigatorHeight - height * navigationScale) / 2;
        sourcePixels = PixelImage(ViewModel.SourceImage);
        targetPixels = PixelImage(ViewModel.TargetImage);
        imageDetails.Children.Clear();
        var sizes = new TextBlock
        {
            Text = "Source: " + Dimensions(ViewModel.SourceImage) + "\nTarget: " + Dimensions(ViewModel.TargetImage),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(0, 0, 8, 0)
        };
        sizes.SetBinding(TextBlock.FontSizeProperty, new System.Windows.Data.Binding(nameof(TextBlock.FontSize)) { Source = targetHeader });
        sizes.SetBinding(TextBlock.FontFamilyProperty, new System.Windows.Data.Binding(nameof(TextBlock.FontFamily)) { Source = targetHeader });
        sizes.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        imageDetails.Children.Add(sizes);
        imageNavigator = new Canvas
        {
            Width = NavigatorWidth,
            Height = NavigatorHeight,
            ClipToBounds = true,
            Background = Brushes.Black,
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "Source + Target 50% / differences highlighted. Drag the frame to navigate."
        };
        imageNavigator.Children.Add(new Image
        {
            Source = CreateNavigationBitmap(ViewModel.SourceImage, ViewModel.TargetImage),
            Width = NavigatorWidth,
            Height = NavigatorHeight,
            IsHitTestVisible = false
        });
        imageViewport = new Thumb { Cursor = Cursors.SizeAll, Focusable = false };
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        border.SetValue(Border.BorderBrushProperty, Brushes.Cyan);
        border.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(25, 0, 255, 255)));
        imageViewport.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = border };
        imageViewport.DragStarted += (_, _) => navigationDrag = new Point(leftScroll.HorizontalOffset, leftScroll.VerticalOffset);
        imageViewport.DragDelta += (_, e) =>
        {
            double scale = (imageZoom?.Value ?? 1) / navigationScale;
            navigationDrag.X += e.HorizontalChange * scale;
            navigationDrag.Y += e.VerticalChange * scale;
            navigationDrag = ScrollImagesTo(navigationDrag.X, navigationDrag.Y);
        };
        imageNavigator.Children.Add(imageViewport);
        imageNavigator.MouseMove += (_, e) =>
        {
            var point = e.GetPosition(imageNavigator);
            SetImagePointDetails(new Point((point.X - navigationLeft) / navigationScale,
                (point.Y - navigationTop) / navigationScale), false);
        };
        imageNavigator.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && FindAncestor<Thumb>(d) != null) return;
            var point = e.GetPosition(imageNavigator);
            double zoom = imageZoom?.Value ?? 1;
            ScrollImagesTo((point.X - navigationLeft) / navigationScale * zoom - leftScroll.ViewportWidth / 2,
                (point.Y - navigationTop) / navigationScale * zoom - leftScroll.ViewportHeight / 2);
            e.Handled = true;
        };
        imageDetails.Children.Add(imageNavigator);
        sourcePixelDetails = AddPixelCard("Source", "SourceColor");
        targetPixelDetails = AddPixelCard("Target", "TargetColor");
        SetImagePointDetails(new Point(-1, -1), false);
    }

    private TextBlock AddPixelCard(string name, string color)
    {
        var text = new TextBlock { FontSize = 11, Width = 108, Height = 42, LineHeight = 14, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        var card = new Border
        {
            Child = text,
            Padding = new Thickness(4),
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            ToolTip = name
        };
        card.SetResourceReference(Border.BackgroundProperty, "CardBackground");
        card.SetResourceReference(Border.BorderBrushProperty, color);
        imageDetails.Children.Add(card);
        return text;
    }

    private static string Dimensions(BitmapSource image) => image == null ? "—" : $"{image.PixelWidth} × {image.PixelHeight} px";

    private void SetImagePointDetails(Point point, bool rememberOrigin)
    {
        if (sourcePixelDetails == null) return;
        // During a drag keep the clicked pixel; mouse-up resumes live hover.
        if (imagePanning && !rememberOrigin) return;
        sourcePixelDetails.Text = DescribePixel(sourcePixels, point);
        targetPixelDetails.Text = DescribePixel(targetPixels, point);
    }
    internal static string DescribePixel(BitmapSource pixels, Point point)
    {
        int x = (int)Math.Floor(point.X), y = (int)Math.Floor(point.Y);
        if (pixels == null || x < 0 || y < 0 || x >= pixels.PixelWidth || y >= pixels.PixelHeight) return "X:— Y:—\nRGB:—\nA:—";
        var pixel = new byte[4];
        PixelImage(pixels).CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return $"X:{x} Y:{y}\nRGB:{pixel[2]},{pixel[1]},{pixel[0]}\nA:{pixel[3]}";
    }

    private static BitmapSource PixelImage(BitmapSource image)
    {
        if (image == null || image.Format == PixelFormats.Bgra32) return image;
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    internal Point ScrollImagesTo(double x, double y)
    {
        if (leftScroll == null || rightScroll == null) return new Point();
        double maxX = Math.Min(leftScroll.ScrollableWidth, rightScroll.ScrollableWidth);
        double maxY = Math.Min(leftScroll.ScrollableHeight, rightScroll.ScrollableHeight);
        x = Math.Clamp(x, 0, Math.Max(0, maxX)); y = Math.Clamp(y, 0, Math.Max(0, maxY));
        leftScroll.ScrollToHorizontalOffset(x); rightScroll.ScrollToHorizontalOffset(x);
        leftScroll.ScrollToVerticalOffset(y); rightScroll.ScrollToVerticalOffset(y);
        return new Point(x, y);
    }

    private void UpdateImageNavigation()
    {
        if (imageViewport == null || leftScroll == null || imageZoom == null) return;
        double zoom = imageZoom.Value;
        double width = Math.Min(imageWidth, leftScroll.ViewportWidth / zoom) * navigationScale;
        double height = Math.Min(imageHeight, leftScroll.ViewportHeight / zoom) * navigationScale;
        imageViewport.Width = Math.Max(0, width); imageViewport.Height = Math.Max(0, height);
        Canvas.SetLeft(imageViewport, navigationLeft + leftScroll.HorizontalOffset / zoom * navigationScale);
        Canvas.SetTop(imageViewport, navigationTop + leftScroll.VerticalOffset / zoom * navigationScale);
    }

    internal static BitmapSource CreateNavigationBitmap(BitmapSource source, BitmapSource target)
    {
        int width = (int)NavigatorWidth, height = (int)NavigatorHeight;
        double fullWidth = Math.Max(source?.PixelWidth ?? 0, target?.PixelWidth ?? 0);
        double fullHeight = Math.Max(source?.PixelHeight ?? 0, target?.PixelHeight ?? 0);
        double scale = Math.Min(width / Math.Max(1, fullWidth), height / Math.Max(1, fullHeight));
        double x = (width - fullWidth * scale) / 2, y = (height - fullHeight * scale) / 2;
        byte[] Sample(BitmapSource image)
        {
            var drawing = new DrawingVisual();
            using (var dc = drawing.RenderOpen())
                if (image != null) dc.DrawImage(image, new Rect(x, y, image.PixelWidth * scale, image.PixelHeight * scale));
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(drawing);
            var bytes = new byte[width * height * 4];
            PixelImage(bitmap).CopyPixels(bytes, width * 4, 0);
            return bytes;
        }
        var left = Sample(source); var right = Sample(target);
        var result = new byte[left.Length];
        for (int i = 0; i < result.Length; i += 4)
        {
            double a = left[i + 3] / 255.0, b = right[i + 3] / 255.0;
            bool different = source != null && target != null && (left[i + 3] != right[i + 3] ||
                (a > 0 && b > 0 && (left[i] != right[i] || left[i + 1] != right[i + 1] || left[i + 2] != right[i + 2])));
            for (int c = 0; c < 3; c++)
            {
                double l = left[i + c] * a + 24 * (1 - a), r = right[i + c] * b + 24 * (1 - b);
                double value = source == null ? r : target == null ? l : (l + r) / 2;
                if (different) value = value * 0.4 + (c == 1 ? 48 : 255) * 0.6;
                result[i + c] = (byte)Math.Round(value);
            }
            result[i + 3] = 255;
        }
        var output = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, result, width * 4);
        output.Freeze();
        return output;
    }
}
