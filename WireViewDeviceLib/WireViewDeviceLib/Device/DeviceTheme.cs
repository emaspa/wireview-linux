using System.Globalization;

namespace WireView2.Device
{
    /// <summary>The display theme fields of a config layout 2 device
    /// (<see cref="WireViewPro2Device.UiConfigStructV2"/>): the four ARGB colours,
    /// display inversion and the background and fan bitmap slot ids, as the
    /// device stores them.</summary>
    public readonly record struct DeviceThemeValues(
        uint PrimaryColor, uint SecondaryColor, uint HighlightColor, uint BackgroundColor,
        bool DisplayInversion, byte BackgroundBitmapId, byte FanBitmapId)
    {
        public static DeviceThemeValues From(WireViewPro2Device.DeviceConfigStructV3 cfg) => new(
            cfg.Ui.PrimaryColor, cfg.Ui.SecondaryColor, cfg.Ui.HighlightColor, cfg.Ui.BackgroundColor,
            cfg.Ui.DisplayInversion == WireViewPro2Device.DISPLAY_INVERSION.DISPLAY_INVERSION_ON,
            cfg.Ui.BackgroundBitmapId, cfg.Ui.FanBitmapId);

        /// <summary><paramref name="cfg"/> with the fields that differ from
        /// <paramref name="was"/> set to these values (every other field as it is),
        /// like <see cref="DeviceConfigEditor.ApplyChanges"/>: a value nobody edited
        /// keeps what the device holds.</summary>
        public WireViewPro2Device.DeviceConfigStructV3 ApplyTo(WireViewPro2Device.DeviceConfigStructV3 cfg, DeviceThemeValues was)
        {
            if (PrimaryColor != was.PrimaryColor) cfg.Ui.PrimaryColor = PrimaryColor;
            if (SecondaryColor != was.SecondaryColor) cfg.Ui.SecondaryColor = SecondaryColor;
            if (HighlightColor != was.HighlightColor) cfg.Ui.HighlightColor = HighlightColor;
            if (BackgroundColor != was.BackgroundColor) cfg.Ui.BackgroundColor = BackgroundColor;
            if (DisplayInversion != was.DisplayInversion)
                cfg.Ui.DisplayInversion = DisplayInversion
                    ? WireViewPro2Device.DISPLAY_INVERSION.DISPLAY_INVERSION_ON
                    : WireViewPro2Device.DISPLAY_INVERSION.DISPLAY_INVERSION_OFF;
            if (BackgroundBitmapId != was.BackgroundBitmapId) cfg.Ui.BackgroundBitmapId = BackgroundBitmapId;
            if (FanBitmapId != was.FanBitmapId) cfg.Ui.FanBitmapId = FanBitmapId;
            return cfg;
        }

        /// <summary>The bytes to write for layout <paramref name="version"/> (2):
        /// <paramref name="current"/> (the device's bytes now) with the fields that
        /// differ from <paramref name="was"/> patched in through
        /// <see cref="WireViewPro2Device.MergeConfigChanges"/>; every other byte,
        /// the CRC field included, stays as the device has it.</summary>
        public byte[] EncodeOnto(int version, byte[] current, DeviceThemeValues was)
        {
            if (version < 2) throw new DeviceConfigException("theme colours need config layout 2 (firmware with the colour theme)");
            var edited = ApplyTo(WireViewPro2Device.DeserializeConfig(version, current), was);
            return WireViewPro2Device.MergeConfigChanges(version, current, edited);
        }
    }

    /// <summary>A bitmap slot id and its name. A class, not a tuple: WireView Mini's
    /// NativeAOT build shares the generic code of reference types.</summary>
    public sealed record DeviceThemeSlot(byte Value, string Label);

    /// <summary>One of the app's theme presets (the Device page's Theme list).</summary>
    /// <param name="Name">The app's preset name (UiThemePreset: ThemeTg1 ... ThemeNoctua).</param>
    /// <param name="Label">How WireView Mini's panel names it.</param>
    public sealed record DeviceThemePreset(
        string Name, string Label,
        uint PrimaryColor, uint SecondaryColor, uint HighlightColor, uint BackgroundColor,
        WireViewPro2Device.THEME_BACKGROUND BackgroundBitmap, WireViewPro2Device.THEME_FAN FanBitmap,
        bool DisplayInversion)
    {
        public bool Matches(DeviceThemeValues v) =>
            v.PrimaryColor == PrimaryColor && v.SecondaryColor == SecondaryColor
            && v.HighlightColor == HighlightColor && v.BackgroundColor == BackgroundColor
            && v.BackgroundBitmapId == (byte)BackgroundBitmap && v.FanBitmapId == (byte)FanBitmap
            && v.DisplayInversion == DisplayInversion;
    }

    /// <summary>
    /// The app's theme presets, shared by its Device page and WireView Mini's
    /// control panel, plus the slot names and the helpers both use. "Custom" is
    /// not a preset: it is what <see cref="Detect"/> answers when no preset
    /// matches every field.
    /// </summary>
    public static class DeviceThemePresets
    {
        public const string Custom = "Custom";

