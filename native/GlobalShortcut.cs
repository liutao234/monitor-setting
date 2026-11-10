using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MonitorSetting;

// Only the requested Win+O chord is consumed. The fallback hook has its own
// message loop; it never waits for WebView, hardware, or the application UI.
internal sealed class GlobalShortcut : IDisposable
{
    internal const int HotkeyId = 0x524d;
    internal const int WakeMessage = 0x8000 + 0x147;
    private const int HotkeyMessage = 0x0312;
    private const int QuitMessage = 0x0012;
    private readonly nint window;
    private readonly HookProc callback;
    private readonly TaskCompletionSource<bool> hookReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly WinOChordState chord = new();
    private Thread? hookThread;
    private uint hookThreadId;
    private nint hook;
    private bool registered;
    private volatile bool disposed;
    private int hookError;

    internal string Backend => registered ? "RegisterHotKey" : "KeyboardHook";

    private GlobalShortcut(nint window)
    {
        this.window = window;
        callback = KeyboardCallback;
    }

    internal static bool TryCreate(nint window, out GlobalShortcut? shortcut, out string? error)
    {
        if (window == 0) throw new ArgumentException("A live application window is required.", nameof(window));
        var instance = new GlobalShortcut(window);
        if (RegisterHotKey(window, HotkeyId, 0x0008 | 0x4000, 0x4f))
        {
            instance.registered = true;
            shortcut = instance;
            error = null;
            return true;
        }

        // Windows reserves Win+O for rotation lock on many installations.
        // Use the same low-level mechanism as configurable desktop hotkeys;
        // there is no registry remap and exiting restores the original chord.
        instance.hookThread = new Thread(instance.RunHook) { IsBackground = true, Name = "MonitorSetting Win+O" };
        instance.hookThread.Start();
        if (instance.hookReady.Task.Wait(TimeSpan.FromSeconds(2)) && instance.hookReady.Task.Result)
        {
            shortcut = instance;
            error = null;
            return true;
        }
        int code = instance.hookError == 0 ? 1460 : instance.hookError;
        instance.Dispose();
        shortcut = null;
        error = new Win32Exception(code).Message;
        return false;
    }

    internal bool MatchesMessage(int message, nint wParam) => !disposed && wParam == HotkeyId &&
        (registered ? message == HotkeyMessage : message == WakeMessage);

    private void RunHook()
    {
        try
        {
            hookThreadId = GetCurrentThreadId();
            // Ensure the queue exists before another thread can request shutdown.
            PeekMessage(out _, 0, 0, 0, 0);
            chord.Reset(IsDown(0x4f));
            if (disposed) { hookReady.TrySetResult(false); return; }
            hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
            if (hook == 0)
            {
                hookError = Marshal.GetLastWin32Error();
                hookReady.TrySetResult(false);
                return;
            }
            hookReady.TrySetResult(true);
            while (!disposed && GetMessage(out var message, 0, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception error)
        {
            hookError = error is Win32Exception native ? native.NativeErrorCode : 31;
            hookReady.TrySetResult(false);
        }
        finally
        {
            if (hook != 0) { UnhookWindowsHookEx(hook); hook = 0; }
        }
    }

    private nint KeyboardCallback(int code, nint message, nint data)
    {
        if (code < 0 || disposed || Marshal.ReadInt32(data) != 0x4f)
            return CallNextHookEx(hook, code, message, data);
        bool down = message == 0x100 || message == 0x104;
        bool up = message == 0x101 || message == 0x105;
        if (!down && !up) return CallNextHookEx(hook, code, message, data);

        // Only O is changing in this callback. Modifier key events have already
        // completed; their current states also recover naturally after lock/unlock.
        var result = chord.Process(down, IsDown(0x5b) || IsDown(0x5c),
            IsDown(0x11), IsDown(0x10), IsDown(0x12));
        if (result == WinOChordResult.Activate)
        {
            // An unassigned key-up prevents the released Win key from opening
            // Start after O was consumed (the Microsoft PowerToys convention).
            // It has no character or modifier effect and is never logged.
            var mask = new Input { Type = 1, Keyboard = new KeyboardInput { VirtualKey = 0xff, Flags = 0x0002 } };
            SendInput(1, [mask], Marshal.SizeOf<Input>());
            PostMessage(window, WakeMessage, HotkeyId, 0);
        }
        return result == WinOChordResult.Pass ? CallNextHookEx(hook, code, message, data) : 1;
    }

    private static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (registered) { UnregisterHotKey(window, HotkeyId); registered = false; }
        if (hookThreadId != 0) PostThreadMessage(hookThreadId, QuitMessage, 0, 0);
        if (hookThread is not null && Thread.CurrentThread != hookThread) hookThread.Join(500);
        GC.KeepAlive(callback);
    }

    private delegate nint HookProc(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct Message
    {
        internal nint Window;
        internal uint Id;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal int X, Y;
        internal uint Private;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput
    {
        internal ushort VirtualKey, ScanCode;
        internal uint Flags, Time;
        internal nuint ExtraInfo;
    }
    // INPUT includes the 32-byte MOUSEINPUT union on x64, even for key input.
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct Input
    {
        [FieldOffset(0)] internal uint Type;
        [FieldOffset(8)] internal KeyboardInput Keyboard;
    }

    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int id, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, int message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint thread, int message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")] private static extern int GetMessage(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}

internal enum WinOChordResult { Pass, Suppress, Activate }

// Small deterministic state machine. It retains only the O press lifecycle,
// preventing auto-repeat or a late key-up from activating or typing O again.
internal sealed class WinOChordState
{
    private bool down;
    private bool consumed;
    internal void Reset(bool initiallyDown = false) { down = initiallyDown; consumed = false; }
    internal WinOChordResult Process(bool keyDown, bool win, bool control, bool shift, bool alt)
    {
        if (!keyDown)
        {
            bool suppress = consumed;
            down = false;
            consumed = false;
            return suppress ? WinOChordResult.Suppress : WinOChordResult.Pass;
        }
        if (down) return consumed ? WinOChordResult.Suppress : WinOChordResult.Pass;
        down = true;
        consumed = win && !control && !shift && !alt;
        return consumed ? WinOChordResult.Activate : WinOChordResult.Pass;
    }
}
