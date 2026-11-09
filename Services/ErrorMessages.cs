using DesktopIniManager.Properties;
using System;
using System.IO;

namespace DesktopIniManager.Services;

internal static class ErrorMessages
{
    private const string LocalizedMark = "DesktopIniManager.LocalizedMessage";

    /// <summary>
    ///     例外のメッセージが言語リソースから取得した利用者向けの文言であることを記録します。
    /// </summary>
    /// <typeparam name="T">例外の型。</typeparam>
    /// <param name="error">記録する例外。</param>
    /// <returns>引数と同じ例外。</returns>
    internal static T Localized<T>(this T error) where T : Exception
    {
        error.Data[LocalizedMark] = true;
        return error;
    }

    /// <summary>
    ///     例外の種類に応じて、利用者向けのエラー メッセージを作成します。
    /// </summary>
    /// <param name="error">変換する例外。</param>
    /// <returns>表示用のエラー メッセージ。</returns>
    internal static string English(Exception error)
    {
        var missing = error as FileNotFoundException;
        if (missing != null)
            return StringOverlay.Get("Err_FileNotFound") + (string.IsNullOrEmpty(missing.FileName)
                ? ""
                : "\n" + string.Format(StringOverlay.Get("Err_FileLabel"), missing.FileName));
        if (error is DirectoryNotFoundException) return StringOverlay.Get("Err_DirectoryNotFound");
        if (error is DriveNotFoundException) return StringOverlay.Get("Err_DriveNotFound");
        if (error is UnauthorizedAccessException) return StringOverlay.Get("Err_AccessDenied");
        if (error is PathTooLongException) return StringOverlay.Get("Err_PathTooLong");
        var code = error.HResult & 0xffff;
        if (error is IOException && (code == 32 || code == 33)) return StringOverlay.Get("Err_FileLocked");
        // 言語リソースの文言は、先頭の文字にかかわらずそのまま表示する。
        if (error.Data.Contains(LocalizedMark)) return error.Message;
        // Preserve our English diagnostics, including paths that contain Japanese characters.
        var message = error.Message;
        if (!string.IsNullOrEmpty(message) &&
            ((message[0] >= 'A' && message[0] <= 'Z') || (message[0] >= 'a' && message[0] <= 'z')))
            return message;
        return string.Format(StringOverlay.Get("Err_Generic"), error.HResult.ToString("X8"), error.GetType().Name);
    }
}