        private const WireViewPro2Device.THEME_BACKGROUND Orange = WireViewPro2Device.THEME_BACKGROUND.ThermalGrizzlyOrange;
        private const WireViewPro2Device.THEME_BACKGROUND Dark = WireViewPro2Device.THEME_BACKGROUND.ThermalGrizzlyDark;
        private const WireViewPro2Device.THEME_BACKGROUND Plain = WireViewPro2Device.THEME_BACKGROUND.Disabled;
        private const WireViewPro2Device.THEME_FAN FanOrange = WireViewPro2Device.THEME_FAN.ThermalGrizzlyOrange;
        private const WireViewPro2Device.THEME_FAN FanDark = WireViewPro2Device.THEME_FAN.ThermalGrizzlyDark;
        private const WireViewPro2Device.THEME_FAN FanPlain = WireViewPro2Device.THEME_FAN.ThermalGrizzlyBlackWhite;

        /// <summary>In the app's order. The colours are the firmware's TG1/TG2/TG3
        /// defaults; TG 4 to 6 are the same three inverted.</summary>
        public static readonly IReadOnlyList<DeviceThemePreset> All =
        [
            new("ThemeTg1", "TG 1 (orange)", 0xFFFFFFFF, 0xFF646464, 0xFFE64121, 0xFF000000, Orange, FanOrange, false),
            new("ThemeTg2", "TG 2 (dark)", 0xFFFFFFFF, 0xFF646464, 0xFFBEBEBE, 0xFF000000, Dark, FanDark, false),
            new("ThemeTg3", "TG 3 (plain)", 0xFF969696, 0xFF505050, 0xFFFFFFFF, 0xFF000000, Plain, FanPlain, false),
            new("ThemeTg4", "TG 4 (orange, inverted)", 0xFFFFFFFF, 0xFF646464, 0xFFE64121, 0xFF000000, Orange, FanOrange, true),
            new("ThemeTg5", "TG 5 (dark, inverted)", 0xFFFFFFFF, 0xFF646464, 0xFFBEBEBE, 0xFF000000, Dark, FanDark, true),
            new("ThemeTg6", "TG 6 (plain, inverted)", 0xFF969696, 0xFF505050, 0xFFFFFFFF, 0xFF000000, Plain, FanPlain, true),
            // The Noctua Edition's firmware defaults (20260902_0741): Thermal Grizzly's
            // Dark artwork with Noctua colors. Any Pro II can show it.
            new("ThemeNoctua", "Noctua", 0xFFD1D3D5, 0xFFE7CEB4, 0xFF653025, 0xFF000000, Dark, FanDark, false),
        ];

        /// <summary>What the app uses for a name it does not know (its Custom entry):
        /// TG 3's values.</summary>
        public static DeviceThemePreset Fallback { get; } = All[2] with { Name = Custom, Label = Custom };

        public static DeviceThemePreset? Find(string? name) =>
            All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

        /// <summary>The preset the app's Theme list shows for these values: the
        /// first one that matches every field, else <see cref="Custom"/>.</summary>
        public static string Detect(DeviceThemeValues values) =>
            All.FirstOrDefault(p => p.Matches(values))?.Name ?? Custom;

        /// <summary>The background slots (THEME_BACKGROUND values).</summary>
        public static readonly IReadOnlyList<DeviceThemeSlot> Backgrounds =
        [
            new((byte)Orange, "Thermal Grizzly orange"),
            new((byte)Dark, "Thermal Grizzly dark"),
            new((byte)Plain, "None (background color)"),
        ];

        /// <summary>The fan animation slots (THEME_FAN values).</summary>
        public static readonly IReadOnlyList<DeviceThemeSlot> Fans =
        [
            new((byte)FanOrange, "Thermal Grizzly orange"),
            new((byte)FanDark, "Thermal Grizzly dark"),
            new((byte)FanPlain, "Black and white"),
        ];

        /// <summary>A background id as the app's editor reads it: an id it does
        /// not know reads as Disabled.</summary>
        public static WireViewPro2Device.THEME_BACKGROUND KnownBackground(byte id) =>
            id is (byte)Orange or (byte)Dark or (byte)Plain ? (WireViewPro2Device.THEME_BACKGROUND)id : Plain;

        /// <summary>A fan id as the app's theme file load reads it: an id it does
        /// not know becomes the background's fan.</summary>
        public static WireViewPro2Device.THEME_FAN KnownFan(byte id, WireViewPro2Device.THEME_BACKGROUND background) =>
            id is (byte)FanOrange or (byte)FanDark or (byte)FanPlain
                ? (WireViewPro2Device.THEME_FAN)id
                : DeviceConfigEditor.FanBitmapFor(background);

        /// <summary>"#rrggbb", or "#aarrggbb" when the alpha is not 255.</summary>
        public static string FormatColor(uint argb) =>
            (argb >> 24) == 0xFF
                ? "#" + (argb & 0xFFFFFF).ToString("x6", CultureInfo.InvariantCulture)
                : "#" + argb.ToString("x8", CultureInfo.InvariantCulture);

        /// <summary>Parses "#rrggbb" (alpha 255) or "#aarrggbb"; false otherwise.</summary>
        public static bool TryParseColor(string? text, out uint argb)
        {
            argb = 0;
            if (text is not { Length: 7 or 9 } || text[0] != '#') return false;
            if (!uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint v))
                return false;
            argb = text.Length == 7 ? 0xFF000000u | v : v;
            return true;
        }
    }
}
