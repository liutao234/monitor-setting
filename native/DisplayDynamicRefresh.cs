using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Native = MonitorSetting.DisplayNative;

namespace MonitorSetting;

public sealed partial class DisplayService
{
    private sealed record DynamicModeCacheEntry(long CreatedAt, bool DriverReadSucceeded, Native.Rational[] Rates);
    private readonly Dictionary<string, DynamicModeCacheEntry> dynamicModes = new(StringComparer.Ordinal);

    private static ControlInfo ReadDynamicRefreshControl(Configuration config, Native.Path path, string gdi,
        int width, int height, Dictionary<string, DynamicModeCacheEntry>? cache = null, double? hdrValue = null)
    {
        bool enabled = (path.Flags & Native.PathBoostRefresh) != 0;
        var control = new ControlInfo { Value = enabled ? 1 : 0 };
        if (!path.IsActive) { control.Reason = "inactive"; return control; }
        if ((config.QueryFlags & Native.QueryVirtualRefresh) == 0)
        {
            control.Reason = "dynamicRefreshWindows11";
            return control;
        }
        // Clone targets share one GDI mode list; per-target DRR cannot be
        // inferred from it. The layout editor also requires independent paths.
        if (config.Paths.Count(candidate => candidate.IsActive && candidate.Source.Adapter.Equals(path.Source.Adapter)
            && candidate.Source.Id == path.Source.Id) > 1)
        {
            control.Reason = "mirrorLayout";
            return control;
        }
        // Validation is specific to the complete current topology and HDR
        // state. Cache it alongside mode discovery so ordinary slider writes
        // do not revalidate every candidate against the graphics driver.
        var key = $"{path.Source.Adapter.High}:{path.Source.Adapter.Low}:{path.Source.Id}:{path.Target.Id}:{gdi}:{width}:{height}:{path.Target.Rotation}:{hdrValue}:" +
            Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(config.Paths.AsSpan()))) +
            Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(config.Modes.AsSpan())));
        bool driverReadSucceeded;
        List<Native.Rational> rates;
        long now = Environment.TickCount64;
        if (cache is not null && cache.TryGetValue(key, out var existing) && now - existing.CreatedAt < ModeCacheLifetimeMilliseconds)
        {
            driverReadSucceeded = existing.DriverReadSucceeded;
            rates = existing.Rates.ToList();
        }
        else
        {
            var modes = ReadKernelDisplayModes(gdi, path.Source.Adapter, path.Source.Id);
            driverReadSucceeded = modes is not null;
            rates = DynamicRates(modes ?? [], width, height, path.Target.Rotation)
                .Where(rate => BuildDynamicConfiguration(config, path, rate) is not null).ToList();
            if (cache is not null) cache[key] = new DynamicModeCacheEntry(now, driverReadSucceeded, rates.ToArray());
        }
        // A currently enabled, Windows-reported DRR path is authoritative even
        // if a driver temporarily fails its optional mode-list query.
        if (enabled && path.Target.RefreshRate.Hertz > 1 &&
            rates.All(rate => Math.Abs(rate.Hertz - path.Target.RefreshRate.Hertz) > .01))
            rates.Add(path.Target.RefreshRate);
        control.Options = rates.OrderBy(rate => rate.Hertz).Select(rate => new ControlOption
        {
            Value = RoundHertz(rate.Hertz).ToString(CultureInfo.InvariantCulture),
            Label = RoundHertz(rate.Hertz).ToString(CultureInfo.InvariantCulture) + " Hz"
        }).ToList();
        control.Supported = enabled || control.Options.Count != 0;
        control.Reason = control.Supported ? "" : !driverReadSucceeded ? "driver" : "dynamicRefreshUnavailable";
        return control;
    }

    // The documented VirtualRefreshRate flag is necessary. Ordinary 60/120 Hz
    // modes, VRR/FreeSync support, and a fast panel alone do not establish DRR.
    private static IEnumerable<Native.Rational> DynamicRates(IEnumerable<Native.KernelDisplayMode> modes,
        int width, int height, uint rotation) => modes
        .Where(mode => mode.IsVirtualRefreshRate && mode.Width == width && mode.Height == height &&
            mode.DisplayOrientation == rotation && mode.RefreshRate.Hertz > 1)
        .Select(mode => mode.RefreshRate).DistinctBy(rate => RoundHertz(rate.Hertz));

    private static Native.KernelDisplayMode[]? ReadKernelDisplayModes(string gdi, Native.Luid adapter, uint source)
    {
        if (string.IsNullOrWhiteSpace(gdi)) return null;
        var open = new Native.OpenGdiAdapter { DeviceName = gdi };
        try
        {
            if (Native.D3DKMTOpenAdapterFromGdiDisplayName(ref open) != 0) return null;
            try
            {
                // The source may be reassigned while displays are plugged in.
                if (!open.AdapterLuid.Equals(adapter) || open.SourceId != source) return null;
                var request = new Native.KernelDisplayModeList { Adapter = open.Adapter, SourceId = open.SourceId };
                if (Native.D3DKMTGetDisplayModeList(ref request) != 0 || request.Count > 32768) return null;
                if (request.Count == 0) return [];
                int size = Marshal.SizeOf<Native.KernelDisplayMode>();
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    uint capacity = request.Count;
                    if (capacity is 0 or > 32768) return null;
                    request.Modes = Marshal.AllocHGlobal(checked((int)capacity * size));
                    try
                    {
                        int result = Native.D3DKMTGetDisplayModeList(ref request);
                        if (result == 0 && request.Count <= capacity)
                        {
                            var modes = new Native.KernelDisplayMode[request.Count];
                            for (int index = 0; index < modes.Length; index++)
                                modes[index] = Marshal.PtrToStructure<Native.KernelDisplayMode>(request.Modes + index * size);
                            return modes;
                        }
                        // STATUS_BUFFER_TOO_SMALL: retry only with the new bounded size.
                        if (result != unchecked((int)0xc0000023)) return null;
                    }
                    finally { Marshal.FreeHGlobal(request.Modes); request.Modes = IntPtr.Zero; }
                }
                return null;
            }
            finally
            {
                var close = new Native.CloseAdapter { Adapter = open.Adapter };
                Native.D3DKMTCloseAdapter(ref close);
            }
        }
        catch (EntryPointNotFoundException) { return null; }
        catch (DllNotFoundException) { return null; }
    }

    private static void ApplyDynamicRefresh(string id, bool enabled, double? requestedRate, Action beforeApply)
    {
        var config = Query(Native.ActivePaths);
        var attached = ReadAttachedDisplays(config, readModes: false);
        var display = Find(attached, id);
        if (display.Info.DynamicRefreshRate == enabled &&
            (!enabled || requestedRate is null || Math.Abs(display.Info.RefreshRate - requestedRate.Value) < .01)) return;
        var capability = ReadDynamicRefreshControl(config, display.Path, display.Info.GdiName, display.Info.Width, display.Info.Height);
        if (!capability.Supported) throw new InvalidOperationException(capability.Reason);

        var pathIndex = Array.FindIndex(config.Paths, path => path.Target.Adapter.Equals(display.Path.Target.Adapter)
            && path.Target.Id == display.Path.Target.Id);
        var path = config.Paths[pathIndex];
        Native.Rational rate;
        if (enabled)
        {
            var modes = ReadKernelDisplayModes(display.Info.GdiName, path.Source.Adapter, path.Source.Id);
            var candidates = DynamicRates(modes ?? [], display.Info.Width, display.Info.Height, path.Target.Rotation).ToList();
            if (display.Info.DynamicRefreshRate) candidates.Add(path.Target.RefreshRate);
            if (requestedRate is double requested)
                candidates = candidates.Where(candidate => Math.Abs(candidate.Hertz - requested) <= .01).ToList();
            rate = default;
            Configuration? chosen = null;
            foreach (var candidate in candidates.OrderBy(candidate => candidate.Hertz > display.Info.PhysicalRefreshRate / 2 + .1)
                .ThenByDescending(candidate => candidate.Hertz))
            {
                chosen = BuildDynamicConfiguration(config, path, candidate);
                if (chosen is not null) { rate = candidate; break; }
            }
            if (chosen is null) throw new InvalidOperationException("dynamicRefreshUnavailable");
            config = chosen;
        }
        else
        {
            rate = GetMode(config.Modes, FindModeIndex(config, path, source: false), 2)?.Signal.VerticalSync ?? default;
            if (rate.Hertz <= 1) throw new InvalidOperationException("readbackFailed");
            path = ConfigureDynamicPath(path, false, rate);
            config.Paths[pathIndex] = path;
        }
        uint flags = Native.SuppliedConfig | Native.SetVirtualModes | Native.SetVirtualRefresh;
        CheckWin32(SetConfig(config.Paths, config.Modes, flags | Native.Validate), "Validate dynamic refresh rate");
        var hdrStates = attached.ToDictionary(item => item.Info.Id, item => ReadHdr(item.Path).Control.Value);
        beforeApply();
        CheckWin32(SetConfig(config.Paths, config.Modes, flags | Native.Apply | Native.SaveToDatabase), "Apply dynamic refresh rate");
        var result = ReadAttachedDisplays(Query(Native.ActivePaths), readModes: false);
        var changed = Find(result, id).Info;
        if (changed.DynamicRefreshRate != enabled || Math.Abs(changed.RefreshRate - rate.Hertz) > .1 ||
            !SameDesktop(attached, result, id) || result.Any(item =>
                hdrStates.GetValueOrDefault(item.Info.Id) is double hdr && ReadHdr(item.Path).Control.Value != hdr))
            throw new InvalidOperationException("readbackFailed");
    }

    private static Native.Path ConfigureDynamicPath(Native.Path path, bool enabled, Native.Rational rate)
    {
        path.Target.RefreshRate = rate;
        if (enabled)
        {
            path.Flags |= Native.PathBoostRefresh;
        }
        else path.Flags &= ~Native.PathBoostRefresh;
        return path;
    }

    private static Configuration? BuildDynamicConfiguration(Configuration original, Native.Path selected, Native.Rational rate)
    {
        uint targetIndex = FindModeIndex(original, selected, source: false);
        if (GetMode(original.Modes, targetIndex, 2) is not Native.Mode targetMode) return null;
        var config = original with { Paths = original.Paths.Where(path => path.IsActive).ToArray(), Modes = original.Modes.ToArray() };
        int pathIndex = Array.FindIndex(config.Paths, path => path.Target.Adapter.Equals(selected.Target.Adapter) && path.Target.Id == selected.Target.Id);
        if (pathIndex < 0) return null;
        config.Paths[pathIndex] = ConfigureDynamicPath(selected, true, rate);
        uint flags = Native.Validate | Native.SuppliedConfig | Native.SetVirtualModes | Native.SetVirtualRefresh;
        if (targetMode.Signal.VerticalSync.Hertz > rate.Hertz + .1 && SetConfig(config.Paths, config.Modes, flags) == 0)
            return config;

        // DRR needs an explicit physical timing as well as its virtual base.
        // A missing target-mode index is rejected by some virtual-aware paths.
        // Only consider a real Windows-reported preferred timing, never derive
        // pixel clocks or assume a 2x refresh pair. Keep signal dimensions fixed.
        var preferred = new Native.PreferredMode
        {
            Header = Native.Header<Native.PreferredMode>(Native.GetTargetPreferredMode, selected.Target.Adapter, selected.Target.Id)
        };
        if (Native.DisplayConfigGetDeviceInfo(ref preferred) != 0 || preferred.Signal.VerticalSync.Hertz <= rate.Hertz + .1 ||
            preferred.Signal.ActiveWidth != targetMode.Signal.ActiveWidth || preferred.Signal.ActiveHeight != targetMode.Signal.ActiveHeight)
            return null;
        targetMode.Signal = preferred.Signal;
        config.Modes[targetIndex] = targetMode;
        return SetConfig(config.Paths, config.Modes, flags) == 0 ? config : null;
    }

    private static bool SameDesktop(List<AttachedDisplay> before, List<AttachedDisplay> after, string changedId) =>
        before.Count == after.Count && before.All(original => after.Any(current =>
            current.Info.Id == original.Info.Id && current.Info.Active == original.Info.Active &&
            current.Info.Primary == original.Info.Primary && current.Info.X == original.Info.X && current.Info.Y == original.Info.Y &&
            current.Info.Width == original.Info.Width && current.Info.Height == original.Info.Height &&
            current.Info.Orientation == original.Info.Orientation && (original.Info.Id == changedId ||
                current.Info.DynamicRefreshRate == original.Info.DynamicRefreshRate &&
                Math.Abs(current.Info.RefreshRate - original.Info.RefreshRate) < .1 &&
                Math.Abs(current.Info.PhysicalRefreshRate - original.Info.PhysicalRefreshRate) < .1)));
}
