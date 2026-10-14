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

    /// <param name="fileName">The .desktop file name, e.g. "wireview2.desktop".</param>
    /// <param name="name">The entry's Name=.</param>
    /// <param name="comment">The entry's Comment=.</param>
    public XdgAutostart(string fileName, string name, string comment)
    {
        _fileName = fileName;
        _name = name;
        _comment = comment;
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

            string contents =
                "[Desktop Entry]\n" +
                $"Name={_name}\n" +
                $"Exec=\"{processPath}\"\n" +
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
}
