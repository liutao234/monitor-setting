using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MonitorSetting;

internal sealed partial class MainForm
{
    private GlobalShortcut? shortcut;
    private NotifyIcon? tray;
    private ContextMenuStrip? trayMenu;
    private ToolStripMenuItem? openItem, exitItem, hotkeyUnavailable;
    private bool exitRequested, restoringHidden;
    private string desktopLocale = LoadDesktopLocale();
    private static string LocaleFile => Path.Combine(Program.DataDirectory, "desktop-language.json");

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (smokeOutput is null && !closing) InitializeDesktopShell();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        shortcut?.Dispose();
        shortcut = null;
        base.OnHandleDestroyed(e);
    }

    private void InitializeDesktopShell()
    {
        if (tray is null)
        {
            openItem = new ToolStripMenuItem();
            openItem.Click += (_, _) => ActivateFromShortcut();
            exitItem = new ToolStripMenuItem();
            exitItem.Click += (_, _) => RequestExit();
            hotkeyUnavailable = new ToolStripMenuItem { Enabled = false, Visible = false };
            trayMenu = new ContextMenuStrip();
            trayMenu.Items.AddRange([openItem, hotkeyUnavailable, new ToolStripSeparator(), exitItem]);
            tray = new NotifyIcon { Icon = SystemIcons.Application, ContextMenuStrip = trayMenu, Visible = true };
            tray.DoubleClick += (_, _) => ActivateFromShortcut();
            UpdateDesktopLocale();
        }
        if (shortcut is null)
        {
            bool registered = GlobalShortcut.TryCreate(Handle, out shortcut, out string? error);
            hotkeyUnavailable!.Visible = !registered;
            if (!registered) Log(new InvalidOperationException(error ?? "Win+O registration failed."));
        }
    }

    internal void ActivateFromShortcut()
    {
        if (closing || IsDisposed) return;
        if (InvokeRequired) { BeginInvoke((Action)ActivateFromShortcut); return; }
        Show();
        if (WindowState == FormWindowState.Minimized) ShowWindow(Handle, 9); // SW_RESTORE preserves maximized state.
        BringToFront();
        Activate();
        if (SetForegroundWindow(Handle)) return;
        // This path is reached only for an explicit hotkey/tray/second-launch
        // activation. Join the foreground input queue just for the focus change.
        nint foreground = GetForegroundWindow();
        uint foregroundThread = GetWindowThreadProcessId(foreground, out _);
        uint thisThread = GetCurrentThreadId();
        bool attached = foregroundThread != 0 && foregroundThread != thisThread &&
            AttachThreadInput(thisThread, foregroundThread, true);
        try { BringToFront(); Activate(); SetForegroundWindow(Handle); }
        finally { if (attached) AttachThreadInput(thisThread, foregroundThread, false); }
    }

    private void RequestExit()
    {
        exitRequested = true;
        Close();
    }

    private async Task RestoreHiddenPending()
    {
        if (restoringHidden || closing) return;
        restoringHidden = true;
        await commands.WaitAsync();
        try
        {
            if (closing) return;
            stateGeneration++;
            bool restored = await Task.Run(() =>
            {
                if (!transaction.HasPending) return false;
                transaction.Revert();
                return true;
            });
            if (restored) { InvalidateCaches(); Notify(new { @event = "displaysChanged" }); }
        }
        catch (Exception error)
        {
            Log(error);
            ActivateFromShortcut();
        }
        finally { stateGeneration++; commands.Release(); restoringHidden = false; }
    }

    private string DesktopText(string zh, string ja, string ko, string en) => desktopLocale switch
    { "ja" => ja, "ko" => ko, "en" => en, _ => zh };

    private static string LoadDesktopLocale()
    {
        try
        {
            string? value = JsonSerializer.Deserialize<string>(File.ReadAllText(LocaleFile));
            return value is "zh-CN" or "ja" or "ko" or "en" ? value : "zh-CN";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return "zh-CN"; }
    }

    private void ApplyDesktopLocale(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("locale", out var element) ||
            element.ValueKind != JsonValueKind.String) return;
        string? locale = element.GetString();
        if (locale is not ("zh-CN" or "ja" or "ko" or "en") || locale == desktopLocale) return;
        desktopLocale = locale;
        if (smokeOutput is null)
            try { File.WriteAllText(LocaleFile, JsonSerializer.Serialize(locale)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        UpdateDesktopLocale();
    }

    private void UpdateDesktopLocale()
    {
        Text = DesktopText("屏幕工作台", "画面ワークスペース", "디스플레이 작업 공간", "Display Workspace");
        if (tray is null) return;
        tray.Text = Text + " · Win+O";
        openItem!.Text = DesktopText("打开显示器设置", "ディスプレイ設定を開く", "디스플레이 설정 열기", "Open display settings") + "  Win+O";
        exitItem!.Text = DesktopText("退出", "終了", "종료", "Exit");
        hotkeyUnavailable!.Text = DesktopText("Win+O 无法启用", "Win+O を利用できません", "Win+O 사용 불가", "Win+O unavailable");
    }

    private void OfferRuntimeInstall()
    {
        string installer = Path.Combine(AppContext.BaseDirectory, "install", "MicrosoftEdgeWebview2Setup.exe");
        string message = DesktopText(
            "首次运行需要 Microsoft Edge WebView2。是否打开安装程序？安装需要联网；完成后请重新启动本软件。",
            "Microsoft Edge WebView2 が必要です。インストーラーを開きますか？インターネット接続が必要です。完了後にアプリを再起動してください。",
            "Microsoft Edge WebView2가 필요합니다. 설치 프로그램을 열까요? 인터넷 연결이 필요하며 설치 후 앱을 다시 실행해 주세요.",
            "Microsoft Edge WebView2 is required. Open its installer? An internet connection is needed. Restart this app after installation.");
        if (MessageBox.Show(this, message, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
        {
            try
            {
                Process.Start(new ProcessStartInfo(File.Exists(installer) ? installer : "https://developer.microsoft.com/microsoft-edge/webview2/")
                { UseShellExecute = true });
            }
            catch (Exception error) { Log(error); MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
        RequestExit();
    }

    private void DisposeDesktopShell()
    {
        shortcut?.Dispose(); shortcut = null;
        if (tray is not null) { tray.Visible = false; tray.Dispose(); tray = null; }
        trayMenu?.Dispose(); trayMenu = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposeDesktopShell();
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint source, uint target, [MarshalAs(UnmanagedType.Bool)] bool attach);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);
}
