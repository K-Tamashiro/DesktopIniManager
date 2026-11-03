using DesktopIniManager.Properties;
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DesktopIniManager.Services;

/// <summary>Hosts the classic Windows shell context menu on the UI's STA thread.</summary>
internal static class ExplorerContextMenu
{
    internal sealed record CommandItem(string Caption, Action Action, bool Enabled = true,
        IReadOnlyList<CommandItem> Children = null);

    public static void Show(Window owner, string[] paths, Point screenPoint,
        IReadOnlyList<CommandItem> customItems = null)
    {
        paths ??= Array.Empty<string>();
        if (paths.Length == 0 && (customItems == null || customItems.Count == 0)) return;
        if (paths.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            throw new ArgumentException(
                Strings.ShellMenu_SelectFilesFromSameFolder,
                nameof(paths));
        var pidls = new IntPtr[paths.Length];
        var customCommands = new Dictionary<uint, Action>();
        uint nextCustomId = 0x8000;
        IShellItemArray array = null;
        object context = null;
        var menu = IntPtr.Zero;
        HwndSource source = null;
        HwndSourceHook hook = null;
        try
        {
            IContextMenu shell = null;
            IContextMenu3 shell3 = null;
            IContextMenu2 shell2 = null;
            if (paths.Length > 0)
            {
                for (var i = 0; i < paths.Length; i++)
                    Marshal.ThrowExceptionForHR(SHParseDisplayName(paths[i], IntPtr.Zero, out pidls[i], 0, out _));
                Marshal.ThrowExceptionForHR(SHCreateShellItemArrayFromIDLists((uint)pidls.Length, pidls, out array));
                var handler = new Guid("3981E225-F559-11D3-8E3A-00C04F6837D5");
                var iid = typeof(IContextMenu).GUID;
                Marshal.ThrowExceptionForHR(array.BindToHandler(IntPtr.Zero, ref handler, ref iid, out context));
                shell = (IContextMenu)context;
                shell3 = context as IContextMenu3;
                shell2 = context as IContextMenu2;
            }
            var hwnd = new WindowInteropHelper(owner).EnsureHandle();
            source = HwndSource.FromHwnd(hwnd) ??
                     throw new InvalidOperationException("The owner window has no HWND source.");
            hook = (_, message, w, l, ref handled) =>
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

            static IntPtr AddSubmenu(IntPtr parent, string caption, bool enabled)
            {
                var submenu = CreatePopupMenu();
                if (submenu == IntPtr.Zero) throw new Win32Exception();
                if (AppendMenu(parent, 0x10u | (enabled ? 0u : 0x1u),
                        (UIntPtr)(ulong)submenu.ToInt64(), caption)) return submenu;
                var error = new Win32Exception();
                DestroyMenu(submenu);
                throw error;
            }

            if (customItems != null)
            {
                void AddItems(IntPtr target, IReadOnlyList<CommandItem> items)
                {
                    foreach (var item in items)
                    {
                        if (item.Children is { Count: > 0 })
                        {
                            var submenu = AddSubmenu(target, item.Caption, item.Enabled);
                            AddItems(submenu, item.Children);
                        }
                        else
                        {
                            var id = nextCustomId++;
                            if (item.Action != null && item.Enabled) customCommands[id] = item.Action;
                            if (!AppendMenu(target,
                                    item.Enabled && item.Action != null ? 0u : 0x1u,
                                    id, item.Caption))
                                throw new Win32Exception();
                        }
                    }
                }
                AddItems(menu, customItems);
            }
            var shellMenu = menu;
            if (customItems != null)
            {
                if (customItems is { Count: > 0 } && !AppendMenu(menu, 0x800, UIntPtr.Zero, null))
                    throw new Win32Exception();
                shellMenu = AddSubmenu(menu, "Explorer Menu", shell != null);
            }
            if (shell != null)
            {
                // Shell command IDs occupy 1..0x7FFF; DIM IDs start at 0x8000.
                Marshal.ThrowExceptionForHR(shell.QueryContextMenu(shellMenu, 0, 1, 0x7FFF, 0));
            }
            var command = TrackPopupMenuEx(menu, 0x100 | 0x2, (int)screenPoint.X, (int)screenPoint.Y, hwnd,
                IntPtr.Zero);
            if (command != 0)
            {
                if (customCommands.TryGetValue(command, out var action))
                    action();
                else if (shell != null && command < 0x8000)
                {
                    var info = new InvokeInfo
                    {
                        Size = Marshal.SizeOf<InvokeInfo>(),
                        Owner = hwnd,
                        Verb = (IntPtr)(command - 1),
                        ShowCommand = 1
                    };
                    Marshal.ThrowExceptionForHR(shell.InvokeCommand(ref info));
                }
            }
        }
        finally
        {
            if (hook != null && source != null) source.RemoveHook(hook);
            if (menu != IntPtr.Zero) DestroyMenu(menu);
            // DestroyMenu recursively destroys attached submenus.
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr item, string caption);

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
        public int ShowCommand;
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
