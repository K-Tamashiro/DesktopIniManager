using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DesktopIniManager.Services;

/// <summary>Hosts the classic Windows shell context menu on the UI's STA thread.</summary>
internal static class ExplorerContextMenu
{
    public static void Show(Window owner, string[] paths, Point screenPoint)
    {
        if (paths == null || paths.Length == 0) return;
        if (paths.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            throw new ArgumentException("シェルメニューでは同じフォルダー内のファイルを選択してください。", nameof(paths));
        var pidls = new IntPtr[paths.Length];
        IShellItemArray array = null;
        object context = null;
        var menu = IntPtr.Zero;
        HwndSource source = null;
        HwndSourceHook hook = null;
        try
        {
            for (var i = 0; i < paths.Length; i++)
                Marshal.ThrowExceptionForHR(SHParseDisplayName(paths[i], IntPtr.Zero, out pidls[i], 0, out _));
            Marshal.ThrowExceptionForHR(SHCreateShellItemArrayFromIDLists((uint)pidls.Length, pidls, out array));
            var handler = new Guid("3981E225-F559-11D3-8E3A-00C04F6837D5"); // BHID_SFUIObject
            var iid = typeof(IContextMenu).GUID;
            Marshal.ThrowExceptionForHR(array.BindToHandler(IntPtr.Zero, ref handler, ref iid, out context));
            var shell = (IContextMenu)context;
            var shell3 = context as IContextMenu3;
            var shell2 = context as IContextMenu2;
            var hwnd = new WindowInteropHelper(owner).EnsureHandle();
            source = HwndSource.FromHwnd(hwnd);
            hook = (h, message, w, l, ref handled) =>
            {
                // Forward only menu messages, never unrelated owner-drawn controls.
                if (message != 0x117 && message != 0x120 &&
                    !((message == 0x2B || message == 0x2C) && w == IntPtr.Zero)) return IntPtr.Zero;
                if (shell3 != null)
                {
                    var hr = shell3.HandleMenuMsg2((uint)message, w, l, out var result);
                    if (hr == 0)
                    {
                        handled = true;
                        return result;
                    }
                }
                else if (shell2 != null && message != 0x120)
                {
                    handled = shell2.HandleMenuMsg((uint)message, w, l) == 0;
                }

                return IntPtr.Zero;
            };
            source.AddHook(hook);
            menu = CreatePopupMenu();
            if (menu == IntPtr.Zero) throw new Win32Exception();
            // CMF_NORMAL: allow Windows and installed extensions to supply their verbs.
            Marshal.ThrowExceptionForHR(shell.QueryContextMenu(menu, 0, 1, 0x7FFF, 0));
            var command = TrackPopupMenuEx(menu, 0x100 | 0x2, (int)screenPoint.X, (int)screenPoint.Y, hwnd,
                IntPtr.Zero);
            if (command != 0)
            {
                var info = new InvokeInfo
                {
                    Size = Marshal.SizeOf<InvokeInfo>(),
                    Owner = hwnd,
                    Verb = (IntPtr)(command - 1),
                    Show = 1
                };
                Marshal.ThrowExceptionForHR(shell.InvokeCommand(ref info));
            }
        }
        finally
        {
            if (hook != null && source != null) source.RemoveHook(hook);
            if (menu != IntPtr.Zero) DestroyMenu(menu);
            if (context != null && Marshal.IsComObject(context)) Marshal.ReleaseComObject(context);
            if (array != null) Marshal.ReleaseComObject(array);
            foreach (var pidl in pidls)
                if (pidl != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(pidl);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint attributes,
        out uint resultAttributes);

    [DllImport("shell32.dll")]
    private static extern int SHCreateShellItemArrayFromIDLists(uint count, IntPtr[] pidls, out IShellItemArray array);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr parameters);

    [StructLayout(LayoutKind.Sequential)]
    private struct InvokeInfo
    {
        public int Size;
        public uint Mask;
        public IntPtr Owner, Verb, Parameters, Directory;
        public int Show;
        public uint HotKey;
        public IntPtr Icon;
    }

    [ComImport]
    [Guid("B63EA76D-1F85-456F-A19C-48159EFA858B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        [PreserveSig]
        int BindToHandler(IntPtr context, ref Guid handler, ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out object result);
    }

    [ComImport]
    [Guid("000214E4-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr menu, uint index, uint first, uint last, uint flags);

        [PreserveSig]
        int InvokeCommand(ref InvokeInfo info);

        [PreserveSig]
        int GetCommandString(UIntPtr command, uint flags, IntPtr reserved, IntPtr text, uint length);
    }

    // Repeat the base vtable slots explicitly for COM interface inheritance.
    [ComImport]
    [Guid("000214F4-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr menu, uint index, uint first, uint last, uint flags);

        [PreserveSig]
        int InvokeCommand(ref InvokeInfo info);

        [PreserveSig]
        int GetCommandString(UIntPtr command, uint flags, IntPtr reserved, IntPtr text, uint length);

        [PreserveSig]
        int HandleMenuMsg(uint message, IntPtr w, IntPtr l);
    }

    [ComImport]
    [Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr menu, uint index, uint first, uint last, uint flags);

        [PreserveSig]
        int InvokeCommand(ref InvokeInfo info);

        [PreserveSig]
        int GetCommandString(UIntPtr command, uint flags, IntPtr reserved, IntPtr text, uint length);

        [PreserveSig]
        int HandleMenuMsg(uint message, IntPtr w, IntPtr l);

        [PreserveSig]
        int HandleMenuMsg2(uint message, IntPtr w, IntPtr l, out IntPtr result);
    }
}