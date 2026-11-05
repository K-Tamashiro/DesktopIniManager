using DesktopIniManager.Properties;
using DesktopIniManager.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DesktopIniManager.ViewModels;

internal sealed partial class DiffViewModel
{
    private DiffEditSession _edits;
    private TextDocument _originalSource, _originalTarget;
    internal Func<Task<bool>> ConfirmChangesRequested { get; set; }
    internal bool IsText => !IsImage && !IsVideo && !IsAudio;
    internal bool HasEdits => IsPatch ? HasPatchEdits : IsDirty(true) || IsDirty(false);

    internal bool IsDirty(bool source)
    {
        if (IsPatch) return !source && CurrentPatch?.IsDirty == true;
        return IsText && (source ? SourceText : TargetText) is { } current &&
               (source ? _originalSource : _originalTarget) is { } original && Text(current) != Text(original);
    }

    internal bool CanUndoHunk(int id, bool fromSource)
    {
        return IsPatch ? fromSource && CurrentPatch.IsRunApplied(id) : _edits?.CanUndo(id, fromSource) == true;
    }

    internal void CopyHunks(IEnumerable<int> ids, bool fromSource)
    {
        if (IsPatch)
        {
            if (!CanChangeComparison || !fromSource) return;
            CurrentPatch.CopyRuns(ids);
            RefreshPatchRows();
            return;
        }
        if (!CanChangeComparison || !IsText || _edits == null) return;
        _edits.Copy(ids, fromSource);
        ApplyEdits();
    }

    internal void UndoHunk(int id, bool fromSource)
    {
        if (IsPatch)
        {
            if (CanChangeComparison && fromSource) { CurrentPatch.UndoRun(id); RefreshPatchRows(); }
            return;
        }
        if (!CanChangeComparison || _edits == null) return;
        _edits.UndoHunk(id, fromSource);
        ApplyEdits();
    }

    internal void UndoEdit()
    {
        if (IsPatch)
        {
            if (CanChangeComparison) { CurrentPatch.Undo(); RefreshPatchRows(); }
            return;
        }
        if (!CanChangeComparison || _edits == null) return;
        _edits.UndoLast();
        ApplyEdits();
    }

    private void ApplyEdits()
    {
        var expandedRows = _expandedEqualBlocks.Select(index => _allLines[index]).ToHashSet();
        SourceText = _edits.Document(true);
        TargetText = _edits.Document(false);
        _allLines = _edits.Rows();
        _expandedEqualBlocks.Clear();
        for (var i = 0; i < _allLines.Count; i++)
            if (expandedRows.Contains(_allLines[i]))
                _expandedEqualBlocks.Add(i);
        UpdateDisplayedLines();
    }

    internal static string Text(TextDocument document)
    {
        return string.Concat(document.Lines.Select((s, i) => s + document.Endings[i]));
    }

    // Encode the complete buffer before opening any destination for writing.
    // Exception fallback also catches supplementary Unicode characters.
    internal static byte[] EncodeDocument(TextDocument document)
    {
        var encoding = (Encoding)document.Encoding.Clone();
        encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
        encoding.DecoderFallback = DecoderFallback.ExceptionFallback;
        var text = Text(document);
        var bytes = encoding.GetBytes(text);
        // Some legacy encodings perform a best-fit/alias conversion even with
        // exception fallback. Refuse any conversion that changes the text.
        var roundtrip = encoding.GetString(bytes);
        if (roundtrip != text)
        {
            var index = 0;
            while (index < Math.Min(text.Length, roundtrip.Length) && text[index] == roundtrip[index]) index++;
            throw new UnrepresentableTextException(index);
        }

        return (document.HasBom ? encoding.GetPreamble() : Array.Empty<byte>()).Concat(bytes).ToArray();
    }

    internal (int Line, int Column) EncodingErrorPosition(bool source, int index)
    {
        var doc = source ? SourceText : TargetText;
        for (var i = 0; i < doc.Lines.Length; i++)
        {
            var length = doc.Lines[i].Length + doc.Endings[i].Length;
            if (index < length || i == doc.Lines.Length - 1) return (i + 1, Math.Min(index, doc.Lines[i].Length));
            index -= length;
        }

        return (1, 0);
    }

    internal async Task SaveTextAsync(bool source)
    {
        if (IsPatch)
        {
            if (!source && CanChangeComparison) SavePatchTarget();
            return;
        }
        if (!CanChangeComparison || !IsDirty(source)) return;
        var document = source ? SourceText : TargetText;
        var original = source ? _originalSource : _originalTarget;
        var bytes = EncodeDocument(document);
        var path = GetPath(source);
        if (string.IsNullOrEmpty(path)) throw new IOException(StringOverlay.Get("Diff_SaveNoPath"));
        _navigating = true;
        try
        {
            await Task.Run(() => WriteDocument(path, bytes, original.OriginalBytes));
            // Reload only the saved side; the opposite buffer may still be dirty.
            var saved = await Task.Run(() => ReadDocument(path));
            if (source)
            {
                SourceText = _originalSource = saved;
                File.Source = DiffStamp.Read(path);
            }
            else
            {
                TargetText = _originalTarget = saved;
                File.Target = DiffStamp.Read(path);
            }

            await RecompareTextAsync();
            RefreshFileDetails();
            if (!IsDetachedComparison && RefreshFileRequested != null) await RefreshFileRequested(File);
        }
        finally
        {
            _navigating = false;
        }
    }

    internal static void WriteDocument(string path, byte[] bytes, byte[] original)
    {
        var exists = System.IO.File.Exists(path);
        if (exists != (original != null) || (exists && !System.IO.File.ReadAllBytes(path).SequenceEqual(original)))
            throw new IOException(StringOverlay.Get("Diff_SaveExternalChange"));
        var attributes = exists ? System.IO.File.GetAttributes(path) : FileAttributes.Normal;
        if ((attributes & FileAttributes.ReadOnly) != 0)
            throw new UnauthorizedAccessException(StringOverlay.Get("Diff_SaveReadOnly"));
        var temp = path + ".dim-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            System.IO.File.WriteAllBytes(temp, bytes);
            if (exists)
            {
                // Recheck after preparation, and replace atomically while retaining
                // the existing file's security metadata and filesystem attributes.
                if (!System.IO.File.ReadAllBytes(path).SequenceEqual(original))
                    throw new IOException(StringOverlay.Get("Diff_SaveExternalChange"));
                System.IO.File.Replace(temp, path, null);
                System.IO.File.SetAttributes(path, attributes);
            }
            else
            {
                ArgumentNullException.ThrowIfNull(path);
                System.IO.File.Move(temp, path);
            }
        }
        finally
        {
            if (System.IO.File.Exists(temp)) System.IO.File.Delete(temp);
        }
    }

    internal sealed class UnrepresentableTextException(int index) : Exception
    {
        internal int Index { get; } = index;
    }
}
