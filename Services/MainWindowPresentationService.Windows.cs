using DesktopIniManager.ViewModels;
using DesktopIniManager.Models;
using DesktopIniManager.Services;
using DesktopIniManager.Views;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Controls;
using System.Windows.Threading;
using FastVolumeIndex;
using DesktopIniManager.Properties;

namespace DesktopIniManager.Services
{
    internal sealed partial class MainWindowPresentationService
    {
        private void RestoreWindowPlacement(string[] arguments)
        {
            if (!TryReadArgument(arguments, "--window-left", out double left)
                || !TryReadArgument(arguments, "--window-top", out double top)
                || !TryReadArgument(arguments, "--window-width", out double width)
                || !TryReadArgument(arguments, "--window-height", out double height))
                return;

            if (width < _window.MinWidth || height < _window.MinHeight)
                return;

            var requested = new Rect(left, top, width, height);
            var virtualDesktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (!requested.IntersectsWith(virtualDesktop))
                return;

            _window.WindowStartupLocation = WindowStartupLocation.Manual;
            _window.Left = left;
            _window.Top = top;
            _window.Width = width;
            _window.Height = height;
            if (arguments.Any(argument => string.Equals(argument, "--window-maximized", StringComparison.OrdinalIgnoreCase)))
                _window.WindowState = WindowState.Maximized;
        }

        private static bool TryReadArgument(string[] arguments, string name, out double value)
        {
            value = 0;
            for (int index = 0; index < arguments.Length - 1; index++)
                if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
                    return double.TryParse(arguments[index + 1], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out value);
            return false;
        }

        private void DeveloperDifferencer()
        {
            if (_differencerWindow == null)
            {
                _differencerWindow = new DeveloperDifferencerWindow { Owner = _window };
                _differencerWindow.Closed += (s, args) => _differencerWindow = null;
                _differencerWindow.Show();
            }
            else WindowActivationService.BringToFront(_differencerWindow);
        }

        private void OpenGrep(IReadOnlyList<string> scopes)
        {
            if (_grepWindow == null)
            {
                _grepWindow = new GrepWindow(ViewModel.GetSelectedGrepScopes, scopes) { Owner = _window };
                _grepWindow.Closed += (closedSender, args) => _grepWindow = null;
                _grepWindow.Show();
            }
            else
            {
                _grepWindow.SetExplicitScopes(scopes);
                if (_grepWindow.WindowState == WindowState.Minimized) _grepWindow.WindowState = WindowState.Normal;
                WindowActivationService.BringToFront(_grepWindow);
            }
        }

        private async Task RelayoutChildWindows()
        {
            bool reopenGrep = _grepWindow != null;
            bool reopenDifferencer = _differencerWindow != null;
            List<DiffViewState> diffViews = Application.Current.Windows.OfType<DiffViewWindow>()
                .Select(window => new DiffViewState
                {
                    Snapshot = window.Snapshot,
                    Difference = window.Difference,
                    Bounds = CaptureBounds(window),
                    WindowState = window.WindowState
                }).ToList();
            IReadOnlyList<string> scopes = reopenGrep ? ViewModel.GetSelectedGrepScopes() : null;
            Rect grepBounds = reopenGrep ? CaptureBounds(_grepWindow) : Rect.Empty;
            WindowState grepState = reopenGrep ? _grepWindow.WindowState : WindowState.Normal;
            Rect differencerBounds = reopenDifferencer ? CaptureBounds(_differencerWindow) : Rect.Empty;
            WindowState differencerState = reopenDifferencer ? _differencerWindow.WindowState : WindowState.Normal;

            var children = Application.Current.Windows.Cast<Window>().Where(window => !ReferenceEquals(window, _window)).ToArray();
            await FadeWindows(children, 1, 0, TimeSpan.FromMilliseconds(220));
            foreach (Window window in children)
            {
                try { window.Close(); } catch { }
            }

            if (reopenDifferencer)
            {
                DeveloperDifferencer();
                RestoreWindowPlacement(_differencerWindow, differencerBounds, differencerState);
                PrepareFadeIn(_differencerWindow);
            }
            if (reopenGrep)
            {
                OpenGrep(scopes);
                RestoreWindowPlacement(_grepWindow, grepBounds, grepState);
                PrepareFadeIn(_grepWindow);
            }

            foreach (DiffViewState state in diffViews)
            {
                var window = new DiffViewWindow(state.Snapshot, state.Difference) { Owner = (Window)_differencerWindow ?? _window };
                RestoreWindowPlacement(window, state.Bounds, state.WindowState);
                PrepareFadeIn(window);
                window.Show();
            }

            Window[] reopened = new Window[] { _differencerWindow, _grepWindow }
                .Where(window => window != null).Concat(Application.Current.Windows.OfType<DiffViewWindow>()).ToArray();
            await FadeWindows(reopened, 0, 1, TimeSpan.FromMilliseconds(280));
        }

        private static void PrepareFadeIn(Window window)
        {
            if (window == null) return;
            window.BeginAnimation(Window.OpacityProperty, null);
            window.Opacity = 0;
        }

        private static Task FadeWindows(Window[] windows, double from, double to, TimeSpan duration)
        {
            if (windows == null || windows.Length == 0) return Task.CompletedTask;
            var tasks = new List<Task>();
            foreach (Window window in windows)
            {
                if (window == null) continue;
                var done = new TaskCompletionSource<bool>();
                var animation = new DoubleAnimation(from, to, duration)
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
                };
                animation.Completed += (sender, args) => done.TrySetResult(true);
                window.BeginAnimation(Window.OpacityProperty, null);
                window.Opacity = from;
                window.BeginAnimation(Window.OpacityProperty, animation);
                tasks.Add(done.Task);
            }
            return tasks.Count == 0 ? Task.CompletedTask : Task.WhenAll(tasks);
        }

        private static Rect CaptureBounds(Window window)
        {
            if (window == null) return Rect.Empty;
            return window.WindowState == WindowState.Normal
                ? new Rect(window.Left, window.Top, window.Width, window.Height)
                : window.RestoreBounds;
        }

    }
}
