using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace FastVolumeIndex
{
    public sealed class NtfsVolumeIndex
    {
        private const uint GenericRead = 0x80000000;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint FileShareDelete = 0x00000004;
        private const uint OpenExisting = 3;
        private const uint FsctlEnumUsnData = 0x000900b3;
        private const int ErrorHandleEof = 38;
        private const int OutputBufferSize = 1024 * 1024;
        private const uint FileFlagBackupSemantics = 0x02000000;

        private readonly Dictionary<ulong, MftEntry> _entries;
        private readonly Dictionary<ulong, List<MftEntry>> _children;
        private readonly Dictionary<ulong, string> _pathCache;

        private NtfsVolumeIndex(string volumeRoot, Dictionary<ulong, MftEntry> entries, TimeSpan elapsed)
        {
            VolumeRoot = volumeRoot;
            _entries = entries;
            _children = entries.Values
                .Where(entry => entry.ParentId != entry.Id)
                .GroupBy(entry => entry.ParentId)
                .ToDictionary(group => group.Key, group => group.ToList());
            _pathCache = new Dictionary<ulong, string>();
            EnumerationTime = elapsed;
        }

        public string VolumeRoot { get; }
        public TimeSpan EnumerationTime { get; }
        public int EntryCount => _entries.Count;
        public int FileCount => _entries.Values.Count(entry => !entry.IsDirectory);
        public int DirectoryCount => _entries.Values.Count(entry => entry.IsDirectory);
        public IEnumerable<MftEntry> Entries => _entries.Values;

        public static NtfsVolumeIndex Create(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A path on an NTFS volume is required.", nameof(path));

            string fullPath = NormalizeLocalPath(Path.GetFullPath(path));
            if (!Directory.Exists(fullPath))
                throw new DirectoryNotFoundException($"Search root was not found: {fullPath}");
            string root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
                throw new NotSupportedException("Only local NTFS volumes are supported by the MFT engine.");

            var drive = new DriveInfo(root);
            if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"Volume {root} uses {drive.DriveFormat}; the MFT engine requires NTFS.");

            string volumePath = @"\\.\" + root.TrimEnd('\\');
            var stopwatch = Stopwatch.StartNew();
            var entries = EnumerateVolume(volumePath);
            stopwatch.Stop();
            return new NtfsVolumeIndex(root, entries, stopwatch.Elapsed);
        }

        public string GetFullPath(MftEntry entry)
        {
            if (entry == null)
                throw new ArgumentNullException(nameof(entry));
            return ResolvePath(entry.Id, new HashSet<ulong>());
        }

        public IReadOnlyList<MftEntry> SearchNames(string searchRoot, string query, bool directoriesOnly = false)
        {
            if (string.IsNullOrWhiteSpace(query))
                throw new ArgumentException("A search term is required.", nameof(query));

            HashSet<ulong> descendantIds = GetSearchScopeIds(searchRoot);
            return _entries.Values
                .Where(entry => (!directoriesOnly || entry.IsDirectory)
                    && entry.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .Where(entry => descendantIds == null || descendantIds.Contains(entry.Id))
                .OrderBy(GetFullPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public IReadOnlyList<MftEntry> FindGitRepositoryRoots(string searchRoot)
        {
            HashSet<ulong> descendantIds = GetSearchScopeIds(searchRoot);
            var repositoryIds = new HashSet<ulong>();

            foreach (MftEntry marker in _entries.Values.Where(entry =>
                string.Equals(entry.Name, ".git", StringComparison.OrdinalIgnoreCase)))
            {
                if (_entries.TryGetValue(marker.ParentId, out MftEntry parent)
                    && (descendantIds == null || descendantIds.Contains(parent.Id)))
                {
                    repositoryIds.Add(parent.Id);
                }
            }

            return repositoryIds.Select(id => _entries[id])
                .OrderBy(GetFullPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public IReadOnlyList<MftEntry> GetChildren(MftEntry parent, bool includeFiles = true)
        {
            if (parent == null)
                throw new ArgumentNullException(nameof(parent));

            if (!_children.TryGetValue(parent.Id, out List<MftEntry> children))
                return new List<MftEntry>();

            return children
                .Where(entry => includeFiles || entry.IsDirectory)
                .OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public MftEntry FindByPath(string path)
        {
            string normalizedPath = NormalizeSearchRoot(path);
            if (string.Equals(normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                VolumeRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
                return FindVolumeRootEntry();

            using (SafeFileHandle handle = CreateFile(normalizedPath, 0,
                FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting,
                FileFlagBackupSemantics, IntPtr.Zero))
            {
                if (handle.IsInvalid || !GetFileInformationByHandle(handle, out ByHandleFileInformation info))
                    return null;

                ulong fileId = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
                if (_entries.TryGetValue(fileId, out MftEntry entry))
                    return entry;
            }

            string comparisonPath = normalizedPath.TrimEnd('\\');
            return _entries.Values
                .Where(entry => entry.IsDirectory)
                .Where(entry => string.Equals(GetFullPath(entry).TrimEnd('\\'), comparisonPath,
                    StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => _children.TryGetValue(entry.Id, out List<MftEntry> children)
                    ? children.Count
                    : 0)
                .FirstOrDefault();
        }

        private MftEntry FindVolumeRootEntry()
        {
            const ulong FileReferenceNumberMask = 0x0000FFFFFFFFFFFFUL;
            MftEntry root = _entries.Values
                .Where(entry => entry.IsDirectory && ((entry.Id & FileReferenceNumberMask) == 5
                    || entry.ParentId == entry.Id
                    || string.Equals(entry.Name, ".", StringComparison.Ordinal)))
                .OrderByDescending(entry => (entry.Id & FileReferenceNumberMask) == 5)
                .ThenByDescending(entry => entry.ParentId == entry.Id)
                .ThenByDescending(entry => string.Equals(entry.Name, ".", StringComparison.Ordinal))
                .ThenByDescending(entry => _children.TryGetValue(entry.Id, out List<MftEntry> children)
                    ? children.Count
                    : 0)
                .FirstOrDefault();
            if (root != null)
                return root;

            // FSCTL_ENUM_USN_DATA may omit the volume root record. In that case,
            // top-level entries share a parent file reference that is not present in
            // the returned records. Use that missing parent as a synthetic root so
            // GetChildren can still enumerate the drive correctly.
            var missingParent = _children
                .Where(pair => !_entries.ContainsKey(pair.Key))
                .OrderByDescending(pair => pair.Value.Count)
                .FirstOrDefault();
            return missingParent.Value == null
                ? null
                : new MftEntry(missingParent.Key, missingParent.Key, ".", FileAttributes.Directory);
        }

        public IReadOnlyList<MftEntry> FindFiles(string searchRoot, IEnumerable<string> extensions,
            IEnumerable<string> exactNames = null)
        {
            HashSet<ulong> descendantIds = GetSearchScopeIds(searchRoot);
            var extensionSet = new HashSet<string>(extensions ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var nameSet = new HashSet<string>(exactNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            return _entries.Values
                .Where(entry => !entry.IsDirectory)
                .Where(entry => extensionSet.Contains(Path.GetExtension(entry.Name)) || nameSet.Contains(entry.Name))
                .Where(entry => descendantIds == null || descendantIds.Contains(entry.Id))
                .OrderBy(GetFullPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private string NormalizeSearchRoot(string searchRoot)
        {
            string path = string.IsNullOrWhiteSpace(searchRoot)
                ? VolumeRoot
                : NormalizeLocalPath(Path.GetFullPath(searchRoot));
            string root = Path.GetPathRoot(path);
            if (!string.Equals(root, VolumeRoot, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Search root must be on volume {VolumeRoot}.", nameof(searchRoot));
            if (string.Equals(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
                return root;
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string NormalizeLocalPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            // Canonicalize drive-letter paths independently of Path.GetPathRoot,
            // which preserves an extra trailing separator for inputs such as E:\\.
            if (path.Length >= 2 && path[1] == ':')
            {
                string drive = path.Substring(0, 2);
                string driveRemainder = path.Substring(2).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                while (driveRemainder.Contains(@"\\"))
                    driveRemainder = driveRemainder.Replace(@"\\", @"\");
                if (driveRemainder.Trim(Path.DirectorySeparatorChar).Length == 0)
                    return drive + Path.DirectorySeparatorChar;
                return drive + (driveRemainder[0] == Path.DirectorySeparatorChar ? driveRemainder : Path.DirectorySeparatorChar + driveRemainder);
            }

            string root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
                return path;

            string remainder = path.Substring(root.Length);
            while (remainder.Contains(@"\\"))
                remainder = remainder.Replace(@"\\", @"\");
            return root + remainder;
        }

        private HashSet<ulong> GetSearchScopeIds(string searchRoot)
        {
            string normalizedPath = NormalizeSearchRoot(searchRoot);
            if (string.Equals(normalizedPath.TrimEnd('\\'), VolumeRoot.TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase))
                return null;

            MftEntry entry = FindByPath(normalizedPath);
            if (entry == null)
                throw new DirectoryNotFoundException($"Search root was not found in the MFT index: {searchRoot}");
            return GetDescendantIds(entry);
        }

        private HashSet<ulong> GetDescendantIds(MftEntry root)
        {
            var result = new HashSet<ulong>();
            var pending = new Stack<ulong>();
            pending.Push(root.Id);
            while (pending.Count > 0)
            {
                ulong id = pending.Pop();
                if (!result.Add(id))
                    continue;
                if (_children.TryGetValue(id, out List<MftEntry> children))
                {
                    foreach (MftEntry child in children)
                        pending.Push(child.Id);
                }
            }
            return result;
        }

        private string ResolvePath(ulong id, HashSet<ulong> visiting)
        {
            if (_pathCache.TryGetValue(id, out string cached))
                return cached;
            if (!_entries.TryGetValue(id, out MftEntry entry))
                return VolumeRoot;
            if (!visiting.Add(id))
                return VolumeRoot;

            string path;
            if (entry.ParentId == entry.Id)
            {
                path = VolumeRoot;
            }
            else if (!_entries.ContainsKey(entry.ParentId))
            {
                path = string.Equals(entry.Name, ".", StringComparison.Ordinal)
                    ? VolumeRoot
                    : Path.Combine(VolumeRoot, entry.Name);
            }
            else
            {
                string parentPath = ResolvePath(entry.ParentId, visiting);
                path = string.Equals(entry.Name, ".", StringComparison.Ordinal)
                    ? parentPath
                    : Path.Combine(parentPath, entry.Name);
            }

            visiting.Remove(id);
            _pathCache[id] = path;
            return path;
        }

        private static Dictionary<ulong, MftEntry> EnumerateVolume(string volumePath)
        {
            using (SafeFileHandle volume = CreateFile(volumePath, GenericRead,
                FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero))
            {
                if (volume.IsInvalid)
                    throw CreateVolumeException(volumePath, Marshal.GetLastWin32Error());

                var entries = new Dictionary<ulong, MftEntry>();
                var input = new MftEnumData { StartFileReferenceNumber = 0, LowUsn = 0, HighUsn = long.MaxValue };
                int inputSize = Marshal.SizeOf(typeof(MftEnumData));
                IntPtr inputBuffer = Marshal.AllocHGlobal(inputSize);
                IntPtr outputBuffer = Marshal.AllocHGlobal(OutputBufferSize);

                try
                {
                    while (true)
                    {
                        Marshal.StructureToPtr(input, inputBuffer, false);
                        bool success = DeviceIoControl(volume, FsctlEnumUsnData, inputBuffer, (uint)inputSize,
                            outputBuffer, OutputBufferSize, out uint bytesReturned, IntPtr.Zero);

                        if (!success)
                        {
                            int error = Marshal.GetLastWin32Error();
                            if (error == ErrorHandleEof)
                                break;
                            throw new Win32Exception(error, "Failed to enumerate NTFS MFT records.");
                        }

                        if (bytesReturned < sizeof(long))
                            break;

                        input.StartFileReferenceNumber = unchecked((ulong)Marshal.ReadInt64(outputBuffer));
                        int offset = sizeof(long);
                        while (offset + 60 <= bytesReturned)
                        {
                            IntPtr record = IntPtr.Add(outputBuffer, offset);
                            uint recordLength = unchecked((uint)Marshal.ReadInt32(record, 0));
                            if (recordLength < 60 || offset + recordLength > bytesReturned)
                                break;

                            ushort majorVersion = unchecked((ushort)Marshal.ReadInt16(record, 4));
                            if (majorVersion == 2)
                            {
                                ulong id = unchecked((ulong)Marshal.ReadInt64(record, 8));
                                ulong parentId = unchecked((ulong)Marshal.ReadInt64(record, 16));
                                var attributes = (FileAttributes)Marshal.ReadInt32(record, 52);
                                ushort nameLength = unchecked((ushort)Marshal.ReadInt16(record, 56));
                                ushort nameOffset = unchecked((ushort)Marshal.ReadInt16(record, 58));
                                if (nameOffset + nameLength <= recordLength)
                                {
                                    string name = Marshal.PtrToStringUni(IntPtr.Add(record, nameOffset), nameLength / 2);
                                    if (!string.IsNullOrEmpty(name))
                                        entries[id] = new MftEntry(id, parentId, name, attributes);
                                }
                            }

                            offset += checked((int)recordLength);
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(outputBuffer);
                    Marshal.FreeHGlobal(inputBuffer);
                }

                return entries;
            }
        }

        private static Exception CreateVolumeException(string volumePath, int error)
        {
            if (error == 5)
                return new UnauthorizedAccessException(
                    $"Access to {volumePath} was denied. Run the process as administrator to use MFT search.",
                    new Win32Exception(error));
            return new Win32Exception(error, $"Could not open NTFS volume {volumePath}.");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MftEnumData
        {
            public ulong StartFileReferenceNumber;
            public long LowUsn;
            public long HighUsn;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
            IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode,
            IntPtr inputBuffer, uint inputBufferSize, IntPtr outputBuffer, int outputBufferSize,
            out uint bytesReturned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file,
            out ByHandleFileInformation fileInformation);
    }
}
