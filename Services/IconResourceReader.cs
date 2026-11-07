using DesktopIniManager.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace DesktopIniManager.Services;

internal static class IconResourceReader
{
    private const uint LoadLibraryAsDataFile = 0x00000002;
    private const uint LoadLibraryAsImageResource = 0x00000020;
    private static readonly IntPtr RtIcon = 3;
    private static readonly IntPtr RtGroupIcon = 14;

    /// <summary>
    ///     ICO ファイル、またはアイコン ライブラリ (ICL/DLL/EXE) からアイコン グループを読み込み、シェルのインデックス順に並べます。
    /// </summary>
    /// <param name="fileName">読み込むファイルのパス。</param>
    /// <returns>アイコン グループの一覧。</returns>
    public static List<IconGroupResource> Read(string fileName)
    {
        if (string.Equals(Path.GetExtension(fileName), ".ico", StringComparison.OrdinalIgnoreCase))
            return new List<IconGroupResource> { ReadStandalone(fileName) };

        var module = LoadLibraryEx(fileName, IntPtr.Zero, LoadLibraryAsDataFile | LoadLibraryAsImageResource);
        if (module == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the icon library.");
        var result = new List<IconGroupResource>();
        try
        {
            EnumResourceNames(module, RtGroupIcon, (_, _, name, _) =>
            {
                EnumResourceLanguages(module, RtGroupIcon, name, (_, _, _, language, _) =>
                {
                    var group = LoadBytes(module, RtGroupIcon, name, language);
                    if (group != null)
                    {
                        var ico = BuildIcon(module, group, language);
                        result.Add(new IconGroupResource
                        {
                            ResourceName = ResourceName(name),
                            Language = language,
                            ImageCount = BitConverter.ToUInt16(group, 4),
                            Preview = CreatePreview(ico)
                        });
                    }

                    return true;
                }, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);
        }
        finally
        {
            FreeLibrary(module);
        }

        var sorted = result.OrderBy(item => NumericName(item.ResourceName)).ThenBy(item => item.ResourceName)
            .ThenBy(item => item.Language).ToList();
        for (var index = 0; index < sorted.Count; index++) sorted[index].ShellIndex = index;
        return sorted;
    }

    /// <summary>
    ///     単体の ICO ファイルを 1 つのアイコン グループとして読み込みます。
    /// </summary>
    /// <param name="fileName">ICO ファイルのパス。</param>
    /// <returns>読み込んだアイコン グループ。</returns>
    private static IconGroupResource ReadStandalone(string fileName)
    {
        var ico = File.ReadAllBytes(fileName);
        if (ico.Length < 6 || BitConverter.ToUInt16(ico, 0) != 0 || BitConverter.ToUInt16(ico, 2) != 1)
            throw new InvalidDataException("The ICO file is invalid.");
        return new IconGroupResource
        {
            ResourceName = Path.GetFileNameWithoutExtension(fileName),
            ShellIndex = 0,
            ImageCount = BitConverter.ToUInt16(ico, 4),
            Preview = CreatePreview(ico)
        };
    }

    /// <summary>
    ///     グループ アイコン リソースと各アイコン イメージから、ICO ファイル形式のバイト列を組み立てます。
    /// </summary>
    /// <param name="module">リソースを読み込んだモジュール。</param>
    /// <param name="group">RT_GROUP_ICON リソースのデータ。</param>
    /// <param name="language">リソースの言語 ID。</param>
    /// <returns>ICO ファイル形式のバイト列。</returns>
    private static byte[] BuildIcon(IntPtr module, byte[] group, ushort language)
    {
        int count = BitConverter.ToUInt16(group, 4);
        var images = new List<byte[]>();
        for (var index = 0; index < count; index++)
        {
            var offset = 6 + index * 14;
            var id = BitConverter.ToUInt16(group, offset + 12);
            var image = LoadBytes(module, RtIcon, id, language) ?? LoadBytesAnyLanguage(module, RtIcon, id);
            if (image == null) throw new InvalidDataException("Icon image " + id + " was not found.");
            images.Add(image);
        }

        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)count);
            var dataOffset = 6 + count * 16;
            for (var index = 0; index < count; index++)
            {
                var source = 6 + index * 14;
                writer.Write(group, source, 12);
                writer.Write((uint)dataOffset);
                dataOffset += images[index].Length;
            }

            foreach (var image in images) writer.Write(image);
            return stream.ToArray();
        }
    }

    /// <summary>
    ///     ICO データの中で最も大きいフレームをプレビュー画像として取得します。
    /// </summary>
    /// <param name="ico">ICO ファイル形式のバイト列。</param>
    /// <returns>フリーズ済みのプレビュー画像。</returns>
    private static BitmapSource CreatePreview(byte[] ico)
    {
        using (var stream = new MemoryStream(ico))
        {
            var decoder =
                new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames.OrderByDescending(item => item.PixelWidth * item.PixelHeight).First();
            frame.Freeze();
            return frame;
        }
    }

