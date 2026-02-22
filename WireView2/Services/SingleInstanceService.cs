using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace WireView2.Services;

public static class SingleInstanceService
{
    private const string MutexName = "WireView2_SingleInstance";
    private static Mutex? _mutex;
    private static readonly string _lockFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PowerMonitor", ".wireview2.lock");

    public static bool IsFirstInstance()
    {
        // Try a named Mutex first (works on both platforms for basic exclusion)
        bool createdNew;
        _mutex = new Mutex(true, MutexName, out createdNew);

        if (createdNew)
        {
            return true;
        }

        // Another instance exists
        _mutex.Dispose();
        _mutex = null;
        return false;
    }

    public static EventWaitHandle CreateOrOpenActivationEvent()
    {
        // On Linux, named EventWaitHandle is not supported.
        // Use a file-based signaling mechanism via polling.
        // For simplicity, return a local (unnamed) event and use file-based signaling.
        if (OperatingSystem.IsLinux())
        {
            return new EventWaitHandle(false, EventResetMode.AutoReset);
        }

        return new EventWaitHandle(false, EventResetMode.AutoReset, "WireView2_SingleInstance_ActivationEvent");
    }
}
