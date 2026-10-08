namespace LemonNeo.Desktop.Config;

public sealed record DesktopStoragePreferences(
    string? LibraryDirectory = null,
    string? CacheDirectory = null);
