using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Native = MonitorSetting.DisplayNative;

namespace MonitorSetting;

public sealed class DisplaySnapshot
{
    public int Version { get; init; } = 1;
    public byte[] Paths { get; init; } = [];
    public byte[] Modes { get; init; } = [];
    public bool VirtualRefreshRates { get; init; }
    public Dictionary<string, bool> HdrStates { get; init; } = new(StringComparer.Ordinal);
}

public sealed partial class DisplayService
{
    private readonly object gate = new();
    private readonly Dictionary<string, ModeCacheEntry> advertisedModes = new(StringComparer.Ordinal);
    private const long ModeCacheLifetimeMilliseconds = 120_000;
    private sealed record ModeCacheEntry(long CreatedAt, List<DisplayMode> Modes);

    private sealed record Configuration(Native.Path[] Paths, Native.Mode[] Modes, uint QueryFlags);
    private sealed record AttachedDisplay(DisplayInfo Info, Native.Path Path, Native.DevMode CurrentMode);

    public List<DisplayInfo> Enumerate(bool includeModes = true)
    {
        lock (gate)
        {
            // Positions, active targets and current settings always come from Windows.
            // Only the large, advertised mode lists are reused between UI reads.
            if (advertisedModes.Count > 64)
                advertisedModes.Clear();
            if (dynamicModes.Count > 64)
                dynamicModes.Clear();
            return ReadAttachedDisplaysCached(Query(Native.AllPaths), readModes: includeModes,
                    advertisedModeCache: includeModes ? advertisedModes : null,
                    dynamicModeCache: includeModes ? dynamicModes : null)
                .Select(display => display.Info)
                .OrderByDescending(display => display.Primary)
                .ThenBy(display => display.Kind, StringComparer.Ordinal)
                .ThenBy(display => display.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }

    public void InvalidateCaches()
    {
        lock (gate)
        {
            advertisedModes.Clear();
            dynamicModes.Clear();
            topologyCapabilities = null;
        }
    }

    public DisplaySnapshot Capture()
    {
        lock (gate)
        {
            var config = Query(Native.ActivePaths);
            if (config.Paths.Length == 0)
                throw new InvalidOperationException("Windows reports no active displays to back up.");

            var hdrStates = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var display in ReadAttachedDisplays(config, readModes: false))
            {
                var (control, _) = ReadHdr(display.Path);
                if (control.Supported && control.Value.HasValue)
                    hdrStates[display.Info.Id] = control.Value.Value != 0;
            }

            return new DisplaySnapshot
            {
                Paths = MemoryMarshal.AsBytes(config.Paths.AsSpan()).ToArray(),
                Modes = MemoryMarshal.AsBytes(config.Modes.AsSpan()).ToArray(),
                VirtualRefreshRates = (config.QueryFlags & Native.QueryVirtualRefresh) != 0,
                HdrStates = hdrStates
            };
        }
    }

    public void Restore(DisplaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (gate)
        {
            var pathSize = Marshal.SizeOf<Native.Path>();
            var modeSize = Marshal.SizeOf<Native.Mode>();
            if (snapshot.Version != 1 || snapshot.Paths is not { Length: > 0 } ||
                snapshot.Modes is null || snapshot.Paths.Length % pathSize != 0 ||
                snapshot.Modes.Length % modeSize != 0 || snapshot.Paths.Length / pathSize > 1024 ||
                snapshot.Modes.Length / modeSize > 4096)
                throw new ArgumentException("The display rollback snapshot is invalid.", nameof(snapshot));

            var paths = MemoryMarshal.Cast<byte, Native.Path>(snapshot.Paths.AsSpan()).ToArray();
            var modes = MemoryMarshal.Cast<byte, Native.Mode>(snapshot.Modes.AsSpan()).ToArray();
            if (paths.Any(path => !path.IsActive))
                throw new ArgumentException("The rollback snapshot contains an inactive path.", nameof(snapshot));

            var flags = Native.SuppliedConfig | Native.SetVirtualModes;
            if (snapshot.VirtualRefreshRates)
                flags |= Native.SetVirtualRefresh;

            var test = SetConfig(paths, modes, flags | Native.Validate);
            if (test == 0)
            {
                CheckWin32(SetConfig(paths, modes, flags | Native.Apply | Native.SaveToDatabase | Native.NoOptimization),
                    "Restore the original display configuration");
            }
            else
            {
                // A missing monitor or a restarted adapter can invalidate a previously valid snapshot.
                // Let Windows recover the last working configuration for the displays still connected.
                var attached = Query(Native.AllPaths).Paths
                    .Where(path => path.Target.Available != 0 || path.IsActive)
                    .Select(path => (path.Target.Adapter, path.Target.Id))
                    .ToHashSet();
                if (paths.All(path => attached.Contains((path.Target.Adapter, path.Target.Id))))
                    CheckWin32(test, "Validate the original display configuration");

                CheckWin32(SetConfig(null, null, Native.Apply | Native.DatabaseCurrent),
                    "Recover a working configuration after a display disconnected");
            }

            if (snapshot.HdrStates is { Count: > 0 })
            {
                foreach (var display in ReadAttachedDisplays(Query(Native.ActivePaths), readModes: false))
                    if (snapshot.HdrStates.TryGetValue(display.Info.Id, out var enabled))
                        SetHdr(display, enabled);
            }
        }
    }

    public void SetHdr(string id, bool enabled)
    {
        lock (gate)
        {
            var display = Find(ReadAttachedDisplays(Query(Native.ActivePaths), readModes: false), id);
            SetHdr(display, enabled);
        }
    }

    private static void SetHdr(AttachedDisplay display, bool enabled)
    {
        var (control, setter) = ReadHdr(display.Path);
        if (control.Value == (enabled ? 1 : 0))
            return;
        if (!control.Supported)
            throw new InvalidOperationException($"Windows cannot change HDR for {display.Info.Name} ({control.Reason}).");

        var state = new Native.ColorState
        {
            Header = Native.Header<Native.ColorState>(setter, display.Path.Target.Adapter, display.Path.Target.Id),
            Flags = enabled ? 1u : 0u
        };
        CheckWin32(Native.DisplayConfigSetDeviceInfo(ref state), $"Change HDR for {display.Info.Name}");
    }

    private static Configuration Query(uint flags)
    {
        flags |= Native.QueryVirtualModes;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            flags |= Native.QueryVirtualRefresh;

        for (var attempt = 0; attempt < 6; attempt++)
        {
            var result = Native.GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount);
            if (result == 0)
            {
                var paths = new Native.Path[pathCount];
                var modes = new Native.Mode[modeCount];
                result = Native.QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (result == 0)
                {
                    Array.Resize(ref paths, (int)pathCount);
                    Array.Resize(ref modes, (int)modeCount);
                    return new Configuration(paths, modes, flags);
                }
            }

            if (result == Native.InsufficientBuffer)
                continue;
            if (result == Native.InvalidParameter && (flags & Native.QueryVirtualRefresh) != 0)
            {
                flags &= ~Native.QueryVirtualRefresh;
                continue;
            }

            CheckWin32(result, "Read Windows display configuration");
        }

        throw new InvalidOperationException("The display configuration changed repeatedly while Windows was reading it.");
    }

