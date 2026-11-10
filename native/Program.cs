using System.Text.Json;

namespace MonitorSetting;

internal static class Program
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        IncludeFields = true,
        WriteIndented = true
    };
    internal static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonitorSetting");

    [STAThread]
    private static void Main(string[] args)
    {
        Directory.CreateDirectory(DataDirectory);
        try
        {
            if (args.Length == 2 && args[0] == "--watchdog")
            {
                LayoutTransaction.Watch(args[1]);
                return;
            }
            if (args.Length == 2 && args[0] == "--diagnostics")
            {
                var displays = new DisplayService().Enumerate();
                new MonitorControls().Populate(displays);
                File.WriteAllText(args[1], JsonSerializer.Serialize(new { displays }, Json));
                return;
            }
            string? smokeOutput = args.Length == 2 && args[0] == "--smoke-test" ? args[1] : null;
            // Read-only verification has an isolated browser profile and must not
            // require closing the user's running application.
            using var activation = smokeOutput is null ? new SingleInstanceActivation() : null;
            using var singleInstance = new Mutex(false, activation?.MutexName);
            bool acquired;
            try { acquired = singleInstance.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) { activation?.RequestActivation(); return; }
            try
            {
                ApplicationConfiguration.Initialize();
                using var form = new MainForm(smokeOutput);
                activation?.Attach(form, form.ActivateFromShortcut);
                Application.Run(form);
            }
            finally
            {
                activation?.Dispose();
                singleInstance.ReleaseMutex();
            }
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(DataDirectory, "startup-error.txt"), error.ToString());
            if (args.Length == 0) MessageBox.Show(error.Message, "MonitorSetting", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.ExitCode = 1;
        }
    }
}
