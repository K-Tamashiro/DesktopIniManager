using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;

namespace DesktopIniManager.Services;

internal static class WindowActivationService
{
    private static readonly ConditionalWeakTable<Window, State> States = new();
    private static bool _installed;

    /// <summary>
    ///     すべてのウィンドウの Loaded イベントにクラス ハンドラーを登録し、前面表示の管理を開始します。
    /// </summary>
    internal static void Install()
    {
        if (_installed) return;
        _installed = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnLoaded));
    }

    /// <summary>
    ///     ウィンドウの初回描画後に前面表示し、閉じた後はオーナー ウィンドウを前面に戻すよう設定します。
    /// </summary>
    /// <param name="sender">読み込まれたウィンドウ。</param>
    /// <param name="args">イベントデータ。</param>
    private static void OnLoaded(object sender, RoutedEventArgs args)
    {
        var window = (Window)sender;
        if (!ReferenceEquals(args.OriginalSource, window) || States.TryGetValue(window, out _)) return;
        var state = new State { Owner = window.Owner };
        States.Add(window, state);
        EventHandler rendered = null;
        rendered = (_, _) =>
        {
            window.ContentRendered -= rendered;
            BringToFront(window);
        };
        window.ContentRendered += rendered;
        window.Closing += (_, e) =>
        {
            state.Owner = window.Owner;
            state.Closing = e;
        };
        window.Closed += (_, _) =>
        {
            state.Closed = true;
            window.ContentRendered -= rendered;
            if (state.Owner == null || window.Dispatcher.HasShutdownStarted) return;
            // Modal owners are enabled again after Closed has returned.
            window.Dispatcher.BeginInvoke(new Action(() => BringToFront(state.Owner)), DispatcherPriority.ContextIdle);
        };
    }

    /// <summary>
    ///     ウィンドウが表示中で、閉じる途中でもなく、前面表示できる状態かどうかを判定します。
    /// </summary>
    /// <param name="window">判定するウィンドウ。</param>
    /// <returns>前面表示できる場合は true。</returns>
    private static bool Available(Window window)
    {
        if (window == null || !window.IsVisible || window.Dispatcher.HasShutdownStarted) return false;
        return !States.TryGetValue(window, out var state) || state == null ||
               (!state.Closed && (state.Closing == null || state.Closing.Cancel));
    }

    /// <summary>
    ///     ウィンドウを前面に表示してアクティブにします。開いている子ウィンドウがあれば、そちらを優先して前面に表示します。
    /// </summary>
    /// <param name="window">前面に表示するウィンドウ。</param>
    /// <param name="selectWindow">利用者が明示的に選択した場合は true。所有するモードレス ウィンドウを最小化します。</param>
    internal static void BringToFront(Window window, bool selectWindow = false)
    {
        if (!Available(window)) return;
        if (selectWindow && window.IsEnabled)
            // Modeless owned windows otherwise stay above the explicitly selected workspace.
            foreach (var owned in window.OwnedWindows.Cast<Window>().Where(Available))
                owned.WindowState = WindowState.Minimized;
        // Keep an open child/dialog above its owner, including on startup resume.
        var child = window.OwnedWindows.Cast<Window>().LastOrDefault(w => Available(w) && w.IsActive)
                    ?? window.OwnedWindows.Cast<Window>().LastOrDefault(Available);
        if (child != null && (!selectWindow || !window.IsEnabled))
        {
            BringToFront(child);
            return;
        }

        if (!window.IsEnabled) return;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        var wasTopmost = window.Topmost;
        try
        {
            window.SetCurrentValue(Window.TopmostProperty, true);
        }
        finally
        {
            window.SetCurrentValue(Window.TopmostProperty, wasTopmost);
        }

        window.Activate();
    }

    private sealed class State
    {
        internal bool Closed;
        internal CancelEventArgs Closing;
        internal Window Owner;
    }
}