using DesktopIniManager.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DesktopIniManager.Services
{
    internal sealed class StartupState
    {
        internal bool DarkMode, TreeCompact;
        internal string Root, Query, IconLibrary, TreeError;
        internal FolderTreeState Tree;
        internal List<FolderMatch> Physical, Solution;
        internal IconGroupResource SelectedIcon;

        internal static StartupState Load(Action<string, int> report)
        {
            report("Loading startup settings…", 0);
            var state = new StartupState
            {
                DarkMode = SettingsService.LoadDarkMode(), TreeCompact = SettingsService.LoadTreeCompact(),
                Root = SettingsService.LoadSearchRoot(), Query = SettingsService.LoadSearchQuery(),
                IconLibrary = SettingsService.LoadIconLibraryPath()
            };
            if (string.IsNullOrWhiteSpace(state.IconLibrary))
                state.IconLibrary = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "folder_set.icl");
            report("Restoring saved folder trees…", 1);
            try
            {
                state.Tree = FolderTreeStateService.Load();
                if (state.Tree != null)
                {
                    var icons = FolderTreeStateService.RestoreIcons(state.Tree.Icons);
                    state.Physical = FolderTreeStateService.Restore(state.Tree.Physical, icons: icons);
                    state.Solution = FolderTreeStateService.Restore(state.Tree.Solution, icons: icons);
                }
            }
            catch (Exception ex) { state.Tree = null; state.TreeError = ex.Message; }
            report("Preparing folder icons…", 2);
            FolderIconService.GetDefaultFolderIcon();
            try
            {
                if (File.Exists(state.IconLibrary)) state.SelectedIcon = IconResourceReader.Read(state.IconLibrary).FirstOrDefault(icon => icon.ShellIndex == 0);
            }
            catch { /* A missing/custom icon library must not prevent startup. */ }
            return state;
        }
    }
}
