using DesktopIniManager.Models;
using DesktopIniManager.Properties;
using DesktopIniManager.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;

namespace DesktopIniManager.ViewModels;

internal sealed partial class GrepWindowViewModel
{
    private bool _changingHistoryTabs;
    private GrepHistoryTab _selectedHistoryTab;
    public ObservableCollection<GrepHistoryTab> HistoryTabs { get; } = new();

    public GrepHistoryTab SelectedHistoryTab
    {
        get => _selectedHistoryTab;
        set
        {
            if (_changingHistoryTabs || IsSearching || value == null || value == _selectedHistoryTab) return;
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
        DeleteAllHistoryTabsCommand =
            new RelayCommand(DeleteAllHistoryTabs, () => !IsSearching && HistoryTabs.Count > 0);
        DeleteOtherHistoryTabsCommand =
            new ParameterCommand(DeleteOtherHistoryTabs, () => !IsSearching && HistoryTabs.Count > 1);
    }

    private void RestoreHistory()
    {
        try
        {
            var saved = ResultHistoryStore.Load<HistoryFile<GrepHistoryTab>>("grep");
            foreach (var tab in saved.Tabs.Take(ResultHistoryStore.Limit))
                HistoryTabs.Add(tab);
            var selected = saved.Tabs.ElementAtOrDefault(saved.SelectedIndex);
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
                SelectedIndex = HistoryTabs.IndexOf(_selectedHistoryTab)
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
        var query = Query;
        bool? regex = UseRegex, matchCase = MatchCase, wholeWord = WholeWord;
        if (_selectedHistoryTab == null) AddHistoryTab();
        if (_selectedHistoryTab == null) return false;
        if (Matches.Count > 0)
        {
            var full = HistoryTabs.Count >= ResultHistoryStore.Limit;
            var answer = _dialogs.Show(
                string.Format(StringOverlay.Get(full ? "History_GrepAppendAtLimit" : "History_GrepAppend"),
                    _selectedHistoryTab.Title),
                DialogTitle, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes && answer != MessageBoxResult.No) return false;
            if (answer == MessageBoxResult.No)
            {
                if (full)
                {
                    ResetMatches();
                }
                else
                {
                    // Preserve the original tab's search query before creating a new tab.
                    Query = _selectedHistoryTab.Query ?? string.Empty;
                    AddHistoryTab();
                }
            }
        }

        Query = query;
        UseRegex = regex;
        MatchCase = matchCase;
        WholeWord = wholeWord;
        _selectedHistoryTab.Title = Query;
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
            Scopes = Scopes.Select(item => item.FolderPath).ToList(),
            EnabledScopes = EnabledScopePaths().ToList()
        };
        _changingHistoryTabs = true;
        try
        {
            HistoryTabs.Insert(0, tab);
        }
        finally
        {
            _changingHistoryTabs = false;
        }

        _selectedHistoryTab = tab;
        ApplyHistoryTab(tab);
        OnPropertyChanged(nameof(SelectedHistoryTab));
        NotifyListCommands();
    }

    private void SelectHistoryTab(GrepHistoryTab tab)
    {
        if (tab == null) return;
        CaptureHistoryTab();
        _changingHistoryTabs = true;
        try
        {
            _selectedHistoryTab = tab;
        }
        finally
        {
            _changingHistoryTabs = false;
        }

        ApplyHistoryTab(tab);
        OnPropertyChanged(nameof(SelectedHistoryTab));
    }

    private void CaptureHistoryTab()
    {
        if (_selectedHistoryTab == null) return;
        _selectedHistoryTab.Query = Query ?? string.Empty;
        _selectedHistoryTab.Profile = SelectedProfile?.Name;
        _selectedHistoryTab.Extensions = Extensions ?? string.Empty;
        _selectedHistoryTab.UseRegex = UseRegex == true;
        _selectedHistoryTab.MatchCase = MatchCase == true;
        _selectedHistoryTab.WholeWord = WholeWord == true;
        _selectedHistoryTab.Filter = ListFilter ?? string.Empty;
        _selectedHistoryTab.Status = Status;
        _selectedHistoryTab.Scopes = Scopes.Select(item => item.FolderPath).ToList();
        _selectedHistoryTab.EnabledScopes =
            Scopes.Where(item => item.IsEnabled).Select(item => item.FolderPath).ToList();
        _selectedHistoryTab.Matches = Matches.ToList();
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
            var profile = LanguageProfile.All.FirstOrDefault(item =>
                string.Equals(item.Name, tab.Profile, StringComparison.OrdinalIgnoreCase));
            if (profile != null) SelectedProfile = profile;
        }

        Extensions = tab.Extensions ?? string.Empty;
        if (tab.Scopes != null)
        {
            SetScopes(tab.Scopes, false);
            var enabled = new HashSet<string>(tab.EnabledScopes ?? tab.Scopes, StringComparer.OrdinalIgnoreCase);
            foreach (var item in Scopes)
                item.IsEnabled = enabled.Contains(item.FolderPath);
        }

        ResetMatches();
        SelectedMatch = null;
        if (tab.Matches != null)
            foreach (var match in tab.Matches)
                Matches.Add(match);
        Status = string.IsNullOrWhiteSpace(tab.Status) ? Strings.Common_Ready : tab.Status;
        NotifyListCommands();
    }

    private void DeleteHistoryTab(object item)
    {
        var tab = item as GrepHistoryTab;
        if (IsSearching || tab == null || !HistoryTabs.Contains(tab)) return;
        var active = tab == _selectedHistoryTab;
        _changingHistoryTabs = true;
        try
        {
            HistoryTabs.Remove(tab);
        }
        finally
        {
            _changingHistoryTabs = false;
        }

        if (active)
        {
            _selectedHistoryTab = null;
            if (HistoryTabs.Count > 0)
            {
                SelectHistoryTab(HistoryTabs[0]);
            }
            else
            {
                ResetMatches();
                SelectedMatch = null;
                OnPropertyChanged(nameof(SelectedHistoryTab));
            }
        }

        SaveHistory();
    }

    private void DeleteAllHistoryTabs()
    {
        if (IsSearching || HistoryTabs.Count == 0) return;
        _changingHistoryTabs = true;
        try
        {
            HistoryTabs.Clear();
        }
        finally
        {
            _changingHistoryTabs = false;
        }

        _selectedHistoryTab = null;
        ResetMatches();
        SelectedMatch = null;
        OnPropertyChanged(nameof(SelectedHistoryTab));
        SaveHistory();
    }

    private void DeleteOtherHistoryTabs(object item)
    {
        var tab = item as GrepHistoryTab;
        if (IsSearching || tab == null || !HistoryTabs.Contains(tab) || HistoryTabs.Count <= 1) return;
        _changingHistoryTabs = true;
        try
        {
            for (var i = HistoryTabs.Count - 1; i >= 0; i--)
                if (!ReferenceEquals(HistoryTabs[i], tab))
                    HistoryTabs.RemoveAt(i);
        }
        finally
        {
            _changingHistoryTabs = false;
        }

        if (_selectedHistoryTab != tab) SelectHistoryTab(tab);
        SaveHistory();
    }
}