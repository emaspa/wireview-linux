namespace WireView2.Net
{
    /// <summary>A device's config as served by GET /config: the raw config bytes in
    /// the device's version layout (decode with WireViewPro2Device.DeserializeConfig).</summary>
    public sealed record ConfigSnapshot(string DeviceId, int Version, byte[] Data);
}
