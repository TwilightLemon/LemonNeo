using LemonNeo.Abstractions.Providers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Desktop.Services;

namespace LemonNeo.Desktop.ViewModels.Metadata;

public sealed class MetadataMatchRow(MetadataMatch match, LocalizationService text,
    RelayCommand<MetadataMatchRow> replaceMetadataCommand, RelayCommand<MetadataMatchRow> addExternalIdsCommand) : ObservableObject
{
    private string? _actionStatusKey;
    public MetadataMatch Match { get; } = match;
    public LocalizationService Text { get; } = text;
    public RelayCommand<MetadataMatchRow> ReplaceMetadataCommand { get; } = replaceMetadataCommand;
    public RelayCommand<MetadataMatchRow> AddExternalIdsCommand { get; } = addExternalIdsCommand;
    public Uri? ArtworkUri => Match.Track.ArtworkUri;
    public ArtworkService? ArtworkLoader { get; init; }
    public string Source => $"{Match.MetadataProviderId} / {Match.SourceNamespace}";
    public string Title => Match.Track.Title;
    public string Artists => string.Join(" / ", Match.Track.Artists.Select(artist => artist.Name));
    public string Album => Match.Track.Album?.Title ?? "—";
    public string Confidence => Match.Confidence.ToString("P0", Text.Culture);
    public string Level => Match.MatchLevel;
    public string ExternalIds => string.Join(Environment.NewLine, Match.Track.ExternalIds.Select(i => $"{i.Namespace}:{i.Kind ?? "-"} = {i.Value}"));
    public bool HasActionStatus => _actionStatusKey is not null;
    public string ActionStatus => _actionStatusKey is null ? "" : Text[_actionStatusKey];

    public void MarkMetadataReplaced() => SetActionStatus("MetadataReplacedConfirmation");
    public void MarkExternalIdsAdded() => SetActionStatus("MetadataIdsAddedConfirmation");
    internal void RefreshLocalizedProperties() => OnPropertyChanged(nameof(ActionStatus));

    private void SetActionStatus(string key)
    {
        _actionStatusKey = key;
        OnPropertyChanged(nameof(ActionStatus));
        OnPropertyChanged(nameof(HasActionStatus));
    }
}
