using WireView2.Device;

namespace WireView2.Net
{
    /// <summary>Outcome of a write command, distinguishing the failure modes so the
    /// UI can show a precise message (local config vs remote rejection vs the host
    /// being unreachable, or wireviewd refusing or failing a local command).</summary>
    public enum CommandOutcome
    {
        Ok,
        NoLocalSecret,   // we have no secret to sign with — nothing was sent
        Unauthorized,    // 401 — the remote rejected the signature (secret mismatch)
        WritesDisabled,  // 403 — the remote has remote writes turned off / no secret
        Unreachable,     // connection refused / timeout / DNS failure
        HttpError,       // some other non-success HTTP status
        RemoteDenied,    // 403 "denied" — the relaying host's wireviewd refused (its user is not in the "wireview" group)
        DaemonDenied,       // wireviewd: privileged command, user not in the "wireview" group
        DaemonError,        // wireviewd could not carry out the command
        DaemonNotConnected, // wireviewd has no link to the device
        DaemonUnavailable,  // wireviewd could not be reached
    }

    /// <param name="Host">For <see cref="CommandOutcome.RemoteDenied"/>: the host whose
    /// wireviewd refused the relayed command.</param>
    public readonly record struct CommandResult(CommandOutcome Outcome, int StatusCode = 0, string? Host = null)
    {
        public bool Ok => Outcome == CommandOutcome.Ok;

        public static readonly CommandResult Success = new(CommandOutcome.Ok, 200);

        /// <summary>Maps a local wireviewd (hwmon) command result.</summary>
        public static CommandResult FromDaemon(DaemonResult result) => result switch
        {
            DaemonResult.Ok           => Success,
            DaemonResult.Denied       => new(CommandOutcome.DaemonDenied),
            DaemonResult.NotConnected => new(CommandOutcome.DaemonNotConnected),
            DaemonResult.Unavailable  => new(CommandOutcome.DaemonUnavailable),
            _                         => new(CommandOutcome.DaemonError),
        };

        /// <summary>Full status-line message for a failed command. The two group
        /// denials get the how-to-fix text instead of a terse reason.</summary>
        public string FailureText(string action) => Outcome switch
        {
            CommandOutcome.DaemonDenied => DaemonResults.DeniedMessage,
            CommandOutcome.RemoteDenied =>
                $"{action} failed: {Describe()}. On that host, run `sudo usermod -aG wireview <user>` " +
                "for the user running WireView there, then try the action again.",
            _ => $"{action} failed: {Describe()}.",
        };

        /// <summary>Short human-readable reason, for appending to a status line.</summary>
        public string Describe() => Outcome switch
        {
            CommandOutcome.Ok             => "OK",
            CommandOutcome.NoLocalSecret  => "no network secret set (add one in Settings)",
            CommandOutcome.Unauthorized   => "rejected by the remote host (secret mismatch)",
            CommandOutcome.WritesDisabled => "the remote host has remote writes disabled",
            CommandOutcome.Unreachable    => "the remote host is unreachable",
            CommandOutcome.HttpError      => StatusCode > 0 ? $"the remote host returned {StatusCode}" : "failed",
            CommandOutcome.RemoteDenied   => $"the host {Host ?? "relaying this device"} needs its user in the 'wireview' group",
            CommandOutcome.DaemonDenied       => DaemonResult.Denied.Describe(),
            CommandOutcome.DaemonError        => DaemonResult.Error.Describe(),
            CommandOutcome.DaemonNotConnected => DaemonResult.NotConnected.Describe(),
            CommandOutcome.DaemonUnavailable  => DaemonResult.Unavailable.Describe(),
            _                             => "failed",
        };
    }
}
