namespace WireView2.Device
{
    /// <summary>How a device is told apart from other WireViews in labels.</summary>
    public static class DeviceLabels
    {
        /// <summary>The last four characters of the device's USB serial number (the
        /// one /dev/serial/by-id and wireviewctl show), from its chip UID; the end of
        /// the UID itself when it is not one.</summary>
        public static string ShortId(string? uid)
        {
            if (string.IsNullOrEmpty(uid)) return "";
            string id = Stm32PortFinder.UsbSerialFromUid(uid) ?? uid;
            return id.Length > 4 ? id[^4..] : id;
        }

        /// <summary>"nickname · 4E46", or the device's model name (without its
        /// "(hwmon)" style transport suffix) when it has no nickname.</summary>
        public static string Label(IWireViewDevice device, string? nickname)
        {
            string name = nickname ?? BaseName(device.DeviceName);
            return WithShortId(string.IsNullOrWhiteSpace(name) ? "WireView" : name, device.UniqueId);
        }

        /// <summary>The device name without its own " (hwmon + daemon)" suffix.</summary>
        public static string BaseName(string deviceName)
        {
            int paren = deviceName.IndexOf(" (", StringComparison.Ordinal);
            return paren > 0 ? deviceName[..paren] : deviceName;
        }

        /// <summary>"name · 4E46": the name with the short id, or the name alone
        /// when the UID is unknown.</summary>
        public static string WithShortId(string name, string? uid)
        {
            string id = ShortId(uid);
            return id.Length == 0 ? name : $"{name} · {id}";
        }
    }
}
