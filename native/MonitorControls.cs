using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MonitorSetting;

public sealed class MonitorControls
{
    private const uint BrightnessCapability = 0x02;
    private const uint ContrastCapability = 0x04;
    private const uint TemperatureCapability = 0x08;
    private const byte SharpnessVcp = 0x87;
    private const int SetParameterVcp = 1;
    private const uint DisplayDeviceActive = 0x01;
    private const uint GetDeviceInterfaceName = 0x01;
    private static readonly string[] PictureControls = ["brightness", "contrast", "sharpness", "temperature"];
    // Indexed by the MC_COLOR_TEMPERATURE value, where zero represents unknown.
    private static readonly int[] Temperatures = [0, 4000, 5000, 6500, 7500, 8200, 9300, 10000, 11500];
    private static readonly TimeSpan ValueLifetime = TimeSpan.FromSeconds(5);
    private readonly object gate = new();
    private readonly Dictionary<string, CachedControls> controlCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CachedCapabilities> capabilityCache = new(StringComparer.Ordinal);
    private string topology = "";
    private sealed record CachedControls(long ReadAt, Dictionary<string, ControlInfo> Controls, Dictionary<string, long> ControlReadAt);
    private sealed record CachedCapabilities(long ReadAt, (uint Flags, uint Temperatures)? Flags, ControlInfo Sharpness);

    public void Invalidate()
    {
        lock (gate)
        {
            controlCache.Clear();
            capabilityCache.Clear();
            topology = "";
        }
    }

    // Reuse verified values after a write without waiting for unrelated displays' DDC/CI buses.
    // Values are copied, so consumers cannot mutate a later response's cached state.
    public void ApplyCached(List<DisplayInfo> displays)
    {
        lock (gate)
        {
            EnsureTopology(displays);
            foreach (var display in displays)
                if (controlCache.TryGetValue(display.Id, out var cached))
                    CopyControls(cached.Controls, display.Controls);
        }
    }

    public void Populate(List<DisplayInfo> displays, bool forceRefresh = false)
    {
        lock (gate)
        {
            if (forceRefresh) Invalidate();
            EnsureTopology(displays);
            PopulateCore(displays);
        }
    }

    // The full topology is still used to validate physical-monitor identity, but only
    // this display is probed. Progressive loading gives every device its own instance.
    public void PopulateDevice(List<DisplayInfo> displays, string displayId)
    {
        lock (gate)
        {
            EnsureTopology(displays);
            PopulateCore(displays, displayId);
        }
    }

    private void PopulateCore(List<DisplayInfo> displays, string? displayId = null)
    {
        foreach (var display in displays)
        {
            if (displayId is not null && display.Id != displayId) continue;
            if (controlCache.TryGetValue(display.Id, out var cached) &&
                System.Diagnostics.Stopwatch.GetElapsedTime(cached.ReadAt) < ValueLifetime)
            {
                CopyControls(cached.Controls, display.Controls);
                continue;
            }
            var reason = display.Active ? "windowsManaged" : "inactive";
            foreach (var name in PictureControls)
                display.Controls[name] = Unavailable(reason);
            display.Controls["response"] = Unavailable(display.Active ? "vendorSpecific" : reason);
            display.Controls.TryAdd("hdr", Unavailable(reason));
            display.Controls.TryAdd("scale", Unavailable(reason));

            if (!display.Active)
            {
                Remember(display);
                continue;
            }

            if (IsBuiltIn(display))
            {
                foreach (var name in PictureControls)
                    display.Controls[name] = Unavailable("unsupported");
                display.Controls["brightness"] = ReadBuiltInBrightness(display.DevicePath);
                Remember(display);
                continue;
            }

            using var monitor = OpenPhysicalMonitor(display, displays, out reason);
            if (monitor is null)
            {
                foreach (var name in PictureControls)
                    display.Controls[name] = Unavailable(reason);
                Remember(display);
                continue;
            }

            var capabilities = GetCapabilities(display, monitor, out bool newlyRead);
            display.Controls["sharpness"] = newlyRead || !capabilities.Sharpness.Supported
                ? Clone(capabilities.Sharpness) : ReadSharpness(monitor);
            foreach (var name in PictureControls)
            {
                if (name != "sharpness")
                    display.Controls[name] = ReadPhysicalControl(monitor, name, capabilities.Flags);
            }
            Remember(display);
        }
    }

