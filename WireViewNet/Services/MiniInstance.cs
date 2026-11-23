namespace WireView2.Services;

/// <summary>
/// WireView Mini's single-instance lock (<see cref="InstanceLock"/>), held for as
/// long as WireView Mini runs. The GUI checks it to leave fault alerts to WireView
/// Mini when the mini runs, also where its port is not reachable (wireviewd holds it).
/// </summary>
public static class MiniInstance
{
    private static readonly InstanceLock Lock = new("wireview-mini.lock", "WireViewMini_SingleInstance");

    public static string LockFilePath => Lock.LockFilePath;

    /// <summary>Takes the lock; null when another mini holds it. Keep the returned
    /// holder alive for the life of the process and dispose it on the way out.</summary>
    public static IDisposable? TryAcquire() => Lock.TryAcquire();

    /// <summary>True when WireView Mini holds the lock (on Unix: one of this user's).
    /// Best-effort, never throws.</summary>
    public static bool IsRunning() => Lock.IsRunning();
}
