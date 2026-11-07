using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;

namespace FastVolumeIndex;

public sealed partial class VolumePathIndex
{
    /// <summary>
    ///     Streams all immediate entries, including hidden/system entries.
    ///     Access failures are reported rather than mistaken for missing comparison files.
    /// </summary>
    public static IEnumerable<NativeDirectoryEntry> EnumerateNativeDirectory(string folder, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var pattern = NativeSearchPath(Normalize(folder));
        NativeFindData data;
        var handle = FindFirstFileExW(pattern, 1, out data, 0, IntPtr.Zero, 2);
        var error = handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        if (handle.IsInvalid && (error == 87 || error == 50))
        {
            handle.Dispose();
            handle = FindFirstFileExW(pattern, 1, out data, 0, IntPtr.Zero, 0);
            error = handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        }

        using (handle)
        {
            if (handle.IsInvalid)
            {
                if (error == 2) yield break; // Empty directory.
                throw new Win32Exception(error, "Cannot enumerate " + folder);
            }

            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (data.FileName != "." && data.FileName != "..")
                {
                    var writeTime = ((long)(uint)data.LastWriteTime.dwHighDateTime << 32)
                                    | (uint)data.LastWriteTime.dwLowDateTime;
                    yield return new NativeDirectoryEntry
                    {
                        Name = data.FileName,
                        Attributes = data.Attributes,
                        Size = ((long)data.FileSizeHigh << 32) | data.FileSizeLow,
                        ModifiedUtc = DateTime.FromFileTimeUtc(writeTime)
                    };
                }

                if (!FindNextFileW(handle, out data))
                {
                    error = Marshal.GetLastWin32Error();
                    if (error != 18) throw new Win32Exception(error, "Cannot continue enumerating " + folder);
                    break;
                }
            }
        }
    }

    /// <summary>Collects names and attributes in one traversal, without a shell process.</summary>
    public static VolumePathIndex BuildFromNativeEnumeration(
        string searchRoot, Action<int> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var rootPath = Normalize(searchRoot);
        if (!Directory.Exists(rootPath)) throw new DirectoryNotFoundException(rootPath);
        var nodes = new Dictionary<string, VolumePathNode>(StringComparer.OrdinalIgnoreCase);
        var root = new VolumePathNode(rootPath, FileAttributes.Directory, true);
        nodes.Add(rootPath, root);
        var projectFiles = new List<string>();
        var pending = new Stack<string>();
        pending.Push(rootPath);
        var scanned = 0;

        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var folder = pending.Pop();
            var pattern = NativeSearchPath(folder);
            NativeFindData data;
            var handle = FindFirstFileExW(pattern, 1, out data, 0, IntPtr.Zero, 2);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                // Some file-system providers do not support LARGE_FETCH.
                if (error == 87 || error == 50)
                {
                    handle = FindFirstFileExW(pattern, 1, out data, 0, IntPtr.Zero, 0);
                }
                else
                {
                    if (CanSkipEnumerationError(error)) continue;
                    throw new Win32Exception(error, "Cannot enumerate " + folder);
                }
            }

            using (handle)
            {
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (CanSkipEnumerationError(error)) continue;
                    throw new Win32Exception(error, "Cannot enumerate " + folder);
                }

                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var name = data.FileName;
                    if (name != "." && name != "..")
                    {
                        var directory = (data.Attributes & FileAttributes.Directory) != 0;
                        // Keep all files and directory nodes in the index.
                        // Visibility is handled by the UI/search layer.
                        {
                            var path = Path.Combine(folder, name);
                            nodes[path] = new VolumePathNode(path, data.Attributes);
                            if (directory)
                            {
                                // Keep link nodes but never follow directory links/junctions.
                                if ((data.Attributes & FileAttributes.ReparsePoint) == 0)
                                    pending.Push(path);
                            }
                            else if (ProjectFileExtensions.Contains(Path.GetExtension(name)))
                            {
                                projectFiles.Add(path);
                            }

                            if ((++scanned & 63) == 0) progress?.Invoke(scanned);
                        }
                    }

                    if (!FindNextFileW(handle, out data))
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error != 18 && !CanSkipEnumerationError(error))
                            throw new Win32Exception(error, "Cannot continue enumerating " + folder);
                        break;
                    }
                }
            }
        }

        token.ThrowIfCancellationRequested();
        projectFiles.Sort(StringComparer.OrdinalIgnoreCase);
        LinkNodes(nodes, root);
        token.ThrowIfCancellationRequested();
        progress?.Invoke(scanned);
        return new VolumePathIndex(rootPath, nodes, root, projectFiles);
    }

    /// <summary>
    ///     列挙中の Win32 エラーが、項目の消失やアクセス拒否のように読み飛ばしてよいものかどうかを判定します。
    /// </summary>
    /// <param name="error">Win32 エラー コード。</param>
    /// <returns>読み飛ばしてよい場合は true。</returns>
    private static bool CanSkipEnumerationError(int error)
    {
        return error == 2 || error == 3 || error == 5;
        // Missing/deleted entry or access denied.
    }

    /// <summary>
    ///     フォルダー パスを長いパス対応の \\?\ 形式に変換し、全項目を列挙する検索パターンを作成します。
    /// </summary>
    /// <param name="folder">列挙するフォルダーのパス。</param>
    /// <returns>FindFirstFileExW に渡す検索パターン。</returns>
    private static string NativeSearchPath(string folder)
    {
        var path = folder;
        if (!path.StartsWith(@"\\?\", StringComparison.Ordinal))
            path = path.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC\" + path.Substring(2)
                : @"\\?\" + path;
        return path.TrimEnd('\\') + @"\*";
    }

    /// <summary>
    ///     Win32 API の FindFirstFileExW を呼び出し、ディレクトリ列挙を開始します。
    /// </summary>
    /// <param name="fileName">検索パターン。</param>
    /// <param name="infoLevel">取得する情報レベル。</param>
    /// <param name="data">最初に見つかった項目の情報。</param>
    /// <param name="searchOp">検索の種類。</param>
    /// <param name="filter">検索フィルター (未使用)。</param>
    /// <param name="flags">追加の検索フラグ。</param>
    /// <returns>検索ハンドル。失敗時は無効なハンドル。</returns>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFindHandle FindFirstFileExW(string fileName, int infoLevel,
        out NativeFindData data, int searchOp, IntPtr filter, uint flags);

    /// <summary>
    ///     Win32 API の FindNextFileW を呼び出し、次の項目を取得します。
    /// </summary>
    /// <param name="handle">検索ハンドル。</param>
    /// <param name="data">次の項目の情報。</param>
    /// <returns>項目を取得できた場合は true。</returns>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextFileW(SafeFindHandle handle, out NativeFindData data);

    /// <summary>
    ///     Win32 API の FindClose を呼び出し、検索ハンドルを閉じます。
    /// </summary>
    /// <param name="handle">閉じる検索ハンドル。</param>
    /// <returns>成功した場合は true。</returns>
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr handle);

    public sealed class NativeDirectoryEntry
    {
        public string Name { get; internal set; }
        public FileAttributes Attributes { get; internal set; }
        public long Size { get; internal set; }
        public DateTime ModifiedUtc { get; internal set; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeFindData
    {
        public FileAttributes Attributes;
        public FILETIME CreationTime;
        public FILETIME LastAccessTime;
        public FILETIME LastWriteTime;
        public uint FileSizeHigh, FileSizeLow, Reserved0, Reserved1;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string FileName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string AlternateFileName;
    }

    private sealed class SafeFindHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        /// <summary>
        ///     所有権を持つ検索ハンドルのラッパーを作成します。
        /// </summary>
        public SafeFindHandle() : base(true)
        {
        }

        /// <summary>
        ///     検索ハンドルを FindClose で解放します。
        /// </summary>
        /// <returns>解放に成功した場合は true。</returns>
        protected override bool ReleaseHandle()
        {
            return FindClose(handle);
        }
    }
}