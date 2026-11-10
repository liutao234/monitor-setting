using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Diagnostics;
using System.Text.Json;

namespace MonitorSetting;

internal sealed partial class MainForm : Form
{
    private const string AppOrigin = "https://monitor.local";
    private readonly WebView2 browser = new() { Dock = DockStyle.Fill };
    private readonly DisplayService displays = new();
    private readonly ProgressiveControls controls = new();
    private readonly LayoutTransaction transaction;
    private readonly SemaphoreSlim commands = new(1);
    private readonly System.Windows.Forms.Timer poll = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer displayChanged = new() { Interval = 1200 };
    private readonly string? smokeOutput;
    private static readonly JsonSerializerOptions BridgeJson = new(Program.Json) { WriteIndented = false };
    private int cacheInvalidation;
    private int navigationGeneration;
    private int stateGeneration;
    private long snapshotRevision;
    private bool readingStatus;
    private int lastSeconds;
    private bool lastPending;
    private string? lastRecoveryError;
    private bool closing;
    private bool closeAllowed;
    private StartupSurface? startup;
    private string appearancePreference;
    private bool shellReady;

    public MainForm(string? smokeOutput)
    {
        this.smokeOutput = smokeOutput;
        transaction = new LayoutTransaction(displays);
        Text = "Display Workspace";
        ClientSize = new Size(1280, 880);
        MinimumSize = new Size(760, 580);
        StartPosition = FormStartPosition.CenterScreen;
        appearancePreference = StartupAppearance.Load();
        BackColor = StartupAppearance.Canvas(appearancePreference);
        browser.DefaultBackgroundColor = BackColor;
        Controls.Add(browser);
        startup = new StartupSurface(BackColor);
        Controls.Add(startup);
        startup.BringToFront();
        controls.Changed += ControlsChanged;
        if (smokeOutput is not null) { Opacity = 0; ShowInTaskbar = false; }
        Shown += Initialize;
        FormClosing += OnWindowClosing;
        displayChanged.Tick += (_, _) =>
        {
            displayChanged.Stop();
            Notify(new { @event = "displaysChanged" });
        };
        poll.Tick += async (_, _) =>
        {
            if (readingStatus || closing) return;
            readingStatus = true;
            int generation = stateGeneration;
            try
            {
                // Native configuration changes and the watchdog share a mutex. Never wait for it on the UI thread.
                var status = await Task.Run(transaction.ReadStatus);
                if (closing || generation != stateGeneration || status.Seconds == lastSeconds && status.Pending == lastPending && status.RecoveryError == lastRecoveryError) return;
                lastSeconds = status.Seconds;
                lastPending = status.Pending;
                lastRecoveryError = status.RecoveryError;
                Notify(new { @event = "pending", seconds = status.Seconds, pending = status.Pending, recoveryError = status.RecoveryError });
                if (!status.Pending) Notify(new { @event = "displaysChanged" });
            }
            catch (Exception error) { Log(error); }
            finally { readingStatus = false; }
        };
    }

