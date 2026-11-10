using System.Diagnostics;
using MonitorSetting;

// Deterministic scheduler/cache regression. Fake hardware only; no Windows settings change.
using var externalEntered = new ManualResetEventSlim();
using var externalRelease = new ManualResetEventSlim();
int externalReads = 0;
int notifications = 0;
var loader = new ProgressiveControls(() => new FakeDevice((list, id) =>
{
    var display = list.Single(display => display.Id == id);
    if (id == "external")
    {
        Interlocked.Increment(ref externalReads);
        externalEntered.Set();
        if (!externalRelease.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Fake external probe not released.");
    }
    foreach (string name in new[] { "brightness", "contrast", "sharpness", "temperature", "response" })
        display.Controls[name] = name == "brightness"
            ? new() { Supported = true, Min = 0, Max = 100, Value = id == "builtin" ? 50 : 75 }
            : new() { Reason = "unsupported" };
}));
loader.Changed += () => Interlocked.Increment(ref notifications);

List<DisplayInfo> Topology() => [
    new() { Id = "builtin", Kind = "builtin", Active = true, DevicePath = "internal-path", GdiName = "display1" },
    new() { Id = "external", Kind = "external", Active = true, DevicePath = "external-path", GdiName = "display2" }
];
void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS " + message);
}
void Await(Func<bool> ready, string failure)
{
    if (!SpinWait.SpinUntil(ready, TimeSpan.FromSeconds(5))) throw new Exception(failure);
}

var first = Topology();
var timer = Stopwatch.StartNew();
loader.Read(first);
Assert(timer.ElapsedMilliseconds < 250, "First snapshot returns without waiting for any hardware probe");
Assert(first.Count == 2 && first.All(display => display.ControlsLoading), "Identified displays arrive with honest loading states");
Assert(externalEntered.Wait(TimeSpan.FromSeconds(3)), "Slow external probe entered");
Await(() => Volatile.Read(ref notifications) >= 1, "Built-in probe did not publish");
var duringSlowRead = Topology();
timer.Restart(); loader.Read(duringSlowRead, refresh: false);
Assert(timer.ElapsedMilliseconds < 100, "Cached snapshot reads remain immediate during external probe");
Assert(!duringSlowRead[0].ControlsLoading && duringSlowRead[1].ControlsLoading, "One device becomes ready independently");
timer.Restart(); loader.Set("builtin", "brightness", 51, Topology());
Assert(timer.ElapsedMilliseconds < 100, "Built-in write is not blocked by external I/O (fake setter)");
var afterWrite = Topology(); loader.Read(afterWrite, refresh: false);
Assert(afterWrite[0].Controls["brightness"].Value == 51, "Verified targeted write updates progressive snapshot");
try { loader.Set("external", "brightness", 52, Topology()); throw new Exception("Expected loading rejection"); }
catch (InvalidOperationException error) { Assert(error.Message == "loading", "Racing same-device write fails promptly instead of blocking command queue"); }
afterWrite[0].Controls["brightness"].Value = -100;
var isolated = Topology(); loader.Read(isolated, refresh: false);
Assert(isolated[0].Controls["brightness"].Value == 51, "UI cannot mutate cached values");

int eventsBeforeInvalidation = Volatile.Read(ref notifications);
loader.Invalidate();
var invalidated = Topology(); loader.Read(invalidated, allowProbes: false);
externalRelease.Set();
Thread.Sleep(100);
var stale = Topology(); loader.Read(stale, refresh: false, allowProbes: false);
Assert(stale[1].ControlsLoading && stale[1].Controls["brightness"].Reason == "loading", "Old external result cannot refill an invalidated cache");
Assert(Volatile.Read(ref notifications) == eventsBeforeInvalidation, "Invalidated job does not publish completion");

var restarted = Topology(); loader.Read(restarted);
Await(() => Volatile.Read(ref externalReads) >= 2 && Volatile.Read(ref notifications) >= eventsBeforeInvalidation + 2,
    "Fresh generation probes did not finish");
var complete = Topology(); loader.Read(complete, refresh: false);
Assert(complete.All(display => !display.ControlsLoading), "New generation finishes normally");
var disconnected = Topology(); disconnected.RemoveAt(1); loader.Read(disconnected, allowProbes: false);
Assert(disconnected.Count == 1, "Disconnected display is absent from snapshots");

externalRelease.Reset(); externalEntered.Reset();
loader.Invalidate(); loader.Read(Topology());
Assert(externalEntered.Wait(TimeSpan.FromSeconds(3)), "Probe running before disposal");
timer.Restart(); loader.Dispose();
Assert(timer.ElapsedMilliseconds < 100, "Closing does not wait for slow native reads");
int beforeRelease = Volatile.Read(ref notifications);
externalRelease.Set(); Thread.Sleep(100);
Assert(Volatile.Read(ref notifications) == beforeRelease, "Disposed loader discards late completion");

internal sealed class FakeDevice(Action<List<DisplayInfo>, string> read) : IControlDevice
{
    public void Invalidate() { }
    public void Populate(List<DisplayInfo> topology, string id) => read(topology, id);
    public void Set(string id, string control, double value, List<DisplayInfo> topology) =>
        topology.Single(display => display.Id == id).Controls[control] = new() { Supported = true, Value = value, Min = 0, Max = 100 };
}
