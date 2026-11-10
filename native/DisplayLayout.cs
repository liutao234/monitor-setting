using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Native = MonitorSetting.DisplayNative;

namespace MonitorSetting;

public sealed partial class DisplayService
{
    private sealed record DisplayUpdate(AttachedDisplay Display, Native.DevMode Mode, bool SetPrimary);

    public void ApplyLayout(LayoutRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requestedMode = request.Mode?.Trim().ToLowerInvariant();
        if (requestedMode == "internal")
            requestedMode = "builtin";
        if (requestedMode is not (null or "extend" or "mirror" or "builtin" or "external" or "solo"))
            throw new ArgumentException("Unknown display mode.", nameof(request));
        if (requestedMode == "solo" && string.IsNullOrWhiteSpace(request.SoloId))
            throw new ArgumentException("Select a display for single-display mode.", nameof(request));
        if (request.Displays is null || request.Displays.Count > 64)
            throw new ArgumentException("The display changes are invalid.", nameof(request));

        var changes = new Dictionary<string, DisplayChange>(StringComparer.Ordinal);
        foreach (var change in request.Displays)
        {
            if (change is null || string.IsNullOrWhiteSpace(change.Id) || !changes.TryAdd(change.Id, change))
                throw new ArgumentException("Each display change must name a different display.", nameof(request));
            if (change.Width.HasValue != change.Height.HasValue || change.Width is <= 0 or > 65535 ||
                change.Height is <= 0 or > 65535 || change.X is < -65535 or > 65535 || change.Y is < -65535 or > 65535 ||
                change.Orientation is not (null or 0 or 90 or 180 or 270) ||
                change.RefreshRate is double refresh && (!double.IsFinite(refresh) || refresh <= 1 || refresh > 2000))
                throw new ArgumentException("A display position, resolution, orientation, or refresh rate is out of range.", nameof(request));
        }

        var hasSettings = changes.Values.Any(HasChanges) || request.PrimaryId is not null;
        if (requestedMode is null && !hasSettings)
            return;
        if (requestedMode == "mirror" && hasSettings)
            throw new InvalidOperationException("Per-display layout changes require independent displays. Switch to Extend first.");

        lock (gate)
        {
            var attached = ReadAttachedDisplays(Query(Native.AllPaths), readModes: false);
            foreach (var id in changes.Keys)
                Find(attached, id);
            if (request.PrimaryId is not null)
                Find(attached, request.PrimaryId);

            var original = Capture();
            var attempted = false;
            try
            {
                if (requestedMode is not null && !MatchesTopology(attached, requestedMode, request.SoloId))
                {
                    ApplyTopology(requestedMode, request.SoloId, attached, () => attempted = true);
                    attached = ReadAttachedDisplays(Query(Native.AllPaths), readModes: false);
                    if (!MatchesTopology(attached, requestedMode, request.SoloId))
                        throw new InvalidOperationException("Windows did not activate the requested display mode.");
                }

                if (!hasSettings)
                    return;

                // Disable the boost before selecting a fixed frequency. The
                // current virtual rate can equal that fixed rate while the
                // physical scanout is still higher, so the legacy mode change
                // must be calculated from a fresh, non-boosted configuration.
                foreach (var change in changes.Values.Where(change => change.DynamicRefreshRate == false))
                    ApplyDynamicRefresh(change.Id, false, null, () => attempted = true);
                if (changes.Values.Any(change => change.DynamicRefreshRate == false))
                    attached = ReadAttachedDisplays(Query(Native.AllPaths), readModes: false);

                var updates = BuildUpdates(attached, changes, request.PrimaryId);
                var modes = updates.Where(update => (update.Mode.Fields & ~Native.DmPosition) != 0).ToList();
                foreach (var update in modes)
                {
                    var mode = update.Mode;
                    mode.Fields &= ~Native.DmPosition;
                    CheckDisplayChange(Native.ChangeDisplaySettingsEx(update.Display.Info.GdiName, ref mode,
                        IntPtr.Zero, Native.CdsTest, IntPtr.Zero), $"Test settings for {update.Display.Info.Name}");
                }

                foreach (var update in modes)
                {
                    var mode = update.Mode;
                    mode.Fields &= ~Native.DmPosition;
                    attempted = true;
                    CheckDisplayChange(Native.ChangeDisplaySettingsEx(update.Display.Info.GdiName, ref mode,
                        IntPtr.Zero, Native.CdsUpdateRegistry | Native.CdsNoReset, IntPtr.Zero),
                        $"Stage settings for {update.Display.Info.Name}");
                }

                if (modes.Count > 0)
                    CheckDisplayChange(Native.CommitDisplaySettings(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero),
                        "Apply the display settings");

                if (updates.Any(update => (update.Mode.Fields & Native.DmPosition) != 0))
                {
                    var layout = BuildPositionConfiguration(Query(Native.ActivePaths), updates);
                    if (layout is not null)
                    {
                        var flags = Native.SuppliedConfig | Native.SetVirtualModes;
                        if ((layout.QueryFlags & Native.QueryVirtualRefresh) != 0)
                            flags |= Native.SetVirtualRefresh;
                        CheckWin32(SetConfig(layout.Paths, layout.Modes, flags | Native.Validate), "Validate the complete display layout");
                        attempted = true;
                        CheckWin32(SetConfig(layout.Paths, layout.Modes, flags | Native.Apply | Native.SaveToDatabase),
                            "Apply the display layout");
                    }
                }

                if (updates.Count > 0)
                    VerifyUpdates(updates);
                foreach (var change in changes.Values.Where(change => change.DynamicRefreshRate == true))
                    ApplyDynamicRefresh(change.Id, true, change.RefreshRate, () => attempted = true);
            }
            catch (Exception error) when (attempted)
            {
                try
                {
                    Restore(original);
                }
                catch (Exception rollbackError)
                {
                    throw new InvalidOperationException($"{error.Message} Restoring the original display settings also failed: {rollbackError.Message}",
                        new AggregateException(error, rollbackError));
                }

                throw;
            }
        }
    }

