using System;
using System.IO;
using System.Linq;

namespace DesktopIniManager.ViewModels;

internal sealed partial class DiffViewModel
{
    private bool _designerHeaderExpanded;

    internal int DesignerHeaderRowCount
    {
        get
        {
            if (IsPatch || _allLines == null || !string.Equals(Path.GetExtension(File.RelativePath), ".frm", StringComparison.OrdinalIgnoreCase)) return 0;
            static int Start(string[] lines) => Array.FindIndex(lines, line =>
                string.Equals(line.Trim(), "Option Explicit", StringComparison.OrdinalIgnoreCase)) + 1;
            var leftStart = Start(SourceText.Lines);
            var rightStart = Start(TargetText.Lines);
            if (leftStart == 0 && SourceText.Lines.Length > 0 || rightStart == 0 && TargetText.Lines.Length > 0) return 0;
            return _allLines.TakeWhile(row => (row.LeftNumber == 0 || row.LeftNumber < leftStart) &&
                                              (row.RightNumber == 0 || row.RightNumber < rightStart)).Count();
        }
    }

    internal void ToggleDesignerHeader()
    {
        _designerHeaderExpanded = !_designerHeaderExpanded;
        UpdateDisplayedLines();
    }
}
