using System;
using System.Runtime.InteropServices;

namespace MonitorSetting;

internal static class DisplayNative
{
    internal const uint AllPaths = 0x1;
    internal const uint ActivePaths = 0x2;
    internal const uint QueryVirtualModes = 0x10;
    internal const uint QueryVirtualRefresh = 0x40;
    internal const uint PathActive = 0x1;
    internal const uint PathVirtualMode = 0x8;
    internal const uint PathBoostRefresh = 0x10;
    internal const uint InvalidModeIndex = uint.MaxValue;

    internal const uint TopologyInternal = 0x1;
    internal const uint TopologyClone = 0x2;
    internal const uint TopologyExtend = 0x4;
    internal const uint TopologyExternal = 0x8;
    internal const uint DatabaseCurrent = 0xf;
    internal const uint SuppliedConfig = 0x20;
    internal const uint Validate = 0x40;
    internal const uint Apply = 0x80;
    internal const uint NoOptimization = 0x100;
    internal const uint SaveToDatabase = 0x200;
    internal const uint AllowChanges = 0x400;
    internal const uint SetVirtualModes = 0x8000;
    internal const uint SetVirtualRefresh = 0x20000;

    internal const int InsufficientBuffer = 122;
    internal const int InvalidParameter = 87;

    internal const uint GetSourceName = 1;
    internal const uint GetTargetName = 2;
    internal const uint GetTargetPreferredMode = 3;
    internal const uint GetAdvancedColorInfo = 9;
    internal const uint SetAdvancedColorState = 10;
    internal const uint GetAdvancedColorInfo2 = 15;
    internal const uint SetHdrState = 16;

