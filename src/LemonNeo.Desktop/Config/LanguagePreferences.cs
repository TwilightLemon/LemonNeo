namespace LemonNeo.Desktop.Config;

public enum LanguagePreference { System, Chinese, English }

public sealed record LanguagePreferences(LanguagePreference Language = LanguagePreference.System);