    private static bool HasChanges(DisplayChange change) => change.X.HasValue || change.Y.HasValue ||
        change.Width.HasValue || change.Height.HasValue || change.Orientation.HasValue || change.RefreshRate.HasValue ||
        change.DynamicRefreshRate.HasValue;

    private static bool MatchesTopology(List<AttachedDisplay> attached, string mode, string? soloId)
    {
        var active = attached.Where(display => display.Info.Active).Select(display => display.Info).ToList();
        if (active.Count == 0)
            return false;
        var positions = active.Select(display => (display.X, display.Y)).Distinct().Count();

        return mode switch
        {
            "extend" => active.Count >= 2 && positions == active.Count,
            "mirror" => active.Count >= 2 && positions == 1,
            "builtin" or "external" => active.All(display => display.Kind == mode),
            "solo" => active.Count == 1 && active[0].Id == soloId,
            _ => false
        };
    }

    private static void ApplyTopology(string mode, string? soloId, List<AttachedDisplay> attached, Action beforeApply)
    {
        if (mode is "extend" or "mirror" && attached.Count < 2)
            throw new InvalidOperationException("Connect at least two displays to use this mode.");

        if (mode is "builtin" or "external")
        {
            var matching = attached.Where(display => display.Info.Kind == mode).ToList();
            if (matching.Count == 0)
                throw new InvalidOperationException(mode == "builtin" ? "No built-in display is available." : "No external display is available.");
        }

        if (mode == "solo")
        {
            var selected = Find(attached, soloId);
            var candidates = Query(Native.AllPaths).Paths.Where(path =>
                path.Target.Adapter.Equals(selected.Path.Target.Adapter) && path.Target.Id == selected.Path.Target.Id &&
                (path.Target.Available != 0 || path.IsActive));
            var lastResult = Native.InvalidParameter;
            foreach (var candidate in candidates.OrderByDescending(path => path.IsActive))
            {
                var path = candidate;
                path.Flags = Native.PathActive;
                path.Source.ModeIndex = Native.InvalidModeIndex;
                path.Target.ModeIndex = Native.InvalidModeIndex;
                if (path.Target.Rotation is < 1 or > 4)
                    path.Target.Rotation = 1;
                path.Target.Scaling = 128;
                path.Target.RefreshRate = default;
                path.Target.ScanLineOrdering = 0;
                var flags = Native.SuppliedConfig | Native.AllowChanges;
                lastResult = SetConfig([path], null, flags | Native.Validate);
                if (lastResult != 0)
                    continue;

                beforeApply();
                CheckWin32(SetConfig([path], null, flags | Native.Apply | Native.SaveToDatabase),
                    $"Activate {selected.Info.Name} by itself");
                return;
            }

            CheckWin32(lastResult, $"Validate single-display mode for {selected.Info.Name}");
            return;
        }

        var topology = mode switch
        {
            "builtin" => Native.TopologyInternal,
            "external" => Native.TopologyExternal,
            "extend" => Native.TopologyExtend,
            _ => Native.TopologyClone
        };
        CheckWin32(SetConfig(null, null, Native.Validate | topology), $"Validate {mode} mode");
        beforeApply();
        CheckWin32(SetConfig(null, null, Native.Apply | topology), $"Switch to {mode} mode");
    }

