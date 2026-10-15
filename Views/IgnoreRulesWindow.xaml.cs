using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using DesktopIniManager.Properties;
using DesktopIniManager.Services;
using DesktopIniManager.ViewModels;

namespace DesktopIniManager.Views;

internal partial class IgnoreRulesWindow : Window
{
    public List<IgnoreRuleRow> Rules { get; }
    internal IEnumerable<string> DisabledRules => Rules.Where(r => !r.Exclude).Select(r => r.Key);

    internal IgnoreRulesWindow(DiffSnapshot snapshot)
    {
        var disabled = new HashSet<string>(snapshot.DisabledIgnoreRules ?? new(), StringComparer.Ordinal);
        Rules = snapshot.IgnoreRules
            .OrderBy(rule => rule.Pattern.EndsWith("/", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(rule => rule.Pattern, StringComparer.OrdinalIgnoreCase)
            .ThenBy(rule => rule.Scope, StringComparer.OrdinalIgnoreCase)
            .Select(rule => new IgnoreRuleRow(rule, !disabled.Contains(rule.Key))).ToList();
        InitializeComponent();
        DataContext = this;
        CancelIcon.Source = DifferencerStatusIcons.GetCustomIcon(26);
        InvertIcon.Source = DifferencerStatusIcons.GetCustomIcon(48);
        ApplyIcon.Source = DifferencerStatusIcons.GetCustomIcon(34);
    }

    private void Invert_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in Rules) row.Exclude = !row.Exclude;
    }
    private void Apply_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}

internal sealed class IgnoreRuleRow : ObservableObject
{
    private bool exclude;
    public bool Exclude { get => exclude; set => SetProperty(ref exclude, value); }
    public string Key { get; }
    public string Pattern { get; }
    public string Location { get; }
    internal IgnoreRuleRow(GitIgnoreRuleInfo rule, bool exclude)
    {
        Key = rule.Key; Pattern = rule.Pattern; this.exclude = exclude;
        string origin = StringOverlay.Get(rule.Source && rule.Target ? "Ignore_Both" : rule.Source ? "Ignore_Source" : "Ignore_Target");
        Location = origin + "  ·  " + (rule.Scope.Length == 0 ? ".gitignore" : rule.Scope + "/.gitignore");
    }
}
