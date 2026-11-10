using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using MonitorSetting;
using Native = MonitorSetting.DisplayNative;

// ABI, capability and snapshot checks only. No SetDisplayConfig APPLY calls.
Require(Marshal.SizeOf<Native.KernelDisplayMode>() == 44, "D3DKMT_DISPLAYMODE ABI must be 44 bytes.");
Require(Marshal.OffsetOf<Native.KernelDisplayMode>("AttributeFlags").ToInt32() == 40,
    "VirtualRefreshRate belongs in the second flags word.");
Require(Marshal.SizeOf<Native.OpenGdiAdapter>() == 80, "GDI adapter request ABI must be 80 bytes.");
Require(Marshal.SizeOf<Native.KernelDisplayModeList>() == (IntPtr.Size == 8 ? 24 : 16), "Mode-list pointer ABI mismatch.");
var candidates = new[]
{
    KernelMode(1920,1080,1,60,0x100), KernelMode(1920,1080,1,60,0x100),
    KernelMode(1920,1080,1,120,0), KernelMode(1920,1080,1,240,0),
    KernelMode(1280,720,1,60,0x100), KernelMode(1920,1080,2,60,0x100),
    new Native.KernelDisplayMode { Width=1920, Height=1080, DisplayOrientation=1,
        RefreshRate=new Native.Rational { Numerator=120, Denominator=1 }, ValidationFlags=0x100 }
};
var dynamicRates = (IEnumerable<Native.Rational>)Call("DynamicRates", candidates, 1920, 1080, 1u)!;
Require(dynamicRates.Select(rate => rate.Hertz).SequenceEqual([60d]),
    "DRR must require virtual-rate flag in correct word and matching dimensions/orientation; ordinary high-Hz and duplicate modes are excluded.");

var path = new Native.Path
{
    Flags = Native.PathActive | Native.PathVirtualMode | 0x400,
    Source = new Native.SourcePath { ModeIndex=0x00030002 },
    Target = new Native.TargetPath { ModeIndex=0x00070005, RefreshRate=new Native.Rational { Numerator=120, Denominator=1 } }
};
var enabled = (Native.Path)Call("ConfigureDynamicPath", path, true, new Native.Rational { Numerator=60, Denominator=1 })!;
Require(enabled.Flags == (path.Flags | Native.PathBoostRefresh) && enabled.Source.ModeIndex == path.Source.ModeIndex &&
    enabled.Target.ModeIndex == path.Target.ModeIndex && enabled.Target.RefreshRate.Hertz == 60,
    "Enabling boost must preserve other path flags, source index, physical timing and packed desktop-image index.");
var disabled = (Native.Path)Call("ConfigureDynamicPath", enabled, false, new Native.Rational { Numerator=120, Denominator=1 })!;
Require(disabled.Flags == path.Flags && disabled.Target.RefreshRate.Hertz == 120,
    "Disabling boost must retain unrelated flags and restore physical rate.");
var legacy = path; legacy.Flags &= ~Native.PathVirtualMode;
var legacyEnabled = (Native.Path)Call("ConfigureDynamicPath", legacy, true, new Native.Rational { Numerator=60, Denominator=1 })!;
Require(legacyEnabled.Target.ModeIndex == legacy.Target.ModeIndex, "Unpacked physical mode index must be retained.");

// Snapshot JSON byte payload must preserve DRR mode flags and exact rationals.
var original = new DisplaySnapshot { Paths=MemoryMarshal.AsBytes(new[] { enabled }.AsSpan()).ToArray(),
    Modes=[], VirtualRefreshRates=true };
var roundTrip = JsonSerializer.Deserialize<DisplaySnapshot>(JsonSerializer.Serialize(original))!;
var restored = MemoryMarshal.Cast<byte, Native.Path>(roundTrip.Paths.AsSpan())[0];
Require(roundTrip.VirtualRefreshRates && restored.Flags == enabled.Flags &&
    restored.Target.ModeIndex == enabled.Target.ModeIndex && restored.Target.RefreshRate.Hertz == 60,
    "Rollback persistence must retain virtual-aware flag, boost flag and rate fields.");

var service = new DisplayService();
var displays = service.Enumerate();
var validations = new List<object>();
Require(displays.Count > 0, "Read-only integration requires a connected display.");
foreach (var display in displays)
{
    Require(display.Controls.TryGetValue("dynamicRefreshRate", out var control), "Every display needs explicit DRR capability.");
    Require(control!.Value == (display.DynamicRefreshRate ? 1 : 0), "DRR capability and current state disagree.");
    Require(control.Supported || control.Reason.Length > 0, "Unavailable DRR needs a reason.");
    if (display.Active) Require(display.PhysicalRefreshRate > 1, "Active display physical refresh missing.");
}
// Validate actual virtual/physical pairs without applying them. All advertised
// options must resolve to a complete configuration accepted by Windows.
var config = Call("Query", Native.ActivePaths)!;
var configType = config.GetType();
var livePaths = (Native.Path[])configType.GetProperty("Paths")!.GetValue(config)!;
var liveModes = (Native.Mode[])configType.GetProperty("Modes")!.GetValue(config)!;
foreach (var display in displays.Where(display => display.Active && display.Controls["dynamicRefreshRate"].Supported))
{
    int index = Array.FindIndex(livePaths, path => (string)Call("GetGdiName", path.Source)! == display.GdiName);
    Require(index >= 0, "Capable display must have an active source.");
    foreach (var option in display.Controls["dynamicRefreshRate"].Options)
    {
        double hertz = double.Parse(option.Value,System.Globalization.CultureInfo.InvariantCulture);
        var candidate=Call("BuildDynamicConfiguration",config,livePaths[index],
            new Native.Rational { Numerator=(uint)Math.Round(hertz*1000), Denominator=1000 });
        Require(candidate is not null, "Every advertised DRR option must pass Windows validation.");
        validations.Add(new { display.Name, baseHertz=hertz, accepted=true });
    }
}
Console.WriteLine(JsonSerializer.Serialize(new { passed=true, writesPerformed=false,
    checks=new[] { "native ABI", "virtual-only mode capability", "resolution and orientation filtering", "packed mode index preservation",
        "boost disable", "rollback JSON preserves DRR", "real Windows capability read" },
    validations, displays=displays.Select(display => new { display.Name, display.RefreshRate, display.PhysicalRefreshRate,
        display.DynamicRefreshRate, capability=display.Controls["dynamicRefreshRate"] })
}, new JsonSerializerOptions { WriteIndented=true }));

static Native.KernelDisplayMode KernelMode(uint width,uint height,uint orientation,uint refresh,uint flags) => new()
{ Width=width, Height=height, DisplayOrientation=orientation, RefreshRate=new Native.Rational { Numerator=refresh,Denominator=1 }, AttributeFlags=flags };
static object? Call(string name, params object[] arguments) => typeof(DisplayService).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null,arguments);
static void Require(bool condition,string message) { if (!condition) throw new InvalidOperationException(message); }
