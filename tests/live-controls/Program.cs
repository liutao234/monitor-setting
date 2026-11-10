using System.Diagnostics;
using System.Text.Json;
using MonitorSetting;

// Real hardware verification is deliberately opt-in. Each display is restored
// in finally, including when a write or an independent readback fails.
if (!args.Contains("--write")) throw new ArgumentException("Explicit --write is required for the small brightness sweep.");
var service = new DisplayService();
var controls = new MonitorControls();
var devices = service.Enumerate();
controls.Populate(devices);
var reports = new List<object>();
foreach (var device in devices.Where(display => display.Active && display.Controls.GetValueOrDefault("brightness")?.Supported == true))
{
    var capability = device.Controls["brightness"];
    if (capability.Value is not double original || capability.Min is not double min || capability.Max is not double max) continue;
    double step = capability.Step ?? 1;
    if (step > 2 || step <= 0) continue;
    double direction = original + 2 * step <= max ? 1 : -1;
    var samples = new[] { original + direction * step, original + direction * 2 * step, original + direction * step };
    if (samples.Any(value => value < min || value > max)) continue;
    var times = new List<double>();
    try
    {
        foreach (double requested in samples)
        {
            var current = service.Enumerate(includeModes: false);
            var watch = Stopwatch.StartNew();
            controls.Set(device.Id, "brightness", requested, current);
            times.Add(Math.Round(watch.Elapsed.TotalMilliseconds, 2));
            if (current.Single(display => display.Id == device.Id).Controls["brightness"].Value != requested)
                throw new Exception("Brightness readback did not match the streamed value.");
        }
    }
    finally
    {
        controls.Set(device.Id, "brightness", original, service.Enumerate(includeModes: false));
        var restored = service.Enumerate(includeModes: false);
        new MonitorControls().PopulateDevice(restored, device.Id);
        if (restored.Single(display => display.Id == device.Id).Controls["brightness"].Value != original)
            throw new Exception("Brightness restoration could not be independently verified.");
    }
    reports.Add(new { device.Name, device.Kind, original, samples, writeAndReadbackMs = times, restored = true });
}
if (reports.Count == 0) throw new Exception("No display supports the bounded brightness sweep.");
Console.WriteLine(JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
