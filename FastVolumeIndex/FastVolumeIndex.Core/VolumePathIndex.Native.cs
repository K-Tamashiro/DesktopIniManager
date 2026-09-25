using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace FastVolumeIndex
{
    public sealed partial class VolumePathIndex
    {
        public sealed class NativeDirectoryEntry
        {
            public string Name { get; internal set; }
            public FileAttributes Attributes { get; internal set; }
            public long Size { get; internal set; }
            public DateTime ModifiedUtc { get; internal set; }
        }

        /// <summary>Streams all immediate entries, including hidden/system entries.
        /// Access failures are reported rather than mistaken for missing comparison files.</summary>
        public static IEnumerable<NativeDirectoryEntry> EnumerateNativeDirectory(string folder, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string pattern = NativeSearchPath(Normalize(folder));
            NativeFindData data;
            SafeFindHandle handle = FindFirstFileExW(pattern, 1, out data, 0, IntPtr.Zero, 2);
            int error = handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
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
                        long writeTime = ((long)(uint)data.LastWriteTime.dwHighDateTime << 32)
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
            string rootPath = Normalize(searchRoot);
            if (!Directory.Exists(rootPath)) throw new DirectoryNotFoundException(rootPath);
            var nodes = new Dictionary<string, VolumePathNode>(StringComparer.OrdinalIgnoreCase);
            var root = new VolumePathNode(rootPath, FileAttributes.Directory, true);
            nodes.Add(rootPath, root);
            var projectFiles = new List<string>();
            var pending = new Stack<string>();
            pending.Push(rootPath);
            int scanned = 0;

            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                string folder = pending.Pop();
                string pattern = NativeSearchPath(folder);
                NativeFindData data;
                SafeFindHandle handle = FindFirstFileExW(pattern, 1, out data, 0, IntPtr.Zero, 2);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    // Some file-system providers do not support LARGE_FETCH.
                    if (error == 87 || error == 50)
                        handle = FindFirstFileExW(pattern, 1, out data, 0, IntPtr.Zero, 0);
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
                        int error = Marshal.GetLastWin32Error();
                        if (CanSkipEnumerationError(error)) continue;
                        throw new Win32Exception(error, "Cannot enumerate " + folder);
                    }
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        string name = data.FileName;
                        if (name != "." && name != "..")
                        {
                            bool directory = (data.Attributes & FileAttributes.Directory) != 0;
                            // Keep all files and directory nodes in the index.
                            // Visibility is handled by the UI/search layer.
                            {
                                string path = Path.Combine(folder, name);
                                nodes[path] = new VolumePathNode(path, data.Attributes);
                                if (directory)
                                {
                                    // Keep link nodes but never follow directory links/junctions.
                                    if ((data.Attributes & FileAttributes.ReparsePoint) == 0)
                                        pending.Push(path);
                                }
                                else if (ProjectFileExtensions.Contains(Path.GetExtension(name)))
                                    projectFiles.Add(path);
                                if ((++scanned & 63) == 0) progress?.Invoke(scanned);
                            }
                        }
                        if (!FindNextFileW(handle, out data))
                        {
                            int error = Marshal.GetLastWin32Error();
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

        private static bool CanSkipEnumerationError(int error) =>
            error == 2 || error == 3 || error == 5; // Missing/deleted entry or access denied.

        private static string NativeSearchPath(string folder)
        {
            string path = folder;
            if (!path.StartsWith(@"\\?\", StringComparison.Ordinal))
                path = path.StartsWith(@"\\", StringComparison.Ordinal)
                    ? @"\\?\UNC\" + path.Substring(2) : @"\\?\" + path;
            return path.TrimEnd('\\') + @"\*";
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NativeFindData
        {
            public FileAttributes Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint FileSizeHigh, FileSizeLow, Reserved0, Reserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string FileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateFileName;
        }

        private sealed class SafeFindHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public SafeFindHandle() : base(true) { }
            protected override bool ReleaseHandle() => FindClose(handle);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern SafeFindHandle FindFirstFileExW(string fileName, int infoLevel,
            out NativeFindData data, int searchOp, IntPtr filter, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FindNextFileW(SafeFindHandle handle, out NativeFindData data);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FindClose(IntPtr handle);
    }
}