    public void Set(string displayId, string control, double value, List<DisplayInfo> displays)
    {
        lock (gate)
        {
            EnsureTopology(displays);
            try { SetCore(displayId, control, value, displays); }
            catch
            {
                controlCache.Remove(displayId);
                capabilityCache.Remove(displayId);
                throw;
            }
        }
    }

    private void SetCore(string displayId, string control, double value, List<DisplayInfo> displays)
    {
        var display = displays.SingleOrDefault(candidate => candidate.Id == displayId)
            ?? throw new InvalidOperationException("displayNotFound");
        if (!display.Active)
            throw new InvalidOperationException("inactive");
        if (!double.IsFinite(value))
            throw new InvalidOperationException("invalidValue");
        if (control == "response")
            throw new InvalidOperationException("vendorSpecific");
        if (!PictureControls.Contains(control))
            throw new InvalidOperationException("windowsManaged");

        if (IsBuiltIn(display))
        {
            if (control != "brightness")
                throw new InvalidOperationException("windowsManaged");
            SetBuiltInBrightness(display, value);
            RememberControl(display, control);
            return;
        }

        using var monitor = OpenPhysicalMonitor(display, displays, out var reason);
        if (monitor is null)
            throw new InvalidOperationException(reason);

        var capabilities = GetCapabilities(display, monitor, out bool newlyRead);
        // During a drag the preceding write already verified this device's range.
        // Reuse that short-lived metadata instead of adding another DDC read to
        // every sample. Physical identity is still checked above, and every cached
        // write is sent to the device and independently read back below.
        var cached = CachedWriteControl(display.Id, control);
        var before = cached ?? (control == "sharpness"
            ? newlyRead || !capabilities.Sharpness.Supported ? Clone(capabilities.Sharpness) : ReadSharpness(monitor)
            : ReadPhysicalControl(monitor, control, capabilities.Flags));
        if (!before.Supported)
            throw new InvalidOperationException(before.Reason);
        if (value != Math.Truncate(value))
            throw new InvalidOperationException("invalidValue");

        if (control == "temperature")
        {
            var kelvin = value.ToString(CultureInfo.InvariantCulture);
            if (!before.Options.Any(option => option.Value == kelvin))
                throw new InvalidOperationException("invalidValue");
        }
        else if (value < before.Min || value > before.Max)
        {
            throw new InvalidOperationException("invalidValue");
        }

        if (cached is null && value == before.Value)
        {
            display.Controls[control] = before;
            RememberControl(display, control);
            return;
        }

        var written = control switch
        {
            "brightness" => SetMonitorBrightness(monitor, (uint)value),
            "contrast" => SetMonitorContrast(monitor, (uint)value),
            "sharpness" => SetVCPFeature(monitor, SharpnessVcp, (uint)value),
            "temperature" => SetMonitorColorTemperature(monitor, Array.IndexOf(Temperatures, (int)value)),
            _ => false,
        };
        if (!written)
            throw new InvalidOperationException("writeFailed");

        // Sharpness's advertised VCP code was already checked before writing.
        var after = control == "sharpness" ? ReadSharpness(monitor) : ReadPhysicalControl(monitor, control, capabilities.Flags);
        display.Controls[control] = after;
        VerifyReadback(after, value);
        RememberControl(display, control);
    }

    private ControlInfo? CachedWriteControl(string displayId, string control)
    {
        if (!controlCache.TryGetValue(displayId, out var cached) ||
            !cached.ControlReadAt.TryGetValue(control, out long readAt) ||
            System.Diagnostics.Stopwatch.GetElapsedTime(readAt) >= ValueLifetime ||
            !cached.Controls.TryGetValue(control, out var value) || !value.Supported)
            return null;
        return Clone(value);
    }

