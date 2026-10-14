using System;

namespace WireView2.Device
{
    /// <summary>Outcome of a wireviewd socket command, as reported by
    /// <see cref="HwmonDevice"/>. The first four mirror the daemon's response
    /// status byte; <see cref="Unavailable"/> means no answer was received.</summary>
    public enum DaemonResult
    {
        Ok,           // RESP_OK
        Error,        // RESP_ERROR: the daemon could not carry out the command
        NotConnected, // RESP_NOT_CONNECTED: the daemon has no serial link to the device
        Denied,       // RESP_DENIED: privileged command, peer not root or in the "wireview" group
        Unavailable,  // no daemon connection, or the request failed in transport
        Busy,         // the port owner is flashing the firmware (WireView Mini); not a wireviewd status
    }

    public static class DaemonResults
    {
        /// <summary>Shown to the user when wireviewd refuses a privileged command.</summary>
        public const string DeniedMessage =
            "This action needs membership of the 'wireview' group: run " +
            "`sudo usermod -aG wireview $USER`, then try the action again.";

        /// <summary>Short reason for a failed command, for appending to a status line.</summary>
        public static string Describe(this DaemonResult result) => result switch
        {
            DaemonResult.Ok           => "OK",
            DaemonResult.Error        => "wireviewd reported an error",
            DaemonResult.NotConnected => "wireviewd is not connected to the device",
            DaemonResult.Denied       => "not permitted (not in the 'wireview' group)",
            DaemonResult.Unavailable  => "wireviewd is not reachable",
            DaemonResult.Busy         => "a firmware update is running",
            _                         => "failed",
        };

        /// <summary>Like <see cref="Describe"/> without naming wireviewd, for a
        /// result relayed by another port owner (the WireView Mini).</summary>
        public static string DescribeNeutral(this DaemonResult result) => result switch
        {
            DaemonResult.Ok           => "OK",
            DaemonResult.Error        => "it reported an error",
            DaemonResult.NotConnected => "it is not connected to the device",
            DaemonResult.Denied       => "not permitted",
            DaemonResult.Unavailable  => "it is not reachable",
            DaemonResult.Busy         => "it is updating the firmware",
            _                         => "failed",
        };
    }

    /// <summary>Thrown when wireviewd refuses a privileged command (status 3).
    /// Its own type so automatic callers can tell "not permitted" apart from a
    /// transient failure: retrying a denial cannot succeed until the user joins
    /// the group and restarts the session.</summary>
    public sealed class DaemonDeniedException : UnauthorizedAccessException
    {
        public DaemonDeniedException() : base(DaemonResults.DeniedMessage) { }
    }
}
