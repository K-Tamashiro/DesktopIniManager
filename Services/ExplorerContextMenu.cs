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

    /// <summary>
    ///     指定パスに対する Explorer のコンテキスト メニューを、独自のコマンド項目と組み合わせて表示し、選択されたコマンドを実行します。
    ///     独自項目がある場合、Explorer の項目は「Explorer Menu」サブメニューにまとめます。
    /// </summary>
    /// <param name="owner">メニューのオーナー ウィンドウ。</param>
    /// <param name="paths">対象のパス (すべて同じフォルダー内である必要があります)。</param>
    /// <param name="screenPoint">メニューを表示するスクリーン座標。</param>
    /// <param name="customItems">先頭に追加する独自のコマンド項目。</param>
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

    /// <summary>
    ///     Win32 API の SHParseDisplayName を呼び出し、パスを ITEMIDLIST に変換します。
    /// </summary>
    /// <param name="name">変換するパス。</param>
    /// <param name="bindContext">バインド コンテキスト。</param>
    /// <param name="pidl">作成された ITEMIDLIST。</param>
    /// <param name="attributes">問い合わせる属性。</param>
    /// <param name="resultAttributes">取得した属性。</param>
    /// <returns>HRESULT。</returns>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint attributes,
        out uint resultAttributes);

    /// <summary>
    ///     Win32 API の SHCreateShellItemArrayFromIDLists を呼び出し、ITEMIDLIST の配列からシェル項目配列を作成します。
    /// </summary>
    /// <param name="count">ITEMIDLIST の数。</param>
    /// <param name="pidls">ITEMIDLIST の配列。</param>
    /// <param name="array">作成されたシェル項目配列。</param>
    /// <returns>HRESULT。</returns>
    [DllImport("shell32.dll")]
    private static extern int SHCreateShellItemArrayFromIDLists(uint count, IntPtr[] pidls, out IShellItemArray array);

    /// <summary>
    ///     Win32 API の CreatePopupMenu を呼び出し、空のポップアップ メニューを作成します。
    /// </summary>
    /// <returns>メニュー ハンドル。失敗時は IntPtr.Zero。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    /// <summary>
    ///     Win32 API の AppendMenu を呼び出し、メニューに項目を追加します。
    /// </summary>
    /// <param name="menu">追加先のメニュー。</param>
    /// <param name="flags">項目の種類と状態を表すフラグ。</param>
    /// <param name="item">コマンド ID またはサブメニューのハンドル。</param>
    /// <param name="caption">項目の表示文字列。</param>
    /// <returns>成功した場合は true。</returns>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr item, string caption);

    /// <summary>
    ///     Win32 API の DestroyMenu を呼び出し、メニューとそのサブメニューを破棄します。
    /// </summary>
    /// <param name="menu">破棄するメニュー。</param>
    /// <returns>成功した場合は true。</returns>
    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    /// <summary>
    ///     Win32 API の TrackPopupMenuEx を呼び出し、ポップアップ メニューを表示して選択を待ちます。
    /// </summary>
    /// <param name="menu">表示するメニュー。</param>
    /// <param name="flags">表示オプション。</param>
    /// <param name="x">表示位置の X 座標。</param>
    /// <param name="y">表示位置の Y 座標。</param>
    /// <param name="owner">オーナー ウィンドウのハンドル。</param>
    /// <param name="parameters">追加パラメーター。</param>
    /// <returns>選択されたコマンド ID。選択されなかった場合は 0。</returns>
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
        /// <summary>
        ///     シェル項目配列を指定したハンドラーにバインドします。
        /// </summary>
        /// <param name="context">バインド コンテキスト。</param>
        /// <param name="handler">ハンドラーの GUID。</param>
        /// <param name="iid">要求するインターフェイスの IID。</param>
        /// <param name="result">取得したハンドラー。</param>
        /// <returns>HRESULT。</returns>
        [PreserveSig]
        int BindToHandler(IntPtr context, ref Guid handler, ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out object result);
    }

    [ComImport]
    [Guid("000214E4-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        /// <summary>
        ///     コンテキスト メニューにシェルのコマンド項目を追加します。
        /// </summary>
        /// <param name="menu">追加先のメニュー。</param>
        /// <param name="index">挿入位置。</param>
        /// <param name="first">使用できる最小のコマンド ID。</param>
        /// <param name="last">使用できる最大のコマンド ID。</param>
        /// <param name="flags">追加方法のフラグ。</param>
        /// <returns>HRESULT。</returns>
        [PreserveSig]
        int QueryContextMenu(IntPtr menu, uint index, uint first, uint last, uint flags);

        /// <summary>
        ///     選択されたシェル コマンドを実行します。
        /// </summary>
        /// <param name="info">実行するコマンドの情報。</param>
        /// <returns>HRESULT。</returns>
        [PreserveSig]
        int InvokeCommand(ref InvokeInfo info);

        /// <summary>
        ///     コマンドの動詞やヘルプ文字列を取得します。
        /// </summary>
        /// <param name="command">コマンド ID。</param>
        /// <param name="flags">取得する情報の種類。</param>
        /// <param name="reserved">予約済み。</param>
        /// <param name="text">文字列を受け取るバッファー。</param>
        /// <param name="length">バッファーのサイズ。</param>
        /// <returns>HRESULT。</returns>
        [PreserveSig]
        int GetCommandString(UIntPtr command, uint flags, IntPtr reserved, IntPtr text, uint length);
    }

    // Repeat the base vtable slots explicitly for COM interface inheritance.
    [ComImport]
    [Guid("000214F4-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        /// <summary>
        ///     コンテキスト メニューにシェルのコマンド項目を追加します。
        /// </summary>
        /// <param name="menu">追加先のメニュー。</param>
        /// <param name="index">挿入位置。</param>
        /// <param name="first">使用できる最小のコマンド ID。</param>
        /// <param name="last">使用できる最大のコマンド ID。</param>
        /// <param name="flags">追加方法のフラグ。</param>
        /// <returns>HRESULT。</returns>
        [PreserveSig]
        int QueryContextMenu(IntPtr menu, uint index, uint first, uint last, uint flags);

        /// <summary>
        ///     選択されたシェル コマンドを実行します。
        /// </summary>
        /// <param name="info">実行するコマンドの情報。</param>
        /// <returns>HRESULT。</returns>
        [PreserveSig]
        int InvokeCommand(ref InvokeInfo info);

        /// <summary>
        ///     コマンドの動詞やヘルプ文字列を取得します。
        /// </summary>
        /// <param name="command">コマンド ID。</param>
        /// <param name="flags">取得する情報の種類。</param>
        /// <param name="reserved">予約済み。</param>
        /// <param name="text">文字列を受け取るバッファー。</param>
        /// <param name="length">バッファーのサイズ。</param>
        /// <returns>HRESULT。</returns>
        [PreserveSig]
        int GetCommandString(UIntPtr command, uint flags, IntPtr reserved, IntPtr text, uint length);

        /// <summary>
        ///     オーナー描画やサブメニュー初期化のメニュー メッセージを処理します。
        /// </summary>
        /// <param name="message">ウィンドウ メッセージ。</param>
        /// <param name="w">WPARAM。</param>
        /// <param name="l">LPARAM。</param>
        /// <returns>HRESULT。</returns>
        [PreserveSig]
        int HandleMenuMsg(uint message, IntPtr w, IntPtr l);
    }

    [ComImport]
    [Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        /// <summary>
        ///     コンテキスト メニューにシェルのコマンド項目を追加します。
        /// </summary>
        /// <param name="menu">追加先のメニュー。</param>
        /// <param name="index">挿入位置。</param>
        /// <param name="first">使用できる最小のコマンド ID。</param>
        /// <param name="last">使用できる最大のコマンド ID。</param>
        /// <param name="flags">追加方法のフラグ。</param>
        /// <returns>HRESULT。</returns>
        [PreserveSig]
        int QueryContextMenu(IntPtr menu, uint index, uint first, uint last, uint flags);

        /// <summary>
        ///     選択されたシェル コマンドを実行します。
        /// </summary>
        /// <param name="info">実行するコマンドの情報。</param>
        /// <returns>HRESULT。</returns>
        [PreserveSig]
        int InvokeCommand(ref InvokeInfo info);

        /// <summary>
        ///     コマンドの動詞やヘルプ文字列を取得します。
        /// </summary>
        /// <param name="command">コマンド ID。</param>
        /// <param name="flags">取得する情報の種類。</param>
        /// <param name="reserved">予約済み。</param>
        /// <param name="text">文字列を受け取るバッファー。</param>
        /// <param name="length">バッファーのサイズ。</param>
        /// <returns>HRESULT。</returns>
        [PreserveSig]
        int GetCommandString(UIntPtr command, uint flags, IntPtr reserved, IntPtr text, uint length);

        /// <summary>
        ///     オーナー描画やサブメニュー初期化のメニュー メッセージを処理します。
        /// </summary>
        /// <param name="message">ウィンドウ メッセージ。</param>
        /// <param name="w">WPARAM。</param>
        /// <param name="l">LPARAM。</param>
        /// <returns>HRESULT。</returns>
        [PreserveSig]
        int HandleMenuMsg(uint message, IntPtr w, IntPtr l);

        /// <summary>
        ///     メニュー メッセージを処理し、結果値を返します。
        /// </summary>
        /// <param name="message">ウィンドウ メッセージ。</param>
        /// <param name="w">WPARAM。</param>
        /// <param name="l">LPARAM。</param>
        /// <param name="result">メッセージの処理結果。</param>
        /// <returns>HRESULT。</returns>
        [PreserveSig]
        int HandleMenuMsg2(uint message, IntPtr w, IntPtr l, out IntPtr result);
    }
}