    private void EnsureTopology(List<DisplayInfo> displays)
    {
        string current = string.Join("\n", displays.OrderBy(display => display.Id, StringComparer.Ordinal)
            .Select(display => $"{display.Id}|{display.Active}|{display.DevicePath}|{display.GdiName}"));
        if (current == topology) return;
        controlCache.Clear();
        capabilityCache.Clear();
        topology = current;
    }

    private CachedCapabilities GetCapabilities(DisplayInfo display, PhysicalHandle monitor, out bool newlyRead)
    {
        if (capabilityCache.TryGetValue(display.Id, out var cached) &&
            System.Diagnostics.Stopwatch.GetElapsedTime(cached.ReadAt) < TimeSpan.FromSeconds(cached.Flags is null ? 30 : 300))
        {
            newlyRead = false;
            return cached;
        }
        // Capability strings are large, slow DDC transactions; capabilities do not change with slider values.
        var sharpness = ReadAdvertisedSharpness(monitor);
        cached = new CachedCapabilities(System.Diagnostics.Stopwatch.GetTimestamp(), ReadCapabilities(monitor), sharpness);
        capabilityCache[display.Id] = cached;
        newlyRead = true;
        return cached;
    }

    private void Remember(DisplayInfo display)
    {
        var values = display.Controls.Where(pair => PictureControls.Contains(pair.Key) || pair.Key == "response")
            .ToDictionary(pair => pair.Key, pair => Clone(pair.Value), StringComparer.Ordinal);
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        controlCache[display.Id] = new CachedControls(now, values, values.Keys.ToDictionary(name => name, _ => now, StringComparer.Ordinal));
    }

    private void RememberControl(DisplayInfo display, string control)
    {
        if (!controlCache.TryGetValue(display.Id, out var cached))
            cached = new CachedControls(0, new(StringComparer.Ordinal), new(StringComparer.Ordinal));
        cached.Controls[control] = Clone(display.Controls[control]);
        cached.ControlReadAt[control] = System.Diagnostics.Stopwatch.GetTimestamp();
        // Do not renew unrelated values' age when only one control has been read.
        controlCache[display.Id] = cached;
    }

    private static void CopyControls(Dictionary<string, ControlInfo> source, Dictionary<string, ControlInfo> destination)
    {
        foreach (var pair in source) destination[pair.Key] = Clone(pair.Value);
    }

    private static ControlInfo Clone(ControlInfo value) => new()
    {
        Supported = value.Supported, Reason = value.Reason, Value = value.Value,
        Min = value.Min, Max = value.Max, Step = value.Step,
        Options = value.Options.Select(option => new ControlOption { Value = option.Value, Label = option.Label }).ToList()
    };

    private static void VerifyReadback(ControlInfo control, double expected)
    {
        if (control.Value is null)
            throw new InvalidOperationException("readbackFailed");
        if (control.Value != expected)
            throw new InvalidOperationException("readbackMismatch");
    }

    private static bool IsBuiltIn(DisplayInfo display) => display.Kind is "builtin" or "internal";

    private static ControlInfo Unavailable(string reason) => new() { Reason = reason };

    private static ControlInfo Range(uint min, uint current, uint max, uint step = 1)
    {
        if (max <= min || current < min || current > max)
            return Unavailable("driver");

        return new ControlInfo { Supported = true, Value = current, Min = min, Max = max, Step = step };
    }

    private static ControlInfo ReadBuiltInBrightness(string devicePath)
    {
        try
        {
            var current = QueryBrightness(devicePath);
            return BrightnessInfo(current, QueryBrightnessMethod(current.InstanceName) is not null);
        }
        catch (Exception error) when (IsWmiError(error))
        {
            return Unavailable("driver");
        }
        catch (InvalidOperationException error) when (error.Message is "driver" or "ambiguousDisplay")
        {
            return Unavailable(error.Message);
        }
    }

