namespace WireView2.Services;

/// <summary>
/// An XDG autostart entry (<c>~/.config/autostart/&lt;file&gt;.desktop</c>) that
/// starts the running executable at login. Used by the GUI's auto-start option
/// and by WireView Mini's <c>--install</c> on Linux.
/// </summary>
public sealed class XdgAutostart
{
    private readonly string _fileName;
    private readonly string _name;
    private readonly string _comment;
    private readonly string _command;

    /// <param name="fileName">The .desktop file name, e.g. "wireview2.desktop".</param>
    /// <param name="name">The entry's Name=.</param>
    /// <param name="comment">The entry's Comment=.</param>
    /// <param name="command">The launcher's name on PATH, e.g. "wireview-linux": the
    /// entry names it instead of the executable when that is in the Nix store.</param>
    public XdgAutostart(string fileName, string name, string comment, string command)
    {
        _fileName = fileName;
        _name = name;
        _comment = comment;
        _command = command;
    }

    /// <summary>Full path of the .desktop file (the autostart folder is created).</summary>
    public string DesktopFilePath
    {
        get
        {
            string autostartDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "autostart");
            Directory.CreateDirectory(autostartDir);
            return Path.Combine(autostartDir, _fileName);
        }
    }

    public bool IsEnabled => File.Exists(DesktopFilePath);

    /// <summary>Writes (enabled) or removes (disabled) the entry. Throws when the
    /// executable path cannot be resolved.</summary>
    public void Set(bool enabled)
    {
        string desktopFile = DesktopFilePath;

        if (enabled)
        {
            string? processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
            {
                throw new InvalidOperationException(
                    "Unable to resolve the executable path for auto-start.");
            }

            string exec = StableExecutable(processPath);
            string contents =
                "[Desktop Entry]\n" +
                $"Name={_name}\n" +
                $"Exec=\"{exec}\"\n" +
                "Type=Application\n" +
                "X-GNOME-Autostart-enabled=true\n" +
                "Terminal=false\n" +
                $"Comment={_comment}\n";

            File.WriteAllText(desktopFile, contents);
        }
        else
        {
            if (File.Exists(desktopFile))
            {
                File.Delete(desktopFile);
            }
        }
    }

    // A Nix package runs from /nix/store/<hash>-..., a path that is gone after the
    // next upgrade and garbage collection. There the entry names the launcher as
    // the profile has it (/run/current-system/sw/bin, ~/.nix-profile/bin and the
    // like), which follows upgrades. Everywhere else, the running executable.
    private string StableExecutable(string processPath)
    {
        const string store = "/nix/store/";
        if (!processPath.StartsWith(store, StringComparison.Ordinal)) return processPath;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            if (dir.StartsWith(store, StringComparison.Ordinal)) continue;
            string candidate = Path.Combine(dir, _command);
            if (File.Exists(candidate)) return candidate;
        }
        return processPath;
    }
}
