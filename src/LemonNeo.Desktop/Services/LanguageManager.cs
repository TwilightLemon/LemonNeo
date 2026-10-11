using Irihi.Lingua;

namespace LemonNeo.Desktop.Services;

[LinguaManager("./Resources/Languages/Strings.json")]
public partial class LanguageManager
{
    /// <summary>
    /// Reads the current value of a generated Lingua key for APIs that require an immediate string.
    /// Prefer the generated observable property or the Translate markup extension for UI bindings.
    /// </summary>
    public static string GetCurrentValue(LinguaKey key) =>
        (key.Manager.GetObservable(key.Key) as LinguaObservableString)?.CurrentValue ?? key.Key;
}
