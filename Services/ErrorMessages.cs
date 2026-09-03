using System;
using System.IO;

namespace DesktopIniManager.Services
{
    internal static class ErrorMessages
    {
        internal static string English(Exception error)
        {
            var missing = error as FileNotFoundException;
            if (missing != null)
                return "The file could not be found." + (string.IsNullOrEmpty(missing.FileName) ? "" : "\nFile: " + missing.FileName);
            if (error is DirectoryNotFoundException) return "The folder or part of the path could not be found. Check Source and Target, then compare again.";
            if (error is DriveNotFoundException) return "The drive could not be found or is unavailable.";
            if (error is UnauthorizedAccessException) return "Access was denied. Check file permissions and read-only attributes.";
            if (error is PathTooLongException) return "The file or folder path is too long.";
            int code = error.HResult & 0xffff;
            if (error is IOException && (code == 32 || code == 33)) return "The file is locked by another process. Close the application using it and try again.";
            // Preserve our English diagnostics, including paths that contain Japanese characters.
            string message = error.Message;
            if (!string.IsNullOrEmpty(message) && ((message[0] >= 'A' && message[0] <= 'Z') || (message[0] >= 'a' && message[0] <= 'z')))
                return message;
            return "The operation could not be completed. Error code: 0x" + error.HResult.ToString("X8") + " (" + error.GetType().Name + ").";
        }
    }
}
