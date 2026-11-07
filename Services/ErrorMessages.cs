using DesktopIniManager.Properties;
using System;
using System.IO;

namespace DesktopIniManager.Services;

internal static class ErrorMessages
{
    /// <summary>
    ///     例外の種類に応じて、利用者向けのエラー メッセージを作成します。
    /// </summary>
    /// <param name="error">変換する例外。</param>
    /// <returns>表示用のエラー メッセージ。</returns>
    internal static string English(Exception error)
    {
        var missing = error as FileNotFoundException;
        if (missing != null)
            return Strings.Err_FileNotFound + (string.IsNullOrEmpty(missing.FileName)
                ? ""
                : "\n" + string.Format(Strings.Err_FileLabel, missing.FileName));
        if (error is DirectoryNotFoundException) return Strings.Err_DirectoryNotFound;
        if (error is DriveNotFoundException) return Strings.Err_DriveNotFound;
        if (error is UnauthorizedAccessException) return Strings.Err_AccessDenied;
        if (error is PathTooLongException) return Strings.Err_PathTooLong;
        var code = error.HResult & 0xffff;
        if (error is IOException && (code == 32 || code == 33)) return Strings.Err_FileLocked;
        // Preserve our English diagnostics, including paths that contain Japanese characters.
        var message = error.Message;
        if (!string.IsNullOrEmpty(message) &&
            ((message[0] >= 'A' && message[0] <= 'Z') || (message[0] >= 'a' && message[0] <= 'z')))
            return message;
        return string.Format(Strings.Err_Generic, error.HResult.ToString("X8"), error.GetType().Name);
    }
}