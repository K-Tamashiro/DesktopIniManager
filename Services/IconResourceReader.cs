using DesktopIniManager.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace DesktopIniManager.Services
{
    internal static class IconResourceReader
    {
        private const uint LoadLibraryAsDataFile = 0x00000002;
        private const uint LoadLibraryAsImageResource = 0x00000020;
        private static readonly IntPtr RtIcon = (IntPtr)3;
        private static readonly IntPtr RtGroupIcon = (IntPtr)14;
        private delegate bool EnumResNameProc(IntPtr module, IntPtr type, IntPtr name, IntPtr parameter);
        private delegate bool EnumResLangProc(IntPtr module, IntPtr type, IntPtr name, ushort language, IntPtr parameter);

        public static List<IconGroupResource> Read(string fileName)
        {
            if (string.Equals(Path.GetExtension(fileName), ".ico", StringComparison.OrdinalIgnoreCase))
                return new List<IconGroupResource> { ReadStandalone(fileName) };

            IntPtr module = LoadLibraryEx(fileName, IntPtr.Zero, LoadLibraryAsDataFile | LoadLibraryAsImageResource);
            if (module == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not open the icon library.");
            var result = new List<IconGroupResource>();
            try
            {
                EnumResourceNames(module, RtGroupIcon, (m, t, name, p) =>
                {
                    EnumResourceLanguages(module, RtGroupIcon, name, (m2, t2, n2, language, p2) =>
                    {
                        byte[] group = LoadBytes(module, RtGroupIcon, name, language);
                        if (group != null)
                        {
                            byte[] ico = BuildIcon(module, group, language);
                            result.Add(new IconGroupResource { ResourceName = ResourceName(name), Language = language, ImageCount = BitConverter.ToUInt16(group, 4), Preview = CreatePreview(ico) });
                        }
                        return true;
                    }, IntPtr.Zero);
                    return true;
                }, IntPtr.Zero);
            }
            finally { FreeLibrary(module); }
            var sorted = result.OrderBy(item => NumericName(item.ResourceName)).ThenBy(item => item.ResourceName).ThenBy(item => item.Language).ToList();
            for (int index = 0; index < sorted.Count; index++) sorted[index].ShellIndex = index;
            return sorted;
        }

        private static IconGroupResource ReadStandalone(string fileName)
        {
            byte[] ico = File.ReadAllBytes(fileName);
            if (ico.Length < 6 || BitConverter.ToUInt16(ico, 0) != 0 || BitConverter.ToUInt16(ico, 2) != 1) throw new InvalidDataException("The ICO file is invalid.");
            return new IconGroupResource { ResourceName = Path.GetFileNameWithoutExtension(fileName), ShellIndex = 0, ImageCount = BitConverter.ToUInt16(ico, 4), Preview = CreatePreview(ico) };
        }

        private static byte[] BuildIcon(IntPtr module, byte[] group, ushort language)
        {
            int count = BitConverter.ToUInt16(group, 4); var images = new List<byte[]>();
            for (int index = 0; index < count; index++)
            {
                int offset = 6 + index * 14; ushort id = BitConverter.ToUInt16(group, offset + 12);
                byte[] image = LoadBytes(module, RtIcon, (IntPtr)id, language) ?? LoadBytesAnyLanguage(module, RtIcon, (IntPtr)id);
                if (image == null) throw new InvalidDataException("Icon image " + id + " was not found.");
                images.Add(image);
            }
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            {
                writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)count); int dataOffset = 6 + count * 16;
                for (int index = 0; index < count; index++) { int source = 6 + index * 14; writer.Write(group, source, 12); writer.Write((uint)dataOffset); dataOffset += images[index].Length; }
                foreach (byte[] image in images) writer.Write(image);
                return stream.ToArray();
            }
        }

        private static BitmapSource CreatePreview(byte[] ico)
        {
            using (var stream = new MemoryStream(ico))
            {
                var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                BitmapSource frame = decoder.Frames.OrderByDescending(item => item.PixelWidth * item.PixelHeight).First(); frame.Freeze(); return frame;
            }
        }

        private static byte[] LoadBytesAnyLanguage(IntPtr module, IntPtr type, IntPtr name)
        {
            byte[] found = null; EnumResourceLanguages(module, type, name, (m, t, n, language, p) => { found = LoadBytes(module, type, name, language); return false; }, IntPtr.Zero); return found;
        }
        private static byte[] LoadBytes(IntPtr module, IntPtr type, IntPtr name, ushort language)
        {
            IntPtr resource = FindResourceEx(module, type, name, language); if (resource == IntPtr.Zero) return null;
            uint size = SizeofResource(module, resource); IntPtr loaded = LoadResource(module, resource); IntPtr pointer = LockResource(loaded); if (pointer == IntPtr.Zero || size == 0) return null;
            byte[] bytes = new byte[size]; Marshal.Copy(pointer, bytes, 0, (int)size); return bytes;
        }
        private static string ResourceName(IntPtr value) => ((ulong)value.ToInt64() >> 16) == 0 ? ((ushort)value.ToInt64()).ToString() : Marshal.PtrToStringUni(value);
        private static int NumericName(string value) => int.TryParse(value, out int number) ? number : int.MaxValue;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryEx(string file, IntPtr fileHandle, uint flags);
        [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr module);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumResourceNames(IntPtr module, IntPtr type, EnumResNameProc callback, IntPtr parameter);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumResourceLanguages(IntPtr module, IntPtr type, IntPtr name, EnumResLangProc callback, IntPtr parameter);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindResourceEx(IntPtr module, IntPtr type, IntPtr name, ushort language);
        [DllImport("kernel32.dll")] private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
        [DllImport("kernel32.dll")] private static extern IntPtr LockResource(IntPtr resourceData);
        [DllImport("kernel32.dll")] private static extern uint SizeofResource(IntPtr module, IntPtr resource);
    }
}
