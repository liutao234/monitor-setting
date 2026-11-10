namespace MonitorSetting;

public sealed class DisplayInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "external";
    public string Connection { get; set; } = "";
    public string GdiName { get; set; } = "";
    public string DevicePath { get; set; } = "";
    public bool Active { get; set; }
    public bool Primary { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Orientation { get; set; }
    public double RefreshRate { get; set; }
    public bool DynamicRefreshRate { get; set; }
    public double PhysicalRefreshRate { get; set; }
    public List<DisplayMode> Modes { get; set; } = new();
    public Dictionary<string, ControlInfo> Controls { get; set; } = new();
    public bool ControlsLoading { get; set; }
}

public sealed class DisplayMode
{
    public int Width { get; set; }
    public int Height { get; set; }
    public double RefreshRate { get; set; }
    public int Orientation { get; set; }
}

public sealed class ControlInfo
{
    public bool Supported { get; set; }
    public string Reason { get; set; } = "";
    public double? Value { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? Step { get; set; }
    public List<ControlOption> Options { get; set; } = new();
}

public sealed class ControlOption
{
    public string Value { get; set; } = "";
    public string Label { get; set; } = "";
}

public sealed class LayoutRequest
{
    public string? Mode { get; set; }
    public string? SoloId { get; set; }
    public string? PrimaryId { get; set; }
    public List<DisplayChange> Displays { get; set; } = new();
}

public sealed class DisplayChange
{
    public string Id { get; set; } = "";
    public int? X { get; set; }
    public int? Y { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? Orientation { get; set; }
    public double? RefreshRate { get; set; }
    public bool? DynamicRefreshRate { get; set; }
}