    private static List<DisplayUpdate> BuildUpdates(List<AttachedDisplay> attached,
        Dictionary<string, DisplayChange> changes, string? primaryId)
    {
        var active = attached.Where(display => display.Info.Active).ToList();
        var updatePositions = primaryId is not null || changes.Values.Any(change => change.X.HasValue || change.Y.HasValue);
        if (active.GroupBy(display => (display.Info.X, display.Info.Y)).Any(group => group.Count() > 1))
            throw new InvalidOperationException("Per-display changes require independent displays. Switch to Extend first.");

        foreach (var change in changes.Values.Where(HasChanges))
            Find(active, change.Id);

        var originalPrimary = active.FirstOrDefault(display => display.Info.Primary)
            ?? throw new InvalidOperationException("Windows does not report an active primary display.");
        var primary = primaryId is null ? originalPrimary : Find(active, primaryId);
        var primaryChanged = primary.Info.Id != originalPrimary.Info.Id;
        changes.TryGetValue(primary.Info.Id, out var primaryChange);
        var originX = primaryChange?.X ?? primary.Info.X;
        var originY = primaryChange?.Y ?? primary.Info.Y;

        var result = new List<DisplayUpdate>();
        var rectangles = new List<(int X, int Y, int Width, int Height)>();
        foreach (var display in active)
        {
            changes.TryGetValue(display.Info.Id, out var change);
            var setPrimary = primaryChanged && display.Info.Id == primary.Info.Id;
            if (!updatePositions && (change is null || !HasChanges(change)))
                continue;

            var mode = display.CurrentMode;
            mode.Fields = 0;
            if (change is not null)
                ApplyModeFields(display, change, ref mode);
            if (updatePositions)
            {
                mode.X = (change?.X ?? display.Info.X) - originX;
                mode.Y = (change?.Y ?? display.Info.Y) - originY;
                if (mode.X is < -65535 or > 65535 || mode.Y is < -65535 or > 65535)
                    throw new InvalidOperationException("The displays are too far apart in the requested layout.");

                rectangles.Add((mode.X, mode.Y, mode.Width > 0 ? (int)mode.Width : display.Info.Width,
                    mode.Height > 0 ? (int)mode.Height : display.Info.Height));
                mode.Fields |= Native.DmPosition;
            }

            mode.DriverExtra = 0;
            if (mode.Fields != 0)
                result.Add(new DisplayUpdate(display, mode, setPrimary));
        }

        if (updatePositions)
            ValidateRectangles(rectangles);

        return result.OrderByDescending(update => update.SetPrimary).ToList();
    }

    private static Configuration? BuildPositionConfiguration(Configuration configuration, List<DisplayUpdate> updates)
    {
        var active = ReadAttachedDisplays(configuration, readModes: false);
        var changed = false;
        foreach (var update in updates.Where(update => (update.Mode.Fields & Native.DmPosition) != 0))
        {
            var display = Find(active, update.Display.Info.Id);
            var index = FindModeIndex(configuration, display.Path, source: true);
            if (GetMode(configuration.Modes, index, 1) is not Native.Mode sourceMode)
                throw new InvalidOperationException($"Windows did not report a desktop position for {display.Info.Name}.");
            if (sourceMode.Source.X == update.Mode.X && sourceMode.Source.Y == update.Mode.Y)
                continue;

            sourceMode.Source.X = update.Mode.X;
            sourceMode.Source.Y = update.Mode.Y;
            configuration.Modes[index] = sourceMode;
            changed = true;
        }

        if (!changed)
            return null;

        // CCD defines the source at (0, 0) as primary. Apply all positions together;
        // the previous primary cannot be moved away from (0, 0) in isolation.
        var ordered = configuration.Paths.OrderByDescending(path =>
            GetMode(configuration.Modes, FindModeIndex(configuration, path, source: true), 1)?.Source is { X: 0, Y: 0 }).ToArray();
        return configuration with { Paths = ordered };
    }

