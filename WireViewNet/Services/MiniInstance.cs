namespace WireView2.Services;

/// <summary>
/// WireView Mini's single-instance lock, held for as long as WireView Mini runs.
/// The GUI checks it to leave fault alerts to WireView Mini when the mini runs,
/// also where its port is not reachable (wireviewd holds it).
/// <para>
/// On Windows it is a named mutex (Local\, the logon session). On Linux and other
/// Unix it is an advisory lock (flock) on <c>wireview-mini.lock</c> next to
/// settings.json: the NativeAOT build every package ships does not honour named
/// mutexes there (a second mini started a full second instance), while a file
/// lock works in any runtime, spans login sessions (autostart and a terminal see
/// the same file) and is dropped by the kernel when the process dies, so a crashed
/// mini never leaves a stale lock. Each user has their own file and lock.
/// .NET takes the lock for <see cref="FileShare.None"/>; setting
/// DOTNET_SYSTEM_IO_DISABLEFILELOCKING turns that off and with it this check.
/// </para>
/// </summary>
public static class MiniInstance
{
    // Windows only. The default (Local\) is the logon session, which is what we want.
    private const string MutexName = "WireViewMini_SingleInstance";

    public static string LockFilePath =>
        Path.Combine(Path.GetDirectoryName(AppSettings.GetSettingsPath())!, "wireview-mini.lock");

    /// <summary>Takes the lock; null when another mini holds it. Keep the returned
    /// holder alive for the life of the process and dispose it on the way out.</summary>
    public static IDisposable? TryAcquire()
    {
        if (OperatingSystem.IsWindows())
            return TryAcquireMutex() is { } mutex ? new LockHolder(mutex) : null;

        string path = LockFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        // A few tries: IsRunning (the GUI, --status) holds the lock for a moment
        // while it probes, and a start must not lose to that.
        for (int attempt = 0; ; attempt++)
        {
            FileStream stream;
            try
            {
                stream = new FileStream(path, options);
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(50);
                continue;
            }
            catch (IOException)
            {
                return null;
            }
            WritePid(stream);
            return new LockHolder(stream);
        }
    }

    /// <summary>True when WireView Mini holds the lock (on Unix: one of this user's).
    /// Best-effort, never throws.</summary>
    public static bool IsRunning()
    {
        if (OperatingSystem.IsWindows()) return IsMutexHeld();
        try
        {
            string path = LockFilePath;
            if (!File.Exists(path)) return false;
            // Getting the lock means nobody holds it: no mini. Let go at once.
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException) { return true; } // the lock is held
        catch { return false; }
    }

    // The pid is for whoever looks at the file; nothing reads it back.
    private static void WritePid(FileStream stream)
    {
        try
        {
            stream.SetLength(0);
            byte[] text = System.Text.Encoding.ASCII.GetBytes(
                Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
            stream.Write(text);
            stream.Flush();
        }
        catch { }
    }

    private static Mutex? TryAcquireMutex()
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

    private static bool IsMutexHeld()
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

    /// <summary>Holds the lock: the owned mutex (released on dispose, on the thread
    /// that took it) or the open lock file (closing it drops the flock).</summary>
    private sealed class LockHolder : IDisposable
    {
        private Mutex? _mutex;
        private FileStream? _stream;

        public LockHolder(Mutex mutex) => _mutex = mutex;
        public LockHolder(FileStream stream) => _stream = stream;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _mutex, null) is { } mutex)
            {
                try { mutex.ReleaseMutex(); } catch { }
                mutex.Dispose();
            }
            Interlocked.Exchange(ref _stream, null)?.Dispose();
        }
    }
}
