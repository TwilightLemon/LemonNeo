namespace LemonNeo.Desktop.Config;

public sealed record SearchScope(string? ProviderId = null, string? Namespace = null);

public sealed record SearchPreferences(SearchScope? Tracks = null, SearchScope? Albums = null, SearchScope? Artists = null);
