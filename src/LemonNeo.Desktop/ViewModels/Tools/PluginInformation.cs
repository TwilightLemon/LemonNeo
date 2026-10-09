namespace LemonNeo.Desktop.ViewModels.Tools;

public sealed record PluginDetail(string Label, string Value);

public sealed record DecoderInformation(
    string Name,
    string Id,
    string Description,
    IReadOnlyList<string> SupportedExtensions,
    bool IsPreferred)
{
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public bool HasSupportedExtensions => SupportedExtensions.Count > 0;
}

public sealed record PluginInformation(
    string Name,
    string Id,
    string Description,
    string PluginSummary,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> SupportedNamespaces,
    IReadOnlyList<string> PlayableNamespaces,
    IReadOnlyList<PluginDetail> Details)
{
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public bool HasPluginSummary => !string.IsNullOrWhiteSpace(PluginSummary);
    public bool HasSupportedNamespaces => SupportedNamespaces.Count > 0;
    public bool HasPlayableNamespaces => PlayableNamespaces.Count > 0;
    public bool HasDetails => Details.Count > 0;
}

public sealed record PluginProviderGroup(string Name, IReadOnlyList<PluginInformation> Items)
{
    public int Count => Items.Count;
}