    private static void SetBuiltInBrightness(DisplayInfo display, double value)
    {
        try
        {
            var before = QueryBrightness(display.DevicePath);
            var methodPath = QueryBrightnessMethod(before.InstanceName)
                ?? throw new InvalidOperationException("driver");
            if (value < before.Levels[0] || value > before.Levels[^1])
                throw new InvalidOperationException("invalidValue");
            var requestedLevel = before.Levels.MinBy(level => Math.Abs(level - value));
            if (requestedLevel == before.Value)
            {
                display.Controls["brightness"] = BrightnessInfo(before, methodAvailable: true);
                return;
            }

            // Projected WMI queries return a relative object path. Reopening that path alone
            // silently selects root\cimv2, where this class does not exist (WBEM_E_INVALID_CLASS).
            using var monitor = new ManagementObject(new ManagementScope(@"\\.\root\wmi"), new ManagementPath(methodPath),
                new ObjectGetOptions(null, TimeSpan.FromSeconds(3), false));
            using var arguments = monitor.GetMethodParameters("WmiSetBrightness");
            arguments["Timeout"] = 0u;
            arguments["Brightness"] = requestedLevel;
            using var result = monitor.InvokeMethod("WmiSetBrightness", arguments, new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(3) });
            // Some WMI monitor providers expose a method with no output object despite
            // the documented return value. A missing status is accepted only after the
            // independent brightness query below verifies the requested physical value.
            var returnedStatus = result?.Properties.Cast<PropertyData>()
                .FirstOrDefault(property => property.Name == "ReturnValue")?.Value;
            if (returnedStatus is not null && (returnedStatus is not uint status || status != 0))
                throw new InvalidOperationException("writeFailed");

            var after = ReadBackBrightness(display.DevicePath, requestedLevel);
            display.Controls["brightness"] = BrightnessInfo(after, methodAvailable: true);
            VerifyReadback(display.Controls["brightness"], requestedLevel);
        }
        catch (Exception error) when (IsWmiError(error))
        {
            throw new InvalidOperationException("driver", error);
        }
    }

    private static WmiBrightness ReadBackBrightness(string devicePath, byte expected)
    {
        WmiBrightness? after = null;
        Exception? failure = null;
        // A few providers publish CurrentBrightness shortly after a successful write.
        // Bound the retry delay; never treat a successful setter alone as verified state.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (attempt != 0) Thread.Sleep(attempt * 50);
            try
            {
                after = QueryBrightness(devicePath);
                failure = null;
                if (after.Value == expected) return after;
            }
            catch (Exception error) when (IsWmiError(error) || error is InvalidOperationException)
            {
                failure = error;
            }
        }
        if (after is null || failure is not null) throw new InvalidOperationException("readbackFailed", failure);
        return after;
    }

    private static bool IsWmiError(Exception error) =>
        error is ManagementException or COMException or UnauthorizedAccessException;

    private static ControlInfo BrightnessInfo(WmiBrightness brightness, bool methodAvailable)
    {
        if (!methodAvailable)
            return Unavailable("driver");
        var levels = brightness.Levels;
        var steps = levels.Zip(levels.Skip(1), (first, second) => (uint)(second - first)).Distinct().ToArray();
        return Range(levels[0], brightness.Value, levels[^1], steps.Length == 1 ? steps[0] : 1u);
    }

    private sealed record WmiBrightness(string InstanceName, byte Value, byte[] Levels);

    private static WmiBrightness QueryBrightness(string devicePath)
    {
        using var search = new ManagementObjectSearcher(@"root\wmi",
            "SELECT Active, InstanceName, CurrentBrightness, Level FROM WmiMonitorBrightness");
        search.Options.Timeout = TimeSpan.FromSeconds(3);
        using var results = search.Get();
        WmiBrightness? found = null;
        foreach (ManagementObject item in results)
        {
            using (item)
            {
                if (item["Active"] is not true || item["InstanceName"] is not string name ||
                    !MatchesWmiInstance(devicePath, name))
                    continue;
                if (found is not null)
                    throw new InvalidOperationException("ambiguousDisplay");
                if (item["CurrentBrightness"] is not byte value || item["Level"] is not byte[] levels)
                    throw new InvalidOperationException("driver");
                var validLevels = levels.Where(level => level <= 100).Distinct().Order().ToArray();
                if (validLevels.Length < 2 || !validLevels.Contains(value))
                    throw new InvalidOperationException("driver");
                found = new WmiBrightness(name, value, validLevels);
            }
        }
        return found ?? throw new InvalidOperationException("driver");
    }

    private static string? QueryBrightnessMethod(string instance)
    {
        using var search = new ManagementObjectSearcher(@"root\wmi",
            "SELECT * FROM WmiMonitorBrightnessMethods");
        search.Options.Timeout = TimeSpan.FromSeconds(3);
        using var results = search.Get();
        string? found = null;
        foreach (ManagementObject item in results)
        {
            using (item)
            {
                if (item["Active"] is not true ||
                    !string.Equals(item["InstanceName"] as string, instance, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (found is not null)
                    throw new InvalidOperationException("ambiguousDisplay");
                found = item.Path.Path;
            }
        }
        return string.IsNullOrEmpty(found) ? null : found;
    }

    private static bool MatchesWmiInstance(string devicePath, string instance)
    {
        // The interface path uses '#' in place of '\' and ends with the monitor interface GUID.
        // The WMI provider can append a separate instance ordinal, such as '_0'.
        if (!devicePath.StartsWith(@"\\?\DISPLAY#", StringComparison.OrdinalIgnoreCase))
            return false;
        var guidStart = devicePath.LastIndexOf('#');
        if (guidStart < 4 || !Guid.TryParse(devicePath.AsSpan(guidStart + 1), out _))
            return false;
        var hardwarePath = devicePath[4..guidStart].Replace('#', '\\');
        if (!instance.StartsWith(hardwarePath, StringComparison.OrdinalIgnoreCase))
            return false;
        var suffix = instance.AsSpan(hardwarePath.Length);
        return suffix.IsEmpty || (suffix.Length > 1 && suffix[0] == '_' &&
            suffix[1..].IndexOfAnyExceptInRange('0', '9') == -1);
    }

    private static PhysicalHandle? OpenPhysicalMonitor(DisplayInfo display, List<DisplayInfo> displays, out string reason)
    {
        reason = "driver";
        if (string.IsNullOrEmpty(display.GdiName) || string.IsNullOrEmpty(display.DevicePath))
            return null;
        if (displays.Count(other => other.Active &&
            string.Equals(other.GdiName, display.GdiName, StringComparison.OrdinalIgnoreCase)) != 1)
        {
            reason = "ambiguousDisplay";
            return null;
        }

        var logicalMatches = new HashSet<nint>();
        MonitorCallback collect = (monitor, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = (uint)Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfoW(monitor, ref info) &&
                string.Equals(info.Device, display.GdiName, StringComparison.OrdinalIgnoreCase))
                logicalMatches.Add(monitor);
            return true;
        };
        if (!EnumDisplayMonitors(0, 0, collect, 0) || logicalMatches.Count == 0)
            return null;
        if (logicalMatches.Count > 1)
        {
            reason = "ambiguousDisplay";
            return null;
        }

        var logical = logicalMatches.Single();
        reason = "ddcUnavailable";
        if (!GetNumberOfPhysicalMonitorsFromHMONITOR(logical, out var count) || count == 0)
            return null;
        if (count != 1)
        {
            reason = "ambiguousDisplay";
            return null;
        }

        var handles = new PhysicalMonitor[1];
        var success = GetPhysicalMonitorsFromHMONITOR(logical, 1, handles);
        var physical = new PhysicalHandle(handles[0].Handle);
        if (success && !physical.IsInvalid)
        {
            // Refuse a stale GDI name if its physical monitor was replaced during enumeration.
            var interfaces = ActiveMonitorInterfaces(display.GdiName);
            if (interfaces.Count == 1 && interfaces.Contains(display.DevicePath))
                return physical;
            reason = interfaces.Count > 1 ? "ambiguousDisplay" : "driver";
        }
        physical.Dispose();
        return null;
    }

    private static HashSet<string> ActiveMonitorInterfaces(string gdiName)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (uint index = 0; ; index++)
        {
            var device = new DisplayDevice { Size = (uint)Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevicesW(gdiName, index, ref device, GetDeviceInterfaceName))
                return result;
            if ((device.StateFlags & DisplayDeviceActive) != 0 && !string.IsNullOrEmpty(device.Id))
                result.Add(device.Id);
        }
    }

    private static (uint Flags, uint Temperatures)? ReadCapabilities(PhysicalHandle monitor) =>
        GetMonitorCapabilities(monitor, out var flags, out var temperatures) ? (flags, temperatures) : null;

    private static ControlInfo ReadPhysicalControl(PhysicalHandle monitor, string control, (uint Flags, uint Temperatures)? capabilities)
    {
        if (control == "sharpness")
            return ReadAdvertisedSharpness(monitor);
        var flags = capabilities?.Flags;
        var readFailure = capabilities is null ? "ddcUnavailable" : "driver";
        switch (control)
        {
            // Some displays return a capabilities string Windows cannot parse even though
            // their standardized brightness/contrast queries work and report valid ranges.
            case "brightness" when flags is null || (flags & BrightnessCapability) != 0:
                return GetMonitorBrightness(monitor, out var bMin, out var bValue, out var bMax)
                    ? Range(bMin, bValue, bMax) : Unavailable(readFailure);
            case "contrast" when flags is null || (flags & ContrastCapability) != 0:
                return GetMonitorContrast(monitor, out var cMin, out var cValue, out var cMax)
                    ? Range(cMin, cValue, cMax) : Unavailable(readFailure);
            case "temperature" when capabilities is { } caps && (caps.Flags & TemperatureCapability) != 0:
                return ReadTemperature(monitor, caps.Temperatures);
            case "temperature" when capabilities is null:
                return Unavailable("ddcUnavailable");
            default:
                return Unavailable("unsupported");
        }
    }

    private static ControlInfo ReadTemperature(PhysicalHandle monitor, uint temperatureFlags)
    {
        if (!GetMonitorColorTemperature(monitor, out var current))
            return Unavailable("driver");
        var options = new List<ControlOption>();
        for (var index = 1; index < Temperatures.Length; index++)
        {
            if ((temperatureFlags & (1u << (index - 1))) == 0)
                continue;
            var kelvin = Temperatures[index].ToString(CultureInfo.InvariantCulture);
            options.Add(new ControlOption { Value = kelvin, Label = $"{kelvin} K" });
        }
        return options.Count == 0 ? Unavailable("unsupported") : new ControlInfo
        {
            Supported = true,
            Value = current > 0 && current < Temperatures.Length ? Temperatures[current] : null,
            Options = options,
        };
    }

    private static ControlInfo ReadAdvertisedSharpness(PhysicalHandle monitor)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (!GetCapabilitiesStringLength(monitor, out var length) || length is < 2 or > 65536)
                continue;
            var capabilities = new StringBuilder((int)length);
            if (CapabilitiesRequestAndCapabilitiesReply(monitor, capabilities, length))
                return HasContinuousSharpness(capabilities.ToString()) ? ReadSharpness(monitor) : Unavailable("unsupported");
        }
        return Unavailable("ddcUnavailable");
    }

    private static bool HasContinuousSharpness(string capabilities)
    {
        // VCP capabilities list features at depth one. Nested numbers are preset values.
        // A nested value list on 87 represents an enumerated form, not a continuous range.
        var text = capabilities.AsSpan().Trim();
        if (text.Length > 1 && text[0] == '(' && text[^1] == ')')
            text = text[1..^1];
        while (!text.IsEmpty)
        {
            text = text.TrimStart();
            var open = text.IndexOf('(');
            if (open <= 0)
                return false;
            var depth = 1;
            var close = open + 1;
            for (; close < text.Length && depth != 0; close++)
            {
                if (text[close] == '(') depth++;
                if (text[close] == ')') depth--;
            }
            if (depth != 0)
                return false;
            if (text[..open].TrimEnd().Equals("vcp", StringComparison.OrdinalIgnoreCase))
            {
                var tokens = text[(open + 1)..(close - 1)];
                while (!tokens.IsEmpty)
                {
                    tokens = tokens.TrimStart();
                    var tokenLength = 0;
                    while (tokenLength < tokens.Length && !char.IsWhiteSpace(tokens[tokenLength]) && tokens[tokenLength] != '(')
                        tokenLength++;
                    var code = tokens[..tokenLength];
                    tokens = tokens[tokenLength..].TrimStart();
                    var enumerated = !tokens.IsEmpty && tokens[0] == '(';
                    if (code.SequenceEqual("87"))
                        return !enumerated;
                    if (!enumerated)
                        continue;
                    var end = tokens.IndexOf(')');
                    if (end < 0)
                        return false;
                    tokens = tokens[(end + 1)..];
                }
                return false;
            }
            text = text[close..];
        }
        return false;
    }

    private static ControlInfo ReadSharpness(PhysicalHandle monitor) =>
        GetVCPFeatureAndVCPFeatureReply(monitor, SharpnessVcp, out var type, out var current, out var max) &&
        type == SetParameterVcp && max <= ushort.MaxValue
            ? Range(0, current, max)
            : Unavailable("driver");

    private sealed class PhysicalHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal PhysicalHandle(nint value) : base(ownsHandle: true) => SetHandle(value);
        protected override bool ReleaseHandle() => DestroyPhysicalMonitor(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public uint Size;
        public Rect Monitor, WorkArea;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string? Device;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public uint Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string? Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? Description;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? Key;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    private struct PhysicalMonitor
    {
        public nint Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? Description;
    }

    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool MonitorCallback(nint monitor, nint deviceContext, nint rect, nint data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint deviceContext, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfoEx info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool EnumDisplayDevicesW(string deviceName, uint deviceIndex, ref DisplayDevice device, uint flags);
    [DllImport("dxva2.dll")]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(nint monitor, out uint count);
    [DllImport("dxva2.dll")]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(nint monitor, uint count, [Out] PhysicalMonitor[] handles);
    [DllImport("dxva2.dll")]
    private static extern bool DestroyPhysicalMonitor(nint monitor);
    [DllImport("dxva2.dll")]
    private static extern bool GetMonitorCapabilities(PhysicalHandle monitor, out uint capabilities, out uint temperatures);
    [DllImport("dxva2.dll")]
    private static extern bool GetMonitorBrightness(PhysicalHandle monitor, out uint min, out uint value, out uint max);
    [DllImport("dxva2.dll")]
    private static extern bool SetMonitorBrightness(PhysicalHandle monitor, uint value);
    [DllImport("dxva2.dll")]
    private static extern bool GetMonitorContrast(PhysicalHandle monitor, out uint min, out uint value, out uint max);
    [DllImport("dxva2.dll")]
    private static extern bool SetMonitorContrast(PhysicalHandle monitor, uint value);
    [DllImport("dxva2.dll")]
    private static extern bool GetMonitorColorTemperature(PhysicalHandle monitor, out int temperature);
    [DllImport("dxva2.dll")]
    private static extern bool SetMonitorColorTemperature(PhysicalHandle monitor, int temperature);
    [DllImport("dxva2.dll")]
    private static extern bool GetCapabilitiesStringLength(PhysicalHandle monitor, out uint length);
    [DllImport("dxva2.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern bool CapabilitiesRequestAndCapabilitiesReply(PhysicalHandle monitor, StringBuilder capabilities, uint length);
    [DllImport("dxva2.dll")]
    private static extern bool GetVCPFeatureAndVCPFeatureReply(PhysicalHandle monitor, byte code, out int type, out uint current, out uint max);
    [DllImport("dxva2.dll")]
    private static extern bool SetVCPFeature(PhysicalHandle monitor, byte code, uint value);
}
