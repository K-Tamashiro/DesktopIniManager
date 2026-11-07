using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopIniManager.Views;

internal sealed partial class DiffViewWindow
{
    private const double NavigatorWidth = 100, NavigatorHeight = 60;
    private Canvas _imageNavigator;
    private Thumb _imageViewport;
    private Point _navigationDrag;
    private double _navigationScale, _navigationLeft, _navigationTop, _imageWidth, _imageHeight;
    private TextBlock _sourcePixelDetails, _targetPixelDetails;
    private BitmapSource _sourcePixels, _targetPixels;

    /// <summary>
    ///     画像のナビゲーター、ピクセル情報、ズーム操作の表示をリセットします。
    /// </summary>
    private void ResetImageNavigation()
    {
        StopImagePan();
        if (_imageViewport?.IsMouseCaptured == true) _imageViewport.ReleaseMouseCapture();
        ImageControlRow.Visibility = Visibility.Collapsed;
        ImageDetails.Children.Clear();
        _imageNavigator = null;
        _imageViewport = null;
        _sourcePixels = _targetPixels = null;
        _sourcePixelDetails = _targetPixelDetails = null;
    }

    /// <summary>
    ///     画像の縮小表示によるナビゲーターと、カーソル位置のピクセル情報の表示を作成します。
    /// </summary>
    /// <param name="width">画像の幅 (ピクセル)。</param>
    /// <param name="height">画像の高さ (ピクセル)。</param>
    private void BuildImageNavigation(double width, double height)
    {
        _imageWidth = width;
        _imageHeight = height;
        _navigationScale = Math.Min(NavigatorWidth / Math.Max(1, width), NavigatorHeight / Math.Max(1, height));
        _navigationLeft = (NavigatorWidth - width * _navigationScale) / 2;
        _navigationTop = (NavigatorHeight - height * _navigationScale) / 2;
        _sourcePixels = PixelImage(ViewModel.SourceImage);
        _targetPixels = PixelImage(ViewModel.TargetImage);
        ImageDetails.Children.Clear();
        var sizes = new TextBlock
        {
            Text = "Source: " + Dimensions(ViewModel.SourceImage) + "\nTarget: " + Dimensions(ViewModel.TargetImage),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(0, 0, 8, 0)
        };
        sizes.SetBinding(TextBlock.FontSizeProperty, new Binding(nameof(TextBlock.FontSize)) { Source = TargetHeader });
        sizes.SetBinding(TextBlock.FontFamilyProperty,
            new Binding(nameof(TextBlock.FontFamily)) { Source = TargetHeader });
        sizes.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        ImageDetails.Children.Add(sizes);
        _imageNavigator = new Canvas
        {
            Width = NavigatorWidth,
            Height = NavigatorHeight,
            ClipToBounds = true,
            Background = Brushes.Black,
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "Source + Target 50% / differences highlighted. Drag the frame to navigate."
        };
        _imageNavigator.Children.Add(new Image
        {
            Source = CreateNavigationBitmap(ViewModel.SourceImage, ViewModel.TargetImage),
            Width = NavigatorWidth,
            Height = NavigatorHeight,
            IsHitTestVisible = false
        });
        _imageViewport = new Thumb { Cursor = Cursors.SizeAll, Focusable = false };
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        border.SetValue(Border.BorderBrushProperty, Brushes.Cyan);
        border.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(25, 0, 255, 255)));
        _imageViewport.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = border };
        _imageViewport.DragStarted += (_, _) =>
            _navigationDrag = new Point(_leftScroll.HorizontalOffset, _leftScroll.VerticalOffset);
        _imageViewport.DragDelta += (_, e) =>
        {
            var scale = (_imageZoom?.Value ?? 1) / _navigationScale;
            _navigationDrag.X += e.HorizontalChange * scale;
            _navigationDrag.Y += e.VerticalChange * scale;
            _navigationDrag = ScrollImagesTo(_navigationDrag.X, _navigationDrag.Y);
        };
        _imageNavigator.Children.Add(_imageViewport);
        _imageNavigator.MouseMove += (_, e) =>
        {
            var point = e.GetPosition(_imageNavigator);
            SetImagePointDetails(new Point((point.X - _navigationLeft) / _navigationScale,
                (point.Y - _navigationTop) / _navigationScale), false);
        };
        _imageNavigator.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && FindAncestor<Thumb>(d) != null) return;
            var point = e.GetPosition(_imageNavigator);
            var zoom = _imageZoom?.Value ?? 1;
            ScrollImagesTo((point.X - _navigationLeft) / _navigationScale * zoom - _leftScroll.ViewportWidth / 2,
                (point.Y - _navigationTop) / _navigationScale * zoom - _leftScroll.ViewportHeight / 2);
            e.Handled = true;
        };
        ImageDetails.Children.Add(_imageNavigator);
        _sourcePixelDetails = AddPixelCard("Source", "SourceColor");
        _targetPixelDetails = AddPixelCard("Target", "TargetColor");
        SetImagePointDetails(new Point(-1, -1), false);
    }

    /// <summary>
    ///     ピクセル情報を表示するカードを追加します。
    /// </summary>
    /// <param name="name">カードの見出し。</param>
    /// <param name="color">見出しの色のリソース名。</param>
    /// <returns>ピクセル情報を表示するテキスト ブロック。</returns>
    private TextBlock AddPixelCard(string name, string color)
    {
        var text = new TextBlock
        {
            FontSize = 11,
            Width = 108,
            Height = 42,
            LineHeight = 14,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };
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
        ImageDetails.Children.Add(card);
        return text;
    }

    /// <summary>
    ///     画像のサイズを表示用の文字列にします。
    /// </summary>
    /// <param name="image">対象の画像。</param>
    /// <returns>「幅 × 高さ px」の文字列。画像がない場合は「—」。</returns>
    private static string Dimensions(BitmapSource image)
    {
        return image == null ? "\u2014" : $"{image.PixelWidth} \u00D7 {image.PixelHeight} px";
    }

    /// <summary>
    ///     指定位置の両側のピクセル情報を表示します。ドラッグ中はクリックした位置の情報を保ちます。
    /// </summary>
    /// <param name="point">画像上の位置。</param>
    /// <param name="rememberOrigin">ドラッグ開始位置として記録する場合は true。</param>
    private void SetImagePointDetails(Point point, bool rememberOrigin)
    {
        if (_sourcePixelDetails == null) return;
        // During a drag keep the clicked pixel; mouse-up resumes live hover.
        if (_imagePanning && !rememberOrigin) return;
        _sourcePixelDetails.Text = DescribePixel(_sourcePixels, point);
        _targetPixelDetails.Text = DescribePixel(_targetPixels, point);
    }

    /// <summary>
    ///     画像上の位置の座標、RGB、アルファ値を表示用の文字列にします。
    /// </summary>
    /// <param name="pixels">対象の画像。</param>
    /// <param name="point">画像上の位置。</param>
    /// <returns>ピクセル情報の文字列。範囲外の場合は「—」。</returns>
    internal static string DescribePixel(BitmapSource pixels, Point point)
    {
        int x = (int)Math.Floor(point.X), y = (int)Math.Floor(point.Y);
        if (pixels == null || x < 0 || y < 0 || x >= pixels.PixelWidth || y >= pixels.PixelHeight)
            return "X:\u2014 Y:\u2014\nRGB:\u2014\nA:\u2014";
        var pixel = new byte[4];
        PixelImage(pixels).CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return $"X:{x} Y:{y}\nRGB:{pixel[2]},{pixel[1]},{pixel[0]}\nA:{pixel[3]}";
    }

    /// <summary>
    ///     画像を、ピクセルを読み取れるフリーズ済みの BGRA32 形式に変換します。
    /// </summary>
    /// <param name="image">変換する画像。</param>
    /// <returns>変換した画像。null の場合は null。</returns>
    private static BitmapSource PixelImage(BitmapSource image)
    {
        if (image == null)
            return null;

        if (image.Format == PixelFormats.Bgra32 && image.IsFrozen)
            return image;

        var source = image.Format == PixelFormats.Bgra32
            ? image
            : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);

        var stride = checked(source.PixelWidth * 4);
        var pixels = new byte[checked(stride * source.PixelHeight)];

        source.CopyPixels(pixels, stride, 0);

        var result = BitmapSource.Create(
            source.PixelWidth,
            source.PixelHeight,
            source.DpiX,
            source.DpiY,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);

        result.Freeze();
        return result;
    }

    /// <summary>
    ///     両側の画像を同じスクロール位置に移動します。
    /// </summary>
    /// <param name="x">横方向の位置。</param>
    /// <param name="y">縦方向の位置。</param>
    /// <returns>範囲内に収めた実際のスクロール位置。</returns>
    internal Point ScrollImagesTo(double x, double y)
    {
        if (_leftScroll == null || _rightScroll == null) return new Point();
        var maxX = Math.Min(_leftScroll.ScrollableWidth, _rightScroll.ScrollableWidth);
        var maxY = Math.Min(_leftScroll.ScrollableHeight, _rightScroll.ScrollableHeight);
        x = Math.Clamp(x, 0, Math.Max(0, maxX));
        y = Math.Clamp(y, 0, Math.Max(0, maxY));
        _leftScroll.ScrollToHorizontalOffset(x);
        _rightScroll.ScrollToHorizontalOffset(x);
        _leftScroll.ScrollToVerticalOffset(y);
        _rightScroll.ScrollToVerticalOffset(y);
        return new Point(x, y);
    }

    /// <summary>
    ///     ナビゲーター上の表示範囲の枠を、現在のズームとスクロール位置に合わせて更新します。
    /// </summary>
    private void UpdateImageNavigation()
    {
        if (_imageViewport == null || _leftScroll == null || _imageZoom == null) return;
        var zoom = _imageZoom.Value;
        var width = Math.Min(_imageWidth, _leftScroll.ViewportWidth / zoom) * _navigationScale;
        var height = Math.Min(_imageHeight, _leftScroll.ViewportHeight / zoom) * _navigationScale;
        _imageViewport.Width = Math.Max(0, width);
        _imageViewport.Height = Math.Max(0, height);
        Canvas.SetLeft(_imageViewport, _navigationLeft + _leftScroll.HorizontalOffset / zoom * _navigationScale);
        Canvas.SetTop(_imageViewport, _navigationTop + _leftScroll.VerticalOffset / zoom * _navigationScale);
    }

    /// <summary>
    ///     2 つの画像のピクセルを XOR した差分画像を作成します。
    /// </summary>
    /// <param name="foreground">前面の画像。</param>
    /// <param name="background">背面の画像。</param>
    /// <returns>XOR した画像。</returns>
    internal static BitmapSource CreateXorBitmap(BitmapSource foreground, BitmapSource background)
    {
        var front = PixelImage(foreground);
        var back = PixelImage(background);
        var width = Math.Max(front.PixelWidth, back.PixelWidth);
        var height = Math.Max(front.PixelHeight, back.PixelHeight);
        var stride = checked(width * 4);
        var result = new byte[checked(stride * height)];
        var frontRow = new byte[stride];
        var backRow = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            Array.Clear(frontRow, 0, stride);
            Array.Clear(backRow, 0, stride);
            if (y < front.PixelHeight)
                front.CopyPixels(new Int32Rect(0, y, front.PixelWidth, 1), frontRow, stride, 0);
            if (y < back.PixelHeight)
                back.CopyPixels(new Int32Rect(0, y, back.PixelWidth, 1), backRow, stride, 0);
            for (var x = 0; x < width; x++)
            {
                int p = x * 4, output = y * stride + p;
                // Include alpha-only differences, but keep the result opaque so equal pixels are black.
                var alpha = frontRow[p + 3] ^ backRow[p + 3];
                for (var c = 0; c < 3; c++)
                    result[output + c] = (byte)((frontRow[p + c] ^ backRow[p + c]) | alpha);
                result[output + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, result, stride);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    ///     両側の画像の差分を示すナビゲーター用の縮小画像を作成します。
    /// </summary>
    /// <param name="source">比較元の画像。</param>
    /// <param name="target">比較先の画像。</param>
    /// <returns>ナビゲーター用の画像。</returns>
    internal static BitmapSource CreateNavigationBitmap(BitmapSource source, BitmapSource target)
    {
        int width = (int)NavigatorWidth, height = (int)NavigatorHeight;
        double fullWidth = Math.Max(source?.PixelWidth ?? 0, target?.PixelWidth ?? 0);
        double fullHeight = Math.Max(source?.PixelHeight ?? 0, target?.PixelHeight ?? 0);
        var scale = Math.Min(width / Math.Max(1, fullWidth), height / Math.Max(1, fullHeight));
        double x = (width - fullWidth * scale) / 2, y = (height - fullHeight * scale) / 2;

        byte[] Sample(BitmapSource image)
        {
            var drawing = new DrawingVisual();
            using (var dc = drawing.RenderOpen())
            {
                if (image != null)
                    dc.DrawImage(image, new Rect(x, y, image.PixelWidth * scale, image.PixelHeight * scale));
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(drawing);
            var bytes = new byte[width * height * 4];
            PixelImage(bitmap).CopyPixels(bytes, width * 4, 0);
            return bytes;
        }

        var left = Sample(source);
        var right = Sample(target);
        var result = new byte[left.Length];
        for (var i = 0; i < result.Length; i += 4)
        {
            double a = left[i + 3] / 255.0, b = right[i + 3] / 255.0;
            var different = source != null && target != null && (left[i + 3] != right[i + 3] ||
                                                                 (a > 0 && b > 0 && (left[i] != right[i] ||
                                                                     left[i + 1] != right[i + 1] ||
                                                                     left[i + 2] != right[i + 2])));
            for (var c = 0; c < 3; c++)
            {
                double l = left[i + c] * a + 24 * (1 - a), r = right[i + c] * b + 24 * (1 - b);
                var value = source == null ? r : target == null ? l : (l + r) / 2;
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