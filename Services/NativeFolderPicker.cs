using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DesktopIniManager.Services;

[SupportedOSPlatform("windows")]
internal static class NativeFolderPicker
{
    /// <summary>
    ///     Windows 標準のフォルダー選択ダイアログを表示します。
    /// </summary>
    /// <param name="owner">オーナー ウィンドウのハンドル。</param>
    /// <param name="initialPath">最初に表示するフォルダー。</param>
    /// <param name="title">ダイアログのタイトル。</param>
    /// <returns>選択したフォルダーのパス。キャンセルした場合は null。</returns>
    public static string Show(IntPtr owner, string initialPath, string title)
    {
        var dialog = (IFileDialog)(Activator.CreateInstance(typeof(FileOpenDialog))
                                   ?? throw new InvalidOperationException(nameof(FileOpenDialog)));
        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | FileOpenOptions.PickFolders | FileOpenOptions.ForceFileSystem |
                              FileOpenOptions.PathMustExist | FileOpenOptions.NoChangeDirectory);
            dialog.SetTitle(title);

            if (!string.IsNullOrWhiteSpace(initialPath) &&
                SHCreateItemFromParsingName(initialPath, IntPtr.Zero, typeof(IShellItem).GUID, out var initialItem) ==
                0)
                try
                {
                    dialog.SetFolder(initialItem);
                }
                finally
                {
                    Marshal.ReleaseComObject(initialItem);
                }

