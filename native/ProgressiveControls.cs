using System.Diagnostics;

namespace MonitorSetting;

internal interface IControlDevice
{
    void Invalidate();
    void Populate(List<DisplayInfo> topology, string id);
    void Set(string id, string control, double value, List<DisplayInfo> topology);
}

internal sealed class NativeControlDevice : IControlDevice
{
    private readonly MonitorControls controls = new();
    public void Invalidate() => controls.Invalidate();
    public void Populate(List<DisplayInfo> topology, string id) => controls.PopulateDevice(topology, id);
    public void Set(string id, string control, double value, List<DisplayInfo> topology) => controls.Set(id, control, value, topology);
}

// No WMI or DDC/CI call runs while the snapshot lock is held. One physical device
// can be slow without holding up another device, topology reads, or confirmation.
internal sealed class ProgressiveControls : IDisposable
{
    private static readonly string[] Names = ["brightness", "contrast", "sharpness", "temperature", "response"];
    private readonly object gate = new();
    private readonly Func<IControlDevice> createDevice;
    private readonly Dictionary<string, Worker> workers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private long epoch;
    private string topologyKey = "";
    private bool disposed;

    private sealed class Worker(IControlDevice device)
    {
        public readonly IControlDevice Device = device;
        public readonly SemaphoreSlim Io = new(1);
        public long Epoch = -1;
    }

    private sealed class Entry
    {
        public Dictionary<string, ControlInfo>? Values;
        public long ReadAt;
        public bool Running;
        public bool Complete;
    }

    public ProgressiveControls(Func<IControlDevice>? createDevice = null) => this.createDevice = createDevice ?? (() => new NativeControlDevice());
    public event Action? Changed;

    public void Invalidate()
    {
        lock (gate)
        {
            epoch++;
            entries.Clear();
            topologyKey = "";
        }
    }

    public void Read(List<DisplayInfo> displays, bool refresh = true, bool allowProbes = true)
    {
        List<(string Id, long Epoch, Worker Worker, Entry Entry)> queued = [];
        lock (gate)
        {
            if (disposed) return;
            EnsureTopology(displays);
            foreach (var display in displays)
            {
                if (!display.Active)
                {
                    foreach (string name in Names) display.Controls[name] = new() { Reason = "inactive" };
                    display.ControlsLoading = false;
                    continue;
                }
                if (!entries.TryGetValue(display.Id, out var entry)) entries[display.Id] = entry = new();
                if (allowProbes && !entry.Running && (!entry.Complete || refresh && Stopwatch.GetElapsedTime(entry.ReadAt) >= TimeSpan.FromSeconds(5)))
                {
                    entry.Running = true;
                    queued.Add((display.Id, epoch, GetWorker(display.Id), entry));
                }
                foreach (string name in Names)
                    display.Controls[name] = entry.Values is not null && entry.Values.TryGetValue(name, out var value)
                        ? Copy(value) : new() { Reason = name == "response" ? "vendorSpecific" : "loading" };
                display.ControlsLoading = entry.Running || !entry.Complete;
            }
        }
        // Never pass the UI snapshot into a background writer.
        foreach (var job in queued)
        {
            var snapshot = displays.Select(CopyDisplay).ToList();
            _ = Task.Run(() => Probe(job.Id, job.Epoch, job.Worker, job.Entry, snapshot));
        }
    }

    private async Task Probe(string id, long expectedEpoch, Worker worker, Entry entry, List<DisplayInfo> snapshot)
    {
        await worker.Io.WaitAsync().ConfigureAwait(false);
        bool publish = false;
        try
        {
            lock (gate)
                if (!IsCurrent(id, expectedEpoch, entry)) return;
            Prepare(worker, expectedEpoch);
            var display = snapshot.Single(display => display.Id == id);
            try { worker.Device.Populate(snapshot, id); }
            catch
            {
                foreach (string name in Names) display.Controls[name] = new() { Reason = "driver" };
            }
            lock (gate)
            {
                if (!IsCurrent(id, expectedEpoch, entry)) return;
                entry.Values = HardwareValues(display);
                entry.ReadAt = Stopwatch.GetTimestamp();
                entry.Running = false;
                entry.Complete = true;
                publish = true;
            }
        }
        finally { worker.Io.Release(); }
        if (publish) Changed?.Invoke();
    }

