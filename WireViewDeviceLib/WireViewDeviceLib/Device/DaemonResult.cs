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
            _                         => "failed",
        };
    }
}
