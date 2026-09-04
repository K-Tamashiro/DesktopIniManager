using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Markup;

namespace DesktopIniManager.Properties
{
    [MarkupExtensionReturnType(typeof(string))]
    public sealed class L : MarkupExtension
    {
        private static readonly List<Target> Targets = new List<Target>();
        private static readonly object Gate = new object();

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
            List<Target> live = new List<Target>();
            lock (Gate)
            {
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
                obj.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (item.Object != null)
                        item.Object.SetValue(item.Property, StringOverlay.Get(item.Key));
                }));
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
