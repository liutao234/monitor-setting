using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Pass the published MonitorSetting.dll path.");
        var assemblyPath = Path.GetFullPath(args[0]);
        var resolver = new AssemblyDependencyResolver(assemblyPath);
        AssemblyLoadContext.Default.Resolving += (_, name) => resolver.ResolveAssemblyToPath(name) is { } path
            ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        var app = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
        var formType = app.GetType("MonitorSetting.MainForm", throwOnError: true)!;
        using var form = (IDisposable)Activator.CreateInstance(formType, new object?[] { null })!;
        var dispatch = formType.GetMethod("Dispatch", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var samples = new List<object>();
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        JsonElement Call(string label, string method, object parameters)
        {
            var watch = Stopwatch.StartNew();
            object? result;
            try { result = dispatch.Invoke(form, [method, JsonSerializer.SerializeToElement(parameters)]); }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
            watch.Stop();
            samples.Add(new { label, elapsedMs = Math.Round(watch.Elapsed.TotalMilliseconds, 2) });
            return JsonSerializer.SerializeToElement(result, json);
        }
        var cold = Call("coldRead", "displays", new { });
        if (!cold.GetProperty("backgroundControls").GetBoolean()) throw new Exception("Progressive controls flag missing.");
        if (cold.GetProperty("displays").GetArrayLength() == 0) throw new Exception("Initial fast response lost identified devices.");
        Call("readWhileProbing", "displays", new { });
        JsonElement WaitReady(string label)
        {
            var watch = Stopwatch.StartNew();
            var read = formType.GetMethod("ReadState", BindingFlags.Instance | BindingFlags.NonPublic)!;
            while (watch.Elapsed < TimeSpan.FromSeconds(60))
            {
                // Snapshot reads do not restart already-completed probes merely to satisfy the test.
                var current = JsonSerializer.SerializeToElement(read.Invoke(form, [false]), json);
                if (current.GetProperty("controlsComplete").GetBoolean())
                {
                    samples.Add(new { label, elapsedMs = Math.Round(watch.Elapsed.TotalMilliseconds, 2) });
                    return current;
                }
                Thread.Sleep(25);
            }
            throw new TimeoutException("Background controls did not complete.");
        }
        var completed = WaitReady("backgroundReady");
        var builtin = completed.GetProperty("displays").EnumerateArray().FirstOrDefault(display =>
            display.GetProperty("kind").GetString() == "builtin" && display.GetProperty("active").GetBoolean());
        if (builtin.ValueKind == JsonValueKind.Undefined) throw new Exception("No active built-in display.");
        var brightness = builtin.GetProperty("controls").GetProperty("brightness");
        if (!brightness.GetProperty("supported").GetBoolean()) throw new Exception("Built-in brightness unavailable.");
        Call("warmRead1", "displays", new { });
        Call("warmRead2", "displays", new { });
        var forced = Call("forcedFastRead", "displays", new { forceRefresh = true });
        if (forced.GetProperty("controlsComplete").GetBoolean()) throw new Exception("Forced refresh must start new background probes.");
        WaitReady("forcedBackgroundReady");
        if (formType.GetField("cacheInvalidation", BindingFlags.Instance | BindingFlags.NonPublic) is { } invalidation)
        {
            invalidation.SetValue(form, 1);
            var partial = Call("readAfterDeviceEvent", "displays", new { });
            if (partial.GetProperty("controlsComplete").GetBoolean())
                throw new Exception("Missing hardware control cache must be marked incomplete.");
            var refreshed = WaitReady("completeAfterDeviceEvent");
            if (!refreshed.GetProperty("controlsComplete").GetBoolean())
                throw new Exception("Refresh did not restore full control capabilities.");
        }
        var final = Call("finalRead", "displays", new { });
        if (final.GetProperty("revision").GetInt64() <= cold.GetProperty("revision").GetInt64())
            throw new Exception("Snapshot revisions did not increase.");
        Console.WriteLine(JsonSerializer.Serialize(new { samples, settingsChanged = false }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
