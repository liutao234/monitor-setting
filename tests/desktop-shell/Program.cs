using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.WinForms;
using MonitorSetting;

internal static class Program
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static Exception? failure;
    private static readonly List<string> checks = [];

    [STAThread]
    private static void Main(string[] args)
    {
        string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonitorSetting");
        if (File.Exists(Path.Combine(data, "pending-layout.json"))) throw new Exception("A display configuration is pending; do not run acceptance now.");
        ApplicationConfiguration.Initialize();
        var app = typeof(DisplayService).Assembly;
        var formType = app.GetType("MonitorSetting.MainForm", true)!;
        var activationType = app.GetType("MonitorSetting.SingleInstanceActivation", true)!;
        using var activation = (IDisposable)Activator.CreateInstance(activationType, Members, null, [null], null)!;
        string mutexName = (string)activationType.GetProperty("MutexName", Members)!.GetValue(activation)!;
        using var mutex = new Mutex(false, mutexName);
        bool acquired;
        try { acquired = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new Exception("A normal MonitorSetting instance is already running.");
        nint originalForeground = GetForegroundWindow();
        using var form = (Form)Activator.CreateInstance(formType, [null])!;
        form.Opacity = 0;
        Action wake = () => formType.GetMethod("ActivateFromShortcut", Members)!.Invoke(form, null);
        activationType.GetMethod("Attach", Members)!.Invoke(activation, [form, wake]);
        form.Shown += async (_, _) =>
        {
            try
            {
                var browser = (WebView2)formType.GetField("browser", Members)!.GetValue(form)!;
                await Until(async () => browser.CoreWebView2 is not null &&
                    await browser.ExecuteScriptAsync("Boolean(window.desktopReady)") == "true", "The native interface did not become ready.");
                var shortcut = formType.GetField("shortcut", Members)!.GetValue(form);
                Check(shortcut is not null, "Win+O installed before injecting any test input");
                string backend = (string)shortcut!.GetType().GetProperty("Backend", Members)!.GetValue(shortcut)!;
                Check(formType.GetField("tray", Members)!.GetValue(form) is NotifyIcon { Visible: true }, "Tray icon is available");
                Console.WriteLine("Backend: " + backend);

                form.Close();
                await Task.Delay(150);
                Check(!form.Visible && !form.IsDisposed, "Closing the window keeps a hidden resident instance");
                InjectWinO();
                await Until(() => Task.FromResult(form.Visible && form.WindowState != FormWindowState.Minimized), "Win+O did not show the hidden window.");
                await Task.Delay(350);
                Check(GetForegroundWindow() == form.Handle, "Win+O foregrounds the window without opening Start after Win release");

                form.WindowState = FormWindowState.Minimized;
                await Task.Delay(150);
                InjectWinO();
                await Until(() => Task.FromResult(form.WindowState != FormWindowState.Minimized), "Win+O did not restore a minimized window.");
                await Task.Delay(350);
                Check(form.Visible && GetForegroundWindow() == form.Handle, "Win+O restores and foregrounds a minimized window");

                if (args.Length > 0)
                {
                    // Model a user launch from a foreground shell such as Explorer.
                    // A deliberately background-launched process is not guaranteed
                    // foreground rights by Windows, which is not a product failure.
                    using var launcher = new Form { Opacity = 0, ShowInTaskbar = false,
                        StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(0, 0, 16, 16) };
                    launcher.Show();
                    launcher.Activate();
                    Check(SetForegroundWindow(launcher.Handle), "Foreground launcher is ready for second-instance activation");
                    form.Close();
                    await Task.Delay(150);
                    Check(!form.Visible, "Resident window hidden before a second launch");
                    using var child = Process.Start(new ProcessStartInfo(Path.GetFullPath(args[0]))
                    { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden })!;
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    await Until(() => Task.FromResult(form.Visible), "Second launch did not signal the resident window.");
                    await Task.Delay(250);
                    Check(child.ExitCode == 0, "Second launch exits successfully without creating another resident instance");
                    Check(GetForegroundWindow() == form.Handle, "A user-initiated second launch foregrounds the existing window");
                }
            }
            catch (Exception error) { failure = error; }
            finally
            {
                formType.GetMethod("RequestExit", Members)!.Invoke(form, null);
            }
        };
        try { Application.Run(form); }
        finally
        {
            activation.Dispose();
            mutex.ReleaseMutex();
            if (originalForeground != 0) SetForegroundWindow(originalForeground);
        }
        Check(form.IsDisposed, "Explicit Exit terminates the resident window");
        Check(formType.GetField("shortcut", Members)!.GetValue(form) is null &&
            formType.GetField("tray", Members)!.GetValue(form) is null, "Exit releases hotkey and tray resources");
        if (failure is not null) throw failure;
        Console.WriteLine(JsonSerializer.Serialize(new { passed = true, hardwareSettingsChanged = false, checks }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task Until(Func<Task<bool>> ready, string message)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(20))
        {
            if (await ready()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException(message);
    }

    private static void InjectWinO()
    {
        foreach (int key in new[] { 0x5b, 0x5c, 0x4f, 0x10, 0x11, 0x12 })
            if ((GetAsyncKeyState(key) & 0x8000) != 0) throw new Exception("A real modifier/O key is held; skipping synthetic keyboard input.");
        Input[] inputs = [Key(0x5b), Key(0x4f), Key(0x4f, true), Key(0x5b, true)];
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new Exception("Win+O test injection was blocked.");
    }

    private static Input Key(ushort key, bool up = false) => new() { Type = 1,
        Keyboard = new KeyboardInput { VirtualKey = key, Flags = up ? 2u : 0u } };
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        checks.Add(label); Console.WriteLine("PASS " + label);
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput
    {
        internal ushort VirtualKey, ScanCode;
        internal uint Flags, Time;
        internal nuint ExtraInfo;
    }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct Input
    {
        [FieldOffset(0)] internal uint Type;
        [FieldOffset(8)] internal KeyboardInput Keyboard;
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
}
