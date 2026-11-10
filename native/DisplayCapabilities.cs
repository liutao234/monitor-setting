using Native = MonitorSetting.DisplayNative;

namespace MonitorSetting;

public sealed partial class DisplayService
{
    private sealed record TopologyCapabilityCache(string Key, long CreatedAt, Dictionary<string, ControlInfo> Controls);
    private TopologyCapabilityCache? topologyCapabilities;

    public Dictionary<string, ControlInfo> GetModeCapabilities(List<DisplayInfo> displays, bool forceRefresh = false)
    {
        lock (gate)
        {
            var key = string.Join("|", displays.OrderBy(display => display.Id, StringComparer.Ordinal).Select(display =>
                $"{display.Id}:{display.Kind}:{display.GdiName}:{display.Active}:{display.Primary}" +
                $":{display.X}:{display.Y}:{display.Width}:{display.Height}:{display.Orientation}:{display.RefreshRate:R}" +
                $":{display.Controls.GetValueOrDefault("hdr")?.Value}"));
            var now = Environment.TickCount64;
            if (!forceRefresh && topologyCapabilities is { } cached && cached.Key == key && now - cached.CreatedAt < 30_000)
                return CloneCapabilities(cached.Controls);

            var flags = new Dictionary<string, uint>
            {
                ["extend"] = Native.TopologyExtend,
                ["mirror"] = Native.TopologyClone,
                ["builtin"] = Native.TopologyInternal,
                ["external"] = Native.TopologyExternal
            };
            var result = flags.ToDictionary(pair => pair.Key, pair =>
            {
                bool connected = pair.Key is "extend" or "mirror"
                    ? displays.Count >= 2 : displays.Any(display => display.Kind == pair.Key);
                bool supported = connected && SetConfig(null, null, Native.Validate | pair.Value) == 0;
                return new ControlInfo { Supported = supported, Reason = supported ? "" : connected ? "driver" : "inactive" };
            });
            // This cache only drives UI availability. ApplyTopology still validates
            // every requested topology immediately before sending an apply command.
            topologyCapabilities = new TopologyCapabilityCache(key, now, result);
            return CloneCapabilities(result);
        }
    }

    private static Dictionary<string, ControlInfo> CloneCapabilities(Dictionary<string, ControlInfo> source) =>
        source.ToDictionary(pair => pair.Key, pair => new ControlInfo
        {
            Supported = pair.Value.Supported, Reason = pair.Value.Reason
        });
}
