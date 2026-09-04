using System;
using System.IO;
using DesktopIniManager.Properties;

namespace DesktopIniManager.Services
{
    internal static class ErrorMessages
    {
        internal static string English(Exception error)
        {
            var missing = error as FileNotFoundException;
            if (missing != null)
                return Strings.Err_FileNotFound + (string.IsNullOrEmpty(missing.FileName) ? "" : "\n" + string.Format(Strings.Err_FileLabel, missing.FileName));
            if (error is DirectoryNotFoundException) return Strings.Err_DirectoryNotFound;
            if (error is DriveNotFoundException) return Strings.Err_DriveNotFound;
            if (error is UnauthorizedAccessException) return Strings.Err_AccessDenied;
            if (error is PathTooLongException) return Strings.Err_PathTooLong;
            int code = error.HResult & 0xffff;
            if (error is IOException && (code == 32 || code == 33)) return Strings.Err_FileLocked;
            // Preserve our English diagnostics, including paths that contain Japanese characters.
            string message = error.Message;
            if (!string.IsNullOrEmpty(message) && ((message[0] >= 'A' && message[0] <= 'Z') || (message[0] >= 'a' && message[0] <= 'z')))
                return message;
            return string.Format(Strings.Err_Generic, error.HResult.ToString("X8"), error.GetType().Name);
        }
    }
}
