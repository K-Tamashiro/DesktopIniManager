using DesktopIniManager.Models;
using DesktopIniManager.Services;
using DesktopIniManager.Properties;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;

namespace DesktopIniManager.ViewModels;

internal sealed partial class GrepWindowViewModel
{
    public ObservableCollection<GrepHistoryTab> HistoryTabs { get; } = new ObservableCollection<GrepHistoryTab>();
    private GrepHistoryTab selectedHistoryTab;
    private bool changingHistoryTabs;
    public GrepHistoryTab SelectedHistoryTab
    {
        get => selectedHistoryTab;
        set
        {
            if (changingHistoryTabs || IsSearching || value == null || value == selectedHistoryTab) return;
            SelectHistoryTab(value);
        }
    }
    public RelayCommand AddHistoryTabCommand { get; private set; }
    public RelayCommand SaveHistoryTabCommand { get; private set; }
    public ParameterCommand DeleteHistoryTabCommand { get; private set; }
    public RelayCommand DeleteAllHistoryTabsCommand { get; private set; }
    public ParameterCommand DeleteOtherHistoryTabsCommand { get; private set; }

    private void InitializeHistoryCommands()
    {
        AddHistoryTabCommand = new RelayCommand(AddHistoryTab, () => !IsSearching);
        SaveHistoryTabCommand = new RelayCommand(() => SaveHistory(), () => !IsSearching && HistoryTabs.Count > 0);
        DeleteHistoryTabCommand = new ParameterCommand(DeleteHistoryTab, () => !IsSearching);
        DeleteAllHistoryTabsCommand = new RelayCommand(DeleteAllHistoryTabs, () => !IsSearching && HistoryTabs.Count > 0);
        DeleteOtherHistoryTabsCommand = new ParameterCommand(DeleteOtherHistoryTabs, () => !IsSearching && HistoryTabs.Count > 1);
    }

    private void RestoreHistory()
    {
        try
        {
            var saved = ResultHistoryStore.Load<HistoryFile<GrepHistoryTab>>("grep");
            foreach (GrepHistoryTab tab in saved.Tabs.Take(ResultHistoryStore.Limit))
                HistoryTabs.Add(tab);
            GrepHistoryTab selected = saved.Tabs.ElementAtOrDefault(saved.SelectedIndex);
            if (HistoryTabs.Count == 0) return;
            SelectHistoryTab(HistoryTabs.Contains(selected) ? selected : HistoryTabs[0]);
        }
        catch (Exception ex)
        {
            _dialogs.Show(string.Format(StringOverlay.Get("History_LoadFailed"), ex.Message), DialogTitle);
        }
    }