    private static List<AttachedDisplay> ReadAttachedDisplays(Configuration config, bool readModes) =>
        ReadAttachedDisplaysCached(config, readModes, null);

    private static List<AttachedDisplay> ReadAttachedDisplaysCached(Configuration config, bool readModes,
        Dictionary<string, ModeCacheEntry>? advertisedModeCache,
        Dictionary<string, DynamicModeCacheEntry>? dynamicModeCache = null)
    {
        var result = new List<AttachedDisplay>();
        var seenTargets = new HashSet<(Native.Luid Adapter, uint Id)>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var modeCache = new Dictionary<string, List<DisplayMode>>(StringComparer.OrdinalIgnoreCase);
        var primaryFound = false;

        foreach (var path in config.Paths.OrderByDescending(path => path.IsActive))
        {
            if (!path.IsActive && path.Target.Available == 0 ||
                !seenTargets.Add((path.Target.Adapter, path.Target.Id)))
                continue;

            var targetName = new Native.TargetName
            {
                Header = Native.Header<Native.TargetName>(Native.GetTargetName, path.Target.Adapter, path.Target.Id)
            };
            var targetNamed = Native.DisplayConfigGetDeviceInfo(ref targetName) == 0;
            var devicePath = targetNamed ? targetName.DevicePath ?? "" : "";
            var id = StableId(path, devicePath);
            if (!seenIds.Add(id))
            {
                id = StableId(path, "");
                seenIds.Add(id);
            }

            var technology = targetNamed ? targetName.Technology : path.Target.Technology;
            var builtin = technology is 6 or 11 or 13 or 0x80000000;
            var name = targetNamed && !string.IsNullOrWhiteSpace(targetName.FriendlyName)
                ? targetName.FriendlyName.Trim() : builtin ? "Built-in display" : "External display";
            var gdi = path.IsActive ? GetGdiName(path.Source) : "";
            var current = Native.DevMode.Create();
            var hasCurrent = gdi.Length != 0 && Native.EnumDisplaySettingsEx(gdi, Native.CurrentSettings, ref current, 0);
            var source = GetMode(config.Modes, FindModeIndex(config, path, source: true), 1)?.Source;
            var target = GetMode(config.Modes, FindModeIndex(config, path, source: false), 2)?.Signal;
            var orientation = path.Target.Rotation is >= 1 and <= 4
                ? (int)(path.Target.Rotation - 1) * 90
                : hasCurrent ? ToDegrees(current.Orientation) : 0;
            var width = path.IsActive ? (int)(source?.Width ?? current.Width) : 0;
            var height = path.IsActive ? (int)(source?.Height ?? current.Height) : 0;
            var x = path.IsActive ? source?.X ?? current.X : 0;
            var y = path.IsActive ? source?.Y ?? current.Y : 0;
            var refresh = path.IsActive ? path.Target.RefreshRate.Hertz : 0;
            if (path.IsActive && refresh <= 1)
                refresh = target?.VerticalSync.Hertz ?? current.Frequency;

            var hdr = readModes ? ReadHdr(path).Control : null;

            List<DisplayMode> modes = [];
            if (readModes)
            {
                if (path.IsActive && hasCurrent)
                {
                    if (!modeCache.TryGetValue(gdi, out var sourceModes))
                    {
                        // The same GDI source may be reassigned after a topology switch.
                        // Bind cached modes to its physical target, adapter and current mode/HDR state.
                        var cacheKey = $"{id}|{gdi}|{path.Source.Adapter.High}:{path.Source.Adapter.Low}:{path.Source.Id}" +
                            $"|{orientation}|{width}:{height}:{refresh:R}|{hdr?.Supported}:{hdr?.Value}";
                        var now = Environment.TickCount64;
                        if (advertisedModeCache is not null && advertisedModeCache.TryGetValue(cacheKey, out var cached) &&
                            now - cached.CreatedAt < ModeCacheLifetimeMilliseconds)
                        {
                            sourceModes = cached.Modes;
                        }
                        else
                        {
                            sourceModes = EnumerateDeviceModes(gdi).Select(ToDisplayMode)
                                .Where(mode => mode.Width > 0 && mode.Height > 0 && mode.RefreshRate > 1)
                                .DistinctBy(ModeKey).ToList();
                            if (advertisedModeCache is not null)
                                advertisedModeCache[cacheKey] = new ModeCacheEntry(now, sourceModes);
                        }
                        modeCache.Add(gdi, sourceModes);
                    }
                    modes.AddRange(sourceModes.Select(CloneMode));
                }

                if (path.IsActive && width > 0 && height > 0 && refresh > 1)
                {
                    var reported = new DisplayMode
                    {
                        Width = width, Height = height, RefreshRate = RoundHertz(refresh), Orientation = orientation
                    };
                    if (!modes.Any(mode => ModeKey(mode) == ModeKey(reported)))
                        modes.Add(reported);
                }

                if (!path.IsActive)
                {
                    var preferred = new Native.PreferredMode
                    {
                        Header = Native.Header<Native.PreferredMode>(Native.GetTargetPreferredMode, path.Target.Adapter, path.Target.Id)
                    };
                    if (Native.DisplayConfigGetDeviceInfo(ref preferred) == 0 && preferred.Width != 0 && preferred.Height != 0)
                    {
                        width = (int)preferred.Width;
                        height = (int)preferred.Height;
                        refresh = preferred.Signal.VerticalSync.Hertz;
                        modes.Add(new DisplayMode { Width = width, Height = height, RefreshRate = RoundHertz(refresh), Orientation = 0 });
                    }
                }
            }

            var primary = path.IsActive && !primaryFound && x == 0 && y == 0;
            primaryFound |= primary;
            var controls = new Dictionary<string, ControlInfo>(StringComparer.Ordinal);
            if (readModes)
            {
                var modeReason = !path.IsActive ? "inactive" : !hasCurrent ? "driver" : "";
                controls["resolution"] = new ControlInfo { Supported = modeReason == "", Reason = modeReason };
                controls["refreshRate"] = new ControlInfo { Supported = modeReason == "", Reason = modeReason, Value = refresh > 1 ? RoundHertz(refresh) : null };
                controls["dynamicRefreshRate"] = ReadDynamicRefreshControl(config, path, gdi, width, height, dynamicModeCache, hdr?.Value);
                controls["orientation"] = new ControlInfo { Supported = modeReason == "", Reason = modeReason, Value = orientation };
                controls["hdr"] = hdr!;
                controls["scale"] = new ControlInfo { Supported = false, Reason = "windowsManaged" };
            }

            var info = new DisplayInfo
            {
                Id = id, Name = name, Kind = builtin ? "builtin" : "external", Connection = ConnectionName(technology, builtin),
                Active = path.IsActive, Primary = primary, X = x, Y = y, Width = width, Height = height,
                Orientation = orientation, RefreshRate = RoundHertz(refresh), GdiName = gdi, DevicePath = devicePath,
                DynamicRefreshRate = (path.Flags & Native.PathBoostRefresh) != 0,
                PhysicalRefreshRate = RoundHertz(path.IsActive ? target?.VerticalSync.Hertz ?? refresh : 0),
                Modes = modes.OrderBy(mode => mode.Orientation).ThenByDescending(mode => mode.Width)
                    .ThenByDescending(mode => mode.Height).ThenByDescending(mode => mode.RefreshRate).ToList(),
                Controls = controls
            };
            result.Add(new AttachedDisplay(info, path, current));
        }

        return result;
    }

