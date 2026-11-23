namespace WireView2.Services;

/// <summary>
/// A per-user single-instance lock, held for as long as the process runs.
/// <para>
/// On Windows it is a named mutex (Local\, the logon session). On Linux and other
/// Unix it is an advisory lock (flock) on a file next to settings.json. A named
/// mutex is no use there: the NativeAOT build does not honour it, and under the
/// JIT it is scoped to the Unix session, so a copy started from a terminal never
/// sees one started from the desktop. A file lock works in any runtime, spans
/// sessions (autostart and a terminal see the same file) and is dropped by the
/// kernel when the process dies, so a crash never leaves a stale lock. Each user
/// has their own file and lock. .NET takes the lock for <see cref="FileShare.None"/>;
/// setting DOTNET_SYSTEM_IO_DISABLEFILELOCKING turns that off and with it this check.
/// </para>
/// </summary>
public sealed class InstanceLock
{
    // Windows only. The default (Local\) is the logon session, which is what we want.
    private readonly string _mutexName;
    private readonly string _fileName;

    /// <param name="fileName">The lock file next to settings.json (Unix).</param>
    /// <param name="mutexName">The named mutex (Windows).</param>
    public InstanceLock(string fileName, string mutexName)
    {
        _fileName = fileName;
        _mutexName = mutexName;
    }

    public string LockFilePath =>
        Path.Combine(Path.GetDirectoryName(AppSettings.GetSettingsPath())!, _fileName);

    /// <summary>Takes the lock; null when another instance holds it. Keep the returned
    /// holder alive for the life of the process and dispose it on the way out.</summary>
    public IDisposable? TryAcquire()
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
        // while it probes, and and a start must not lose to that.
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

    /// <summary>True when another process holds the lock (on Unix: one of this user's).
    /// Best-effort, never throws.</summary>
    public bool IsRunning()
    {
        if (OperatingSystem.IsWindows()) return IsMutexHeld();
        try
        {
            string path = LockFilePath;
            if (!File.Exists(path)) return false;
            // Getting the lock means nobody holds it. Let go at once.
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

    private Mutex? TryAcquireMutex()
    {
        var mutex = new Mutex(true, _mutexName, out bool createdNew);
        if (createdNew) return mutex;
        // An abandoned lock (the previous instance crashed) can be taken over.
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

    private bool IsMutexHeld()
    {
        try
        {
            if (!Mutex.TryOpenExisting(_mutexName, out var mutex)) return false;
            using (mutex)
            {
                try
                {
                    // Getting it means nobody holds it: a stale name.
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