    private async void Initialize(object? sender, EventArgs args)
    {
        try
        {
            // Paint immediately; starting WebView2 or restoring a pending layout
            // never leaves an otherwise empty native window on screen.
            startup?.Refresh();
            if (smokeOutput is not null && startup is not null)
            {
                Directory.CreateDirectory(smokeOutput);
                using var image = new Bitmap(startup.Width, startup.Height);
                startup.DrawToBitmap(image, startup.ClientRectangle);
                image.Save(Path.Combine(smokeOutput, "startup-native.png"));
            }
            if (smokeOutput is null)
            {
                try { await Task.Run(transaction.Revert); } catch (Exception error) { Log(error); }
            }
            if (closing) return;
            string web = Path.Combine(AppContext.BaseDirectory, "web");
            if (!File.Exists(Path.Combine(web, "basic", "index.html"))) throw new FileNotFoundException("Application web files are missing.");
            string profile = smokeOutput is null ? Path.Combine(Program.DataDirectory, "WebView")
                : Path.Combine(Path.GetFullPath(smokeOutput), "WebView");
            var environment = await CoreWebView2Environment.CreateAsync(null, profile);
            if (closing) return;
            await browser.EnsureCoreWebView2Async(environment);
            if (closing) return;
            browser.CoreWebView2.SetVirtualHostNameToFolderMapping("monitor.local", web, CoreWebView2HostResourceAccessKind.DenyCors);
            var settings = browser.CoreWebView2.Settings;
            settings.AreHostObjectsAllowed = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.IsStatusBarEnabled = false;
            browser.CoreWebView2.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            browser.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
            browser.CoreWebView2.NavigationStarting += (_, e) =>
            {
                e.Cancel = !IsAppUri(e.Uri);
                if (!e.Cancel) navigationGeneration++;
            };
            browser.CoreWebView2.WebMessageReceived += Receive;
            browser.CoreWebView2.NavigationCompleted += (_, _) => CompleteStartup();
            browser.CoreWebView2.ProcessFailed += (_, _) => Notify(new { @event = "displaysChanged" });
            poll.Start();
            if (smokeOutput is null) browser.CoreWebView2.Navigate(AppOrigin + "/basic/index.html");
            else await SmokeTest();
        }
        catch (Exception error)
        {
            if (closing) return;
            CompleteStartup();
            Log(error);
            if (smokeOutput is not null)
            {
                Directory.CreateDirectory(smokeOutput);
                File.WriteAllText(Path.Combine(smokeOutput, "error.txt"), error.ToString());
                Environment.ExitCode = 1;
                Close();
            }
            else if (error is WebView2RuntimeNotFoundException) OfferRuntimeInstall();
            else MessageBox.Show(this, error.Message, "MonitorSetting", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static bool IsAppUri(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host == "monitor.local" && uri.IsDefaultPort;

    private async void Receive(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!IsAppUri(args.Source) || args.WebMessageAsJson.Length > 65536) return;
        string? id = null;
        bool acquired = false;
        string method = "invalid";
        long queuedMilliseconds = 0;
        var elapsed = Stopwatch.StartNew();
        int generation = navigationGeneration;
        try
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            var root = document.RootElement;
            id = root.GetProperty("id").GetString();
            method = root.GetProperty("method").GetString() ?? "";
            var parameters = root.TryGetProperty("params", out var value) ? value.Clone() : default;
            // Frontend paint and appearance are local shell messages. They must
            // not wait for a display read or any external DDC/CI transaction.
            if (method is "shellReady" or "appearance")
            {
                if (closing) return;
                ApplyAppearance(parameters);
                if (method == "shellReady") { shellReady = true; CompleteStartup(); }
                Notify(new { id, result = new { ok = true } });
                return;
            }
            // Local actions must not wait behind slow external-monitor DDC/CI probes.
            if (method is not ("identify" or "openSettings"))
            {
                await commands.WaitAsync();
                acquired = true;
            }
            queuedMilliseconds = elapsed.ElapsedMilliseconds;
            if (closing || generation != navigationGeneration) return;
            if (root.TryGetProperty("deadlineUtcMs", out var deadline) && deadline.TryGetInt64(out long expiresAt)
                && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= expiresAt)
                throw new TimeoutException("timeout");
            object? result;
            if (method == "identify")
            {
                var list = await Task.Run(() => displays.Enumerate(includeModes: false));
                if (closing || generation != navigationGeneration) return;
                ShowIdentifiers(list);
                result = new { ok = true };
            }
            else if (method == "openSettings")
            {
                Process.Start(new ProcessStartInfo("ms-settings:display") { UseShellExecute = true });
                result = new { ok = true };
            }
            else
            {
                bool mutating = method is "control" or "layout" or "confirm" or "revert";
                if (mutating) stateGeneration++;
                try { result = await Task.Run(() => Dispatch(method, parameters)); }
                finally { if (mutating) stateGeneration++; }
            }
            if (generation == navigationGeneration) Notify(new { id, result });
        }
        catch (Exception error)
        {
            Log(error);
            if (generation == navigationGeneration) Notify(new { id, error = new { code = error.Message, message = error.Message } });
        }
        finally
        {
            if (acquired) commands.Release();
            LogTiming(method, queuedMilliseconds, elapsed.ElapsedMilliseconds);
        }
    }

    private object Dispatch(string method, JsonElement parameters)
    {
        if (Interlocked.Exchange(ref cacheInvalidation, 0) != 0) InvalidateCaches();
        switch (method)
        {
            case "displays":
                bool forceRefresh = parameters.ValueKind == JsonValueKind.Object
                    && parameters.TryGetProperty("forceRefresh", out var force) && force.ValueKind == JsonValueKind.True;
                if (forceRefresh) InvalidateCaches();
                return ReadState(includeControls: !transaction.HasPending);
            case "layout":
                var layout = parameters.Deserialize<LayoutRequest>(Program.Json) ?? throw new ArgumentException("invalidValue");
                transaction.Apply(() => displays.ApplyLayout(layout));
                return ReadState(includeControls: false);
            case "control":
                if (transaction.HasPending) throw new InvalidOperationException("pendingConfirmation");
                string id = parameters.GetProperty("displayId").GetString() ?? "";
                string control = parameters.GetProperty("control").GetString() ?? "";
                double value = parameters.GetProperty("value").GetDouble();
                if (!double.IsFinite(value)) throw new ArgumentException("invalidValue");
                if (control == "hdr")
                {
                    if (value != 0 && value != 1) throw new ArgumentException("invalidValue");
                    transaction.Apply(() => displays.SetHdr(id, value == 1));
                    return ReadState(includeControls: false);
                }
                controls.Set(id, control, value, displays.Enumerate(includeModes: false));
                return ReadState(includeControls: false);
            case "confirm": transaction.Confirm(); return ReadState(includeControls: false);
            case "revert": transaction.Revert(); InvalidateCaches(); return ReadState();
            default: throw new ArgumentException("unknownCommand");
        }
    }

    private object ReadState(bool includeControls = true)
    {
        var list = displays.Enumerate();
        var status = transaction.ReadStatus();
        controls.Read(list, refresh: includeControls, allowProbes: !status.Pending);
        var active = list.Where(d => d.Active).ToList();
        string mode = active.Count == 1 ? (active[0].Kind == "builtin" ? "builtin" : "external")
            : active.Count > 1 && active.Select(d => (d.X, d.Y)).Distinct().Count() == 1 ? "mirror" : "extend";
        var modeCapabilities = displays.GetModeCapabilities(list);
        bool controlsComplete = list.All(display => !display.ControlsLoading);
        return new { displays = list, mode, modeCapabilities, controlsComplete, backgroundControls = true,
            revision = Interlocked.Increment(ref snapshotRevision),
            pending = status.Pending, seconds = status.Seconds, recoveryError = status.RecoveryError };
    }

    private void ControlsChanged()
    {
        if (closing || IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke((Action)PublishControls); }
        catch (InvalidOperationException) { }
    }

    private async void PublishControls()
    {
        if (closing) return;
        await commands.WaitAsync();
        try
        {
            if (closing) return;
            // The physical probe is already finished. Re-read only current topology
            // and cached control snapshots so this event cannot resurrect old layout
            // or confirmation state. The command gate orders it after an in-flight write.
            var state = await Task.Run(() =>
            {
                if (Interlocked.Exchange(ref cacheInvalidation, 0) != 0) InvalidateCaches();
                return ReadState(includeControls: false);
            });
            if (!closing) Notify(new { @event = "controlsChanged", state });
        }
        catch (Exception error) { Log(error); }
        finally { commands.Release(); }
    }

    private void InvalidateCaches()
    {
        displays.InvalidateCaches();
        controls.Invalidate();
    }

    private void ApplyAppearance(JsonElement parameters)
    {
        ApplyDesktopLocale(parameters);
        if (parameters.ValueKind != JsonValueKind.Object) return;
        bool hasValue = parameters.TryGetProperty("preference", out var value) || parameters.TryGetProperty("theme", out value);
        if (!hasValue || value.ValueKind != JsonValueKind.String) return;
        string? preference = value.GetString();
        if (preference is not ("system" or "light" or "dark")) return;
        if (preference != appearancePreference && smokeOutput is null) StartupAppearance.Save(preference);
        appearancePreference = preference;
        BackColor = StartupAppearance.Canvas(preference);
        browser.DefaultBackgroundColor = BackColor;
        if (startup is not null) startup.BackColor = BackColor;
    }

    private void CompleteStartup()
    {
        if (startup is null) return;
        Controls.Remove(startup);
        startup.Dispose();
        startup = null;
    }

    private async void OnWindowClosing(object? sender, FormClosingEventArgs args)
    {
        if (closeAllowed) return;
        if (smokeOutput is null && !exitRequested && args.CloseReason == CloseReason.UserClosing)
        {
            args.Cancel = true;
            Hide();
            await RestoreHiddenPending();
            return;
        }
        args.Cancel = true;
        if (closing) return;
        closing = true;
        DisposeDesktopShell();
        CompleteStartup();
        controls.Dispose();
        poll.Stop();
        displayChanged.Stop();
        await commands.WaitAsync();
        try { if (smokeOutput is null) await Task.Run(() => transaction.Revert()); }
        catch (Exception error) { Log(error); }
        finally { commands.Release(); }
        closeAllowed = true;
        Close();
    }

    private void ShowIdentifiers(List<DisplayInfo> list)
    {
        foreach (var item in list.Select((display, index) => (display, index)).Where(item => item.display.Active))
        {
            var screen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == item.display.GdiName);
            if (screen is null) continue;
            var badge = new Form
            {
                FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual,
                BackColor = Color.FromArgb(24, 60, 120), ForeColor = Color.White, TopMost = true,
                ShowInTaskbar = false, Size = new Size(160, 120),
                Location = new Point(screen.Bounds.X + 32, screen.Bounds.Y + 32)
            };
            badge.Controls.Add(new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 48, FontStyle.Bold), Text = (item.index + 1).ToString()
            });
            var timer = new System.Windows.Forms.Timer { Interval = 2500 };
            timer.Tick += (_, _) => { timer.Stop(); badge.Close(); timer.Dispose(); badge.Dispose(); };
            badge.Show(this);
            timer.Start();
        }
    }

    private void Notify(object message)
    {
        if (closing || browser.CoreWebView2 is null) return;
        browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, BridgeJson));
    }

    protected override void WndProc(ref Message message)
    {
        if (shortcut?.MatchesMessage(message.Msg, message.WParam) == true)
        {
            ActivateFromShortcut();
            return;
        }
        base.WndProc(ref message);
        if (message.Msg is 0x007E or 0x0219 && !closing)
        {
            Interlocked.Exchange(ref cacheInvalidation, 1);
            displayChanged.Stop();
            displayChanged.Start();
        }
    }

    private async Task SmokeTest()
    {
        Directory.CreateDirectory(smokeOutput!);
        var timings = new List<object>();
        string? firstDocument = null;
        int? firstRequests = null;
        foreach (string route in new[] { "basic", "multi-display", "color" })
        {
            var watch = Stopwatch.StartNew();
            double? switchMs = null;
            if (route == "basic") browser.CoreWebView2.Navigate($"{AppOrigin}/{route}/index.html?lang=zh-CN");
            else
            {
                string switched = await browser.ExecuteScriptAsync($$"""
                    (() => {
                      const link = [...document.querySelectorAll('.tabs a')].find(a => new URL(a.href).pathname.endsWith('/{{route}}/index.html'));
                      if (!link) throw new Error('Missing settings navigation');
                      const start = performance.now(); link.click();
                      return { elapsedMs: performance.now() - start };
                    })()
                    """);
                using var result = JsonDocument.Parse(switched);
                switchMs = result.RootElement.GetProperty("elapsedMs").GetDouble();
            }
            bool ready = false;
            for (int attempt = 0; attempt < 120; attempt++)
            {
                await Task.Delay(100);
                string result = await browser.ExecuteScriptAsync($"Boolean(window.desktopReady && document.documentElement.dataset.desktopRoute === '{route}' && location.pathname === '/{route}/index.html')");
                if (result == "true") { ready = true; break; }
            }
            if (!ready) throw new TimeoutException($"Native page did not initialize: {route}");
            long initialReadyMs = watch.ElapsedMilliseconds;
            if (route == "basic")
            {
                await using var initial = File.Create(Path.Combine(smokeOutput!, "basic-initial.png"));
                await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, initial);
            }
            bool complete = false;
            for (int attempt = 0; attempt < 600; attempt++)
            {
                string result = await browser.ExecuteScriptAsync("Boolean(window.desktopControlsReady)");
                if (result == "true") { complete = true; break; }
                await Task.Delay(100);
            }
            if (!complete) throw new TimeoutException($"Background controls did not finish: {route}");
            string document = await browser.ExecuteScriptAsync("String(performance.timeOrigin)");
            firstDocument ??= document;
            if (document != firstDocument) throw new InvalidOperationException("A category switch reloaded the document.");
            string requestCount = await browser.ExecuteScriptAsync("window.desktopRequestCounts?.displays ?? null");
            int? requests = int.TryParse(requestCount, out int count) ? count : null;
            firstRequests ??= requests;
            if (route != "basic" && requests != firstRequests) throw new InvalidOperationException("A category switch repeated a display query.");
            if (route == "multi-display") await ValidateArrangement();
            timings.Add(new { route, initialReadyMs, controlsReadyMs = watch.ElapsedMilliseconds, switchMs, requests, sameDocument = true });
            if (route == "color")
            {
                await browser.ExecuteScriptAsync("document.querySelector('.color-device:last-child')?.click()");
                await Task.Delay(200);
            }
            await using var stream = File.Create(Path.Combine(smokeOutput!, route + ".png"));
            await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
        }
        // Size the actual WebView explicitly: a hidden top-level window can
        // retain larger native bounds even after its managed size is changed.
        MinimumSize = Size.Empty;
        Size = new Size(760, 580);
        browser.Dock = DockStyle.None;
        browser.Bounds = new Rectangle(0, 0, 744, 541);
        await Task.Delay(150);
        string smallViewport = await browser.ExecuteScriptAsync("innerWidth <= 744 && innerHeight <= 541");
        if (smallViewport != "true") throw new InvalidOperationException("The small-window test did not resize the WebView viewport.");
        foreach (string route in new[] { "basic", "multi-display", "color" })
        {
            await browser.ExecuteScriptAsync($"[...document.querySelectorAll('.tabs a')].find(a => new URL(a.href).pathname.endsWith('/{route}/index.html')).click()");
            await Task.Delay(150);
            if (route == "multi-display") await ValidateArrangement();
            string geometry = await browser.ExecuteScriptAsync("JSON.stringify({ width: innerWidth, height: innerHeight, dpr: devicePixelRatio, scale: window.monitorAppearance?.scale, pageTransform: getComputedStyle(document.querySelector('.page')).transform, scrollX, scrollY, monitors: document.querySelectorAll('.native-monitor').length, mapWidth: document.querySelector('.native-map')?.clientWidth, mapHeight: document.querySelector('.native-map')?.clientHeight })");
            File.WriteAllText(Path.Combine(smokeOutput!, route + "-small.json"), JsonSerializer.Deserialize<string>(geometry));
            await using var stream = File.Create(Path.Combine(smokeOutput!, route + "-small.png"));
            await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
        }
        File.WriteAllText(Path.Combine(smokeOutput!, "state.json"), JsonSerializer.Serialize(await Task.Run(() => ReadState(includeControls: false)), Program.Json));
        File.WriteAllText(Path.Combine(smokeOutput!, "timings.json"), JsonSerializer.Serialize(timings, Program.Json));
        File.WriteAllText(Path.Combine(smokeOutput!, "startup.json"), JsonSerializer.Serialize(new { shellReady, nativeSurfaceDisposed = startup is null }, Program.Json));
        File.WriteAllText(Path.Combine(smokeOutput!, "passed.txt"), "All categories switched in one document with live display data, without repeating display queries. Small-window screenshots captured. No display settings were changed.");
        Close();
    }

    private async Task ValidateArrangement()
    {
        string valid = await browser.ExecuteScriptAsync("Boolean(document.querySelector('.native-map')?.clientWidth > 0 && document.querySelector('.native-map')?.clientHeight > 0 && [...document.querySelectorAll('.native-monitor')].some(node => node.getBoundingClientRect().width > 0 && node.getBoundingClientRect().height > 0))");
        if (valid != "true") throw new InvalidOperationException("The arrangement map did not render visible displays.");
    }

    private static void Log(Exception error) => File.AppendAllText(Path.Combine(Program.DataDirectory, "errors.log"), $"{DateTime.UtcNow:O} {error}\n");

    private static void LogTiming(string method, long queueMs, long totalMs)
    {
        try
        {
            string path = Path.Combine(Program.DataDirectory, "communication.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1_048_576) File.Move(path, path + ".old", true);
            File.AppendAllText(path, $"{DateTime.UtcNow:O} {method} queue={queueMs}ms total={totalMs}ms\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