    public void Set(string id, string control, double value, List<DisplayInfo> displays)
    {
        Worker worker;
        Entry entry;
        long expectedEpoch;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            EnsureTopology(displays);
            if (!displays.Any(display => display.Id == id && display.Active)) throw new InvalidOperationException("displayNotFound");
            worker = GetWorker(id);
            if (!entries.TryGetValue(id, out entry!)) entries[id] = entry = new();
            expectedEpoch = epoch;
        }
        // UI disables hardware controls during a probe; a racing request should fail
        // promptly instead of queuing all commands behind a slow physical bus.
        if (!worker.Io.Wait(0)) throw new InvalidOperationException("loading");
        try
        {
            lock (gate)
                if (!IsCurrent(id, expectedEpoch, entry)) throw new InvalidOperationException("displayNotFound");
            Prepare(worker, expectedEpoch);
            worker.Device.Set(id, control, value, displays);
            var display = displays.Single(display => display.Id == id);
            lock (gate)
            {
                if (!IsCurrent(id, expectedEpoch, entry)) return;
                entry.Values ??= new(StringComparer.Ordinal);
                entry.Values[control] = Copy(display.Controls[control]);
            }
        }
        catch
        {
            lock (gate)
                if (IsCurrent(id, expectedEpoch, entry)) entry.ReadAt = 0;
            throw;
        }
        finally { worker.Io.Release(); }
    }

    private Worker GetWorker(string id)
    {
        if (!workers.TryGetValue(id, out var worker)) workers[id] = worker = new(createDevice());
        return worker;
    }

    private static void Prepare(Worker worker, long expectedEpoch)
    {
        if (worker.Epoch == expectedEpoch) return;
        worker.Device.Invalidate();
        worker.Epoch = expectedEpoch;
    }

    private bool IsCurrent(string id, long expectedEpoch, Entry entry) => !disposed && epoch == expectedEpoch
        && entries.TryGetValue(id, out var current) && ReferenceEquals(current, entry);

    private void EnsureTopology(List<DisplayInfo> displays)
    {
        string key = string.Join("\n", displays.OrderBy(display => display.Id, StringComparer.Ordinal)
            .Select(display => $"{display.Id}|{display.Active}|{display.Kind}|{display.DevicePath}|{display.GdiName}"));
        if (key == topologyKey) return;
        epoch++;
        entries.Clear();
        topologyKey = key;
    }

    private static Dictionary<string, ControlInfo> HardwareValues(DisplayInfo display) => Names.ToDictionary(name => name,
        name => display.Controls.TryGetValue(name, out var value) ? Copy(value) : new ControlInfo { Reason = "unsupported" }, StringComparer.Ordinal);

    private static ControlInfo Copy(ControlInfo value) => new()
    {
        Supported = value.Supported, Reason = value.Reason, Value = value.Value, Min = value.Min, Max = value.Max, Step = value.Step,
        Options = value.Options.Select(option => new ControlOption { Value = option.Value, Label = option.Label }).ToList()
    };

    private static DisplayInfo CopyDisplay(DisplayInfo display) => new()
    {
        Id = display.Id, Name = display.Name, Kind = display.Kind, Connection = display.Connection,
        DevicePath = display.DevicePath, GdiName = display.GdiName, Active = display.Active, Primary = display.Primary,
        X = display.X, Y = display.Y, Width = display.Width, Height = display.Height, Orientation = display.Orientation,
        RefreshRate = display.RefreshRate, Modes = display.Modes, Controls = display.Controls.ToDictionary(pair => pair.Key, pair => Copy(pair.Value))
    };

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            epoch++;
            entries.Clear();
        }
        // Native read calls cannot be cancelled safely. Their results are discarded;
        // they retain and dispose their own physical handles without delaying close.
    }
}
