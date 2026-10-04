using CommunityToolkit.Mvvm.ComponentModel;
using LemonNeo.Plugin.Abstractions;

namespace LemonNeo.Desktop.ViewModels;

public sealed partial class MetadataSourceOption(string sourceNamespace, bool isSelected) : ObservableObject
{
    public string Namespace { get; } = sourceNamespace;
    [ObservableProperty] private bool _isSelected = isSelected;

    /// <summary>Prefer metadata sources whose identifiers loaded playback providers can consume.</summary>
    public static IReadOnlyList<MetadataSourceOption> Create(IProviderRegistry providers, IPlaybackSourceProviderRegistry playbackSources)
    {
        var namespaces = providers.Providers.OfType<IMetadataProvider>()
            .SelectMany(provider => provider.MetadataNamespaces).Where(ns => !string.IsNullOrWhiteSpace(ns))
            .Select(ns => ns.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var supported = playbackSources.Sources
            .SelectMany(source => source.AcceptedIdSchemes.Select(scheme => scheme.Namespace).Append(source.ProviderId))
            .Where(ns => !string.IsNullOrWhiteSpace(ns)).Select(ns => ns.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Metadata remains useful when no loaded playback source covers these namespaces.
        var hasPreferred = namespaces.Any(supported.Contains);
        return namespaces.OrderByDescending(supported.Contains).ThenBy(ns => ns, StringComparer.OrdinalIgnoreCase)
            .Select(ns => new MetadataSourceOption(ns, !hasPreferred || supported.Contains(ns))).ToArray();
    }
}
