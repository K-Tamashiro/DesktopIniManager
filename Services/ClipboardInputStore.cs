using DesktopIniManager.Properties;
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
    private readonly string _directory;
    private readonly List<string> _files = new();
    private bool _disposed;

    /// <summary>
    ///     クリップボード内容の一時コピーを保存するフォルダーを決定します。
    /// </summary>
    /// <param name="root">一時フォルダーの親。null の場合はシステムの一時フォルダー。</param>
    internal ClipboardInputStore(string root = null)
    {
        _directory = Path.Combine(root ?? Path.GetTempPath(),
            "DesktopIniManager-clipboard-" + Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    ///     作成した一時ファイルの削除をバックグラウンドで開始します。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ = CleanUpAsync();
    }

    /// <summary>
    ///     クリップボードのファイル・WAVE 音声・画像・テキストを一時ファイルにコピーし、比較に使えるパスを返します。
    /// </summary>
    /// <param name="data">クリップボードのデータ オブジェクト。</param>
    /// <returns>作成した一時ファイルのパス (1 つまたは 2 つ)。</returns>
    internal string[] Capture(IDataObject data)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ClipboardInputStore));
        if (data == null) throw new InvalidOperationException(StringOverlay.Get("Err_ClipboardEmpty")).Localized();
        if (data.GetDataPresent(DataFormats.FileDrop))
        {
            var paths = data.GetData(DataFormats.FileDrop) as string[];
            if (paths == null || paths.Length < 1 || paths.Length > 2 || paths.Any(path => !File.Exists(path)))
                throw new InvalidOperationException(StringOverlay.Get("Err_ClipboardFileCount")).Localized();
            return paths.Select(path => Write(Path.GetExtension(path), output =>
            {
                using var input = File.OpenRead(path);
                input.CopyTo(output);
            })).ToArray();
        }

        if (data.GetDataPresent(DataFormats.WaveAudio))
        {
            var audio = data.GetData(DataFormats.WaveAudio);
            return new[]
            {
                Write(".wav", output =>
                {
                    if (audio is byte[] bytes)
                    {
                        output.Write(bytes);
                    }
                    else if (audio is Stream stream)
                    {
                        var previous = stream.CanSeek ? stream.Position : 0;
                        try
                        {
                            if (stream.CanSeek) stream.Position = 0;
                            stream.CopyTo(output);
                        }
                        finally
                        {
                            if (stream.CanSeek) stream.Position = previous;
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException(StringOverlay.Get("Err_ClipboardWave")).Localized();
                    }
                })
            };
        }

        if (data.GetData(DataFormats.Bitmap, true) is BitmapSource bitmap)
            return new[]
            {
                Write(".png", output =>
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    encoder.Save(output);
                })
            };
        var text = data.GetData(DataFormats.UnicodeText) as string ?? data.GetData(DataFormats.Text) as string;
        if (text != null)
            return new[]
            {
                Write(".txt", output =>
                {
                    using var writer = new StreamWriter(output, new UTF8Encoding(false), 1024, true);
                    writer.Write(text);
                })
            };
        throw new InvalidOperationException(
            StringOverlay.Get("Err_ClipboardNoContent")).Localized();
    }

    /// <summary>
    ///     一時フォルダーに新しいファイルを作成し、指定の処理で内容を書き込みます。
    /// </summary>
    /// <param name="extension">作成するファイルの拡張子。</param>
    /// <param name="write">出力ストリームへの書き込み処理。</param>
    /// <returns>作成したファイルのパス。</returns>
    private string Write(string extension, Action<Stream> write)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "Clipboard-" + Guid.NewGuid().ToString("N") + extension);
        _files.Add(path);
        using var output = File.Create(path);
        write(output);
        return path;
    }

    /// <summary>
    ///     作成した一時ファイルとフォルダーの削除を、使用中のファイルが解放されるまで一定回数再試行します。
    /// </summary>
    private async Task CleanUpAsync()
    {
        // Background comparison/sampling may briefly retain a file after window close.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var remaining = false;
            foreach (var path in _files)
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    remaining = true;
                }
                catch (UnauthorizedAccessException)
                {
                    remaining = true;
                }

            if (!remaining)
            {
                try
                {
                    if (Directory.Exists(_directory)) Directory.Delete(_directory);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }

                return;
            }

            await Task.Delay(250).ConfigureAwait(false);
        }
    }
}