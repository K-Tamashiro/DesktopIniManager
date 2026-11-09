using DesktopIniManager.Models;
using DesktopIniManager.Properties;
using DesktopIniManager.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;

namespace DesktopIniManager.ViewModels;

internal sealed partial class GrepWindowViewModel : ObservableObject
{
    private static readonly EditorPreset[] EditorPresets =
    {
        new(@"C:\Program Files\MIFES11\MIW.exe", @"/+{line}@{column} ""{file}"""),
        new(@"C:\Program Files\Hidemaru\Hidemaru.exe", @"/j{line},{column} ""{file}"""),
        new(@"%LOCALAPPDATA%\Programs\Mery\Mery.exe", @"/l {line} /cl {column} ""{file}"""),
        new("code", @"--goto ""{file}:{line}:{column}""")
    };

    private readonly IUserDialogService _dialogs;

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _resultTimer;
    private readonly Func<IReadOnlyList<string>> _scopeProvider;
    private bool _allScopesSelected = true;
    private string _editorArguments = string.Empty;
    private string _editorPath = string.Empty;
    private string _extensions = string.Empty;
    private bool _isCancelling;
    private bool _isSearching;
    private string _listFilter = string.Empty;
    private bool? _matchCase = false;
    private ConcurrentQueue<GrepMatch> _pendingMatches = new();
    private string _query = string.Empty;
    private string _scopeCountLabel = string.Empty;
    private CancellationTokenSource _searchCts;
    private GrepMatch _selectedMatch;
    private LanguageProfile _selectedProfile;
    private string _status = StringOverlay.Get("Common_Ready");
    private bool? _useRegex = false;
    private bool? _wholeWord = false;

