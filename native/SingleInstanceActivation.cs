using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace MonitorSetting;

// This channel carries one request only: show the already-running window.
// It exists before the instance mutex is checked, so a second launch during
// WebView startup leaves a retained signal instead of being silently lost.
internal sealed class SingleInstanceActivation : IDisposable
{
    private readonly EventWaitHandle requested;
    private readonly MemoryMappedFile processIdentity;
    private readonly MemoryMappedViewAccessor identity;
    private RegisteredWaitHandle? listener;
    private Form? window;
    private Action? activate;
    private int pending;
    private volatile bool ready;
    private volatile bool disposed;

    internal SingleInstanceActivation(string? testScope = null)
    {
        using var currentUser = WindowsIdentity.GetCurrent();
        string user = currentUser.User?.Value ?? Environment.UserName;
        string prefix = "Local\\MonitorSetting." + user + (testScope is null ? "" : "." + testScope);
        MutexName = prefix + ".Application";
        requested = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + ".Activate");
        processIdentity = MemoryMappedFile.CreateOrOpen(prefix + ".Process", sizeof(int), MemoryMappedFileAccess.ReadWrite);
        identity = processIdentity.CreateViewAccessor(0, sizeof(int), MemoryMappedFileAccess.ReadWrite);
    }

    internal string MutexName { get; }

    internal void RequestActivation()
    {
        int processId = identity.ReadInt32(0);
        // The process launched by the user is allowed to transfer foreground
        // permission specifically to our resident process, never to all apps.
        if (processId > 0) AllowSetForegroundWindow((uint)processId);
        requested.Set();
    }

    internal void Attach(Form owner, Action showWindow)
    {
        if (window is not null) throw new InvalidOperationException("Activation is already attached.");
        window = owner;
        activate = showWindow;
        identity.Write(0, Environment.ProcessId);
        owner.Shown += OnShown;
        owner.HandleCreated += OnHandleCreated;
        ready = owner.Visible && owner.IsHandleCreated;
        listener = ThreadPool.RegisterWaitForSingleObject(requested, (_, _) =>
        {
            if (disposed) return;
            Interlocked.Exchange(ref pending, 1);
            Dispatch();
        }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    private void OnShown(object? sender, EventArgs e)
    {
        ready = true;
        Drain();
    }

    private void OnHandleCreated(object? sender, EventArgs e) => Dispatch();

    private void Dispatch()
    {
        if (disposed || !ready || window is not { IsDisposed: false, IsHandleCreated: true } owner) return;
        try { owner.BeginInvoke((Action)Drain); }
        catch (InvalidOperationException) { /* A pending activation survives handle recreation. */ }
    }

    private void Drain()
    {
        if (disposed || !ready || window is not { IsDisposed: false, IsHandleCreated: true }) return;
        if (Interlocked.Exchange(ref pending, 0) != 0) activate?.Invoke();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        listener?.Unregister(null);
        if (window is not null)
        {
            window.Shown -= OnShown;
            window.HandleCreated -= OnHandleCreated;
            identity.Write(0, 0);
        }
        identity.Dispose();
        processIdentity.Dispose();
        requested.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
