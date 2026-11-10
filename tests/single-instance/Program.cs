using MonitorSetting;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        string scope = "test-" + Guid.NewGuid().ToString("N");
        using var resident = new SingleInstanceActivation(scope);
        using var second = new SingleInstanceActivation(scope);
        using var independent = new SingleInstanceActivation(scope + "-other");
        using var window = new ActivationTestWindow { Opacity = 0, ShowInTaskbar = false };
        int activations = 0;
        Exception? failure = null;
        int uiThread = Environment.CurrentManagedThreadId;
        second.RequestActivation();
        resident.Attach(window, () =>
        {
            if (Environment.CurrentManagedThreadId != uiThread) failure = new Exception("Activation escaped the UI thread.");
            activations++;
        });
        window.Shown += async (_, _) =>
        {
            try
            {
                await WaitUntil(() => activations == 1);
                window.Hide();
                second.RequestActivation();
                await WaitUntil(() => activations == 2);
                independent.RequestActivation();
                await Task.Delay(80);
                if (activations != 2) throw new Exception("A different activation namespace reached the window.");
                window.DestroyForTest();
                second.RequestActivation();
                await Task.Delay(80);
                if (activations != 2) throw new Exception("Activation ran while the HWND was absent.");
                window.CreateForTest();
                await WaitUntil(() => activations == 3);
                resident.Dispose();
                second.RequestActivation();
                await Task.Delay(80);
                if (activations != 3) throw new Exception("Disposed listener accepted an activation.");
            }
            catch (Exception error) { failure = error; }
            finally { window.Close(); }
        };
        Application.Run(window);
        if (failure is not null) throw failure;
        Console.WriteLine("PASS early launch retained, hidden-window activation, UI-thread dispatch, namespace isolation, HWND recreation and listener disposal");
    }

    private static async Task WaitUntil(Func<bool> ready)
    {
        for (int i = 0; i < 100 && !ready(); i++) await Task.Delay(20);
        if (!ready()) throw new TimeoutException("Activation was not delivered.");
    }
}

internal sealed class ActivationTestWindow : Form
{
    internal void DestroyForTest() => DestroyHandle();
    internal void CreateForTest() => CreateHandle();
}
