using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MonitorSetting;

// A tiny native-painted bridge while WebView2 starts. This is symbolic artwork,
// not a discovered device, a progress estimate, or an additional loading gate.
internal sealed class StartupSurface : Control
{
    private readonly System.Windows.Forms.Timer animation = new() { Interval = 33 };
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private bool animate;
    private Form? owner;

    internal StartupSurface(Color canvas)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Fill;
        BackColor = canvas;
        TabStop = false;
        AccessibleRole = AccessibleRole.None;
        animate = ClientAnimationsEnabled();
        animation.Tick += (_, _) => Invalidate(new Rectangle(ClientSize.Width / 2 - 100, ClientSize.Height / 2 - 90, 200, 190));
    }

    protected override void OnParentChanged(EventArgs e)
    {
        if (owner is not null) owner.Resize -= OwnerResized;
        base.OnParentChanged(e);
        owner = FindForm();
        if (owner is not null) owner.Resize += OwnerResized;
        UpdateAnimation();
    }

    private void OwnerResized(object? sender, EventArgs e) => UpdateAnimation();

    private void UpdateAnimation()
    {
        if (Visible && animate && owner is { Visible: true, WindowState: not FormWindowState.Minimized }) animation.Start();
        else animation.Stop();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdateAnimation();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float scale = Math.Clamp(Math.Min(ClientSize.Width / 640f, ClientSize.Height / 400f), .65f, 1f);
        double seconds = elapsed.Elapsed.TotalSeconds;
        float arrival = animate ? (float)(1 - Math.Pow(2, -Math.Min(1, seconds / .45) * 10)) : 1;
        float drift = animate ? (float)Math.Sin(seconds * Math.PI / 1.8) * 1.5f : 0;
        float alpha = animate ? .35f + .65f * arrival : 1;
        graphics.TranslateTransform(ClientSize.Width / 2f, ClientSize.Height / 2f + (1 - arrival) * 8 + drift);
        graphics.ScaleTransform(scale, scale);
        bool dark = BackColor.GetBrightness() < .5;
        Color ink = SystemInformation.HighContrast ? SystemColors.WindowText : dark ? Color.FromArgb(155, 164, 179) : Color.FromArgb(108, 119, 137);
        Color surface = dark ? Color.FromArgb(35, 37, 41) : Color.FromArgb(248, 249, 251);
        using var shell = Squircle(new RectangleF(-77, -56, 154, 98), 5);
        using var fill = new SolidBrush(Color.FromArgb((int)(alpha * 255), surface));
        using var stroke = new Pen(Color.FromArgb((int)(alpha * 220), ink), 1.8f);
        using var stand = new Pen(Color.FromArgb((int)(alpha * 170), ink), 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.FillPath(fill, shell);
        graphics.DrawPath(stroke, shell);
        graphics.DrawLine(stand, 0, 44, 0, 58);
        graphics.DrawLine(stand, -21, 59, 21, 59);
        // The second inset curve is an unlabelled screen surface, never a status.
        using var glass = Squircle(new RectangleF(-65, -45, 130, 75), 5);
        using var glassPen = new Pen(Color.FromArgb((int)(alpha * (dark ? 35 : 25)), ink), 1);
        graphics.DrawPath(glassPen, glass);
    }

    private static GraphicsPath Squircle(RectangleF bounds, float exponent)
    {
        var points = new PointF[128];
        for (int i = 0; i < points.Length; i++)
        {
            double angle = 2 * Math.PI * i / points.Length;
            double cosine = Math.Cos(angle), sine = Math.Sin(angle);
            points[i] = new PointF(bounds.X + bounds.Width / 2 * (1 + (float)(Math.Sign(cosine) * Math.Pow(Math.Abs(cosine), 2 / exponent))),
                bounds.Y + bounds.Height / 2 * (1 + (float)(Math.Sign(sine) * Math.Pow(Math.Abs(sine), 2 / exponent))));
        }
        var path = new GraphicsPath();
        path.AddPolygon(points);
        return path;
    }

    private static bool ClientAnimationsEnabled() =>
        SystemParametersInfo(0x1042, 0, out bool enabled, 0) && enabled;

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter,
        [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (owner is not null) owner.Resize -= OwnerResized;
            animation.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal static class StartupAppearance
{
    private static string PreferenceFile => Path.Combine(Program.DataDirectory, "startup-theme.json");

    internal static string Load()
    {
        try
        {
            string? value = System.Text.Json.JsonSerializer.Deserialize<string>(File.ReadAllText(PreferenceFile));
            return value is "system" or "light" or "dark" ? value : "system";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { return "system"; }
    }

    internal static void Save(string preference)
    {
        try { File.WriteAllText(PreferenceFile, System.Text.Json.JsonSerializer.Serialize(preference)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    internal static Color Canvas(string preference)
    {
        if (SystemInformation.HighContrast) return SystemColors.Window;
        bool dark = preference == "dark";
        if (preference == "system")
        {
            try
            {
                using var settings = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                dark = settings?.GetValue("AppsUseLightTheme") is int value && value == 0;
            }
            catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return dark ? Color.FromArgb(20, 20, 20) : Color.White;
    }
}
