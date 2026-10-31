using System;
using System.IO;
using System.Threading;

namespace DesktopIniManager.Services;

internal static class AppSlot
{
    private static Mutex slotMutex;
    private static EventWaitHandle _frontEvent;

    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopIniManager");

    public static int Number { get; private set; } = 1;
    public static string Name => "dim-" + Number;
    public static string Directory => Path.Combine(Root, Name);

    public static event Action FrontRequested;

    public static bool TryAcquire(string[] args, out string failure)
    {
        failure = null;
        var requested = RequestedSlot(args);
        if (requested > 0)
        {
            if (Acquire(requested)) return true;
            failure = "dim-" + requested + " は起動中です。";
            return false;
        }

        for (var slot = 1; slot <= 5; slot++)
            if (Acquire(slot))
                return true;
        failure = "dim-1 から dim-5 はすべて起動中です。";
        return false;
    }

    public static string StateText(int slot)
    {
        if (slot == Number) return "In Use";
        return IsHeld(slot) ? "Open" : "Free";
    }

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

        slotMutex = mutex;
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
}