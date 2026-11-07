using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DesktopIniManager.Services;

internal sealed class InputHistoryStore
{
    internal const int Capacity = 20;
    private readonly string _directory;

    /// <summary>
    ///     入力履歴を保存するフォルダーを指定してストアを作成します。
    /// </summary>
    /// <param name="directory">履歴ファイルを保存するフォルダー。</param>
    internal InputHistoryStore(string directory)
    {
        _directory = directory;
    }

    /// <summary>
    ///     履歴キーから履歴ファイルのパスを作成します。キーには英数字、ハイフン、アンダースコアだけを使用できます。
    /// </summary>
    /// <param name="key">履歴キー。</param>
    /// <returns>履歴ファイルのパス。</returns>
    private string FilePath(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_'))
            throw new ArgumentException(nameof(key));
        return Path.Combine(_directory, key + ".txt");
    }

    /// <summary>
    ///     Base64 で保存された履歴を読み込み、重複と空の値を除いて最大件数まで返します。
    /// </summary>
    /// <param name="key">履歴キー。</param>
    /// <returns>新しい順の履歴。</returns>
    internal List<string> Load(string key)
    {
        var result = new List<string>();
        try
        {
            foreach (var line in File.ReadLines(FilePath(key)))
                try
                {
                    var value = Encoding.UTF8.GetString(Convert.FromBase64String(line));
                    if (IsFolderHistoryKey(key)) value = NormalizeFolderPath(value);
                    if (!string.IsNullOrWhiteSpace(value) &&
                        !result.Exists(entry => FolderHistoryEquals(key, entry, value))) result.Add(value);
                    if (result.Count == Capacity) break;
                }
                catch (FormatException)
                {
                }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return result;
    }

    /// <summary>
    ///     値を履歴に追加して保存します。既存の同じ値は取り除きます。
    /// </summary>
    /// <param name="key">履歴キー。</param>
    /// <param name="value">追加する値。</param>
    /// <param name="promote">先頭に移動する場合は true。false の場合は未登録のときだけ末尾に追加します。</param>
    /// <returns>更新後の履歴。</returns>
    internal List<string> Remember(string key, string value, bool promote = true)
    {
        var entries = Load(key);
        // Preserve whitespace and case: both can be significant in searches and arguments.
        if (string.IsNullOrWhiteSpace(value)) return entries;
        if (IsFolderHistoryKey(key)) value = NormalizeFolderPath(value);
        if (!promote && entries.Exists(entry => FolderHistoryEquals(key, entry, value))) return entries;
        entries.RemoveAll(entry => FolderHistoryEquals(key, entry, value));
        if (promote) entries.Insert(0, value);
        else entries.Add(value);
        return Save(key, entries);
    }

    /// <summary>
    ///     指定キーの履歴ファイルを削除します。
    /// </summary>
    /// <param name="key">履歴キー。</param>
    internal void Clear(string key)
    {
        try
        {
            File.Delete(FilePath(key));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    ///     履歴を指定した値の一覧で置き換えて保存します。
    /// </summary>
    /// <param name="key">履歴キー。</param>
    /// <param name="values">新しい履歴の値。</param>
    /// <returns>保存した履歴。</returns>
    internal List<string> Replace(string key, IEnumerable<string> values)
    {
        var entries = new List<string>();
        if (values != null)
            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value) || entries.Contains(value)) continue;
                entries.Add(value);
                if (entries.Count == Capacity) break;
            }

        return Save(key, entries);
    }

    /// <summary>
    ///     指定した値を履歴から削除して保存します。
    /// </summary>
    /// <param name="key">履歴キー。</param>
    /// <param name="value">削除する値。</param>
    /// <returns>更新後の履歴。</returns>
    internal List<string> Remove(string key, string value)
    {
        var entries = Load(key);
        if (string.IsNullOrWhiteSpace(value)) return entries;
        entries.RemoveAll(entry => FolderHistoryEquals(key, entry, value));
        return Save(key, entries);
    }

    /// <summary>
    ///     履歴を最大件数に切り詰め、各値を Base64 で 1 行ずつ保存します。
    /// </summary>
    /// <param name="key">履歴キー。</param>
    /// <param name="entries">保存する履歴。</param>
    /// <returns>保存した履歴。</returns>
    private List<string> Save(string key, List<string> entries)
    {
        if (entries.Count > Capacity) entries.RemoveRange(Capacity, entries.Count - Capacity);
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllLines(FilePath(key), entries.Select(s => Convert.ToBase64String(Encoding.UTF8.GetBytes(s))),
                new UTF8Encoding(false));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return entries;
    }

    /// <summary>
    ///     キーが比較元・比較先フォルダーの履歴かどうかを判定します。
    /// </summary>
    /// <param name="key">履歴キー。</param>
    /// <returns>フォルダー履歴の場合は true。</returns>
    private static bool IsFolderHistoryKey(string key)
    {
        return string.Equals(key, "Differencer-Source", StringComparison.OrdinalIgnoreCase)
               || string.Equals(key, "Differencer-Target", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     フォルダー パス末尾の区切り文字を取り除きます。ドライブ ルートは「C:\」の形にそろえます。
    /// </summary>
    /// <param name="path">正規化するパス。</param>
    /// <returns>正規化したパス。</returns>
    private static string NormalizeFolderPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path ?? "";
        var trimmed = path.TrimEnd('\\', '/');
        if (trimmed.Length == 2 && trimmed[1] == ':') return trimmed + "\\";
        return trimmed;
    }

    /// <summary>
    ///     履歴の 2 つの値が同じかどうかを判定します。フォルダー履歴ではパスを正規化し、大文字と小文字を区別せずに比較します。
    /// </summary>
    /// <param name="key">履歴キー。</param>
    /// <param name="left">比較する値。</param>
    /// <param name="right">比較する値。</param>
    /// <returns>同じ値とみなす場合は true。</returns>
    private static bool FolderHistoryEquals(string key, string left, string right)
    {
        if (!IsFolderHistoryKey(key)) return left == right;
        return string.Equals(NormalizeFolderPath(left), NormalizeFolderPath(right), StringComparison.OrdinalIgnoreCase);
    }
}