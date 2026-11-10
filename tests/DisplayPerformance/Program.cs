using System.Diagnostics;
using System.Text.Json;
using MonitorSetting;

// Read-only Windows integration checks. No display settings are written.
var service = new DisplayService();
var watch = Stopwatch.StartNew();
var first = service.Enumerate();
var coldEnumerateMs = watch.Elapsed.TotalMilliseconds;
watch.Restart();
var firstCapabilities = service.GetModeCapabilities(first);
var coldCapabilitiesMs = watch.Elapsed.TotalMilliseconds;
Require(first.Count > 0, "At least one connected display is required.");
var originalModes = Modes(first);
var originalCapabilities = Capabilities(firstCapabilities);

var enumerateTimes = new List<double>();
var capabilityTimes = new List<double>();
for (var iteration = 0; iteration < 8; iteration++)
{
    watch.Restart();
    var current = service.Enumerate();
    enumerateTimes.Add(watch.Elapsed.TotalMilliseconds);
    watch.Restart();
    var capabilities = service.GetModeCapabilities(current);
    capabilityTimes.Add(watch.Elapsed.TotalMilliseconds);
    Require(Modes(current) == originalModes, "A warm read changed advertised modes.");
    Require(Capabilities(capabilities) == originalCapabilities, "A warm read changed topology capabilities.");
}

// A consumer must never be able to poison cached capability or mode objects.
var writableMode = first.SelectMany(display => display.Modes).FirstOrDefault();
if (writableMode is not null) writableMode.Width = -1;
firstCapabilities["extend"].Supported = !firstCapabilities["extend"].Supported;
var isolated = service.Enumerate();
Require(Modes(isolated) == originalModes, "Returned mode objects share mutable cache storage.");
Require(Capabilities(service.GetModeCapabilities(isolated)) == originalCapabilities,
    "Returned capability objects share mutable cache storage.");

watch.Restart();
var light = service.Enumerate(includeModes: false);
var lightEnumerateMs = watch.Elapsed.TotalMilliseconds;
Require(light.All(display => display.Modes.Count == 0), "A light read enumerated advertised modes.");
Require(Geometry(light) == Geometry(isolated), "A light read lost current display geometry.");

service.InvalidateCaches();
var refreshed = service.Enumerate();
Require(Modes(refreshed) == originalModes, "Invalidation did not preserve current advertised modes.");
Require(Capabilities(service.GetModeCapabilities(refreshed, forceRefresh: true)) == originalCapabilities,
    "Fresh Windows validation disagrees with cached capabilities.");

Console.WriteLine(JsonSerializer.Serialize(new
{
    passed = true,
    displays = refreshed.Select(display => new { display.Name, modes = display.Modes.Count }),
    coldEnumerateMs, coldCapabilitiesMs,
    warmEnumerateMedianMs = Median(enumerateTimes), warmCapabilitiesMedianMs = Median(capabilityTimes),
    lightEnumerateMs,
    checks = new[] { "cached modes match fresh reads", "cached capabilities match live validation",
        "returned objects cannot mutate caches", "light reads preserve live geometry", "explicit invalidation" }
}, new JsonSerializerOptions { WriteIndented = true }));

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static double Median(List<double> values) => values.Order().ElementAt(values.Count / 2);
static string Modes(List<DisplayInfo> displays) => JsonSerializer.Serialize(displays.OrderBy(display => display.Id)
    .Select(display => new { display.Id, display.Modes }));
static string Geometry(List<DisplayInfo> displays) => JsonSerializer.Serialize(displays.OrderBy(display => display.Id)
    .Where(display => display.Active).Select(display => new
    {
        display.Id, display.Name, display.Kind, display.Active, display.Primary, display.GdiName,
        display.X, display.Y, display.Width, display.Height, display.Orientation, display.RefreshRate
    }));
static string Capabilities(Dictionary<string, ControlInfo> controls) => JsonSerializer.Serialize(controls.OrderBy(pair => pair.Key)
    .Select(pair => new { pair.Key, pair.Value.Supported, pair.Value.Reason }));