    private static void ApplyModeFields(AttachedDisplay display, DisplayChange change, ref Native.DevMode mode)
    {
        // A DRR base rate is a virtual rate, never a ChangeDisplaySettingsEx
        // physical frequency. Apply it later through the virtual-aware CCD API.
        double? fixedRate = change.DynamicRefreshRate == true ? null : change.RefreshRate;
        if (change.Width is null && change.Orientation is null && fixedRate is null)
            return;
        if (string.IsNullOrEmpty(display.Info.GdiName) || mode.Width == 0 || mode.Height == 0)
            throw new InvalidOperationException($"Windows does not provide an editable display mode for {display.Info.Name}.");

        var nativeOrientation = change.Orientation is int degrees ? ToDevOrientation(degrees) : mode.Orientation;
        var orientationChangesDimensions = (nativeOrientation & 1) != (mode.Orientation & 1);
        var width = (uint?)change.Width ?? (orientationChangesDimensions ? mode.Height : mode.Width);
        var height = (uint?)change.Height ?? (orientationChangesDimensions ? mode.Width : mode.Height);
        var currentFrequency = mode.Frequency;
        var currentFlags = mode.DisplayFlags;
        var actualRefresh = display.Info.RefreshRate;

        if (width == mode.Width && height == mode.Height && nativeOrientation == mode.Orientation &&
            (fixedRate is null || Math.Abs(fixedRate.Value - actualRefresh) < 0.1))
            return;

        var candidates = EnumerateDeviceModes(display.Info.GdiName)
            .Where(candidate => candidate.Width == width && candidate.Height == height && candidate.Orientation == nativeOrientation);
        if (fixedRate is double requestedRate)
        {
            candidates = candidates.Where(candidate => Math.Abs(candidate.Frequency - requestedRate) < 0.1 ||
                candidate.Frequency == currentFrequency && Math.Abs(actualRefresh - requestedRate) < 0.1);
        }

        var supported = candidates.OrderBy(candidate => Math.Abs((double)candidate.Frequency - currentFrequency))
            .ThenBy(candidate => candidate.DisplayFlags == currentFlags ? 0 : 1)
            .ThenByDescending(candidate => candidate.BitsPerPel)
            .FirstOrDefault();
        if (supported.Width == 0)
            throw new InvalidOperationException($"{display.Info.Name} does not advertise the requested display mode.");

        var updateFields = Native.DmWidth | Native.DmHeight | Native.DmBitsPerPel;
        updateFields |= supported.Fields & (Native.DmOrientation | Native.DmFrequency | Native.DmDisplayFlags);
        mode = supported;
        mode.Fields = updateFields;
    }

    private static void ValidateRectangles(List<(int X, int Y, int Width, int Height)> rectangles)
    {
        var adjacency = new List<int>[rectangles.Count];
        for (var index = 0; index < rectangles.Count; index++)
            adjacency[index] = [];

        for (var first = 0; first < rectangles.Count; first++)
        {
            var a = rectangles[first];
            for (var second = first + 1; second < rectangles.Count; second++)
            {
                var b = rectangles[second];
                var xOverlap = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
                var yOverlap = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
                if (xOverlap > 0 && yOverlap > 0)
                    throw new InvalidOperationException("Displays must not overlap in an extended layout.");
                if ((xOverlap == 0 && yOverlap > 0) || (yOverlap == 0 && xOverlap > 0))
                {
                    adjacency[first].Add(second);
                    adjacency[second].Add(first);
                }
            }
        }

        var connected = new HashSet<int>();
        var pending = new Queue<int>();
        if (rectangles.Count > 0)
            pending.Enqueue(0);
        while (pending.TryDequeue(out var index))
        {
            if (connected.Add(index))
                foreach (var neighbor in adjacency[index])
                    pending.Enqueue(neighbor);
        }

        if (connected.Count != rectangles.Count)
            throw new InvalidOperationException("Every display must share an edge with the rest of the extended desktop.");
    }

    private static void VerifyUpdates(List<DisplayUpdate> updates)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var current = ReadAttachedDisplays(Query(Native.ActivePaths), readModes: false);
            if (updates.All(update => UpdateMatches(current, update)))
                return;
            if (attempt < 3)
                Thread.Sleep(50);
        }

        throw new InvalidOperationException("The driver did not apply all of the requested display changes.");
    }

    private static bool UpdateMatches(List<AttachedDisplay> current, DisplayUpdate update)
    {
        var found = current.FirstOrDefault(display => display.Info.Id == update.Display.Info.Id);
        if (found is null)
            return false;

        var mode = update.Mode;
        return (!update.SetPrimary || found.Info.Primary) &&
            ((mode.Fields & Native.DmPosition) == 0 || found.Info.X == mode.X && found.Info.Y == mode.Y) &&
            ((mode.Fields & Native.DmWidth) == 0 || found.Info.Width == mode.Width && found.Info.Height == mode.Height) &&
            ((mode.Fields & Native.DmOrientation) == 0 || found.CurrentMode.Orientation == mode.Orientation) &&
            ((mode.Fields & Native.DmFrequency) == 0 || found.CurrentMode.Frequency == mode.Frequency ||
                Math.Abs(found.Info.RefreshRate - mode.Frequency) < 1);
    }

    private static void CheckDisplayChange(int result, string operation)
    {
        if (result == 0)
            return;
        var reason = result switch
        {
            1 => "Windows requires a restart for these settings",
            -1 => "the display driver rejected the settings",
            -2 => "the graphics mode is unsupported",
            -3 => "the settings could not be written to the registry",
            -4 => "the driver rejected the operation flags",
            -5 => "the driver rejected a parameter",
            -6 => "the display adapter does not support this multi-display configuration",
            _ => "Windows returned an unexpected error"
        };
        throw new InvalidOperationException($"{operation} failed: {reason} ({result}).");
    }
}