    internal bool SaveHistory()
    {
        try
        {
            CaptureHistoryTab();
            ResultHistoryStore.Save("grep", new HistoryFile<GrepHistoryTab>
            {
                Tabs = HistoryTabs.ToList(),
                SelectedIndex = HistoryTabs.IndexOf(selectedHistoryTab)
            });
            return true;
        }
        catch (Exception ex)
        {
            _dialogs.Show(ErrorMessages.English(ex), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private bool PrepareSearchTab()
    {
        string query = Query;
        bool? regex = UseRegex, matchCase = MatchCase, wholeWord = WholeWord;
        if (selectedHistoryTab == null) AddHistoryTab();
        if (selectedHistoryTab == null) return false;
        if (_matches.Count > 0)
        {
            bool full = HistoryTabs.Count >= ResultHistoryStore.Limit;
            var answer = _dialogs.Show(string.Format(StringOverlay.Get(full ? "History_GrepAppendAtLimit" : "History_GrepAppend"), selectedHistoryTab.Title),
                DialogTitle, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes && answer != MessageBoxResult.No) return false;
            if (answer == MessageBoxResult.No)
            {
                if (full) ResetMatches();
                else AddHistoryTab();
            }
        }
        Query = query; UseRegex = regex; MatchCase = matchCase; WholeWord = wholeWord;
        selectedHistoryTab.Title = Query;
        return true;
    }

    private void AddHistoryTab()
    {
        if (HistoryTabs.Count >= ResultHistoryStore.Limit)
        {
            _dialogs.Show(StringOverlay.Get("History_GrepLimit"), DialogTitle);
            return;
        }
        CaptureHistoryTab();
        var tab = new GrepHistoryTab
        {
            Profile = SelectedProfile?.Name,
            Extensions = Extensions,
            Scopes = _scopes.Select(item => item.FolderPath).ToList(),
            EnabledScopes = EnabledScopePaths().ToList()
        };
        changingHistoryTabs = true;
        try { HistoryTabs.Insert(0, tab); }
        finally { changingHistoryTabs = false; }
        selectedHistoryTab = tab;
        ApplyHistoryTab(tab);
        OnPropertyChanged(nameof(SelectedHistoryTab));
        NotifyListCommands();
    }

    private void SelectHistoryTab(GrepHistoryTab tab)
    {
        if (tab == null) return;
        CaptureHistoryTab();
        changingHistoryTabs = true;
        try { selectedHistoryTab = tab; }
        finally { changingHistoryTabs = false; }
        ApplyHistoryTab(tab);
        OnPropertyChanged(nameof(SelectedHistoryTab));
    }

    private void CaptureHistoryTab()
    {
        if (selectedHistoryTab == null) return;
        selectedHistoryTab.Title = string.IsNullOrWhiteSpace(Query) ? selectedHistoryTab.Title : Query;
        selectedHistoryTab.Query = Query ?? string.Empty;
        selectedHistoryTab.Profile = SelectedProfile?.Name;
        selectedHistoryTab.Extensions = Extensions ?? string.Empty;
        selectedHistoryTab.UseRegex = UseRegex == true;
        selectedHistoryTab.MatchCase = MatchCase == true;
        selectedHistoryTab.WholeWord = WholeWord == true;
        selectedHistoryTab.Filter = ListFilter ?? string.Empty;
        selectedHistoryTab.Status = Status;
        selectedHistoryTab.Scopes = _scopes.Select(item => item.FolderPath).ToList();
        selectedHistoryTab.EnabledScopes = _scopes.Where(item => item.IsEnabled).Select(item => item.FolderPath).ToList();
        selectedHistoryTab.Matches = _matches.ToList();
    }

    private void ApplyHistoryTab(GrepHistoryTab tab)
    {
        Query = tab.Query ?? string.Empty;
        ListFilter = tab.Filter ?? string.Empty;
        UseRegex = tab.UseRegex;
        MatchCase = tab.MatchCase;
        WholeWord = tab.WholeWord;
        if (!string.IsNullOrWhiteSpace(tab.Profile))
        {
            LanguageProfile profile = LanguageProfile.All.FirstOrDefault(item =>
                string.Equals(item.Name, tab.Profile, StringComparison.OrdinalIgnoreCase));
            if (profile != null) SelectedProfile = profile;
        }
        Extensions = tab.Extensions ?? string.Empty;
        if (tab.Scopes != null)
        {
            SetScopes(tab.Scopes, keepEnabledState: false);
            var enabled = new System.Collections.Generic.HashSet<string>(tab.EnabledScopes ?? tab.Scopes, StringComparer.OrdinalIgnoreCase);
            foreach (GrepScopeItem item in _scopes)
                item.IsEnabled = enabled.Contains(item.FolderPath);
        }
        ResetMatches();
        SelectedMatch = null;
        if (tab.Matches != null)
            foreach (GrepMatch match in tab.Matches) _matches.Add(match);
        Status = string.IsNullOrWhiteSpace(tab.Status) ? Strings.Common_Ready : tab.Status;
        NotifyListCommands();
    }

    private void DeleteHistoryTab(object item)
    {
        var tab = item as GrepHistoryTab;
        if (IsSearching || tab == null || !HistoryTabs.Contains(tab)) return;
        bool active = tab == selectedHistoryTab;
        changingHistoryTabs = true;
        try { HistoryTabs.Remove(tab); }
        finally { changingHistoryTabs = false; }
        if (active)
        {
            selectedHistoryTab = null;
            if (HistoryTabs.Count > 0) SelectHistoryTab(HistoryTabs[0]);
            else { ResetMatches(); SelectedMatch = null; OnPropertyChanged(nameof(SelectedHistoryTab)); }
        }
        SaveHistory();
    }

    private void DeleteAllHistoryTabs()
    {
        if (IsSearching || HistoryTabs.Count == 0) return;
        changingHistoryTabs = true;
        try { HistoryTabs.Clear(); }
        finally { changingHistoryTabs = false; }
        selectedHistoryTab = null;
        ResetMatches();
        SelectedMatch = null;
        OnPropertyChanged(nameof(SelectedHistoryTab));
        SaveHistory();
    }

    private void DeleteOtherHistoryTabs(object item)
    {
        var tab = item as GrepHistoryTab;
        if (IsSearching || tab == null || !HistoryTabs.Contains(tab) || HistoryTabs.Count <= 1) return;
        changingHistoryTabs = true;
        try
        {
            for (int i = HistoryTabs.Count - 1; i >= 0; i--)
                if (!ReferenceEquals(HistoryTabs[i], tab)) HistoryTabs.RemoveAt(i);
        }
        finally { changingHistoryTabs = false; }
        if (selectedHistoryTab != tab) SelectHistoryTab(tab);
        SaveHistory();
    }
}