    /// <summary>
    ///     Grep ウィンドウの ViewModel を作成し、コマンドを初期化します。
    /// </summary>
    /// <param name="scopeProvider">メイン画面でチェックされたフォルダーを取得する処理。</param>
    /// <param name="dispatcher">UI スレッドのディスパッチャー。</param>
    /// <param name="dialogs">メッセージ表示に使うダイアログ サービス。</param>
    internal GrepWindowViewModel(Func<IReadOnlyList<string>> scopeProvider, Dispatcher dispatcher,
        IUserDialogService dialogs)
    {
        _scopeProvider = scopeProvider;
        _dispatcher = dispatcher;
        _dialogs = dialogs;
        SearchCommand = new AsyncRelayCommand(SearchAsync, ex => _dialogs.Show(ErrorMessages.English(ex), DialogTitle),
            () => !IsSearching);
        CancelCommand = new RelayCommand(Cancel, () => IsSearching && !IsCancelling);
        ClearQueryCommand = new RelayCommand(() => Query = string.Empty, () => !string.IsNullOrEmpty(Query));
        ClearListFilterCommand =
            new RelayCommand(() => ListFilter = string.Empty, () => !string.IsNullOrEmpty(ListFilter));
        ClearResultsCommand = new RelayCommand(ClearResults, () => !IsSearching && Matches.Count > 0);
        ReloadScopesCommand = new RelayCommand(ReloadFromMainWindow, () => !IsSearching);
        OpenMatchCommand = new RelayCommand(OpenMatch);
        BrowseEditorCommand = new RelayCommand(() => BrowseEditorRequested?.Invoke());
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke());
        ExpandResultGroupsCommand = new RelayCommand(() => ResultGroupsExpansionRequested?.Invoke(true));
        CollapseResultGroupsCommand = new RelayCommand(() => ResultGroupsExpansionRequested?.Invoke(false));
        OpenResultsInEditorCommand = new RelayCommand(OpenResultsInEditor, () => VisibleMatchCount() > 0);
        SaveResultsCommand = new RelayCommand(() => SaveResultsRequested?.Invoke(), () => VisibleMatchCount() > 0);
        ApplyEditorPresetCommand = new RelayCommand(ApplyEditorPreset);
        Results = CollectionViewSource.GetDefaultView(Matches);
        Results.Filter = MatchListFilter;
        Results.GroupDescriptions.Add(new PropertyGroupDescription(nameof(GrepMatch.GroupPath)));
        _resultTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background,
            (_, _) => DrainPendingMatches(LiveDrainBudget), _dispatcher);
        _resultTimer.Stop();
        InitializeHistoryCommands();
    }

    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value))
                ClearQueryCommand?.NotifyCanExecuteChanged();
        }
    }

    public string Extensions
    {
        get => _extensions;
        set => SetProperty(ref _extensions, value);
    }

    public string EditorPath
    {
        get => _editorPath;
        set
        {
            if (SetProperty(ref _editorPath, value)) ApplyEditorPreset();
        }
    }

    public string EditorArguments
    {
        get => _editorArguments;
        set => SetProperty(ref _editorArguments, value);
    }

    public bool? UseRegex
    {
        get => _useRegex;
        set => SetProperty(ref _useRegex, value);
    }

    public bool? MatchCase
    {
        get => _matchCase;
        set => SetProperty(ref _matchCase, value);
    }

    public bool? WholeWord
    {
        get => _wholeWord;
        set => SetProperty(ref _wholeWord, value);
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public string ScopeCountLabel
    {
        get => _scopeCountLabel;
        set => SetProperty(ref _scopeCountLabel, value);
    }

    public bool AllScopesSelected
    {
        get => _allScopesSelected;
        set
        {
            if (!SetProperty(ref _allScopesSelected, value)) return;
            foreach (var item in Scopes)
                item.IsEnabled = value;
        }
    }

    public string ListFilter
    {
        get => _listFilter;
        set
        {
            if (SetProperty(ref _listFilter, value))
            {
                Results?.Refresh();
                ClearListFilterCommand?.NotifyCanExecuteChanged();
            }
        }
    }

    public GrepMatch SelectedMatch
    {
        get => _selectedMatch;
        set => SetProperty(ref _selectedMatch, value);
    }

    public bool IsSearching
    {
        get => _isSearching;
        private set => SetProperty(ref _isSearching, value);
    }

    public bool IsCancelling
    {
        get => _isCancelling;
        private set => SetProperty(ref _isCancelling, value);
    }

    internal string DialogTitle { get; set; }
    public ObservableCollection<GrepScopeItem> Scopes { get; } = new();

    public ObservableCollection<GrepMatch> Matches { get; } = new();

    public IEnumerable<LanguageProfile> Profiles => LanguageProfile.All;
    public bool CanEdit => !IsSearching;

    public LanguageProfile SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (ReferenceEquals(_selectedProfile, value)) return;
            SearchHistoryRequested?.Invoke();
            if (_selectedProfile?.IsFree == true) SettingsService.SaveGrepFreeExtensions(Extensions.Trim());
            SetProperty(ref _selectedProfile, value);
            Extensions = value?.IsFree == true
                ? SettingsService.LoadGrepFreeExtensions()
                : value?.ExtensionText ?? string.Empty;
        }
    }

    public AsyncRelayCommand SearchCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ClearQueryCommand { get; }
    public RelayCommand ClearListFilterCommand { get; }
    public RelayCommand ClearResultsCommand { get; }
    public RelayCommand ReloadScopesCommand { get; }
    public RelayCommand OpenMatchCommand { get; }
    public RelayCommand BrowseEditorCommand { get; }
    public RelayCommand CloseCommand { get; }
    public RelayCommand ExpandResultGroupsCommand { get; }
    public RelayCommand CollapseResultGroupsCommand { get; }
    public RelayCommand OpenResultsInEditorCommand { get; }
    public RelayCommand SaveResultsCommand { get; }
    public RelayCommand ApplyEditorPresetCommand { get; }
    public ICollectionView Results { get; }
    public double[] ColumnWidths { get; private set; }

    public EditorPreset FirstEditorPreset => EditorPresets[0];

    public event Action SaveResultsRequested;

    public event Action BrowseEditorRequested;

    public event Action CloseRequested;

    public event Action<bool> ResultGroupsExpansionRequested;

    public event Action SearchHistoryRequested;

    public event Action ResultGroupsResetRequested;

    public event Action<GrepMatch> MatchScrollRequested;

    /// <summary>
    ///     保存された言語プロファイル、列幅、エディター設定、検索履歴を読み込み、初期の検索範囲を設定します。
    /// </summary>
    /// <param name="initialScopes">初期の検索範囲。</param>
    internal void Initialize(IReadOnlyList<string> initialScopes)
    {
        var savedProfile = SettingsService.LoadGrepProfile();
        SelectedProfile = LanguageProfile.All.FirstOrDefault(profile =>
                              string.Equals(profile.Name, savedProfile, StringComparison.OrdinalIgnoreCase))
                          ?? LanguageProfile.All.First(profile => !profile.IsFree);
        ColumnWidths = SettingsService.LoadGrepColumnWidths();
        if (SeedEditorPresets())
        {
            EditorPath = FirstEditorPreset.Executable;
            EditorArguments = FirstEditorPreset.Arguments;
            SettingsService.SaveEditor(EditorPath, EditorArguments);
        }
        else
        {
            EditorPath = SettingsService.LoadEditorPath();
            EditorArguments = SettingsService.LoadEditorArguments();
        }

        RestoreHistory();
        SetExplicitScopes(initialScopes ?? _scopeProvider?.Invoke() ?? Array.Empty<string>());
    }

    /// <summary>
    ///     結果一覧の列幅が有効な場合に保存します。
    /// </summary>
    /// <param name="widths">列幅。</param>
    internal void SaveColumnWidths(double[] widths)
    {
        if (widths.Length >= 4 && widths[0] >= 80 && widths[1] >= 120)
            SettingsService.SaveGrepColumnWidths(widths);
    }

    /// <summary>
    ///     選択したエディターに対応するプリセットの引数を適用します。
    /// </summary>
    private void ApplyEditorPreset()
    {
        string arguments;
        if (TryApplyEditorPreset(EditorPath, out arguments)) EditorArguments = arguments;
    }

    /// <summary>
    ///     履歴を保存して検索を取り消し、エディターとプロファイルの設定を保存します。
    /// </summary>
    /// <returns>閉じてよい場合は true。履歴を保存できなかった場合は false。</returns>
    internal bool Close()
    {
        if (!SaveHistory()) return false;
        Cancel();
        _resultTimer.Stop();
        SettingsService.SaveEditor(EditorPath.Trim(), EditorArguments);
        SettingsService.SaveGrepProfile(SelectedProfile?.Name);
        if (SelectedProfile?.IsFree == true) SettingsService.SaveGrepFreeExtensions(Extensions.Trim());
        return true;
    }

    /// <summary>
    ///     検索範囲を指定したフォルダーに置き換えます。検索中は変更できません。
    /// </summary>
    /// <param name="scopes">検索範囲のフォルダー。</param>
    public void SetExplicitScopes(IReadOnlyList<string> scopes)
    {
        if (_searchCts != null)
        {
            Status = StringOverlay.Get("Grep_CancelBeforeChange");
            return;
        }

        SetScopes(scopes, false);
    }

    /// <summary>
    ///     ドロップされたフォルダーを検索範囲に追加します。検索中は変更できません。
    /// </summary>
    /// <param name="folders">追加するフォルダー。</param>
    public void AddDroppedFolders(IReadOnlyList<string> folders)
    {
        if (_searchCts != null)
        {
            Status = StringOverlay.Get("Grep_CancelBeforeChange");
            return;
        }

        if (folders == null || folders.Count == 0) return;
        SetScopes(Scopes.Select(item => item.FolderPath).Concat(folders));
    }

    /// <summary>
    ///     メイン画面でチェックされているフォルダーを検索範囲として読み込み直します。検索中は変更できません。
    /// </summary>
    public void ReloadFromMainWindow()
    {
        if (_searchCts != null)
        {
            Status = StringOverlay.Get("Grep_CancelBeforeChange");
            return;
        }

        SetScopes(_scopeProvider == null ? Array.Empty<string>() : _scopeProvider(), false);
    }

    /// <summary>
    ///     検索範囲を正規化して設定し直します。入れ子のフォルダーは親にまとめます。
    /// </summary>
    /// <param name="paths">検索範囲のフォルダー。</param>
    /// <param name="keepEnabledState">既存のフォルダーの有効・無効を引き継ぐ場合は true。</param>
    private void SetScopes(IEnumerable<string> paths, bool keepEnabledState = true)
    {
        var previous = keepEnabledState
            ? Scopes.ToDictionary(item => item.FolderPath, item => item.IsEnabled, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Scopes)
            item.PropertyChanged -= ScopeEnabledChanged;
        var normalized = NormalizeScopes(paths).ToArray();
        Scopes.Clear();
        foreach (var path in normalized)
        {
            var enabled = keepEnabledState
                ? !previous.TryGetValue(path, out var was) || was
                : true;
            var item = new GrepScopeItem(path, enabled);
            item.PropertyChanged += ScopeEnabledChanged;
            Scopes.Add(item);
        }

        UpdateScopeCountLabel();
        Status = EnabledScopePaths().Length == 0 ? StringOverlay.Get("Grep_NoFoldersSelected") : StringOverlay.Get("Common_Ready");
    }

    /// <summary>
    ///     検索範囲の有効・無効が変わったときに、件数表示とステータスを更新します。
    /// </summary>
    /// <param name="sender">変更された検索範囲。</param>
    /// <param name="e">イベントデータ。</param>
    private void ScopeEnabledChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GrepScopeItem.IsEnabled)) return;
        UpdateScopeCountLabel();
        if (!IsSearching)
            Status = EnabledScopePaths().Length == 0 ? StringOverlay.Get("Grep_NoFoldersSelected") : StringOverlay.Get("Common_Ready");
    }

    /// <summary>
    ///     有効な検索範囲の数と総数を表示する文字列を更新します。
    /// </summary>
    private void UpdateScopeCountLabel()
    {
        var enabled = Scopes.Count(item => item.IsEnabled);
        var total = Scopes.Count;
        var format = total == 1 ? StringOverlay.Get("Grep_FolderSingular") : StringOverlay.Get("Grep_FolderPlural");
        ScopeCountLabel = enabled == total
            ? string.Format(format, total)
            : string.Format(format, enabled) + " / " + total;
    }

    /// <summary>
    ///     有効な検索範囲のフォルダーを取得します。
    /// </summary>
    /// <returns>フォルダー パスの配列。</returns>
    private string[] EnabledScopePaths()
    {
        return Scopes.Where(item => item.IsEnabled).Select(item => item.FolderPath).ToArray();
    }

    /// <summary>
    ///     検索結果をクリアします。
    /// </summary>
    private void ClearResults()
    {
        ResultGroupsResetRequested?.Invoke();
        Matches.Clear();
        NotifyListCommands();
        Status = StringOverlay.Get("Common_Ready");
    }

    /// <summary>
    ///     結果が絞り込み文字列に一致するかどうかを、大文字小文字・全角半角などを区別せずに判定します。
    /// </summary>
    /// <param name="item">判定する結果。</param>
    /// <returns>表示する場合は true。</returns>
    private bool MatchListFilter(object item)
    {
        var match = item as GrepMatch;
        if (match == null) return false;
        var filter = ListFilter ?? string.Empty;
        if (string.IsNullOrWhiteSpace(filter)) return true;
        var haystack = string.Join(" ", match.ScopeName, match.FileName, match.RelativePath, match.FilePath,
            match.GroupPath, match.LineText);
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        const CompareOptions options =
            CompareOptions.IgnoreCase
            | CompareOptions.IgnoreWidth
            | CompareOptions.IgnoreKanaType
            | CompareOptions.IgnoreNonSpace;
        return filter.Split(new[] { ' ', '\t', '\u3000' }, StringSplitOptions.RemoveEmptyEntries)
            .All(token => compare.IndexOf(haystack, token, options) >= 0);
    }

    /// <summary>
    ///     絞り込み後に表示されている結果を列挙します。
    /// </summary>
    /// <returns>表示中の結果。</returns>
    internal IEnumerable<GrepMatch> VisibleMatches()
    {
        return Results.Cast<GrepMatch>();
    }

    /// <summary>
    ///     絞り込み後に表示されている結果の件数を取得します。
    /// </summary>
    /// <returns>表示中の件数。</returns>
    private int VisibleMatchCount()
    {
        return Results == null ? 0 : VisibleMatches().Count();
    }

    /// <summary>
    ///     表示中の結果を、ファイルごとにまとめたテキストに整形します。
    /// </summary>
    /// <returns>整形したテキスト。</returns>
    internal string FormatVisibleResults()
    {
        var builder = new StringBuilder();
        string currentGroup = null;
        foreach (var match in VisibleMatches())
        {
            if (!string.Equals(currentGroup, match.GroupPath, StringComparison.OrdinalIgnoreCase))
            {
                if (builder.Length > 0) builder.AppendLine();
                builder.AppendLine(match.FilePath);
                currentGroup = match.GroupPath;
            }

            builder.Append("  ").Append(match.LineNumber).Append(':').Append(match.ColumnNumber)
                .Append("  ").Append(match.LineText).AppendLine();
        }

        return builder.ToString();
    }

    /// <summary>
    ///     表示中の結果をクリップボードにコピーし、一時ファイルに書き出してエディターで開きます。
    /// </summary>
    private void OpenResultsInEditor()
    {
        var text = FormatVisibleResults();
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            // Ignore clipboard errors
        }

        try
        {
            var path = Path.Combine(Path.GetTempPath(), "DesktopIniManager-" + AppSlot.Name + "-grep-results.txt");
            File.WriteAllText(path, text);
            var arguments = (EditorArguments ?? string.Empty)
                .Replace("{file}", path).Replace("{line}", "1").Replace("{column}", "1");
            if (string.IsNullOrWhiteSpace(arguments)) arguments = "\"" + path + "\"";
            Process.Start(new ProcessStartInfo(ExpandEditorPath(EditorPath), arguments) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _dialogs.Show(string.Format(StringOverlay.Get("Grep_EditorFailed"), ErrorMessages.English(ex)), DialogTitle,
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    ///     表示中の結果をテキスト ファイルに保存します。
    /// </summary>
    /// <param name="path">保存先のパス。</param>
    internal void SaveResultsTo(string path)
    {
        File.WriteAllText(path, FormatVisibleResults());
    }

    /// <summary>
    ///     指定したファイル グループの結果を一覧から削除します。
    /// </summary>
    /// <param name="groupPath">グループのパス。</param>
    internal void RemoveGroup(string groupPath)
    {
        if (string.IsNullOrEmpty(groupPath)) return;
        for (var index = Matches.Count - 1; index >= 0; index--)
            if (string.Equals(Matches[index].GroupPath, groupPath, StringComparison.OrdinalIgnoreCase))
                Matches.RemoveAt(index);
        NotifyListCommands();
    }

    /// <summary>
    ///     ファイル グループに対応するファイル パスを取得します。
    /// </summary>
    /// <param name="groupPath">グループのパス。</param>
    /// <returns>ファイル パス。見つからない場合は null。</returns>
    internal string FilePathForGroup(string groupPath)
    {
        return Matches
            .FirstOrDefault(match => string.Equals(match.GroupPath, groupPath, StringComparison.OrdinalIgnoreCase))
            ?.FilePath;
    }

    /// <summary>
    ///     ファイル グループのファイル パスをクリップボードにコピーします。
    /// </summary>
    /// <param name="groupPath">グループのパス。</param>
    internal void CopyGroupPath(string groupPath)
    {
        var path = FilePathForGroup(groupPath);
        if (string.IsNullOrEmpty(path)) return;
        Clipboard.SetText(path);
    }

    /// <summary>
    ///     ファイル グループのファイルがあるフォルダーを Explorer で開きます。
    /// </summary>
    /// <param name="groupPath">グループのパス。</param>
    internal void OpenGroupFolder(string groupPath)
    {
        var path = FilePathForGroup(groupPath);
        if (string.IsNullOrEmpty(path)) return;
        var folder = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
    }

    /// <summary>
    ///     結果一覧と履歴タブに関係するコマンドの実行可否の変更を通知します。
    /// </summary>
    private void NotifyListCommands()
    {
        ClearResultsCommand?.NotifyCanExecuteChanged();
        OpenResultsInEditorCommand?.NotifyCanExecuteChanged();
        SaveResultsCommand?.NotifyCanExecuteChanged();
        SaveHistoryTabCommand?.NotifyCanExecuteChanged();
        DeleteAllHistoryTabsCommand?.NotifyCanExecuteChanged();
        DeleteOtherHistoryTabsCommand?.NotifyCanExecuteChanged();
    }

    /// <summary>
    ///     存在するフォルダーだけを正規化して重複を除き、入れ子のフォルダーを取り除きます。
    /// </summary>
    /// <param name="paths">検索範囲のフォルダー。</param>
    /// <returns>正規化した検索範囲。</returns>
    private static IEnumerable<string> NormalizeScopes(IEnumerable<string> paths)
    {
        var selected = (paths ?? Enumerable.Empty<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Select(MainWindowViewModel.NormalizeFolderPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return MainWindowViewModel.ExcludeNestedFolders(selected);
    }

    /// <summary>
    ///     検索条件を検証して結果を追記するタブを決め、コードまたはドキュメントの Grep をバックグラウンドで実行して、結果を順次表示します。
    /// </summary>
    internal async Task SearchAsync()
    {
        if (_searchCts != null) return;
        SearchHistoryRequested?.Invoke();
        var query = Query;
        var profile = SelectedProfile;
        var scopes = EnabledScopePaths();
        if (Scopes.Count == 0)
        {
            _dialogs.Show(StringOverlay.Get("Grep_SelectFolders"), DialogTitle);
            return;
        }

        if (scopes.Length == 0)
        {
            _dialogs.Show(StringOverlay.Get("Grep_NoFoldersSelected"), DialogTitle);
            return;
        }

        if (string.IsNullOrEmpty(query))
        {
            _dialogs.Show(StringOverlay.Get("Grep_EnterText"), DialogTitle);
            return;
        }

        if (profile == null) return;
        var extensions = ParseExtensions(Extensions);
        if (extensions.Length == 0)
        {
            _dialogs.Show(StringOverlay.Get("Grep_EnterExtensions"), DialogTitle);
            return;
        }

        if (profile.IsFree) SettingsService.SaveGrepFreeExtensions(Extensions.Trim());
        profile = new LanguageProfile(profile.Name, extensions);
        var useRegex = UseRegex == true;
        var matchCase = MatchCase == true;
        var wholeWord = WholeWord == true;
        if (!PrepareSearchTab()) return;

        var cts = new CancellationTokenSource();
        var searchToken = cts.Token;
        _searchCts = cts;
        var pending = new ConcurrentQueue<GrepMatch>();
        _pendingMatches = pending;
        _resultTimer.Start();
        SetSearching(true);
        try
        {
            var document = profile.IsDocument;
            var result = await Task.Run(() => document
                ? new DocumentGrepService().Search(scopes, extensions, query, useRegex, matchCase, wholeWord,
                    (done, total) => _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    {
                        if (_searchCts?.Token == searchToken && !searchToken.IsCancellationRequested)
                            Status = string.Format(StringOverlay.Get("Grep_SearchingFiles"), done.ToString("N0"),
                                total.ToString("N0"));
                    })), searchToken,
                    match =>
                    {
                        if (!searchToken.IsCancellationRequested) pending.Enqueue(match);
                    })
                : new CodeGrepService().Search(scopes, profile, query, useRegex, matchCase, wholeWord,
                    (done, total) => _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    {
                        if (_searchCts?.Token == searchToken && !searchToken.IsCancellationRequested)
                            Status = string.Format(StringOverlay.Get("Grep_SearchingFiles"), done.ToString("N0"),
                                total.ToString("N0"));
                    })), searchToken,
                    match =>
                    {
                        if (!searchToken.IsCancellationRequested) pending.Enqueue(match);
                    }));
            await DrainAllPendingMatchesAsync(cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            Status = result.SkippedCount == 0
                ? string.Format(StringOverlay.Get("Grep_Result"), result.Matches.Count.ToString("N0"),
                    result.FileCount.ToString("N0"))
                : string.Format(StringOverlay.Get("Grep_ResultSkipped"), result.Matches.Count.ToString("N0"),
                    result.FileCount.ToString("N0"), result.SkippedCount);
        }
        catch (OperationCanceledException)
        {
            _pendingMatches = new ConcurrentQueue<GrepMatch>();
            Status = StringOverlay.Get("Grep_SearchCancelled");
        }
        catch (ArgumentException ex)
        {
            _dialogs.Show(string.Format(StringOverlay.Get("Grep_InvalidExpression"), ErrorMessages.English(ex)), DialogTitle,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            Status = StringOverlay.Get("Grep_InvalidExpressionStatus");
        }
        catch (Exception ex)
        {
            _dialogs.Show(ErrorMessages.English(ex), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            Status = StringOverlay.Get("Grep_SearchFailed");
        }
        finally
        {
            _resultTimer.Stop();
            if (ReferenceEquals(_searchCts, cts)) _searchCts = null;
            SetSearching(false);
            cts.Dispose();
            SaveHistory();
        }
    }

    /// <summary>
    ///     結果一覧を、表示の更新を抑えてクリアします。
    /// </summary>
    private void ResetMatches()
    {
        ResultGroupsResetRequested?.Invoke();
        if (Results != null)
            using (Results.DeferRefresh())
            {
                Matches.Clear();
            }
        else
            Matches.Clear();

        NotifyListCommands();
    }

    // Drain by elapsed time instead of a fixed item count. A fixed 20 items per 50 ms tick
    // capped the display at 400 matches per second even when the search had finished.
    private static readonly TimeSpan LiveDrainBudget = TimeSpan.FromMilliseconds(15);
    private static readonly TimeSpan FinalDrainBudget = TimeSpan.FromMilliseconds(40);

    /// <summary>
    ///     バックグラウンドで見つかった結果を、指定した時間内で UI の一覧に追加します。
    /// </summary>
    /// <param name="budget">追加処理に使ってよい時間。</param>
    private void DrainPendingMatches(TimeSpan budget)
    {
        var count = 0;
        var watch = Stopwatch.StartNew();
        GrepMatch match;
        GrepMatch last = null;
        while (_pendingMatches.TryDequeue(out match))
        {
            Matches.Add(match);
            last = match;
            if ((++count & 15) == 0 && watch.Elapsed >= budget) break;
        }

        if (last != null)
        {
            NotifyListCommands();
            MatchScrollRequested?.Invoke(last);
        }
    }

    /// <summary>
    ///     残っている結果を、UI に処理を譲りながらすべて一覧に追加します。
    /// </summary>
    /// <param name="token">キャンセル トークン。</param>
    private async Task DrainAllPendingMatchesAsync(CancellationToken token)
    {
        while (!_pendingMatches.IsEmpty)
        {
            token.ThrowIfCancellationRequested();
            DrainPendingMatches(FinalDrainBudget);
            await Dispatcher.Yield(DispatcherPriority.Background);
        }
    }

    /// <summary>
    ///     検索中かどうかを設定し、関係するコマンドの実行可否を更新します。
    /// </summary>
    /// <param name="value">検索中の場合は true。</param>
    private void SetSearching(bool value)
    {
        IsSearching = value;
        IsCancelling = false;
        OnPropertyChanged(nameof(CanEdit));
        SearchCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        ReloadScopesCommand.NotifyCanExecuteChanged();
        AddHistoryTabCommand?.NotifyCanExecuteChanged();
        SaveHistoryTabCommand?.NotifyCanExecuteChanged();
        DeleteHistoryTabCommand?.NotifyCanExecuteChanged();
        DeleteAllHistoryTabsCommand?.NotifyCanExecuteChanged();
        DeleteOtherHistoryTabsCommand?.NotifyCanExecuteChanged();
        NotifyListCommands();
    }

    /// <summary>
    ///     空白・カンマ・セミコロン区切りの拡張子リストを解析し、先頭にドットを付けて重複を除きます。「(none)」は拡張子なしを表します。
    /// </summary>
    /// <param name="text">拡張子リスト。</param>
    /// <returns>拡張子の配列。</returns>
    private static string[] ParseExtensions(string text)
    {
        return (text ?? string.Empty).Split(new[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Select(value => value == "(none)" ? string.Empty :
                value.StartsWith(".", StringComparison.Ordinal) ? value : "." + value)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    ///     実行中の検索を取り消し、未表示の結果を破棄します。
    /// </summary>
    internal void Cancel()
    {
        if (_searchCts == null) return;
        _searchCts.Cancel();
        _resultTimer.Stop();
        _pendingMatches = new ConcurrentQueue<GrepMatch>();
        IsCancelling = true;
        CancelCommand.NotifyCanExecuteChanged();
        Status = StringOverlay.Get("Grep_Cancelling");
    }

    /// <summary>
    ///     選択した結果を開きます。ドキュメントは関連付けアプリで、テキストは設定したエディターで該当行を開きます。
    /// </summary>
    private void OpenMatch()
    {
        var match = SelectedMatch;
        if (match == null) return;
        try
        {
            if (DocumentGrepService.IsDocumentPath(match.FilePath))
            {
                Process.Start(new ProcessStartInfo(match.FilePath) { UseShellExecute = true });
                return;
            }

            SettingsService.SaveEditor(EditorPath.Trim(), EditorArguments);
            var arguments = (EditorArguments ?? string.Empty)
                .Replace("{file}", match.FilePath).Replace("{line}", match.LineNumber.ToString())
                .Replace("{column}", match.ColumnNumber.ToString());
            Process.Start(new ProcessStartInfo(ExpandEditorPath(EditorPath), arguments) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _dialogs.Show(string.Format(StringOverlay.Get("Grep_EditorFailed"), ErrorMessages.English(ex)), DialogTitle,
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    ///     エディターのパスから引用符を取り除き、環境変数を展開します。
    /// </summary>
    /// <param name="editor">エディターのパス。</param>
    /// <returns>展開したパス。</returns>
    internal static string ExpandEditorPath(string editor)
    {
        if (string.IsNullOrWhiteSpace(editor)) return editor;
        return Environment.ExpandEnvironmentVariables(editor.Trim().Trim('"'));
    }

    /// <summary>
    ///     エディターのプリセット (MIFES、秀丸、Mery、VS Code) を、初回だけ入力履歴に登録します。
    /// </summary>
    /// <returns>登録した場合は true。</returns>
    public bool SeedEditorPresets()
    {
        var settingsDirectory = AppSlot.Directory;
        var markerPath = Path.Combine(settingsDirectory, "editor-presets-miw.txt");
        var store = new InputHistoryStore(Path.Combine(settingsDirectory, "input-history"));
        if (File.Exists(markerPath)) return false;
        store.Replace("Grep-Editor", EditorPresets.Select(preset => preset.Executable));
        store.Replace("Grep-EditorArguments", EditorPresets.Select(preset => preset.Arguments));
        try
        {
            Directory.CreateDirectory(settingsDirectory);
            File.WriteAllText(markerPath, "mifes");
        }
        catch
        {
            // Ignore clipboard errors
        }

        return true;
    }

    /// <summary>
    ///     エディターに対応するプリセットの引数を取得します。
    /// </summary>
    /// <param name="editor">エディターのパス。</param>
    /// <param name="arguments">プリセットの引数。</param>
    /// <returns>プリセットが見つかった場合は true。</returns>
    public bool TryApplyEditorPreset(string editor, out string arguments)
    {
        arguments = null;
        var preset = MatchEditorPreset(editor);
        if (preset == null) return false;
        arguments = preset.Arguments;
        return true;
    }

    /// <summary>
    ///     エディターのパスまたはファイル名が一致するプリセットを探します。
    /// </summary>
    /// <param name="editor">エディターのパス。</param>
    /// <returns>一致したプリセット。見つからない場合は null。</returns>
    private static EditorPreset MatchEditorPreset(string editor)
    {
        if (string.IsNullOrWhiteSpace(editor)) return null;
        var path = ExpandEditorPath(editor);
        var name = Path.GetFileName(path);
        foreach (var preset in EditorPresets)
        {
            if (string.Equals(preset.Executable, path, StringComparison.OrdinalIgnoreCase)) return preset;
            if (!string.IsNullOrEmpty(name) &&
                string.Equals(preset.FileName, name, StringComparison.OrdinalIgnoreCase)) return preset;
        }

        if (string.Equals(path, "code", StringComparison.OrdinalIgnoreCase))
            return EditorPresets[EditorPresets.Length - 1];
        if (string.Equals(name, "MIW.exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Mifes.exe", StringComparison.OrdinalIgnoreCase))
            return EditorPresets[0];
        return null;
    }

    public sealed class EditorPreset
    {
        /// <summary>
        ///     エディターのプリセットを作成します。
        /// </summary>
        /// <param name="executable">エディターの実行ファイル。</param>
        /// <param name="arguments">引数テンプレート。</param>
        internal EditorPreset(string executable, string arguments)
        {
            Executable = executable;
            Arguments = arguments;
        }

        public string Executable { get; }
        public string Arguments { get; }
        public string FileName => Path.GetFileName(Executable);
    }
}

internal sealed class GrepScopeItem : ObservableObject
{
    private bool _isEnabled;

    /// <summary>
    ///     検索範囲の項目を作成します。
    /// </summary>
    /// <param name="path">フォルダーのパス。</param>
    /// <param name="enabled">検索対象にする場合は true。</param>
    public GrepScopeItem(string path, bool enabled = true)
    {
        FolderPath = path;
        _isEnabled = enabled;
    }

    public string FolderPath { get; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }
}