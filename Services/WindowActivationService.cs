using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;

namespace DesktopIniManager.Services
{
    internal static class WindowActivationService
    {
        private sealed class State
        {
            internal Window Owner;
            internal CancelEventArgs Closing;
            internal bool Closed;
        }
        private static readonly ConditionalWeakTable<Window, State> states = new ConditionalWeakTable<Window, State>();
        private static bool installed;

        internal static void Install()
        {
            if (installed) return;
            installed = true;
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));
        }

        private static void OnLoaded(object sender, RoutedEventArgs args)
        {
            var window = (Window)sender;
            if (!ReferenceEquals(args.OriginalSource, window) || states.TryGetValue(window, out _)) return;
            var state = new State { Owner = window.Owner };
            states.Add(window, state);
            EventHandler rendered = null;
            rendered = (s, e) => { window.ContentRendered -= rendered; BringToFront(window); };
            window.ContentRendered += rendered;
            window.Closing += (s, e) => { state.Owner = window.Owner; state.Closing = e; };
            window.Closed += (s, e) =>
            {
                state.Closed = true;
                window.ContentRendered -= rendered;
                if (state.Owner == null || window.Dispatcher.HasShutdownStarted) return;
                // Modal owners are enabled again after Closed has returned.
                window.Dispatcher.BeginInvoke(new Action(() => BringToFront(state.Owner)), DispatcherPriority.ContextIdle);
            };
        }

        private static bool Available(Window window)
        {
            if (window == null || !window.IsVisible || window.Dispatcher.HasShutdownStarted) return false;
            State state;
            return !states.TryGetValue(window, out state) || (!state.Closed && (state.Closing == null || state.Closing.Cancel));
        }

        internal static void BringToFront(Window window)
        {
            if (!Available(window)) return;
            // Keep an open child/dialog above its owner, including on startup resume.
            Window child = window.OwnedWindows.Cast<Window>().LastOrDefault(w => Available(w) && w.IsActive)
                ?? window.OwnedWindows.Cast<Window>().LastOrDefault(Available);
            if (child != null) { BringToFront(child); return; }
            if (!window.IsEnabled) return;
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            bool wasTopmost = window.Topmost;
            try { window.SetCurrentValue(Window.TopmostProperty, true); }
            finally { window.SetCurrentValue(Window.TopmostProperty, wasTopmost); }
            window.Activate();
        }
    }
}
