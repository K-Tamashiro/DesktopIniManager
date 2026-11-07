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

    /// <summary>
    ///     指定側が編集されて未保存かどうかを判定します。
    /// </summary>
    /// <param name="source">比較元の場合は true。</param>
    /// <returns>未保存の変更がある場合は true。</returns>
    internal bool IsDirty(bool source)
    {
        if (IsPatch) return !source && CurrentPatch?.IsDirty == true;
        return IsText && (source ? SourceText : TargetText) is { } current &&
               (source ? _originalSource : _originalTarget) is { } original && Text(current) != Text(original);
    }

    /// <summary>
    ///     指定した HUNK のコピー (パッチの場合は適用) を元に戻せるかどうかを判定します。
    /// </summary>
    /// <param name="id">HUNK の ID。</param>
    /// <param name="fromSource">比較元から比較先へのコピーの場合は true。</param>
    /// <returns>元に戻せる場合は true。</returns>
    internal bool CanUndoHunk(int id, bool fromSource)
    {
        return IsPatch ? fromSource && CurrentPatch.IsRunApplied(id) : _edits?.CanUndo(id, fromSource) == true;
    }

    /// <summary>
    ///     選択した HUNK を反対側へコピーします。パッチ表示では比較元から比較先への適用だけを行います。
    /// </summary>
    /// <param name="ids">コピーする HUNK の ID。</param>
    /// <param name="fromSource">比較元から比較先へコピーする場合は true。</param>
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

    /// <summary>
    ///     指定した HUNK のコピーまたは適用を元に戻します。
    /// </summary>
    /// <param name="id">HUNK の ID。</param>
    /// <param name="fromSource">比較元から比較先へのコピーの場合は true。</param>
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

    /// <summary>
    ///     最後の編集を元に戻します。
    /// </summary>
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

    /// <summary>
    ///     編集セッションから両側の文書と行を作り直し、展開していた変更なしブロックの状態を保って表示を更新します。
    /// </summary>
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

    /// <summary>
    ///     文書の各行と改行コードを連結して、ファイル全体のテキストにします。
    /// </summary>
    /// <param name="document">対象の文書。</param>
    /// <returns>連結したテキスト。</returns>
    internal static string Text(TextDocument document)
    {
        return string.Concat(document.Lines.Select((s, i) => s + document.Endings[i]));
    }

    // Encode the complete buffer before opening any destination for writing.
    // Exception fallback also catches supplementary Unicode characters.
    /// <summary>
    ///     文書を元のエンコーディングでバイト列に変換します。表現できない文字や、往復変換で内容が変わる変換は拒否します。
    /// </summary>
    /// <param name="document">変換する文書。</param>
    /// <returns>BOM を含むバイト列。</returns>
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

    /// <summary>
    ///     テキスト内の文字位置を、行番号と列に変換します。
    /// </summary>
    /// <param name="source">比較元の場合は true。</param>
    /// <param name="index">テキスト全体での文字位置。</param>
    /// <returns>1 から始まる行番号と、0 から始まる列。</returns>
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

    /// <summary>
    ///     編集した側をファイルに保存し、保存した側だけを読み込み直します。パッチ表示では適用先のファイルを保存します。
    /// </summary>
    /// <param name="source">比較元を保存する場合は true。</param>
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

    /// <summary>
    ///     外部で変更されていないことを確認してから、一時ファイル経由でファイルを置き換え、属性とセキュリティ情報を保ちます。読み取り専用ファイルへの保存は拒否します。
    /// </summary>
    /// <param name="path">保存先のパス。</param>
    /// <param name="bytes">書き込むバイト列。</param>
    /// <param name="original">読み込み時の内容。新規ファイルの場合は null。</param>
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
