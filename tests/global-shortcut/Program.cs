using MonitorSetting;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var state = new WinOChordState();
        Check(state.Process(true, true, false, false, false) == WinOChordResult.Activate, "Win+O activates once");
        Check(state.Process(true, true, false, false, false) == WinOChordResult.Suppress, "Held O repeat is swallowed without another activation");
        Check(state.Process(false, false, false, false, false) == WinOChordResult.Suppress, "Release remains suppressed if Win was released first");
        Check(state.Process(true, false, false, false, false) == WinOChordResult.Pass, "Plain O is untouched");
        Check(state.Process(true, true, false, false, false) == WinOChordResult.Pass, "Adding Win to an already held O does not trigger a repeat");
        state.Process(false, false, false, false, false);
        foreach (var modifiers in new[] { (true, false, false), (false, true, false), (false, false, true) })
        {
            Check(state.Process(true, true, modifiers.Item1, modifiers.Item2, modifiers.Item3) == WinOChordResult.Pass, "Other modifier combinations are untouched");
            state.Process(false, false, false, false, false);
        }
        state.Reset(true);
        Check(state.Process(true, true, false, false, false) == WinOChordResult.Pass, "An O held before hook registration does not activate");
        state.Process(false, false, false, false, false);
        Check(state.Process(true, true, false, false, false) == WinOChordResult.Activate, "The next fresh Win+O press activates normally");

        // Registration and disposal only: no keys are injected and no display settings change.
        using var window = new Form();
        Check(GlobalShortcut.TryCreate(window.Handle, out var shortcut, out var error), "Native hotkey installed: " + error);
        using (shortcut)
        {
            Console.WriteLine("Backend: " + shortcut!.Backend);
            int message = shortcut.Backend == "RegisterHotKey" ? 0x0312 : GlobalShortcut.WakeMessage;
            Check(shortcut.MatchesMessage(message, GlobalShortcut.HotkeyId), "Wake message is accepted");
            Check(!shortcut.MatchesMessage(message, GlobalShortcut.HotkeyId + 1), "Other hotkey identifiers are ignored");
            Check(!shortcut.MatchesMessage(0x100, GlobalShortcut.HotkeyId), "Ordinary keyboard messages are ignored");
        }
        Check(!shortcut!.MatchesMessage(GlobalShortcut.WakeMessage, GlobalShortcut.HotkeyId), "Disposed shortcut no longer handles wake messages");
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        Console.WriteLine("PASS " + label);
    }
}
