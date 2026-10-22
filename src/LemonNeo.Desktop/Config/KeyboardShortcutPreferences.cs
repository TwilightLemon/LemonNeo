namespace LemonNeo.Desktop.Config;

/// <summary>
/// Per-action gesture overrides. A missing action uses its catalog default; an empty value disables it.
/// Values are <see cref="Avalonia.Input.KeyGesture"/> strings such as "Ctrl+O" or "Cmd+Left".
/// </summary>
public sealed record KeyboardShortcutPreferences(IReadOnlyDictionary<string, string> Bindings)
{
    public KeyboardShortcutPreferences() : this(new Dictionary<string, string>())
    {
    }
}
