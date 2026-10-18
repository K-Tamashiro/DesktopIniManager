using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace DesktopIniManager.Services;

// Each viewer owns only the temporary copies it creates; clipboard originals are untouched.
internal sealed class ClipboardInputStore : IDisposable
{
    private readonly string directory;
    private readonly List<string> files = new();
    private bool disposed;

    internal ClipboardInputStore(string root = null)
    {
        directory = Path.Combine(root ?? Path.GetTempPath(), "DesktopIniManager-clipboard-" + Guid.NewGuid().ToString("N"));
    }

    internal string[] Capture(IDataObject data)
    {
        if (disposed) throw new ObjectDisposedException(nameof(ClipboardInputStore));
        if (data == null) throw new InvalidOperationException("クリップボードは空です。");
        if (data.GetDataPresent(DataFormats.FileDrop))
        {
            var paths = data.GetData(DataFormats.FileDrop) as string[];
            if (paths == null || paths.Length < 1 || paths.Length > 2 || paths.Any(path => !File.Exists(path)))
                throw new InvalidOperationException("比較するファイルを1つまたは2つコピーしてください。");
            return paths.Select(path => Write(Path.GetExtension(path), output =>
            {
                using var input = File.OpenRead(path);
                input.CopyTo(output);
            })).ToArray();
        }
        if (data.GetDataPresent(DataFormats.WaveAudio))
        {
            object audio = data.GetData(DataFormats.WaveAudio);
            return new[] { Write(".wav", output =>
            {
                if (audio is byte[] bytes) output.Write(bytes);
                else if (audio is Stream stream)
                {
                    long previous = stream.CanSeek ? stream.Position : 0;
                    try { if (stream.CanSeek) stream.Position = 0; stream.CopyTo(output); }
                    finally { if (stream.CanSeek) stream.Position = previous; }
                }
                else throw new InvalidOperationException("クリップボードのWAVE音声を読み込めません。");
            }) };
        }
        if (data.GetData(DataFormats.Bitmap, true) is BitmapSource bitmap)
        {
            return new[] { Write(".png", output =>
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(output);
            }) };
        }
        string text = data.GetData(DataFormats.UnicodeText) as string ?? data.GetData(DataFormats.Text) as string;
        if (text != null)
            return new[] { Write(".txt", output =>
            {
                using var writer = new StreamWriter(output, new UTF8Encoding(false), 1024, leaveOpen: true);
                writer.Write(text);
            }) };
        throw new InvalidOperationException("貼り付け可能なファイル・WAVE音声・画像・テキストがありません。編集ソフト独自の音声形式の場合は、WAVを書き出してファイルをコピーしてください。");
    }

    private string Write(string extension, Action<Stream> write)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "Clipboard-" + Guid.NewGuid().ToString("N") + extension);
        files.Add(path);
        using var output = File.Create(path);
        write(output);
        return path;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        _ = CleanUpAsync();
    }

    private async Task CleanUpAsync()
    {
        // Background comparison/sampling may briefly retain a file after window close.
        for (int attempt = 0; attempt < 20; attempt++)
        {
            bool remaining = false;
            foreach (string path in files)
            {
                try { File.Delete(path); }
                catch (IOException) { remaining = true; }
                catch (UnauthorizedAccessException) { remaining = true; }
            }
            if (!remaining)
            {
                try { if (Directory.Exists(directory)) Directory.Delete(directory); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                return;
            }
            await Task.Delay(250).ConfigureAwait(false);
        }
    }
}
