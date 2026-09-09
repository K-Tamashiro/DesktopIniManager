using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;

namespace DesktopIniManager.Properties
{
    [MarkupExtensionReturnType(typeof(string))]
    public sealed class L : MarkupExtension
    {
        private static readonly List<Target> Targets = new List<Target>();
        private static readonly object Gate = new object();
        private static DispatcherOperation pending;

        static L()
        {
            StringOverlay.CultureChanged += (s, e) => Refresh();
        }

        public L() { }
        public L(string key) { Key = key; }
        public string Key { get; set; }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            var provide = serviceProvider.GetService(typeof(IProvideValueTarget)) as IProvideValueTarget;
            if (provide != null)
            {
                object targetObject = provide.TargetObject;
                if (targetObject != null && targetObject.GetType().FullName == "System.Windows.SharedDp")
                    return this;
                var target = targetObject as DependencyObject;
                var property = provide.TargetProperty as DependencyProperty;
                if (target != null && property != null)
                {
                    lock (Gate)
                        Targets.Add(new Target(target, property, Key));
                }
            }
            return StringOverlay.Get(Key);
        }

        private static void Refresh()
        {
            Dispatcher dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;
            if (pending != null && pending.Status == DispatcherOperationStatus.Pending)
                return;
            pending = dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(Apply));
        }

        private static void Apply()
        {
            List<Target> live;
            lock (Gate)
            {
                live = new List<Target>(Targets.Count);
                for (int i = Targets.Count - 1; i >= 0; i--)
                {
                    Target item = Targets[i];
                    if (item.Object == null)
                    {
                        Targets.RemoveAt(i);
                        continue;
                    }
                    live.Add(item);
                }
            }
            foreach (Target item in live)
            {
                DependencyObject obj = item.Object;
                if (obj == null) continue;
                string value = StringOverlay.Get(item.Key);
                object current = obj.GetValue(item.Property);
                if (Equals(current, value)) continue;
                obj.SetValue(item.Property, value);
            }
        }

        private sealed class Target
        {
            private readonly WeakReference reference;
            internal Target(DependencyObject obj, DependencyProperty property, string key)
            {
                reference = new WeakReference(obj);
                Property = property;
                Key = key;
            }
            internal DependencyObject Object { get { return reference.Target as DependencyObject; } }
            internal DependencyProperty Property { get; }
            internal string Key { get; }
        }
    }
}
