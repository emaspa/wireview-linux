namespace WireView2.Device
{
    /// <summary>
    /// Another process that owns the WireView's serial port and can lend it out
    /// for a while: wireviewd (through <see cref="HwmonDevice"/>) or the headless
    /// wireview-agent (through its LAN device on localhost). Used by
    /// <see cref="DirectSerialSession"/> for transfers that only exist on the
    /// direct serial protocol (device log, theme assets).
    /// </summary>
    public interface ISerialPortOwner
    {
        /// <summary>Short name for messages, e.g. "wireviewd".</summary>
        string SerialOwnerName { get; }

        /// <summary>Asks the owner to stop all serial I/O for <paramref name="seconds"/>.
        /// Re-sending before the deadline extends it; the owner takes the port
        /// back on its own once it passes.</summary>
        DaemonResult RequestSuspendSerial(int seconds);

        /// <summary>Hands the port back before the deadline.</summary>
        DaemonResult ResumeSerial();

        /// <summary>Marks the start of a direct session, so the owner-side device
        /// does not treat the pause in its data as a lost device.</summary>
        void BeginSerialSession();

        /// <summary>Marks the end of a direct session.</summary>
        void EndSerialSession();
    }
}
