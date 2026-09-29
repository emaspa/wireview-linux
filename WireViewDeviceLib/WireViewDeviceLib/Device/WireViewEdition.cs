using System;
using System.Globalization;

namespace WireView2.Device
{
    /// <summary>Which WireView product a device is. The Noctua Edition (product 6)
    /// speaks the same protocol, sensor frame and config as the Pro II (product 5);
    /// only the product id and the display name differ.</summary>
    public enum WireViewEdition
    {
        Unknown,
        Pro2,
        Pro2Noctua,
    }

    /// <summary>Edition identification from the device's vendor/product ids, as
    /// reported by CMD_READ_VENDOR_DATA, wireviewd's GET_DEVICE_INFO or a LAN
    /// publisher's <c>hwRev</c> ("EF05", "EF06").</summary>
    public static class WireViewEditions
    {
        public const byte ThermalGrizzlyVendorId = 0xEF;
        public const byte Pro2ProductId = 0x05;
        public const byte Pro2NoctuaProductId = 0x06;

        public const string Pro2Name = "WireView Pro II";
        public const string Pro2NoctuaName = "WireView Pro II Noctua Edition";

        public static WireViewEdition FromIds(byte vendorId, byte productId)
        {
            if (vendorId != ThermalGrizzlyVendorId) return WireViewEdition.Unknown;
            return productId switch
            {
                Pro2ProductId => WireViewEdition.Pro2,
                Pro2NoctuaProductId => WireViewEdition.Pro2Noctua,
                _ => WireViewEdition.Unknown,
            };
        }

        /// <summary>"WireView Pro II" or "WireView Pro II Noctua Edition". An unknown
        /// edition reads as the Pro II: every device this app talks to is one.</summary>
        public static string DisplayName(WireViewEdition edition) =>
            edition == WireViewEdition.Pro2Noctua ? Pro2NoctuaName : Pro2Name;

        /// <summary>True for the products the Pro II protocol serves (5 and 6).
        /// WireView II and its Phanteks Edition (7, 8) are not supported yet.</summary>
        public static bool IsSupported(byte vendorId, byte productId) =>
            FromIds(vendorId, productId) != WireViewEdition.Unknown;

        /// <summary>Upstream's <c>{Vendor:X2}{Product:X2}</c> hardware revision.</summary>
        public static string FormatHardwareRevision(byte vendorId, byte productId) =>
            $"{vendorId:X2}{productId:X2}";

        /// <summary>Parses a four-hex-digit hardware revision ("EF06").</summary>
        public static bool TryParseHardwareRevision(string? hardwareRevision, out byte vendorId, out byte productId)
        {
            vendorId = 0;
            productId = 0;
            if (hardwareRevision == null || hardwareRevision.Length != 4)
                return false;
            return byte.TryParse(hardwareRevision.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vendorId)
                && byte.TryParse(hardwareRevision.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out productId);
        }

        /// <summary>Edition from a display name, for publishers that send no usable
        /// hardware revision (wireviewd 1.6.0 and older send an empty hwRev).</summary>
        public static WireViewEdition FromName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return WireViewEdition.Unknown;
            if (name.Contains("Noctua", StringComparison.OrdinalIgnoreCase)) return WireViewEdition.Pro2Noctua;
            if (name.Contains(Pro2Name, StringComparison.OrdinalIgnoreCase)) return WireViewEdition.Pro2;
            return WireViewEdition.Unknown;
        }

        /// <summary>Vendor/product ids of a known edition, (0, 0) for Unknown.</summary>
        public static (byte VendorId, byte ProductId) IdsOf(WireViewEdition edition) => edition switch
        {
            WireViewEdition.Pro2 => (ThermalGrizzlyVendorId, Pro2ProductId),
            WireViewEdition.Pro2Noctua => (ThermalGrizzlyVendorId, Pro2NoctuaProductId),
            _ => ((byte)0, (byte)0),
        };
    }
}
