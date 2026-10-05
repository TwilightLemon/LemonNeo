using System.Globalization;
using Irihi.Lingua;
using LemonNeo.Plugin.Abstractions;

namespace LemonNeo.Desktop.Infrastructure;

public enum LanguagePreference { System, Chinese, English }

[LinguaManager("./Resources/Languages/Strings.json")]
public partial class LanguageManager;

/// <summary>Maps the persisted preference to Lingua's generated, reactive language manager.</summary>
public sealed class LocalizationService : IDisposable
{
    private readonly CultureInfo _systemCulture = CultureInfo.CurrentUICulture;
    private bool _hasApplied;
    private readonly IDisposable? _subscription;
    public LocalizationService(ISettingsScope? settings = null)
    {
        Apply(settings?.Get<LanguagePreferences>().Language ?? LanguagePreference.System);
        _subscription = settings?.Subscribe<LanguagePreferences>(value => Apply(value.Language));
    }

    public event EventHandler? Changed;
    public CultureInfo Culture { get; private set; } = CultureInfo.InvariantCulture;
    public LanguagePreference Preference { get; private set; }
    public string this[string key] =>
        (LanguageManager.Instance.GetObservable(key) as LinguaObservableString)?.CurrentValue ?? key;
    public string Format(string key, params object?[] arguments) => string.Format(Culture, this[key], arguments);

    public void Apply(LanguagePreference preference)
    {
        var normalized = Enum.IsDefined(preference) ? preference : LanguagePreference.System;
        // Reapplying the same language does not require relocalization.
        if (_hasApplied && Preference == normalized) return;
        Preference = normalized;
        var chinese = Preference == LanguagePreference.Chinese ||
                      Preference == LanguagePreference.System && _systemCulture.TwoLetterISOLanguageName == "zh";
        Culture = Preference == LanguagePreference.System ? _systemCulture : CultureInfo.GetCultureInfo(chinese ? "zh-CN" : "en-US");
        LanguageManager.Instance.UpdateCulture(chinese ? CultureInfo.GetCultureInfo("zh-CN") : CultureInfo.InvariantCulture);
        CultureInfo.DefaultThreadCurrentCulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        _hasApplied = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _subscription?.Dispose();
}
