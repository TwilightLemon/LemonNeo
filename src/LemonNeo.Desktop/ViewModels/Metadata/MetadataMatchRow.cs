using LemonNeo.Abstractions.Providers;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Desktop.Services;

namespace LemonNeo.Desktop.ViewModels.Metadata;

public sealed record MetadataMatchRow(MetadataMatch Match, LocalizationService Text,
    RelayCommand<MetadataMatchRow> ReplaceMetadataCommand, RelayCommand<MetadataMatchRow> AddExternalIdsCommand)
{
    public Uri? ArtworkUri => Match.Metadata.ArtworkUri;
    public ArtworkService? ArtworkLoader { get; init; }
    public string Source => $"{Match.MetadataProviderId} / {Match.SourceNamespace}";
    public string Title => Match.Metadata.Title;
    public string Artists => string.Join(" / ", Match.Metadata.Artists);
    public string Album => Match.Metadata.Album ?? "—";
    public string Confidence => Match.Confidence.ToString("P0", Text.Culture);
    public string Level => Match.MatchLevel;
    public string ExternalIds => string.Join(Environment.NewLine, Match.Metadata.ExternalIds.Select(i => $"{i.Namespace}:{i.Kind ?? "-"} = {i.Value}"));
}