    private static AttachedDisplay Find(List<AttachedDisplay> displays, string? id) =>
        displays.FirstOrDefault(display => string.Equals(display.Info.Id, id, StringComparison.Ordinal))
        ?? throw new InvalidOperationException("The selected display is no longer available. Refresh the display list and try again.");

    private static string StableId(Native.Path path, string devicePath)
    {
        var key = devicePath.Length == 0
            ? $"target:{path.Target.Adapter.High:x8}:{path.Target.Adapter.Low:x8}:{path.Target.Id:x8}"
            : devicePath.ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16)).ToLowerInvariant();
    }

    private static string ConnectionName(uint technology, bool builtin) => builtin ? "Internal" : technology switch
    {
        0 => "VGA",
        1 => "S-Video",
        2 => "Composite",
        3 => "Component",
        4 => "DVI",
        5 => "HDMI",
        9 => "SDI",
        10 => "DisplayPort",
        12 => "UDI",
        15 => "Miracast",
        16 => "Indirect wired",
        17 => "Virtual display",
        18 => "DisplayPort (USB tunnel)",
        _ => "External"
    };

    private static Native.Mode? GetMode(Native.Mode[] modes, uint index, uint type) =>
        index < (uint)modes.Length && modes[index].Type == type ? modes[index] : null;

    private static uint FindModeIndex(Configuration config, Native.Path path, bool source)
    {
        var rawIndex = source ? path.Source.ModeIndex : path.Target.ModeIndex;
        var adapter = source ? path.Source.Adapter : path.Target.Adapter;
        var id = source ? path.Source.Id : path.Target.Id;
        var type = source ? 1u : 2u;

        bool Matches(uint index) => GetMode(config.Modes, index, type) is Native.Mode mode &&
            mode.Id == id && mode.Adapter.Equals(adapter);

        var split = (path.Flags & Native.PathVirtualMode) != 0;
        var preferred = split ? rawIndex >> 16 : rawIndex;
        if (Matches(preferred))
            return preferred;

        // In a virtual-aware query some drivers return the packed index even
        // for paths whose own virtual-mode flag is clear.
        var fallback = split ? rawIndex : rawIndex >> 16;
        return Matches(fallback) ? fallback : Native.InvalidModeIndex;
    }

    private static string GetGdiName(Native.SourcePath source)
    {
        var name = new Native.SourceName
        {
            Header = Native.Header<Native.SourceName>(Native.GetSourceName, source.Adapter, source.Id)
        };
        return Native.DisplayConfigGetDeviceInfo(ref name) == 0 ? name.Name ?? "" : "";
    }

    private static IEnumerable<Native.DevMode> EnumerateDeviceModes(string gdi)
    {
        for (var index = 0; index < 32768; index++)
        {
            var mode = Native.DevMode.Create();
            if (!Native.EnumDisplaySettingsEx(gdi, index, ref mode, Native.RotatedModes))
                yield break;

            if (mode.BitsPerPel >= 32)
                yield return mode;
        }
    }

    private static DisplayMode ToDisplayMode(Native.DevMode mode) => new()
    {
        Width = (int)mode.Width,
        Height = (int)mode.Height,
        RefreshRate = mode.Frequency,
        Orientation = ToDegrees(mode.Orientation)
    };

    private static DisplayMode CloneMode(DisplayMode mode) => new()
    {
        Width = mode.Width, Height = mode.Height, RefreshRate = mode.RefreshRate, Orientation = mode.Orientation
    };

    private static (int, int, double, int) ModeKey(DisplayMode mode) =>
        (mode.Width, mode.Height, mode.RefreshRate, mode.Orientation);

    private static int ToDegrees(uint counterClockwise) => (int)((4 - counterClockwise % 4) % 4) * 90;
    private static uint ToDevOrientation(int degrees) => (uint)((360 - degrees) % 360 / 90);
    private static double RoundHertz(double rate) => Math.Round(rate, 3);

    private static (ControlInfo Control, uint Setter) ReadHdr(Native.Path path)
    {
        if (!path.IsActive)
            return (new ControlInfo { Supported = false, Reason = "inactive" }, 0);

        var request = new Native.AdvancedColorInfo2
        {
            Header = Native.Header<Native.AdvancedColorInfo2>(Native.GetAdvancedColorInfo2, path.Target.Adapter, path.Target.Id)
        };
        if (Native.DisplayConfigGetDeviceInfo(ref request) == 0)
        {
            var supported = (request.Flags & 0x10) != 0;
            var limited = (request.Flags & 0x8) != 0;
            return (new ControlInfo
            {
                Supported = supported && !limited, Reason = !supported ? "driver" : limited ? "windowsManaged" : "",
                Value = supported ? (request.Flags & 0x20) != 0 ? 1 : 0 : null
            }, Native.SetHdrState);
        }

        // Windows 11 22H2 introduced Advanced Color for SDR. On those systems use
        // the HDR-specific query only, because the legacy flag may describe WCG.
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
            return (new ControlInfo { Supported = false, Reason = "driver" }, 0);

        var legacy = new Native.AdvancedColorInfo
        {
            Header = Native.Header<Native.AdvancedColorInfo>(Native.GetAdvancedColorInfo, path.Target.Adapter, path.Target.Id)
        };
        if (Native.DisplayConfigGetDeviceInfo(ref legacy) != 0)
            return (new ControlInfo { Supported = false, Reason = "driver" }, 0);

        var legacySupported = (legacy.Flags & 1) != 0;
        var legacyDisabled = (legacy.Flags & 8) != 0;
        return (new ControlInfo
        {
            Supported = legacySupported && !legacyDisabled,
            Reason = !legacySupported ? "driver" : legacyDisabled ? "windowsManaged" : "",
            Value = legacySupported ? (legacy.Flags & 2) != 0 ? 1 : 0 : null
        }, Native.SetAdvancedColorState);
    }

    private static int SetConfig(Native.Path[]? paths, Native.Mode[]? modes, uint flags) =>
        Native.SetDisplayConfig((uint)(paths?.Length ?? 0), paths, (uint)(modes?.Length ?? 0), modes, flags);

    private static void CheckWin32(int result, string operation)
    {
        if (result != 0)
            throw new Win32Exception(result, $"{operation} failed: {new Win32Exception(result).Message} ({result}).");
    }
}
