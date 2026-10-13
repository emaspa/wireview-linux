namespace WireView2.Services;

/// <summary>
/// The running agent's control-panel token: a random value the agent mints at
/// start (a new one every run) and requires on every control-panel API request.
/// The agent writes it to <c>agent-panel.token</c> next to settings.json,
/// readable by its owner only, with the port it actually listens on as a second
/// line (the publish port, or a loopback fallback port while that one is taken),
/// so <c>wireview-agent --open</c> and <c>--status</c> (other processes of the same
/// user) can find it; the agent deletes the file on exit.
/// </summary>
public static class AgentPanelToken
{
    public static string FilePath =>
        Path.Combine(Path.GetDirectoryName(AppSettings.GetSettingsPath())!, "agent-panel.token");

    /// <summary>Writes the token and, once bound, the port the agent listens on.</summary>
    public static void Write(string token, int? port = null) =>
        AppSettings.WriteOwnerOnly(FilePath, token + "\n" + (port is { } p ? p.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" : ""));

    /// <summary>The token of the running agent, or null when there is no file.</summary>
    public static string? Read() => ReadBoth().Token;

    /// <summary>The port the running agent listens on, or null when unknown.</summary>
    public static int? ReadPort() => ReadBoth().Port;

    private static (string? Token, int? Port) ReadBoth()
    {
        try
        {
            string path = FilePath;
            if (!File.Exists(path)) return (null, null);
            var lines = File.ReadAllLines(path);
            string? token = lines.Length > 0 && lines[0].Trim().Length >= 32 ? lines[0].Trim() : null;
            int? port = lines.Length > 1 && int.TryParse(lines[1].Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int p) && p is > 0 and <= 65535 ? p : null;
            return (token, port);
        }
        catch { return (null, null); }
    }

    public static void Delete()
    {
        try { File.Delete(FilePath); } catch { }
    }
}
