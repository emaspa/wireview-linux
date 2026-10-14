namespace WireView2.Services;

/// <summary>
/// WireView Mini's single-instance lock: a named mutex WireView Mini holds
/// while it runs. The GUI checks it to leave fault alerts to WireView Mini when the
/// mini runs, also where its port is not reachable (wireviewd holds it).
/// </summary>
public static class MiniInstance
{
    // Unix scopes an unprefixed name to the login session (setsid), so a WireView Mini
    // started by autostart and one started in a terminal would not see each
    // other; Global\ is machine wide there. On Windows the default (Local\) is
    // the logon session, which is what we want.
    private static string MutexName => OperatingSystem.IsWindows()
        ? "WireViewMini_SingleInstance"
        : @"Global\WireViewMini_SingleInstance";

    /// <summary>Takes the lock; null when another mini holds it. Keep the
    /// returned mutex alive (and owned) for the life of the process.</summary>
    public static Mutex? TryAcquire()
    {
        var mutex = new Mutex(true, MutexName, out bool createdNew);
        if (createdNew) return mutex;
        // An abandoned lock (the previous mini crashed) can be taken over.
        try
        {
            if (mutex.WaitOne(0)) return mutex;
        }
        catch (AbandonedMutexException)
        {
            return mutex;
        }
        mutex.Dispose();
        return null;
    }

    /// <summary>True when WireView Mini holds the lock on this machine. Best-effort.</summary>
    public static bool IsRunning()
    {
        try
        {
            if (!Mutex.TryOpenExisting(MutexName, out var mutex)) return false;
            using (mutex)
            {
                try
                {
                    // Getting it means nobody holds it: a stale name, no mini.
                    if (mutex.WaitOne(0))
                    {
                        mutex.ReleaseMutex();
                        return false;
                    }
                    return true;
                }
                catch (AbandonedMutexException)
                {
                    mutex.ReleaseMutex();
                    return false;
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            return true; // exists, held by another user
        }
        catch
        {
            return false;
        }
    }
}