    /// <summary>
    ///     言語を問わず、最初に見つかったリソースのデータを読み込みます。
    /// </summary>
    /// <param name="module">リソースを読み込んだモジュール。</param>
    /// <param name="type">リソースの種類。</param>
    /// <param name="name">リソース名。</param>
    /// <returns>リソースのデータ。見つからない場合は null。</returns>
    private static byte[] LoadBytesAnyLanguage(IntPtr module, IntPtr type, IntPtr name)
    {
        byte[] found = null;
        EnumResourceLanguages(module, type, name, (_, _, _, language, _) =>
        {
            found = LoadBytes(module, type, name, language);
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    ///     指定した種類・名前・言語のリソース データを読み込みます。
    /// </summary>
    /// <param name="module">リソースを読み込んだモジュール。</param>
    /// <param name="type">リソースの種類。</param>
    /// <param name="name">リソース名。</param>
    /// <param name="language">リソースの言語 ID。</param>
    /// <returns>リソースのデータ。見つからない場合は null。</returns>
    private static byte[] LoadBytes(IntPtr module, IntPtr type, IntPtr name, ushort language)
    {
        var resource = FindResourceEx(module, type, name, language);
        if (resource == IntPtr.Zero) return null;
        var size = SizeofResource(module, resource);
        var loaded = LoadResource(module, resource);
        var pointer = LockResource(loaded);
        if (pointer == IntPtr.Zero || size == 0) return null;
        var bytes = new byte[size];
        Marshal.Copy(pointer, bytes, 0, (int)size);
        return bytes;
    }

    /// <summary>
    ///     整数 ID または文字列のリソース名を文字列に変換します。
    /// </summary>
    /// <param name="value">リソース名を表すポインター。</param>
    /// <returns>リソース名の文字列。</returns>
    private static string ResourceName(IntPtr value)
    {
        return (ulong)value.ToInt64() >> 16 == 0 ? ((ushort)value.ToInt64()).ToString() : Marshal.PtrToStringUni(value);
    }

    /// <summary>
    ///     並べ替え用に、リソース名を数値に変換します。
    /// </summary>
    /// <param name="value">リソース名。</param>
    /// <returns>数値に変換できた値。できない場合は int.MaxValue。</returns>
    private static int NumericName(string value)
    {
        return int.TryParse(value, out var number) ? number : int.MaxValue;
    }

    /// <summary>
    ///     Win32 API の LoadLibraryEx を呼び出し、モジュールを読み込みます。
    /// </summary>
    /// <param name="file">読み込むファイル。</param>
    /// <param name="fileHandle">予約済み。</param>
    /// <param name="flags">読み込みオプション。</param>
    /// <returns>モジュール ハンドル。失敗時は IntPtr.Zero。</returns>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string file, IntPtr fileHandle, uint flags);

    /// <summary>
    ///     Win32 API の FreeLibrary を呼び出し、モジュールを解放します。
    /// </summary>
    /// <param name="module">解放するモジュール。</param>
    /// <returns>成功した場合は true。</returns>
    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(IntPtr module);

    /// <summary>
    ///     Win32 API の EnumResourceNames を呼び出し、指定種類のリソース名を列挙します。
    /// </summary>
    /// <param name="module">対象のモジュール。</param>
    /// <param name="type">リソースの種類。</param>
    /// <param name="callback">リソース名ごとに呼び出されるコールバック。</param>
    /// <param name="parameter">コールバックに渡す値。</param>
    /// <returns>成功した場合は true。</returns>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool
        EnumResourceNames(IntPtr module, IntPtr type, EnumResNameProc callback, IntPtr parameter);

    /// <summary>
    ///     Win32 API の EnumResourceLanguages を呼び出し、リソースの言語を列挙します。
    /// </summary>
    /// <param name="module">対象のモジュール。</param>
    /// <param name="type">リソースの種類。</param>
    /// <param name="name">リソース名。</param>
    /// <param name="callback">言語ごとに呼び出されるコールバック。</param>
    /// <param name="parameter">コールバックに渡す値。</param>
    /// <returns>成功した場合は true。</returns>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumResourceLanguages(IntPtr module, IntPtr type, IntPtr name, EnumResLangProc callback,
        IntPtr parameter);

    /// <summary>
    ///     Win32 API の FindResourceEx を呼び出し、リソースを検索します。
    /// </summary>
    /// <param name="module">対象のモジュール。</param>
    /// <param name="type">リソースの種類。</param>
    /// <param name="name">リソース名。</param>
    /// <param name="language">言語 ID。</param>
    /// <returns>リソース情報のハンドル。見つからない場合は IntPtr.Zero。</returns>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindResourceEx(IntPtr module, IntPtr type, IntPtr name, ushort language);

    /// <summary>
    ///     Win32 API の LoadResource を呼び出し、リソースを読み込みます。
    /// </summary>
    /// <param name="module">対象のモジュール。</param>
    /// <param name="resource">リソース情報のハンドル。</param>
    /// <returns>読み込んだリソースのハンドル。</returns>
    [DllImport("kernel32.dll")]
    private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);

    /// <summary>
    ///     Win32 API の LockResource を呼び出し、リソース データへのポインターを取得します。
    /// </summary>
    /// <param name="resourceData">読み込んだリソースのハンドル。</param>
    /// <returns>リソース データへのポインター。</returns>
    [DllImport("kernel32.dll")]
    private static extern IntPtr LockResource(IntPtr resourceData);

    /// <summary>
    ///     Win32 API の SizeofResource を呼び出し、リソースのサイズを取得します。
    /// </summary>
    /// <param name="module">対象のモジュール。</param>
    /// <param name="resource">リソース情報のハンドル。</param>
    /// <returns>リソースのバイト数。</returns>
    [DllImport("kernel32.dll")]
    private static extern uint SizeofResource(IntPtr module, IntPtr resource);

    private delegate bool EnumResNameProc(IntPtr module, IntPtr type, IntPtr name, IntPtr parameter);

    private delegate bool EnumResLangProc(IntPtr module, IntPtr type, IntPtr name, ushort language, IntPtr parameter);
}