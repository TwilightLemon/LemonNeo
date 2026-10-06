namespace LemonNeo.Hosting;

public sealed record HostOptions(string PluginDirectory, string LibraryDirectory, bool EnableMockSync = false);