    internal const int CurrentSettings = -1;
    internal const uint RotatedModes = 0x4;
    internal const uint CdsUpdateRegistry = 0x1;
    internal const uint CdsTest = 0x2;
    internal const uint CdsSetPrimary = 0x10;
    internal const uint CdsNoReset = 0x10000000;
    internal const uint DmPosition = 0x20;
    internal const uint DmOrientation = 0x80;
    internal const uint DmBitsPerPel = 0x40000;
    internal const uint DmWidth = 0x80000;
    internal const uint DmHeight = 0x100000;
    internal const uint DmDisplayFlags = 0x200000;
    internal const uint DmFrequency = 0x400000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid
    {
        internal uint Low;
        internal int High;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rational
    {
        internal uint Numerator;
        internal uint Denominator;

        internal readonly double Hertz => Denominator == 0 ? 0 : (double)Numerator / Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SourceMode
    {
        internal uint Width;
        internal uint Height;
        internal uint PixelFormat;
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VideoSignal
    {
        internal ulong PixelRate;
        internal Rational HorizontalSync;
        internal Rational VerticalSync;
        internal uint ActiveWidth;
        internal uint ActiveHeight;
        internal uint TotalWidth;
        internal uint TotalHeight;
        internal uint Standard;
        internal uint ScanLineOrdering;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    internal struct Mode
    {
        [FieldOffset(0)] internal uint Type;
        [FieldOffset(4)] internal uint Id;
        [FieldOffset(8)] internal Luid Adapter;
        [FieldOffset(16)] internal SourceMode Source;
        [FieldOffset(16)] internal VideoSignal Signal;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SourcePath
    {
        internal Luid Adapter;
        internal uint Id;
        internal uint ModeIndex;
        internal uint Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TargetPath
    {
        internal Luid Adapter;
        internal uint Id;
        internal uint ModeIndex;
        internal uint Technology;
        internal uint Rotation;
        internal uint Scaling;
        internal Rational RefreshRate;
        internal uint ScanLineOrdering;
        internal int Available;
        internal uint Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Path
    {
        internal SourcePath Source;
        internal TargetPath Target;
        internal uint Flags;

        internal readonly bool IsActive => (Flags & PathActive) != 0;
        internal readonly uint SourceModeIndex => (Flags & PathVirtualMode) != 0 ? Source.ModeIndex >> 16 : Source.ModeIndex;
        internal readonly uint TargetModeIndex => (Flags & PathVirtualMode) != 0 ? Target.ModeIndex >> 16 : Target.ModeIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceHeader
    {
        internal uint Type;
        internal uint Size;
        internal Luid Adapter;
        internal uint Id;
    }

    internal static DeviceHeader Header<T>(uint type, Luid adapter, uint id) where T : struct =>
        new() { Type = type, Size = (uint)Marshal.SizeOf<T>(), Adapter = adapter, Id = id };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SourceName
    {
        internal DeviceHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Name;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct TargetName
    {
        internal DeviceHeader Header;
        internal uint Flags;
        internal uint Technology;
        internal ushort Manufacturer;
        internal ushort Product;
        internal uint Connector;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string FriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string DevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PreferredMode
    {
        internal DeviceHeader Header;
        internal uint Width;
        internal uint Height;
        internal VideoSignal Signal;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AdvancedColorInfo
    {
        internal DeviceHeader Header;
        internal uint Flags;
        internal uint Encoding;
        internal uint BitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AdvancedColorInfo2
    {
        internal DeviceHeader Header;
        internal uint Flags;
        internal uint Encoding;
        internal uint BitsPerColorChannel;
        internal uint ActiveMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ColorState
    {
        internal DeviceHeader Header;
        internal uint Flags;
    }

    // DEVMODEW's display-specific fields occupy fixed offsets. The remaining bytes are
    // preserved when a mode returned by EnumDisplaySettingsEx is passed back to Windows.
    [StructLayout(LayoutKind.Explicit, Size = 220)]
    internal struct DevMode
    {
        [FieldOffset(68)] internal ushort Size;
        [FieldOffset(70)] internal ushort DriverExtra;
        [FieldOffset(72)] internal uint Fields;
        [FieldOffset(76)] internal int X;
        [FieldOffset(80)] internal int Y;
        [FieldOffset(84)] internal uint Orientation;
        [FieldOffset(168)] internal uint BitsPerPel;
        [FieldOffset(172)] internal uint Width;
        [FieldOffset(176)] internal uint Height;
        [FieldOffset(180)] internal uint DisplayFlags;
        [FieldOffset(184)] internal uint Frequency;

        internal static DevMode Create() => new() { Size = (ushort)Marshal.SizeOf<DevMode>() };
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct OpenGdiAdapter
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string DeviceName;
        internal uint Adapter;
        internal Luid AdapterLuid;
        internal uint SourceId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CloseAdapter { internal uint Adapter; }

    // D3DKMDT_DISPLAYMODE_FLAGS is TWO uints: a zero-width C++ bitfield
    // starts ModePruningReason in the second word. VirtualRefreshRate is bit 8
    // of that word, not bit 8 of the first word.
    [StructLayout(LayoutKind.Sequential)]
    internal struct KernelDisplayMode
    {
        internal uint Width, Height, Format, IntegerRefreshRate;
        internal Rational RefreshRate;
        internal uint ScanLineOrdering, DisplayOrientation, DisplayFixedOutput;
        internal uint ValidationFlags, AttributeFlags;
        internal readonly bool IsVirtualRefreshRate => (AttributeFlags & 0x100) != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KernelDisplayModeList
    {
        internal uint Adapter, SourceId;
        internal IntPtr Modes;
        internal uint Count;
    }

    [DllImport("gdi32.dll", ExactSpelling = true)]
    internal static extern int D3DKMTOpenAdapterFromGdiDisplayName(ref OpenGdiAdapter request);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    internal static extern int D3DKMTCloseAdapter(ref CloseAdapter request);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    internal static extern int D3DKMTGetDisplayModeList(ref KernelDisplayModeList request);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] Path[] paths,
        ref uint modeCount, [Out] Mode[] modes, IntPtr topology);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int SetDisplayConfig(uint pathCount, [In] Path[]? paths,
        uint modeCount, [In] Mode[]? modes, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int DisplayConfigGetDeviceInfo(ref SourceName request);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int DisplayConfigGetDeviceInfo(ref TargetName request);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int DisplayConfigGetDeviceInfo(ref PreferredMode request);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int DisplayConfigGetDeviceInfo(ref AdvancedColorInfo request);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int DisplayConfigGetDeviceInfo(ref AdvancedColorInfo2 request);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern int DisplayConfigSetDeviceInfo(ref ColorState request);

    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsExW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplaySettingsEx(string deviceName, int index, ref DevMode mode, uint flags);

    [DllImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern int ChangeDisplaySettingsEx(string deviceName, ref DevMode mode, IntPtr window, uint flags, IntPtr param);

    [DllImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW", ExactSpelling = true)]
    internal static extern int CommitDisplaySettings(IntPtr deviceName, IntPtr mode, IntPtr window, uint flags, IntPtr param);
}
