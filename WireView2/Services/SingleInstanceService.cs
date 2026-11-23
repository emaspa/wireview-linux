using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace WireView2.Services;

public static class SingleInstanceService
{
    // A file lock on Linux (wireview2.lock next to settings.json), a named mutex
    // on Windows; see InstanceLock for why not a mutex everywhere.
    private static readonly InstanceLock Lock = new("wireview2.lock", "WireView2_SingleInstance");
    private static IDisposable? _held;

    /// <summary>Takes the lock and keeps it for the life of the process; false when
    /// another copy of the app already holds it.</summary>
    public static bool IsFirstInstance()
    {
        _held = Lock.TryAcquire();
        return _held != null;
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
