using System;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopIniManager.Services;

// Compose local GIF rectangles on the logical screen, applying disposal before
// the next frame. Keep decoder frames, not a full-screen bitmap for every frame.
internal sealed class GifFrames
{
    private readonly Color _backgroundColor;
    private readonly Frame[] _frames;
    private readonly int _width, _height;

    internal GifFrames(GifBitmapDecoder decoder)
    {
        _frames = decoder.Frames.Select(frame =>
        {
            var info = frame.Metadata as BitmapMetadata;
            var pixels = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var bytes = new byte[checked(pixels.PixelWidth * pixels.PixelHeight * 4)];
            pixels.CopyPixels(bytes, pixels.PixelWidth * 4, 0);
            return new Frame(Read(info, "/imgdesc/Left"), Read(info, "/imgdesc/Top"), pixels.PixelWidth,
                pixels.PixelHeight,
                Read(info, "/grctlext/Disposal"), Read(info, "/grctlext/Delay") * 10,
                Read(info, "/grctlext/TransparencyFlag") != 0, bytes);
        }).ToArray();
        var metadata = decoder.Metadata;
        _width = Read(metadata, "/logscrdesc/Width", decoder.Frames[0].PixelWidth);
        _height = Read(metadata, "/logscrdesc/Height", decoder.Frames[0].PixelHeight);
        var background = Read(metadata, "/logscrdesc/BackgroundColorIndex");
        _backgroundColor = decoder.Palette != null && background < decoder.Palette.Colors.Count
            ? decoder.Palette.Colors[background]
            : Color.FromArgb(0, 0, 0, 0);
    }

    internal int Count => _frames.Length;

    private static int Read(BitmapMetadata metadata, string query, int fallback = 0)
    {
        if (metadata == null || !metadata.ContainsQuery(query)) return fallback;
        return Convert.ToInt32(metadata.GetQuery(query));
    }

    internal int DelayMilliseconds(int index)
    {
        return _frames[index].Delay;
    }

    internal BitmapSource Compose(int index)
    {
        if (index < 0 || index >= Count) return null;
        var canvas = new byte[checked(_width * _height * 4)];
        if (!_frames[0].Transparent)
            for (var at = 0; at < canvas.Length; at += 4)
            {
                canvas[at] = _backgroundColor.B;
                canvas[at + 1] = _backgroundColor.G;
                canvas[at + 2] = _backgroundColor.R;
                canvas[at + 3] = _backgroundColor.A;
            }

        for (var i = 0; i <= index; i++)
        {
            var frame = _frames[i];
            int x = frame.X, y = frame.Y;
            var disposal = frame.Disposal;
            var previous = disposal == 3 ? (byte[])canvas.Clone() : null;
            var stride = checked(frame.Width * 4);
            var bytes = frame.Pixels;
            for (var row = 0; row < frame.Height && y + row < _height; row++)
                for (var col = 0; col < frame.Width && x + col < _width; col++)
                {
                    int from = row * stride + col * 4, to = ((y + row) * _width + x + col) * 4;
                    if (bytes[from + 3] != 0) Buffer.BlockCopy(bytes, from, canvas, to, 4);
                }

            if (i == index) break;
            if (previous != null)
            {
                canvas = previous;
            }
            else if (disposal == 2)
            {
                // Transparent GIFs restore transparent background; opaque GIFs use
                // the logical screen's global palette background color.
                var background = Color.FromArgb(0, 0, 0, 0);
                if (!frame.Transparent) background = _backgroundColor;
                for (var row = y; row < Math.Min(_height, y + frame.Height); row++)
                    for (var col = x; col < Math.Min(_width, x + frame.Width); col++)
                    {
                        var at = (row * _width + col) * 4;
                        canvas[at] = background.B;
                        canvas[at + 1] = background.G;
                        canvas[at + 2] = background.R;
                        canvas[at + 3] = background.A;
                    }
            }
        }

        var image = BitmapSource.Create(_width, _height, 96, 96, PixelFormats.Bgra32, null, canvas, _width * 4);
        image.Freeze();
        return image;
    }

    private sealed record Frame(
        int X,
        int Y,
        int Width,
        int Height,
        int Disposal,
        int Delay,
        bool Transparent,
        byte[] Pixels);
}