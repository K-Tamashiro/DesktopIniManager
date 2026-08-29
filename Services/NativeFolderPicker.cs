using System;
using System.Runtime.InteropServices;

namespace DesktopIniManager.Services
{
    internal static class NativeFolderPicker
    {
        public static string Show(IntPtr owner, string initialPath, string title)
        {
            IFileDialog dialog = (IFileDialog)new FileOpenDialog();
            try
            {
                dialog.GetOptions(out FileOpenOptions options);
                dialog.SetOptions(options | FileOpenOptions.PickFolders | FileOpenOptions.ForceFileSystem | FileOpenOptions.PathMustExist | FileOpenOptions.NoChangeDirectory);
                dialog.SetTitle(title);

                IShellItem initialItem = null;
                if (!string.IsNullOrWhiteSpace(initialPath) && SHCreateItemFromParsingName(initialPath, IntPtr.Zero, typeof(IShellItem).GUID, out initialItem) == 0)
                {
                    try { dialog.SetFolder(initialItem); }
                    finally { Marshal.ReleaseComObject(initialItem); }
                }

                int result = dialog.Show(owner);
                if (result == unchecked((int)0x800704C7)) return null; // cancelled
                if (result < 0) Marshal.ThrowExceptionForHR(result);
                dialog.GetResult(out IShellItem selectedItem);
                try
                {
                    selectedItem.GetDisplayName(ShellItemDisplayName.FileSystemPath, out IntPtr pathPointer);
                    try { return Marshal.PtrToStringUni(pathPointer); }
                    finally { Marshal.FreeCoTaskMem(pathPointer); }
                }
                finally { Marshal.ReleaseComObject(selectedItem); }
            }
            finally { Marshal.ReleaseComObject(dialog); }
        }

        [Flags]
        private enum FileOpenOptions : uint
        {
            PickFolders = 0x20, ForceFileSystem = 0x40, NoChangeDirectory = 0x8, PathMustExist = 0x800
        }
        private enum ShellItemDisplayName : uint { FileSystemPath = 0x80058000 }

        [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
        private class FileOpenDialog { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("42F85136-DB7E-439C-85F1-E4075D135FC8")]
        private interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint count, IntPtr filterSpec);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(FileOpenOptions options);
            void GetOptions(out FileOpenOptions options);
            void SetDefaultFolder(IShellItem item);
            void SetFolder(IShellItem item);
            void GetFolder(out IShellItem item);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
            void AddPlace(IShellItem item, int alignment);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
            void Close(int error);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr filter);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
        private interface IShellItem
        {
            void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr pointer);
            void GetParent(out IShellItem parent);
            void GetDisplayName(ShellItemDisplayName displayName, out IntPtr name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem item, uint hint, out int order);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr bindingContext, [MarshalAs(UnmanagedType.LPStruct)] Guid iid, out IShellItem item);
    }
}
