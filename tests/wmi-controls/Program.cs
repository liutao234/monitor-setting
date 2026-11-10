using System.Diagnostics;
using System.Management;
using System.Reflection;
using System.Text.Json;
using MonitorSetting;

// Read-only by default. Explicit --write makes one nearby built-in brightness change,
// verifies the WMI provider's value, and restores the original value in finally.
if (args.Length == 0) throw new ArgumentException("Pass the --diagnostics JSON path, optionally followed by --write.");
var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
var baseline = JsonDocument.Parse(File.ReadAllText(args[0])).RootElement.GetProperty("displays");
List<DisplayInfo> Devices() => baseline.Deserialize<List<DisplayInfo>>(json)!;
var controls = new MonitorControls();
var readings = new Dictionary<string, double>();
void Timed(string label, Action action)
{
    var watch = Stopwatch.StartNew(); action(); watch.Stop();
    readings[label] = Math.Round(watch.Elapsed.TotalMilliseconds, 2);
    Console.WriteLine($"{label}: {readings[label]} ms");
}
void Assert(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    Console.WriteLine("PASS " + description);
}

var initial = Devices();
Timed("coldPopulate", () => controls.Populate(initial));
var builtin = initial.Single(display => display.Active && display.Kind == "builtin");
Assert(builtin.Controls["brightness"].Supported, "Built-in brightness is available");
var warm = Devices();
Timed("warmPopulate", () => controls.Populate(warm));
warm.Single(display => display.Id == builtin.Id).Controls["brightness"].Value = -500;
var cached = Devices();
Timed("applyCached", () => controls.ApplyCached(cached));
Assert(cached.Single(display => display.Id == builtin.Id).Controls["brightness"].Value == builtin.Controls["brightness"].Value,
    "Consumer changes do not mutate cached control values");

// Regression for the original InvalidClass failure: the returned method instance
// must retain root\\wmi even when re-created, and its parameters must be accessible.
using (var search = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods"))
using (var results = search.Get())
foreach (ManagementObject item in results)
{
    using (item)
    {
        if (item["Active"] is not true) continue;
        var method = typeof(MonitorControls).GetMethod("QueryBrightnessMethod", BindingFlags.NonPublic | BindingFlags.Static)!;
        string path = (string)method.Invoke(null, [item["InstanceName"]])!;
        Assert(path.Contains(@"\root\wmi:", StringComparison.OrdinalIgnoreCase), "Method lookup retains root\\wmi namespace");
        using var reopened = new ManagementObject(new ManagementScope(@"\\.\root\wmi"), new ManagementPath(path), null);
        using var parameters = reopened.GetMethodParameters("WmiSetBrightness");
        Assert(parameters.Properties["Timeout"].Type == CimType.UInt32 && parameters.Properties["Brightness"].Type == CimType.UInt8,
            "Reopened method exposes valid UInt32 timeout and UInt8 brightness parameters");
    }
}

double ReadBuiltin()
{
    var single = new List<DisplayInfo> { builtin };
    var reader = new MonitorControls(); reader.Populate(single, forceRefresh: true);
    return single[0].Controls["brightness"].Value ?? throw new Exception("No real WMI readback");
}

if (args.Contains("--write"))
{
    double original = ReadBuiltin();
    var info = builtin.Controls["brightness"];
    double requested = original >= info.Max ? original - (info.Step ?? 1) : original + (info.Step ?? 1);
    Assert(requested >= info.Min && requested <= info.Max && requested != original, "Adjacent brightness level selected");
    try
    {
        var devices = Devices();
        Timed("builtinWriteAndReadback", () => controls.Set(builtin.Id, "brightness", requested, devices));
        Assert(ReadBuiltin() == requested, $"Physical WMI provider readback equals requested {requested}%");
        var postWrite = Devices(); controls.ApplyCached(postWrite);
        Assert(postWrite.Single(display => display.Id == builtin.Id).Controls["brightness"].Value == requested,
            "Post-write cached snapshot contains verified value");
    }
    finally
    {
        controls.Set(builtin.Id, "brightness", original, Devices());
        Assert(ReadBuiltin() == original, $"Restored original physical brightness {original}%");
    }
}

var changed = Devices();
changed[0].DevicePath += "-test-replacement";
foreach (var display in changed) display.Controls.Clear();
controls.ApplyCached(changed);
Assert(changed.All(display => display.Controls.Count == 0), "Changed device identity invalidates topology cache without hardware IO");
controls.Invalidate();
var empty = Devices(); foreach (var display in empty) display.Controls.Clear(); controls.ApplyCached(empty);
Assert(empty.All(display => display.Controls.Count == 0), "Explicit invalidation removes every cached control");
Console.WriteLine(JsonSerializer.Serialize(readings));
