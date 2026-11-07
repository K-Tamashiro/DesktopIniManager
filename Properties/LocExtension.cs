using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;

namespace DesktopIniManager.Properties;

[MarkupExtensionReturnType(typeof(string))]
public sealed class L : MarkupExtension
{
    private static readonly List<Target> Targets = new();
    private static readonly Lock Gate = new();
    private static DispatcherOperation _pending;

    /// <summary>
    ///     表示カルチャの変更時に、登録済みのバインド先を更新するよう購読します。
    /// </summary>
    static L()
    {
        StringOverlay.CultureChanged += (_, _) => Refresh();
    }

    /// <summary>
    ///     キーを指定せずにマークアップ拡張を作成します。
    /// </summary>
    public L()
    {
    }

    /// <summary>
    ///     リソース キーを指定してマークアップ拡張を作成します。
    /// </summary>
    /// <param name="key">ローカライズ文字列のリソース キー。</param>
    public L(string key)
    {
        Key = key;
    }

    public string Key { get; set; }

    /// <summary>
    ///     ローカライズ文字列を返し、カルチャ変更時に更新できるようバインド先のプロパティを登録します。
    /// </summary>
    /// <param name="serviceProvider">マークアップ拡張のサービス プロバイダー。</param>
    /// <returns>ローカライズ文字列。共有テンプレート内では自分自身。</returns>
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var provide = serviceProvider.GetService(typeof(IProvideValueTarget)) as IProvideValueTarget;
        if (provide != null)
        {
            var targetObject = provide.TargetObject;
            if (targetObject != null && targetObject.GetType().FullName == "System.Windows.SharedDp")
                return this;
            var target = targetObject as DependencyObject;
            var property = provide.TargetProperty as DependencyProperty;
            if (target != null && property != null)
                lock (Gate)
                {
                    Targets.Add(new Target(target, property, Key));
                }
        }

        return StringOverlay.Get(Key);
    }

    /// <summary>
    ///     登録済みのバインド先の更新を、UI スレッドで 1 回だけ実行されるようにスケジュールします。
    /// </summary>
    private static void Refresh()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        if (_pending != null && _pending.Status == DispatcherOperationStatus.Pending)
            return;
        _pending = dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(Apply));
    }

    /// <summary>
    ///     破棄済みのバインド先を取り除き、生存しているバインド先に現在のカルチャの文字列を設定します。
    /// </summary>
    private static void Apply()
    {
        List<Target> live;
        lock (Gate)
        {
            live = new List<Target>(Targets.Count);
            for (var i = Targets.Count - 1; i >= 0; i--)
            {
                var item = Targets[i];
                if (item.Object == null)
                {
                    Targets.RemoveAt(i);
                    continue;
                }

                live.Add(item);
            }
        }

        foreach (var item in live)
        {
            var obj = item.Object;
            if (obj == null) continue;
            var value = StringOverlay.Get(item.Key);
            var current = obj.GetValue(item.Property);
            if (Equals(current, value)) continue;
            obj.SetValue(item.Property, value);
        }
    }

    private sealed class Target
    {
        private readonly WeakReference _reference;

        /// <summary>
        ///     バインド先のオブジェクトを弱参照で保持する登録情報を作成します。
        /// </summary>
        /// <param name="obj">バインド先のオブジェクト。</param>
        /// <param name="property">バインド先のプロパティ。</param>
        /// <param name="key">リソース キー。</param>
        internal Target(DependencyObject obj, DependencyProperty property, string key)
        {
            _reference = new WeakReference(obj);
            Property = property;
            Key = key;
        }

        internal DependencyObject Object => _reference.Target as DependencyObject;
        internal DependencyProperty Property { get; }
        internal string Key { get; }
    }
}