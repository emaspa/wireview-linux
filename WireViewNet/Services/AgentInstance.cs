namespace WireView2.Services;

/// <summary>
/// The headless agent's single-instance lock: a named mutex the agent holds
/// while it runs. The GUI checks it to leave fault alerts to the agent when the
/// agent runs, also where its port is not reachable (wireviewd holds it).
/// </summary>
public static class AgentInstance
{
    // Unix scopes an unprefixed name to the login session (setsid), so an agent
    // started by autostart and one started in a terminal would not see each
    // other; Global\ is machine wide there. On Windows the default (Local\) is
    // the logon session, which is what we want.
    private static string MutexName => OperatingSystem.IsWindows()
        ? "WireViewAgent_SingleInstance"
        : @"Global\WireViewAgent_SingleInstance";

    /// <summary>Takes the lock; null when another agent holds it. Keep the
    /// returned mutex alive (and owned) for the life of the process.</summary>
    public static Mutex? TryAcquire()
    {
        var mutex = new Mutex(true, MutexName, out bool createdNew);
        if (createdNew) return mutex;
        // An abandoned lock (the previous agent crashed) can be taken over.
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

    /// <summary>True when an agent holds the lock on this machine. Best-effort.</summary>
    public static bool IsRunning()
    {
        try
        {
            if (!Mutex.TryOpenExisting(MutexName, out var mutex)) return false;
            using (mutex)
            {
                try
                {
                    // Getting it means nobody holds it: a stale name, no agent.
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
