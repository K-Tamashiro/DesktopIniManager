using DesktopIniManager.Properties;
using DesktopIniManager.Services;
using DesktopIniManager.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace DesktopIniManager.Views;

internal partial class IgnoreRulesWindow
{
    public List<IgnoreRuleRow> Rules { get; }
    internal IEnumerable<string> DisabledRules => Rules.Where(r => !r.Exclude).Select(r => r.Key);

    /// <summary>
    ///     .gitignore ルールの除外設定ウィンドウを作成します。フォルダー パターンを先に並べます。
    /// </summary>
    /// <param name="snapshot">ルールを含む比較スナップショット。</param>
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

    /// <summary>
    ///     すべてのルールの除外設定を反転します。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">イベントデータ。</param>
    private void Invert_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in Rules) row.Exclude = !row.Exclude;
    }

    /// <summary>
    ///     設定を確定してダイアログを閉じます。
    /// </summary>
    /// <param name="sender">イベントの送信元。</param>
    /// <param name="e">イベントデータ。</param>
    private void Apply_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}

internal sealed class IgnoreRuleRow : ObservableObject
{
    private bool _exclude;
    public bool Exclude { get => _exclude; set => SetProperty(ref _exclude, value); }
    public string Key { get; }
    public string Pattern { get; }
    public string Location { get; }

    /// <summary>
    ///     .gitignore ルールの行を作成し、ルールがどちら側のどの .gitignore にあるかの表示を作ります。
    /// </summary>
    /// <param name="rule">ルールの情報。</param>
    /// <param name="exclude">ルールを適用 (除外) する場合は true。</param>
    internal IgnoreRuleRow(GitIgnoreRuleInfo rule, bool exclude)
    {
        Key = rule.Key; Pattern = rule.Pattern; this._exclude = exclude;
        string origin = StringOverlay.Get(rule.Source && rule.Target ? "Ignore_Both" : rule.Source ? "Ignore_Source" : "Ignore_Target");
        Location = origin + "  ·  " + (rule.Scope.Length == 0 ? ".gitignore" : rule.Scope + "/.gitignore");
    }
}