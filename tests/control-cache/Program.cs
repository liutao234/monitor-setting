using System.Collections;
using System.Diagnostics;
using System.Reflection;
using MonitorSetting;

// Cache policy only: this test never opens or writes a physical display.
var controls = new MonitorControls();
var display = new DisplayInfo { Id = "external", Active = true, DevicePath = "device-A", GdiName = "display1" };
var getter = typeof(MonitorControls).GetMethod("CachedWriteControl", BindingFlags.Instance | BindingFlags.NonPublic)!;
var remember = typeof(MonitorControls).GetMethod("RememberControl", BindingFlags.Instance | BindingFlags.NonPublic)!;
ControlInfo? Cached(string id = "external", string name = "brightness") => (ControlInfo?)getter.Invoke(controls, [id, name]);
void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS " + message);
}
void Remember(bool supported = true)
{
    display.Controls["brightness"] = new() { Supported = supported, Min = 0, Max = 100, Step = 1, Value = 42 };
    remember.Invoke(controls, [display, "brightness"]);
}

controls.ApplyCached([display]);
Assert(Cached() is null, "A device without a verified cache requires a fresh hardware read");
Remember();
var first = Cached();
Assert(first is { Supported: true, Min: 0, Max: 100, Value: 42 }, "Recent verified bounds can be reused during continuous input");
first!.Max = 1000;
Assert(Cached()!.Max == 100, "Consumers cannot mutate cached write bounds");
Assert(Cached("other-device") is null && Cached(name: "contrast") is null, "Bounds are isolated by device and parameter");
Remember(supported: false);
Assert(Cached() is null, "Unsupported cached controls never authorize a write");
Remember();
var cache = (IDictionary)typeof(MonitorControls).GetField("controlCache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controls)!;
var oldEntry = cache[display.Id]!;
var timestamps = (Dictionary<string, long>)oldEntry.GetType().GetProperty("ControlReadAt")!.GetValue(oldEntry)!;
timestamps["brightness"] = Stopwatch.GetTimestamp() - 6 * Stopwatch.Frequency;
Assert(Cached() is null, "Expired bounds require a fresh hardware read");
display.Controls["contrast"] = new() { Supported = true, Min = 0, Max = 100, Value = 50 };
remember.Invoke(controls, [display, "contrast"]);
Assert(Cached() is null && Cached(name: "contrast") is not null, "Writing another parameter does not renew stale bounds");
Remember();
display.DevicePath = "replacement-device";
controls.ApplyCached([display]);
Assert(Cached() is null, "A replaced physical display invalidates write bounds");
Remember();
controls.Invalidate();
Assert(Cached() is null, "Explicit refresh invalidates write bounds");
