using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using DesktopIniManager.Models;
using DesktopIniManager.Properties;

namespace DesktopIniManager.ViewModels
{
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
                CaptureHistoryTab();
                SetProperty(ref selectedHistoryTab, value);
                Query = value.Query;
                SelectedProfile = LanguageProfile.All.FirstOrDefault(p => p.Name == value.Profile) ?? SelectedProfile;
                Extensions = value.Extensions;
                UseRegex = value.UseRegex; MatchCase = value.MatchCase; WholeWord = value.WholeWord;
                SetScopes(value.Scopes);
                foreach (var scope in _scopes) scope.IsEnabled = value.EnabledScopes.Contains(scope.FolderPath, StringComparer.OrdinalIgnoreCase);
                ResetMatches();
                foreach (var match in value.Matches) _matches.Add(match);
                ListFilter = value.Filter; Status = value.Status;
                SelectedMatch = null;
                NotifyListCommands();
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
            SaveHistoryTabCommand = new RelayCommand(() => { if (SaveHistory()) Status = StringOverlay.Get("History_Saved"); }, () => !IsSearching);
            DeleteHistoryTabCommand = new ParameterCommand(item => DeleteHistoryTab(item as GrepHistoryTab), () => !IsSearching);
            DeleteAllHistoryTabsCommand = new RelayCommand(DeleteAllHistoryTabs, () => !IsSearching && HistoryTabs.Count > 0);
            DeleteOtherHistoryTabsCommand = new ParameterCommand(item => DeleteOtherHistoryTabs(item as GrepHistoryTab), () => !IsSearching && HistoryTabs.Count > 1);
        }
        private void CaptureHistoryTab()
        {
            if (selectedHistoryTab == null) return;
            var tab = selectedHistoryTab;
            tab.Query = Query; tab.Profile = SelectedProfile?.Name; tab.Extensions = Extensions;
            tab.UseRegex = UseRegex == true; tab.MatchCase = MatchCase == true; tab.WholeWord = WholeWord == true;
            tab.Filter = ListFilter; tab.Status = Status;
            tab.Scopes = _scopes.Select(s => s.FolderPath).ToList();
            tab.EnabledScopes = EnabledScopePaths().ToList();
            tab.Matches = _matches.ToList();
        }
        private void AddHistoryTab()
        {
            if (HistoryTabs.Count >= ResultHistoryStore.Limit)
            {
                _dialogs.Show(StringOverlay.Get("History_GrepLimit"), DialogTitle);
                return;
            }
            CaptureHistoryTab();
            var tab = new GrepHistoryTab { Title = StringOverlay.Get("History_NewSearch"), Profile = SelectedProfile?.Name, Extensions = Extensions,
                Scopes = _scopes.Select(s => s.FolderPath).ToList(), EnabledScopes = EnabledScopePaths().ToList() };
            HistoryTabs.Add(tab);
            SelectedHistoryTab = tab;
        }
        private bool PrepareSearchTab()
        {
            string query = Query;
            bool regex = UseRegex == true, matchCase = MatchCase == true, wholeWord = WholeWord == true;
            if (SelectedHistoryTab == null) AddHistoryTab();
            if (SelectedHistoryTab == null) return false;
            if (_matches.Count > 0)
            {
                bool full = HistoryTabs.Count >= ResultHistoryStore.Limit;
                var answer = _dialogs.Show(string.Format(StringOverlay.Get(full ? "History_GrepAppendAtLimit" : "History_GrepAppend"), SelectedHistoryTab.Title), DialogTitle, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes && answer != MessageBoxResult.No) return false;
                if (answer == MessageBoxResult.No)
                {
                    if (full) ResetMatches();
                    else AddHistoryTab();
                }
            }
            Query = query; UseRegex = regex; MatchCase = matchCase; WholeWord = wholeWord;
            SelectedHistoryTab.Title = Query;
            return true;
        }
        private void DeleteHistoryTab(GrepHistoryTab tab)
        {
            if (tab == null || !HistoryTabs.Contains(tab)) return;
            if (_dialogs.Show(string.Format(StringOverlay.Get("History_DeleteConfirm"), tab.Title), DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            bool active = tab == selectedHistoryTab;
            changingHistoryTabs = true;
            try { HistoryTabs.Remove(tab); }
            finally { changingHistoryTabs = false; }
            if (active)
            {
                selectedHistoryTab = null;
                if (HistoryTabs.Count == 0) AddHistoryTab();
                else SelectedHistoryTab = HistoryTabs[0];
            }
            SaveHistory();
        }
        private void DeleteAllHistoryTabs()
        {
            if (IsSearching || HistoryTabs.Count == 0) return;
            if (_dialogs.Show(StringOverlay.Get("History_DeleteAllConfirm"), DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            changingHistoryTabs = true;
            try { HistoryTabs.Clear(); }
            finally { changingHistoryTabs = false; }
            selectedHistoryTab = null;
            AddHistoryTab();
            SaveHistory();
        }
        private void DeleteOtherHistoryTabs(GrepHistoryTab tab)
        {
            if (IsSearching || tab == null || !HistoryTabs.Contains(tab) || HistoryTabs.Count <= 1) return;
            if (_dialogs.Show(string.Format(StringOverlay.Get("History_DeleteOthersConfirm"), tab.Title), DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            changingHistoryTabs = true;
            try
            {
                for (int i = HistoryTabs.Count - 1; i >= 0; i--)
                    if (!ReferenceEquals(HistoryTabs[i], tab)) HistoryTabs.RemoveAt(i);
            }
            finally { changingHistoryTabs = false; }
            if (selectedHistoryTab != tab) SelectedHistoryTab = tab;
            SaveHistory();
        }
        internal bool SaveHistory()
        {
            try
            {
                CaptureHistoryTab();
                ResultHistoryStore.Save("grep", new HistoryFile<GrepHistoryTab> { Tabs = HistoryTabs.ToList(), SelectedIndex = HistoryTabs.IndexOf(selectedHistoryTab) });
                return true;
            }
            catch (Exception ex) { _dialogs.Show(string.Format(StringOverlay.Get("History_SaveFailed"), ex.Message), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error); return false; }
        }
        private void RestoreHistory()
        {
            try
            {
                var saved = ResultHistoryStore.Load<HistoryFile<GrepHistoryTab>>("grep");
                foreach (var tab in saved.Tabs.Take(ResultHistoryStore.Limit)) HistoryTabs.Add(tab);
                if (HistoryTabs.Count > 0) SelectedHistoryTab = HistoryTabs[0];
                else AddHistoryTab();
            }
            catch (Exception ex) { _dialogs.Show(string.Format(StringOverlay.Get("History_LoadFailed"), ex.Message), DialogTitle); AddHistoryTab(); }
        }
    }
}
