using DesktopIniManager.Properties;
using System;
using System.IO;
using System.Threading;

namespace DesktopIniManager.Services;

internal static class AppSlot
{
    private static Mutex _slotMutex;
    private static EventWaitHandle _frontEvent;

    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopIniManager");

    public static int Number { get; private set; } = 1;
    public static string Name => "dim-" + Number;
    public static string Directory => Path.Combine(Root, Name);

    public static event Action FrontRequested;

    /// <summary>
    ///     引数で指定されたスロット、または空いている最初のスロット (dim-1 から dim-5) を確保します。
    /// </summary>
    /// <param name="args">コマンドライン引数。-d1 から -d5 でスロットを指定できます。</param>
    /// <param name="failure">確保できなかった場合のメッセージ。</param>
    /// <returns>スロットを確保できた場合は true。</returns>
    public static bool TryAcquire(string[] args, out string failure)
    {
        failure = null;
        var requested = RequestedSlot(args);
        if (requested > 0)
        {
            if (Acquire(requested)) return true;
            failure = string.Format(StringOverlay.Get("App_SlotRunning"), "dim-" + requested);
            return false;
        }

        for (var slot = 1; slot <= 5; slot++)
            if (Acquire(slot))
                return true;
        failure = StringOverlay.Get("App_AllSlotsRunning");
        return false;
    }

    /// <summary>
    ///     スロットの状態を表す文字列を取得します。
    /// </summary>
    /// <param name="slot">スロット番号。</param>
    /// <returns>自分が使用中なら "In Use"、他プロセスが使用中なら "Open"、空きなら "Free"。</returns>
    public static string StateText(int slot)
    {
        if (slot == Number) return "In Use";
        return IsHeld(slot) ? "Open" : "Free";
    }

    /// <summary>
    ///     指定スロットのミューテックスを別のプロセスが保持しているかどうかを判定します。
    /// </summary>
    /// <param name="slot">スロット番号。</param>
    /// <returns>保持されている場合は true。</returns>
    private static bool IsHeld(int slot)
    {
        using (var mutex = new Mutex(false, @"Local\DesktopIniManager-dim-" + slot))
        {
            try
            {
                if (!mutex.WaitOne(0)) return true;
                mutex.ReleaseMutex();
                return false;
            }
            catch (AbandonedMutexException)
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    return false;
                }

                return false;
            }
        }
    }

    /// <summary>
    ///     コマンドライン引数から -d1 から -d5 のスロット指定を取得します。
    /// </summary>
    /// <param name="args">コマンドライン引数。</param>
    /// <returns>指定されたスロット番号。指定がない場合は 0。</returns>
    private static int RequestedSlot(string[] args)
    {
        if (args == null) return 0;
        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg) || arg.Length != 3) continue;
            if (!arg.StartsWith("-d", StringComparison.OrdinalIgnoreCase)) continue;
            if (arg[2] < '1' || arg[2] > '5') continue;
            return arg[2] - '0';
        }

        return 0;
    }

    /// <summary>
    ///     スロットのミューテックスを取得し、前面表示要求を待ち受けるバックグラウンド スレッドを開始します。
    /// </summary>
    /// <param name="slot">スロット番号。</param>
    /// <returns>取得できた場合は true。</returns>
    private static bool Acquire(int slot)
    {
        var mutex = new Mutex(false, @"Local\DesktopIniManager-dim-" + slot);
        try
        {
            if (!mutex.WaitOne(0))
            {
                mutex.Dispose();
                return false;
            }
        }
        catch (AbandonedMutexException)
        {
        }

        _slotMutex = mutex;
        Number = slot;
        _frontEvent = new EventWaitHandle(false, EventResetMode.AutoReset,
            @"Local\DesktopIniManager-dim-" + slot + "-front");
        var thread = new Thread(() =>
        {
            while (_frontEvent.WaitOne())
                FrontRequested?.Invoke();
        });
        thread.IsBackground = true;
        thread.Start();
        return true;
    }

    /// <summary>
    ///     別プロセスで動作中のスロットに前面表示を要求します。
    /// </summary>
    /// <param name="slot">スロット番号。</param>
    public static void Activate(int slot)
    {
        if (slot == Number) return;
        try
        {
            using (var signal = EventWaitHandle.OpenExisting(@"Local\DesktopIniManager-dim-" + slot + "-front"))
            {
                signal.Set();
            }
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
    }

    /// <summary>
    ///     確保しているスロットのミューテックスを解放します。
    /// </summary>
    public static void Release()
    {
        _slotMutex?.ReleaseMutex();
        _slotMutex?.Dispose();
        _slotMutex = null;
    }
}