            var result = dialog.Show(owner);
            if (result == unchecked((int)0x800704C7)) return null; // cancelled
            if (result < 0) Marshal.ThrowExceptionForHR(result);
            dialog.GetResult(out var selectedItem);
            try
            {
                selectedItem.GetDisplayName(ShellItemDisplayName.FileSystemPath, out var pathPointer);
                try
                {
                    return Marshal.PtrToStringUni(pathPointer);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pathPointer);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(selectedItem);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(dialog);
        }
    }

    /// <summary>
    ///     Win32 API の SHCreateItemFromParsingName を呼び出し、パスからシェル項目を作成します。
    /// </summary>
    /// <param name="path">対象のパス。</param>
    /// <param name="bindingContext">バインド コンテキスト。</param>
    /// <param name="iid">要求するインターフェイスの IID。</param>
    /// <param name="item">作成されたシェル項目。</param>
    /// <returns>HRESULT。</returns>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindingContext,
        [MarshalAs(UnmanagedType.LPStruct)] Guid iid, out IShellItem item);

    [Flags]
    private enum FileOpenOptions : uint
    {
        PickFolders = 0x20,
        ForceFileSystem = 0x40,
        NoChangeDirectory = 0x8,
        PathMustExist = 0x800
    }

    private enum ShellItemDisplayName : uint
    {
        FileSystemPath = 0x80058000
    }

    [ComImport]
    [Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialog
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("42F85136-DB7E-439C-85F1-E4075D135FC8")]
    private interface IFileDialog
    {
        /// <summary>
        ///     ダイアログをモーダル表示します。
        /// </summary>
        /// <param name="parent">オーナー ウィンドウのハンドル。</param>
        /// <returns>HRESULT。キャンセル時は ERROR_CANCELLED。</returns>
        [PreserveSig]
        int Show(IntPtr parent);

        /// <summary>
        ///     ファイルの種類フィルターを設定します。
        /// </summary>
        /// <param name="count">フィルターの数。</param>
        /// <param name="filterSpec">フィルター定義の配列。</param>
        void SetFileTypes(uint count, IntPtr filterSpec);

        /// <summary>
        ///     選択するファイルの種類フィルターを設定します。
        /// </summary>
        /// <param name="index">フィルターのインデックス。</param>
        void SetFileTypeIndex(uint index);

        /// <summary>
        ///     選択中のファイルの種類フィルターを取得します。
        /// </summary>
        /// <param name="index">フィルターのインデックス。</param>
        void GetFileTypeIndex(out uint index);

        /// <summary>
        ///     ダイアログのイベント ハンドラーを登録します。
        /// </summary>
        /// <param name="events">イベント ハンドラー。</param>
        /// <param name="cookie">登録を識別する値。</param>
        void Advise(IntPtr events, out uint cookie);

        /// <summary>
        ///     登録したイベント ハンドラーを解除します。
        /// </summary>
        /// <param name="cookie">登録を識別する値。</param>
        void Unadvise(uint cookie);

        /// <summary>
        ///     ダイアログのオプションを設定します。
        /// </summary>
        /// <param name="options">設定するオプション。</param>
        void SetOptions(FileOpenOptions options);

        /// <summary>
        ///     ダイアログの現在のオプションを取得します。
        /// </summary>
        /// <param name="options">現在のオプション。</param>
        void GetOptions(out FileOpenOptions options);

        /// <summary>
        ///     既定のフォルダーを設定します。
        /// </summary>
        /// <param name="item">既定のフォルダー。</param>
        void SetDefaultFolder(IShellItem item);

        /// <summary>
        ///     最初に表示するフォルダーを設定します。
        /// </summary>
        /// <param name="item">表示するフォルダー。</param>
        void SetFolder(IShellItem item);

        /// <summary>
        ///     現在のフォルダーを取得します。
        /// </summary>
        /// <param name="item">現在のフォルダー。</param>
        void GetFolder(out IShellItem item);

        /// <summary>
        ///     現在の選択項目を取得します。
        /// </summary>
        /// <param name="item">選択項目。</param>
        void GetCurrentSelection(out IShellItem item);

        /// <summary>
        ///     ファイル名欄の文字列を設定します。
        /// </summary>
        /// <param name="name">ファイル名。</param>
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);

        /// <summary>
        ///     ファイル名欄の文字列を取得します。
        /// </summary>
        /// <param name="name">ファイル名。</param>
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);

        /// <summary>
        ///     ダイアログのタイトルを設定します。
        /// </summary>
        /// <param name="title">タイトル。</param>
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);

        /// <summary>
        ///     OK ボタンの表示文字列を設定します。
        /// </summary>
        /// <param name="text">表示文字列。</param>
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);

        /// <summary>
        ///     ファイル名欄のラベルを設定します。
        /// </summary>
        /// <param name="label">ラベル。</param>
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);

        /// <summary>
        ///     選択結果を取得します。
        /// </summary>
        /// <param name="item">選択された項目。</param>
        void GetResult(out IShellItem item);

        /// <summary>
        ///     ナビゲーション ウィンドウに場所を追加します。
        /// </summary>
        /// <param name="item">追加する場所。</param>
        /// <param name="alignment">追加位置。</param>
        void AddPlace(IShellItem item, int alignment);

        /// <summary>
        ///     既定の拡張子を設定します。
        /// </summary>
        /// <param name="extension">拡張子。</param>
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);

        /// <summary>
        ///     ダイアログを閉じます。
        /// </summary>
        /// <param name="error">Show が返す HRESULT。</param>
        void Close(int error);

        /// <summary>
        ///     ダイアログの状態を保存するための GUID を設定します。
        /// </summary>
        /// <param name="guid">クライアント GUID。</param>
        void SetClientGuid(ref Guid guid);

        /// <summary>
        ///     保存されたダイアログの状態を消去します。
        /// </summary>
        void ClearClientData();

        /// <summary>
        ///     表示する項目のフィルターを設定します。
        /// </summary>
        /// <param name="filter">フィルター。</param>
        void SetFilter(IntPtr filter);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    private interface IShellItem
    {
        /// <summary>
        ///     シェル項目を指定したハンドラーにバインドします。
        /// </summary>
        /// <param name="context">バインド コンテキスト。</param>
        /// <param name="handler">ハンドラーの GUID。</param>
        /// <param name="iid">要求するインターフェイスの IID。</param>
        /// <param name="pointer">取得したインターフェイス。</param>
        void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr pointer);

        /// <summary>
        ///     親のシェル項目を取得します。
        /// </summary>
        /// <param name="parent">親項目。</param>
        void GetParent(out IShellItem parent);

        /// <summary>
        ///     シェル項目の表示名またはパスを取得します。
        /// </summary>
        /// <param name="displayName">取得する名前の種類。</param>
        /// <param name="name">名前の文字列へのポインター。CoTaskMemFree で解放する必要があります。</param>
        void GetDisplayName(ShellItemDisplayName displayName, out IntPtr name);

        /// <summary>
        ///     シェル項目の属性を取得します。
        /// </summary>
        /// <param name="mask">取得する属性のマスク。</param>
        /// <param name="attributes">取得した属性。</param>
        void GetAttributes(uint mask, out uint attributes);

        /// <summary>
        ///     2 つのシェル項目を比較します。
        /// </summary>
        /// <param name="item">比較する項目。</param>
        /// <param name="hint">比較方法。</param>
        /// <param name="order">比較結果。</param>
        void Compare(IShellItem item, uint hint, out int order);
    }
}