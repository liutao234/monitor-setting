using System.Diagnostics;
using System.Text.Json;

namespace MonitorSetting;

internal sealed class PendingLayout
{
    public string Token { get; set; } = "";
    public DateTime DeadlineUtc { get; set; }
    public DisplaySnapshot Snapshot { get; set; } = new();
    public string? RecoveryError { get; set; }
}

internal sealed class LayoutTransaction
{
    private static readonly string PendingFile = Path.Combine(Program.DataDirectory, "pending-layout.json");
    private readonly DisplayService displays;

    public LayoutTransaction(DisplayService displays) => this.displays = displays;

    private static T Locked<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, "Local\\MonitorSetting.LayoutTransaction");
        try { mutex.WaitOne(); } catch (AbandonedMutexException) { }
        try { return action(); }
        finally { mutex.ReleaseMutex(); }
    }

    private static PendingLayout? Read() => File.Exists(PendingFile)
        ? JsonSerializer.Deserialize<PendingLayout>(File.ReadAllText(PendingFile), Program.Json)
        : null;

    private static void Save(PendingLayout pending)
    {
        string temporary = PendingFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(pending, Program.Json));
        File.Move(temporary, PendingFile, true);
    }

    private static void RestorePending(DisplayService service, PendingLayout pending)
    {
        try
        {
            service.Restore(pending.Snapshot);
            File.Delete(PendingFile);
        }
        catch (Exception error)
        {
            pending.RecoveryError = error.Message;
            Save(pending);
            throw;
        }
    }

    public int SecondsRemaining => Locked(() =>
    {
        var pending = Read();
        return pending is null ? 0 : Math.Max(0, (int)Math.Ceiling((pending.DeadlineUtc - DateTime.UtcNow).TotalSeconds));
    });
    public bool HasPending => Locked(() => Read() is not null);
    public string? RecoveryError => Locked(() => Read()?.RecoveryError);

    public (bool Pending, int Seconds, string? RecoveryError) ReadStatus() => Locked(() =>
    {
        var pending = Read();
        return (pending is not null,
            pending is null ? 0 : Math.Max(0, (int)Math.Ceiling((pending.DeadlineUtc - DateTime.UtcNow).TotalSeconds)),
            pending?.RecoveryError);
    });

    public void Apply(Action change) => Locked(() =>
    {
        if (Read() is not null) throw new InvalidOperationException("pendingConfirmation");
        var pending = new PendingLayout
        {
            Token = Guid.NewGuid().ToString("N"),
            DeadlineUtc = DateTime.UtcNow.AddSeconds(20),
            Snapshot = displays.Capture()
        };
        Save(pending);
        try
        {
            var launch = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            launch.ArgumentList.Add("--watchdog");
            launch.ArgumentList.Add(pending.Token);
            using var watchdog = Process.Start(launch) ?? throw new IOException("watchdogFailed");
            change();
        }
        catch
        {
            RestorePending(displays, pending);
            throw;
        }
        return true;
    });

    public void Confirm() => Locked(() =>
    {
        var pending = Read();
        if (pending is null) throw new InvalidOperationException("confirmationExpired");
        if (DateTime.UtcNow >= pending.DeadlineUtc)
        {
            RestorePending(displays, pending);
            throw new InvalidOperationException("confirmationExpired");
        }
        File.Delete(PendingFile);
        return true;
    });

    public void Revert() => Locked(() =>
    {
        var pending = Read();
        if (pending is null) return false;
        RestorePending(displays, pending);
        return true;
    });

    public static void Watch(string token)
    {
        // A separate process restores the desktop even if the UI process crashes.
        while (true)
        {
            var pending = Locked(Read);
            if (pending is null || pending.Token != token) return;
            var remaining = pending.DeadlineUtc - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                Thread.Sleep((int)Math.Min(500, remaining.TotalMilliseconds + 1));
                continue;
            }
            for (int attempt = 0; attempt < 3; attempt++)
            {
                bool restored = Locked(() =>
                {
                    var current = Read();
                    if (current?.Token != token) return true;
                    try
                    {
                        RestorePending(new DisplayService(), current);
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                });
                if (restored) return;
                Thread.Sleep(1000);
            }
            return;
        }
    }
}
